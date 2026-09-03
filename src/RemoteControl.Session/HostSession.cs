using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using RemoteControl.Capture;
using RemoteControl.Codec;
using RemoteControl.Common;
using RemoteControl.Input;
using RemoteControl.Net.Congestion;
using RemoteControl.Net.Transport;
using RemoteControl.Net.Video;
using RemoteControl.Protocol;
using CodecColorConverter = RemoteControl.Codec.ColorConverter;

namespace RemoteControl.Session;

public static class HostSession
{
    /// <summary>
    /// Runs the LAN/P2P host loop: capture -> native NVENC -> UDP, over an already-connected
    /// <paramref name="transport"/>. Retries across desktop-mode changes, reusing the same
    /// transport throughout -- essential for the P2P path, where recreating the socket would
    /// abandon the punched NAT mapping and need a fresh hole-punch.
    /// </summary>
    public static void Run(
        ILogger logger,
        IUdpTransport transport,
        string peerDescription,
        SessionOptions options,
        Action<SessionStats>? onStats,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                RunSession(logger, transport, peerDescription, options, onStats, cancellationToken);
                return;
            }
            catch (DesktopConfigurationChangedException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                logger.Warn($"{ex.Message} Starting a new LAN video session for the new mode.");
            }
        }
    }

    private static void RunSession(
        ILogger logger,
        IUdpTransport socket,
        string peerDescription,
        SessionOptions options,
        Action<SessionStats>? onStats,
        CancellationToken cancellationToken)
    {
        var targetFrames = options.TargetFrames;
        var parityPercent = options.ParityPercent;
        var dropPercent = options.DropPercent;
        var adaptiveBitrate = options.AdaptiveBitrate;
        var adaptiveFec = options.AdaptiveFec;
        var intraRefresh = options.IntraRefresh;
        var remoteInput = options.RemoteInput;
        var fpsNumerator = options.FpsNumerator;
        var fpsDenominator = options.FpsDenominator;

        logger.Info($"Phase 1 LAN host: capture -> native NVENC -> UDP {peerDescription}" +
                    $"{(adaptiveFec ? $", adaptive FEC (ceiling {(parityPercent > 0 ? parityPercent : 50)}% parity)" : parityPercent > 0 ? $", {parityPercent}% FEC parity" : "")}" +
                    $"{(dropPercent > 0 ? $", simulating {dropPercent}% video-shard loss (diagnostic only)" : "")}" +
                    $"{(adaptiveBitrate ? ", adaptive bitrate enabled" : "")}" +
                    $"{(intraRefresh ? ", continuous intra-refresh (no periodic full IDR)" : "")}.");

        using var mfDevice = MfDevice.Create(logger);
        var displays = DisplayEnumerator.Enumerate(mfDevice.Device);
        if (displays.Count == 0)
            throw new InvalidOperationException("The D3D11 adapter has no attached desktop outputs.");
        if (options.OutputIndex >= displays.Count)
            throw new ArgumentException($"OutputIndex {options.OutputIndex} is out of range; the D3D11 adapter has {displays.Count} attached desktop output(s).");

        var selected = displays[(int)options.OutputIndex];
        logger.Info($"Capturing output {selected.OutputIndex}: {selected.DeviceName}, {selected.Width}x{selected.Height}.");
        using var duplicator = new DesktopDuplicator(
            mfDevice.Device,
            mfDevice.ImmediateContext,
            selected.OutputIndex,
            logger);
        if ((duplicator.Width & 1) != 0 || (duplicator.Height & 1) != 0)
            throw new NotSupportedException($"NV12 requires even dimensions; selected output is {duplicator.Width}x{duplicator.Height}.");

        using var converter = new CodecColorConverter(mfDevice, duplicator.Width, duplicator.Height, logger: logger);
        using var encoder = new NvencEncoder(
            mfDevice,
            duplicator.Width,
            duplicator.Height,
            fpsNumerator,
            fpsDenominator,
            lowLatency: true,
            bitrateBps: options.BitrateBps,
            intraRefresh: intraRefresh,
            logger: logger);
        Span<byte> sessionBytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(sessionBytes);
        var sessionId = BinaryPrimitives.ReadUInt64LittleEndian(sessionBytes);
        var configuration = LanDatagramCodec.CreateConfiguration(
            sessionId,
            duplicator.Width,
            duplicator.Height,
            fpsNumerator,
            fpsDenominator);
        WaitForClient(socket, configuration, sessionId, logger, cancellationToken);

        // Adaptive FEC (docs/PHASE-4.md): starts at 0 parity and scales up off the client's
        // measured QualityReport loss rate instead of committing to one fixed ratio for the
        // whole session -- avoids wasting bandwidth on a clean link and under-protecting on a
        // bad one. --parity-percent still sets the ceiling it's clamped to (default 50%) so it
        // can never balloon unboundedly on a very lossy link.
        var parityCeiling = parityPercent > 0 ? parityPercent / 100.0 : 0.5;
        var packetizer = new VideoPacketizer(parityRatio: adaptiveFec ? 0.0 : parityPercent / 100.0);
        var dropRng = dropPercent > 0 ? new Random() : null;

        // Injects the client's captured mouse/keyboard onto this machine's real desktop -- see
        // docs/PHASE-3.md. Target bounds are the same display being captured/streamed (lesson #1:
        // physical pixels, the same coordinate space the client normalized against).
        var inputInjector = remoteInput ? new InputInjector() : null;
        var inputEventsReceived = 0L;
        var inputEventsDuplicateOrStale = 0L;
        var inputSequenceDedup = new InputSequenceDedup();
        // Newest input sequence injected so far -- stamped onto each captured frame (as a
        // FrameInputMarker) so the client can measure its own input-to-present latency
        // (docs/PHASE-4.md). Plain assignment, not monotonic: dedup accepts by membership, so
        // a late redundant copy of an earlier sequence can land after a newer one; the client
        // filters out markers that go backwards.
        uint? lastInjectedInputSequence = null;
        var run = Stopwatch.StartNew();
        TimeSpan? runLimit = targetFrames == 0
            ? null
            : TimeSpan.FromSeconds(Math.Max(20, targetFrames / 30.0));

        var captured = 0;
        var encoded = 0;
        var frameIndex = 0u;
        var packetsSent = 0L;
        var encodedBytesSent = 0L;
        var wireBytesSent = 0L;
        var acquireTimeouts = 0;
        var droppedShardsSimulated = 0;
        var sendTimes = new List<double>();

        // Round-trip latency probe, sent ~1x/sec (docs/PHASE-1.md gate item 3).
        // RTT is computed entirely from the host's own Stopwatch clock (send
        // time vs. this same clock when the echo comes back), so no
        // cross-machine clock sync is needed for it. The wall-clock fields
        // only feed the offset *estimate*, useful for correlating host/client
        // log timestamps once this runs across two real machines -- on
        // localhost both ends share a clock, so offset is expected to be ~0.
        var latencyBuffer = new byte[64]; // Fits LatencyEcho (37B) with room to spare.
        var nextLatencyProbe = TimeSpan.Zero;
        var latencyRttMs = new List<double>();
        var latencyOffsetMs = new List<double>();
        var lastRttMs = 0.0;
        var lastLossRate = 0.0;

        // Adaptive bitrate (docs/PHASE-4.md): reacts to the client's QualityReport (frame loss)
        // and the same RTT samples the latency probe above already measures. Bounded to never
        // exceed the encoder's own starting bitrate -- only ever backs off and recovers, never
        // pushes past the configured target quality.
        var congestion = adaptiveBitrate
            ? new CongestionController(startingBitrateBps: encoder.CurrentBitrateBps, minBitrateBps: 1_000_000, maxBitrateBps: encoder.CurrentBitrateBps)
            : null;

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   (targetFrames == 0 || encoded < targetFrames) &&
                   (runLimit is null || run.Elapsed < runLimit.Value))
            {
                if (run.Elapsed >= nextLatencyProbe)
                {
                    nextLatencyProbe = run.Elapsed + TimeSpan.FromSeconds(1);
                    socket.Send(LanDatagramCodec.CreateLatencyProbe(sessionId, Stopwatch.GetTimestamp(), DateTime.UtcNow.Ticks));
                    onStats?.Invoke(new SessionStats(
                        Frames: encoded,
                        Fps: encoded / run.Elapsed.TotalSeconds,
                        RttMs: lastRttMs,
                        LossRate: lastLossRate,
                        BitrateBps: encoder.CurrentBitrateBps));
                }

                while (socket.Available > 0)
                {
                    var received = socket.Receive(latencyBuffer);
                    if (!LanDatagramCodec.TryRead(latencyBuffer.AsSpan(0, received), out var message) ||
                        message.SessionId != sessionId)
                    {
                        continue;
                    }

                    if (message.Kind == LanDatagramKind.LatencyEcho)
                    {
                        var (probePerfTicks, probeWallTicks, clientWallTicks) = LanDatagramCodec.ReadLatencyEcho(message.Payload.Span);
                        var nowPerfTicks = Stopwatch.GetTimestamp();
                        var rttMs = (nowPerfTicks - probePerfTicks) * 1000.0 / Stopwatch.Frequency;
                        var rttTicks = (nowPerfTicks - probePerfTicks) * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
                        latencyRttMs.Add(rttMs);
                        lastRttMs = rttMs;
                        // Standard symmetric-latency estimate: offset = clientClock - hostClock - RTT/2.
                        latencyOffsetMs.Add((clientWallTicks - probeWallTicks - rttTicks / 2.0) / TimeSpan.TicksPerMillisecond);

                        if (congestion is not null)
                        {
                            var newBitrate = congestion.OnSample(frameLossRate: null, rttMs: rttMs);
                            if (newBitrate != encoder.CurrentBitrateBps)
                                encoder.SetBitrate(newBitrate);
                        }
                    }
                    else if (message.Kind == LanDatagramKind.QualityReport && (congestion is not null || adaptiveFec))
                    {
                        var lossRate = LanDatagramCodec.ReadQualityReport(message.Payload.Span);
                        lastLossRate = lossRate;
                        if (congestion is not null)
                        {
                            var newBitrate = congestion.OnSample(frameLossRate: lossRate, rttMs: null);
                            if (newBitrate != encoder.CurrentBitrateBps)
                                encoder.SetBitrate(newBitrate);
                        }

                        if (adaptiveFec)
                        {
                            // 2x safety margin over the measured average -- a single loss-rate sample
                            // doesn't capture burst variance, and under-protecting costs a corrupted
                            // frame while over-protecting only costs a little bandwidth. Simple linear
                            // heuristic, not a real burst-loss model -- tighten if real testing shows
                            // it over/under-shoots.
                            packetizer.ParityRatio = Math.Clamp(lossRate * 2.0, 0.0, parityCeiling);
                        }
                    }
                    else if (message.Kind == LanDatagramKind.Input && inputInjector is not null)
                    {
                        try
                        {
                            var (sequenceNumber, encodedEvent) = LanDatagramCodec.ReadInput(message.Payload);
                            // Exact-membership dedup, not strict ordering -- see InputSequenceDedup's remarks
                            // for the real bug a naive "only accept increasing" gate has with KeyDown/KeyUp pairs.
                            if (inputSequenceDedup.TryAccept(sequenceNumber))
                            {
                                var inputEvent = InputEventCodec.Decode(encodedEvent.Span);
                                inputInjector.Inject(inputEvent, (int)selected.Left, (int)selected.Top, (int)selected.Width, (int)selected.Height);
                                inputEventsReceived++;
                                lastInjectedInputSequence = sequenceNumber;
                            }
                            else
                            {
                                inputEventsDuplicateOrStale++;
                            }
                        }
                        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
                        {
                            logger.Warn($"Discarding malformed input event: {ex.Message}");
                        }
                    }
                    else if (message.Kind == LanDatagramKind.InputStateSync && inputInjector is not null)
                    {
                        inputInjector.ReconcileHeldState(LanDatagramCodec.ReadInputStateSync(message.Payload.Span));
                    }
                }

                if (!duplicator.TryAcquireNextFrame(100, out var desktopFrame))
                {
                    acquireTimeouts++;
                    continue;
                }

                using (desktopFrame)
                {
                    captured++;
                    var sampleTime = (captured - 1L) * 10_000_000L * fpsDenominator / fpsNumerator;
                    var sampleDuration = 10_000_000L * fpsDenominator / fpsNumerator;
                    var (nv12, nv12Subresource) = converter.Convert(
                        desktopFrame!.Texture,
                        sampleTime,
                        sampleDuration);
                    // ponytail: assumes NVENC's low-latency P1 config emits exactly one decoded
                    // output per Encode call (matches captured==encoded seen on every real run
                    // tonight). If a future run ever shows those diverge, upgrade this single
                    // snapshot to a Queue<uint?> enqueued here, dequeued once per callback below.
                    var inputSequenceAtCapture = lastInjectedInputSequence;
                    using (nv12)
                    {
                        encoder.Encode(nv12, encodedBytes =>
                        {
                            // Sent before this frame's own video shards (below), and before the
                            // send timer starts, so it doesn't pollute the packetize+send stats --
                            // see docs/PHASE-4.md's input-to-present latency measurement.
                            if (inputSequenceAtCapture is { } injectedSequence)
                                socket.Send(LanDatagramCodec.CreateFrameInputMarker(sessionId, injectedSequence));

                            var sendTimer = Stopwatch.StartNew();
                            encoded++;
                            encodedBytesSent += encodedBytes.Length;
                            foreach (var videoPacket in packetizer.Packetize(frameIndex++, encodedBytes))
                            {
                                // Diagnostic-only, for proving FEC recovery works over the real
                                // socket path -- not a real network's loss, so it's a plain RNG
                                // check on the shard we're about to send, not queue/burst modeling.
                                if (dropRng is not null && dropRng.Next(100) < dropPercent)
                                {
                                    droppedShardsSimulated++;
                                    continue;
                                }

                                var datagram = LanDatagramCodec.WrapVideo(sessionId, videoPacket);
                                socket.Send(datagram);
                                packetsSent++;
                                wireBytesSent += datagram.Length;
                            }
                            sendTimer.Stop();
                            sendTimes.Add(sendTimer.Elapsed.TotalMilliseconds);
                        }, nv12Subresource);
                    }
                }

                Pace(encoded, run, fpsNumerator, fpsDenominator);
            }

            encoder.Drain(encodedBytes =>
            {
                encoded++;
                encodedBytesSent += encodedBytes.Length;
                foreach (var videoPacket in packetizer.Packetize(frameIndex++, encodedBytes))
                {
                    var datagram = LanDatagramCodec.WrapVideo(sessionId, videoPacket);
                    socket.Send(datagram);
                    packetsSent++;
                    wireBytesSent += datagram.Length;
                }
            });

            var end = LanDatagramCodec.CreateEnd(sessionId);
            for (var i = 0; i < 3; i++)
                socket.Send(end);
        }
        finally
        {
            inputInjector?.ReleaseAllHeld(); // lesson #3 safety net: never leave a stuck button/modifier on session end.
        }

        logger.Info(
            $"[lan-host] captured={captured}, encoded={encoded}, packets={packetsSent}, " +
            $"payload={encodedBytesSent / 1024.0:0.0}KiB, wire={wireBytesSent / 1024.0:0.0}KiB, timeouts={acquireTimeouts}, " +
            $"rate={(encoded / run.Elapsed.TotalSeconds):0.##}fps" +
            $"{(dropPercent > 0 ? $", simulated-dropped-shards={droppedShardsSimulated}" : "")}.");
        if (sendTimes.Count > 5)
        {
            var steady = sendTimes.Skip(5).ToList();
            logger.Info(
                $"[lan-host] packetize+send avg={steady.Average():0.###}ms " +
                $"min={steady.Min():0.###}ms max={steady.Max():0.###}ms (n={steady.Count}, warmup skipped).");
        }
        if (latencyRttMs.Count > 0)
        {
            logger.Info(
                $"[lan-host] latency rtt avg={latencyRttMs.Average():0.###}ms min={latencyRttMs.Min():0.###}ms " +
                $"max={latencyRttMs.Max():0.###}ms, clock-offset avg={latencyOffsetMs.Average():0.###}ms (n={latencyRttMs.Count}).");
        }
        if (congestion is not null)
        {
            logger.Info($"[lan-host] adaptive bitrate ended at {encoder.CurrentBitrateBps / 1_000_000.0:0.##}Mbps.");
        }
        if (adaptiveFec)
        {
            logger.Info($"[lan-host] adaptive FEC ended at {packetizer.ParityRatio * 100:0.#}% parity (ceiling {parityCeiling * 100:0.#}%).");
        }
        if (inputInjector is not null)
        {
            logger.Info($"[lan-host] input-events-received={inputEventsReceived}, input-events-duplicate-or-stale={inputEventsDuplicateOrStale}.");
        }

        if (targetFrames > 0 && encoded < targetFrames && !cancellationToken.IsCancellationRequested)
            throw new InvalidOperationException($"LAN host did not encode {targetFrames} frames within {runLimit!.Value.TotalSeconds:0}s (got {encoded}).");
        logger.Info("PASS -- LAN host completed its real capture/encode/send run.");
    }

    private static void Pace(int encodedFrames, Stopwatch run, uint fpsNumerator, uint fpsDenominator)
    {
        var targetTicks = encodedFrames * TimeSpan.TicksPerSecond * fpsDenominator / fpsNumerator;
        var targetElapsed = TimeSpan.FromTicks(targetTicks);
        var remaining = targetElapsed - run.Elapsed;
        if (remaining > TimeSpan.FromMilliseconds(2))
            Thread.Sleep(remaining - TimeSpan.FromMilliseconds(1));

        while (run.Elapsed < targetElapsed)
            Thread.SpinWait(64);
    }

    private static void WaitForClient(IUdpTransport socket, byte[] configuration, ulong sessionId, ILogger logger, CancellationToken cancellationToken)
    {
        logger.Info("Waiting indefinitely for the client handshake before sending the first IDR frame.");
        var waiting = Stopwatch.StartNew();
        var nextSend = TimeSpan.Zero;
        var receiveBuffer = new byte[256];

        while (!cancellationToken.IsCancellationRequested)
        {
            if (waiting.Elapsed >= nextSend)
            {
                socket.Send(configuration);
                nextSend = waiting.Elapsed + TimeSpan.FromMilliseconds(250);
            }

            if (!socket.Poll(50_000))
                continue;

            var received = socket.Receive(receiveBuffer);
            if (LanDatagramCodec.TryRead(receiveBuffer.AsSpan(0, received), out var message) &&
                message.Kind == LanDatagramKind.Ready &&
                message.SessionId == sessionId)
            {
                logger.Info("Client is configured and ready; starting the video stream.");
                return;
            }
        }
    }
}
