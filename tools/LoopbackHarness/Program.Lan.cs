using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RemoteControl.Capture;
using RemoteControl.Codec;
using RemoteControl.Common;
using RemoteControl.Net.Transport;
using RemoteControl.Net.Video;
using RemoteControl.Render;
using CodecColorConverter = RemoteControl.Codec.ColorConverter;

namespace RemoteControl.Tools.LoopbackHarness;

internal static partial class Program
{
    private const int LanReceiveBufferSize = 4 * 1024 * 1024;

    private static string? ReadOption(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0)
            return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{option} requires a value.");
        return args[index + 1];
    }

    private static IPEndPoint ParseRemoteEndpoint(string value)
    {
        if (!IPEndPoint.TryParse(value, out var endpoint) || endpoint.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException($"--lan-host requires an IPv4 endpoint such as 127.0.0.1:47998; got '{value}'.");
        return endpoint;
    }

    private static int ParseListenPort(string value)
    {
        if (!int.TryParse(value, out var port) || port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            throw new ArgumentException($"--lan-client requires a UDP port from 0 through 65535; got '{value}'.");
        return port;
    }

    private static void RunLanHost(ILogger logger, IPEndPoint clientEndpoint, int targetFrames, int parityPercent, int dropPercent)
    {
        using IUdpTransport socket = new UdpTransport(LanReceiveBufferSize, LanReceiveBufferSize);
        socket.Connect(clientEndpoint);
        RunLanHostWithTransport(logger, socket, clientEndpoint.ToString(), targetFrames, parityPercent, dropPercent);
    }

    /// <summary>
    /// Retries <see cref="RunLanHostSession"/> across desktop-mode changes,
    /// reusing the same <paramref name="socket"/> throughout -- essential for
    /// the P2P path, where recreating the socket would abandon the punched
    /// NAT mapping and need a fresh hole-punch.
    /// </summary>
    private static void RunLanHostWithTransport(
        ILogger logger, IUdpTransport socket, string peerDescription, int targetFrames, int parityPercent, int dropPercent)
    {
        while (true)
        {
            try
            {
                RunLanHostSession(logger, socket, peerDescription, targetFrames, parityPercent, dropPercent);
                return;
            }
            catch (DesktopConfigurationChangedException ex)
            {
                logger.Warn($"{ex.Message} Starting a new LAN video session for the new mode.");
            }
        }
    }

    private static void RunLanHostSession(
        ILogger logger, IUdpTransport socket, string peerDescription, int targetFrames, int parityPercent, int dropPercent)
    {
        logger.Info($"Phase 1 LAN host: capture -> native NVENC -> UDP {peerDescription}" +
                    $"{(parityPercent > 0 ? $", {parityPercent}% FEC parity" : "")}" +
                    $"{(dropPercent > 0 ? $", simulating {dropPercent}% video-shard loss (diagnostic only)" : "")}.");

        using var mfDevice = MfDevice.Create(logger);
        var displays = DisplayEnumerator.Enumerate(mfDevice.Device);
        if (displays.Count == 0)
            throw new InvalidOperationException("The D3D11 adapter has no attached desktop outputs.");

        var selected = displays[0];
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
            FpsNumerator,
            FpsDenominator,
            lowLatency: true,
            logger: logger);
        Span<byte> sessionBytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(sessionBytes);
        var sessionId = BinaryPrimitives.ReadUInt64LittleEndian(sessionBytes);
        var configuration = LanDatagramCodec.CreateConfiguration(
            sessionId,
            duplicator.Width,
            duplicator.Height,
            FpsNumerator,
            FpsDenominator);
        WaitForLanClient(socket, configuration, sessionId, logger);

        var packetizer = new VideoPacketizer(parityRatio: parityPercent / 100.0);
        var dropRng = dropPercent > 0 ? new Random() : null;
        var run = Stopwatch.StartNew();
        TimeSpan? runLimit = targetFrames == 0
            ? null
            : TimeSpan.FromSeconds(Math.Max(20, targetFrames / 30.0));
        var stopRequested = false;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopRequested = true;
        };
        Console.CancelKeyPress += cancelHandler;

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

        try
        {
            while (!stopRequested &&
                   (targetFrames == 0 || encoded < targetFrames) &&
                   (runLimit is null || run.Elapsed < runLimit.Value))
            {
                if (run.Elapsed >= nextLatencyProbe)
                {
                    nextLatencyProbe = run.Elapsed + TimeSpan.FromSeconds(1);
                    socket.Send(LanDatagramCodec.CreateLatencyProbe(sessionId, Stopwatch.GetTimestamp(), DateTime.UtcNow.Ticks));
                }

                while (socket.Available > 0)
                {
                    var received = socket.Receive(latencyBuffer);
                    if (!LanDatagramCodec.TryRead(latencyBuffer.AsSpan(0, received), out var echo) ||
                        echo.Kind != LanDatagramKind.LatencyEcho ||
                        echo.SessionId != sessionId)
                    {
                        continue;
                    }

                    var (probePerfTicks, probeWallTicks, clientWallTicks) = LanDatagramCodec.ReadLatencyEcho(echo.Payload.Span);
                    var nowPerfTicks = Stopwatch.GetTimestamp();
                    var rttMs = (nowPerfTicks - probePerfTicks) * 1000.0 / Stopwatch.Frequency;
                    var rttTicks = (nowPerfTicks - probePerfTicks) * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
                    latencyRttMs.Add(rttMs);
                    // Standard symmetric-latency estimate: offset = clientClock - hostClock - RTT/2.
                    latencyOffsetMs.Add((clientWallTicks - probeWallTicks - rttTicks / 2.0) / TimeSpan.TicksPerMillisecond);
                }

                if (!duplicator.TryAcquireNextFrame(100, out var desktopFrame))
                {
                    acquireTimeouts++;
                    continue;
                }

                using (desktopFrame)
                {
                    captured++;
                    var sampleTime = (captured - 1L) * 10_000_000L * FpsDenominator / FpsNumerator;
                    var sampleDuration = 10_000_000L * FpsDenominator / FpsNumerator;
                    var (nv12, nv12Subresource) = converter.Convert(
                        desktopFrame!.Texture,
                        sampleTime,
                        sampleDuration);
                    using (nv12)
                    {
                        encoder.Encode(nv12, encodedBytes =>
                        {
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

                PaceLanHost(encoded, run);
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
            Console.CancelKeyPress -= cancelHandler;
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

        if (targetFrames > 0 && encoded < targetFrames)
            throw new InvalidOperationException($"LAN host did not encode {targetFrames} frames within {runLimit!.Value.TotalSeconds:0}s (got {encoded}).");
        logger.Info("PASS -- LAN host completed its real capture/encode/send run.");
    }

    private static void PaceLanHost(int encodedFrames, Stopwatch run)
    {
        var targetTicks = encodedFrames * TimeSpan.TicksPerSecond * FpsDenominator / FpsNumerator;
        var targetElapsed = TimeSpan.FromTicks(targetTicks);
        var remaining = targetElapsed - run.Elapsed;
        if (remaining > TimeSpan.FromMilliseconds(2))
            Thread.Sleep(remaining - TimeSpan.FromMilliseconds(1));

        while (run.Elapsed < targetElapsed)
            Thread.SpinWait(64);
    }

    private static void WaitForLanClient(IUdpTransport socket, byte[] configuration, ulong sessionId, ILogger logger)
    {
        logger.Info("Waiting indefinitely for the LAN client handshake before sending the first IDR frame (Ctrl+C to stop).");
        var waiting = Stopwatch.StartNew();
        var nextSend = TimeSpan.Zero;
        var receiveBuffer = new byte[256];

        while (true)
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
                logger.Info("LAN client is configured and ready; starting the video stream.");
                return;
            }
        }
    }

    private static void RunLanClient(ILogger logger, int listenPort, int targetFrames, bool verifyFrame)
    {
        logger.Info($"Phase 1 LAN client: listening for UDP video on 0.0.0.0:{listenPort}.");
        using IUdpTransport socket = new UdpTransport(LanReceiveBufferSize, sendBufferSize: 0);
        socket.Bind(new IPEndPoint(IPAddress.Any, listenPort));
        logger.Info($"LAN client bound to {socket.LocalEndPoint}; start the host with --lan-host <this-PC-ip>:{listenPort}.");
        RunLanClientSession(logger, socket, targetFrames, verifyFrame);
    }

    private static void RunLanClientSession(ILogger logger, IUdpTransport socket, int targetFrames, bool verifyFrame)
    {
        using var mfDevice = MfDevice.Create(logger);
        var displays = DisplayEnumerator.Enumerate(mfDevice.Device);
        if (displays.Count == 0)
            throw new InvalidOperationException("The D3D11 adapter has no attached desktop outputs.");
        var presentationDisplay = displays.Count > 1 ? displays[1] : displays[0];
        using var window = new PresentationWindow(
            presentationDisplay,
            "Remote-Control-Native — Phase 1 LAN client");
        logger.Info("The presentation window stays black until a host completes the handshake.");

        var receiveBuffer = new byte[ushort.MaxValue + 1];
        EndPoint source = new IPEndPoint(IPAddress.Any, 0);
        IPEndPoint? activeHost = null;
        LanClientVideoSession? session = null;
        var run = Stopwatch.StartNew();
        var lastPacket = run.Elapsed;
        var datagramsReceived = 0L;
        var malformedDatagrams = 0L;
        var ended = false;

        try
        {
            while (!window.IsClosed &&
                   !ended &&
                   (targetFrames == 0 || session is null || session.Presented < targetFrames))
            {
                window.PumpEvents();
                if (session is not null && window.TryConsumeResize(out var width, out var height))
                    session.Resize(width, height);

                if (!socket.Poll(10_000))
                {
                    if (session is not null && run.Elapsed - lastPacket > TimeSpan.FromSeconds(10))
                        throw new TimeoutException("LAN client received no video for 10 seconds after the stream started.");
                    continue;
                }

                source = new IPEndPoint(IPAddress.Any, 0);
                var received = socket.ReceiveFrom(receiveBuffer, ref source);
                lastPacket = run.Elapsed;
                datagramsReceived++;
                if (!LanDatagramCodec.TryRead(receiveBuffer.AsSpan(0, received), out var message))
                {
                    malformedDatagrams++;
                    continue;
                }

                var sender = (IPEndPoint)source;
                if (message.Kind == LanDatagramKind.Configuration)
                {
                    if (session is null || session.SessionId != message.SessionId)
                    {
                        session?.Dispose();
                        session = new LanClientVideoSession(
                            mfDevice,
                            window,
                            message,
                            verifyFrame,
                            logger);
                        activeHost = sender;
                    }

                    if (activeHost is not null && sender.Equals(activeHost))
                        socket.SendTo(LanDatagramCodec.CreateReady(message.SessionId), activeHost);
                    continue;
                }

                if (session is null || activeHost is null || !sender.Equals(activeHost) || message.SessionId != session.SessionId)
                    continue;

                if (message.Kind == LanDatagramKind.Video)
                {
                    if (!session.TryProcessVideoPacket(message.Payload.Span))
                        malformedDatagrams++;
                }
                else if (message.Kind == LanDatagramKind.End)
                {
                    session.Drain();
                    ended = true;
                }
                else if (message.Kind == LanDatagramKind.LatencyProbe)
                {
                    var (probePerfTicks, probeWallTicks) = LanDatagramCodec.ReadLatencyProbe(message.Payload.Span);
                    var echo = LanDatagramCodec.CreateLatencyEcho(message.SessionId, probePerfTicks, probeWallTicks, DateTime.UtcNow.Ticks);
                    socket.SendTo(echo, activeHost);
                }
            }
        }
        finally
        {
            if (session is not null)
            {
                logger.Info(
                    $"[lan-client] datagrams={datagramsReceived}, completed={session.CompletedFrames}, " +
                    $"decoded={session.Decoded}, presented={session.Presented}, malformed={malformedDatagrams}, " +
                    $"incomplete={session.IncompleteFrames}, dropped-incomplete={session.DroppedIncompleteFrames}.");
                session.Dispose();
            }
        }

        if (session is null)
        {
            logger.Warn("LAN client window was closed before a host connected.");
            return;
        }
        if (targetFrames > 0 && session.Presented < targetFrames)
        {
            logger.Warn(
                $"LAN client presented {session.Presented}/{targetFrames} target frames; " +
                $"{targetFrames - session.Presented} frame(s) were skipped rather than terminating the stream.");
        }
        else if (!window.IsClosed)
        {
            logger.Info("PASS -- LAN client received, reassembled, decoded, and presented the stream.");
        }
    }

    private sealed class LanClientVideoSession : IDisposable
    {
        private readonly HardwareDecoder _decoder;
        private readonly SwapChainPresenter _presenter;
        private readonly VideoDepacketizer _depacketizer = new();
        private readonly MfDevice _mfDevice;
        private readonly ILogger _logger;
        private readonly bool _verifyFrame;
        private bool _verificationSaved;
        private bool _drained;

        public ulong SessionId { get; }
        public int CompletedFrames { get; private set; }
        public int Decoded { get; private set; }
        public int Presented { get; private set; }
        public int IncompleteFrames => _depacketizer.InProgressFrameCount;
        public int DroppedIncompleteFrames => _depacketizer.DroppedIncompleteFrameCount;

        public LanClientVideoSession(
            MfDevice mfDevice,
            PresentationWindow window,
            LanDatagram configuration,
            bool verifyFrame,
            ILogger logger)
        {
            _mfDevice = mfDevice;
            _logger = logger;
            _verifyFrame = verifyFrame;
            SessionId = configuration.SessionId;
            SwapChainPresenter? presenter = null;
            HardwareDecoder? decoder = null;
            try
            {
                presenter = new SwapChainPresenter(
                    mfDevice.Device,
                    mfDevice.ImmediateContext,
                    window.Handle,
                    configuration.Width,
                    configuration.Height,
                    window.ClientWidth,
                    window.ClientHeight,
                    logger);
                decoder = new HardwareDecoder(
                    mfDevice,
                    configuration.Width,
                    configuration.Height,
                    configuration.FpsNumerator,
                    configuration.FpsDenominator,
                    logger);
            }
            catch
            {
                decoder?.Dispose();
                presenter?.Dispose();
                throw;
            }

            _presenter = presenter;
            _decoder = decoder;
            logger.Info(
                $"LAN video session {SessionId:X16} configured for " +
                $"{configuration.Width}x{configuration.Height}@" +
                $"{(double)configuration.FpsNumerator / configuration.FpsDenominator:0.##}fps.");
        }

        public bool TryProcessVideoPacket(ReadOnlySpan<byte> packet)
        {
            byte[]? encodedFrame;
            try
            {
                encodedFrame = _depacketizer.AddPacket(packet);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return false;
            }

            if (encodedFrame is null)
                return true;

            CompletedFrames++;
            _decoder.Decode(encodedFrame, Present);
            return true;
        }

        public void Resize(uint width, uint height) => _presenter.Resize(width, height);

        public void Drain()
        {
            if (_drained)
                return;
            _drained = true;
            _decoder.Drain(Present);
        }

        private void Present(DecodedFrame decodedFrame)
        {
            using (decodedFrame.Texture)
            {
                Decoded++;
                if (_verifyFrame && !_verificationSaved)
                {
                    _verificationSaved = true;
                    var path = Path.Combine(AppContext.BaseDirectory, "phase1-lan-client-verify-frame.png");
                    FrameVerifier.SaveNv12FrameAsPng(
                        _mfDevice.Device,
                        _mfDevice.ImmediateContext,
                        decodedFrame.Texture,
                        path,
                        decodedFrame.SubresourceIndex);
                    _logger.Info($"Wrote LAN client verification frame: {path}");
                }

                if (_presenter.Present(decodedFrame.Texture, decodedFrame.SubresourceIndex) == PresentOutcome.Presented)
                    Presented++;
            }
        }

        public void Dispose()
        {
            _decoder.Dispose();
            _presenter.Dispose();
        }
    }
}
