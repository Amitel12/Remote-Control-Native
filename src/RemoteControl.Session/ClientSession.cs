using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemoteControl.Capture;
using RemoteControl.Codec;
using RemoteControl.Common;
using RemoteControl.Input;
using RemoteControl.Net.Transport;
using RemoteControl.Protocol;

namespace RemoteControl.Session;

public static class ClientSession
{
    /// <summary>
    /// Runs the LAN/P2P client loop over an already-bound/connected <paramref name="transport"/>:
    /// waits for the host's handshake, then reassembles/decodes/presents the video stream in a
    /// dedicated <see cref="SessionWindow"/>.
    /// </summary>
    public static void Run(
        ILogger logger,
        IUdpTransport socket,
        SessionOptions options,
        Action<SessionStats>? onStats,
        CancellationToken cancellationToken)
    {
        var targetFrames = options.TargetFrames;
        var verifyFrame = options.VerifyFrame;
        var remoteInput = options.RemoteInput;
        var dropInputPercent = options.DropInputPercent;

        using var mfDevice = MfDevice.Create(logger);
        var displays = DisplayEnumerator.Enumerate(mfDevice.Device);
        if (displays.Count == 0)
            throw new InvalidOperationException("The D3D11 adapter has no attached desktop outputs.");
        var presentationDisplay = displays.Count > 1 ? displays[1] : displays[0];
        using var window = new SessionWindow(
            presentationDisplay,
            "Remote-Control-Native — Phase 1 LAN client");
        logger.Info("The presentation window stays black until a host completes the handshake.");

        var receiveBuffer = new byte[ushort.MaxValue + 1];
        EndPoint source = new IPEndPoint(IPAddress.Any, 0);
        IPEndPoint? activeHost = null;
        ClientVideoSession? session = null;
        var run = Stopwatch.StartNew();
        var lastPacket = run.Elapsed;
        var datagramsReceived = 0L;
        var malformedDatagrams = 0L;
        var ended = false;

        // Captures real local mouse/keyboard on the presentation window and forwards each event to
        // the host to inject -- see docs/PHASE-3.md. Opt-in: --remote-input on both ends.
        using var inputCapture = remoteInput ? new RawInputCapture(window.Handle) : null;
        var inputSendBuffer = new byte[InputEventCodec.MaxSize];
        var inputEventsCaptured = 0L; // logical events (one per real keystroke/click/move).
        var inputEventsSent = 0L; // physical sends that succeeded -- up to 2x captured, since each is sent twice.
        var inputEventsDroppedSimulated = 0L;
        var nextInputSequence = 0u;

        // Input-to-present latency (docs/PHASE-4.md) -- not true glass-to-glass: SwapChainPresenter
        // presents with syncInterval 0, so this stops at the queued flip, before scanout (~5-20ms
        // uncounted). Both ends of the measurement are this process's own Stopwatch, so no
        // cross-machine clock sync is needed -- see FrameInputMarker's remarks in LanDatagramCodec.
        var pendingInputSends = new Queue<(uint Sequence, long Ticks)>();
        var inputToPresentMs = new List<double>();
        uint? lastMarkerSequence = null;
        long? armedSendTicks = null;

        if (inputCapture is not null)
        {
            // Redundant send (docs/PHASE-3.md): a lost plain keystroke (no modifier, so
            // InputStateSync's held-state resync above can't fix it -- there's no "held" to
            // reconcile) previously just vanished with zero recovery. Sending each event twice --
            // once immediately, once ~20ms later -- turns a single independent packet-loss chance
            // p into p^2 for both copies to be lost. The sequence number lets the host apply only
            // the first copy it sees and ignore the rest.
            void SendInputCopy(uint sequenceNumber, byte[] encodedEvent, IPEndPoint host, ulong sessionId)
            {
                // Diagnostic-only, for proving out reliability (docs/PHASE-3.md) -- not a real
                // network's loss, deliberately deterministic-rate so a lost MouseUp/KeyUp specifically
                // can be reproduced on demand instead of hoping for it under generic packet loss.
                // Each of the two copies rolls independently, matching how a real network would drop
                // packets independently rather than "the logical event" as a whole.
                if (dropInputPercent > 0 && Random.Shared.Next(100) < dropInputPercent)
                {
                    Interlocked.Increment(ref inputEventsDroppedSimulated);
                    return;
                }
                socket.SendTo(LanDatagramCodec.WrapInput(sessionId, sequenceNumber, encodedEvent), host);
                Interlocked.Increment(ref inputEventsSent);
            }

            inputCapture.Captured += inputEvent =>
            {
                if (activeHost is not { } host || session is not { } activeSession) return;
                inputEventsCaptured++;
                var length = InputEventCodec.Encode(inputEvent, inputSendBuffer);
                var encodedEvent = inputSendBuffer[..length]; // copied: the shared scratch buffer isn't safe to reference across the delayed resend below.
                var sequenceNumber = nextInputSequence++;
                var sessionId = activeSession.SessionId;

                // Mouse moves fire at report rate (125-1000Hz), so "newest input at capture time"
                // would almost always be a move injected ~1ms ago -- measuring those would collapse
                // input-to-present down to downstream-only latency, silently hiding the entire
                // client->host leg most of the time. Discrete events are also what a user actually
                // judges responsiveness by.
                if (inputEvent is not InputEvent.MouseMove)
                {
                    if (pendingInputSends.Count >= 256)
                        pendingInputSends.Dequeue();
                    pendingInputSends.Enqueue((sequenceNumber, Stopwatch.GetTimestamp()));
                }

                SendInputCopy(sequenceNumber, encodedEvent, host, sessionId);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(20);
                    try { SendInputCopy(sequenceNumber, encodedEvent, host, sessionId); }
                    catch (SocketException) { /* socket may have been disposed by session end -- not worth surfacing from a background resend. */ }
                });
            };
            logger.Info("Remote input capture active -- real mouse/keyboard on the presentation window will be sent to the host." +
                        (dropInputPercent > 0 ? $" Simulating {dropInputPercent}% dropped input events (diagnostic only)." : ""));
        }

        // Windowed frame-loss feedback for RemoteControl.Net.Congestion.CongestionController
        // (docs/PHASE-4.md), sent back to the host ~1x/sec -- see "QualityReport" in LanDatagramCodec.
        var nextQualityReport = TimeSpan.Zero;
        var lastReportedCompleted = 0;
        var lastReportedDropped = 0;
        var lastReportedSkipped = 0;

        // Held-state resync (docs/PHASE-3.md): sent often enough that a single lost sync attempt
        // (subject to the same --drop-input-percent simulation as everything else on this socket)
        // doesn't meaningfully delay self-healing a lost MouseUp/KeyUp.
        var nextInputStateSync = TimeSpan.Zero;
        var inputStateSyncInterval = TimeSpan.FromMilliseconds(300);

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   !window.IsClosed &&
                   !ended &&
                   (targetFrames == 0 || session is null || session.Presented < targetFrames))
            {
                window.PumpEvents();
                if (session is not null && window.TryConsumeResize(out var width, out var height))
                    session.Resize(width, height);

                if (inputCapture is not null && activeHost is not null && session is not null && run.Elapsed >= nextInputStateSync)
                {
                    nextInputStateSync = run.Elapsed + inputStateSyncInterval;
                    if (dropInputPercent == 0 || Random.Shared.Next(100) >= dropInputPercent)
                        socket.SendTo(LanDatagramCodec.CreateInputStateSync(session.SessionId, inputCapture.GetHeldMask()), activeHost);
                }

                if (session is not null && activeHost is not null && run.Elapsed >= nextQualityReport)
                {
                    nextQualityReport = run.Elapsed + TimeSpan.FromSeconds(1);
                    var completedDelta = session.CompletedFrames - lastReportedCompleted;
                    var droppedDelta = session.DroppedIncompleteFrames - lastReportedDropped;
                    var skippedDelta = session.SkippedForReordering - lastReportedSkipped;
                    var opportunities = completedDelta + droppedDelta;
                    var lossRate = opportunities > 0 ? (float)(droppedDelta + skippedDelta) / opportunities : 0f;
                    socket.SendTo(LanDatagramCodec.CreateQualityReport(session.SessionId, lossRate), activeHost);
                    lastReportedCompleted = session.CompletedFrames;
                    lastReportedDropped = session.DroppedIncompleteFrames;
                    lastReportedSkipped = session.SkippedForReordering;
                    onStats?.Invoke(new SessionStats(
                        Frames: session.Presented,
                        Fps: session.Presented / run.Elapsed.TotalSeconds,
                        RttMs: 0,
                        LossRate: lossRate,
                        BitrateBps: 0));
                }

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
                        session = new ClientVideoSession(
                            mfDevice,
                            window.Handle,
                            window.ClientWidth,
                            window.ClientHeight,
                            message,
                            verifyFrame,
                            logger);
                        activeHost = sender;
                        pendingInputSends.Clear();
                        lastMarkerSequence = null;
                        armedSendTicks = null;
                    }

                    if (activeHost is not null && sender.Equals(activeHost))
                        socket.SendTo(LanDatagramCodec.CreateReady(message.SessionId), activeHost);
                    continue;
                }

                if (session is null || activeHost is null || !sender.Equals(activeHost) || message.SessionId != session.SessionId)
                    continue;

                if (message.Kind == LanDatagramKind.Video)
                {
                    var presentedBefore = session.Presented;
                    if (!session.TryProcessVideoPacket(message.Payload.Span))
                        malformedDatagrams++;
                    else if (armedSendTicks is { } sentAt && session.Presented > presentedBefore)
                    {
                        // Reassembly -> decode -> present all run synchronously inside the call
                        // above, so this is the frame the armed marker announced.
                        armedSendTicks = null;
                        inputToPresentMs.Add((Stopwatch.GetTimestamp() - sentAt) * 1000.0 / Stopwatch.Frequency);
                    }
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
                else if (message.Kind == LanDatagramKind.FrameInputMarker)
                {
                    // Level-triggered by the host (one per frame), so a lost marker self-heals on
                    // the next frame. Wrap-safe "strictly newer" filter: a reordered older marker
                    // must not re-arm an input already measured.
                    var injected = LanDatagramCodec.ReadFrameInputMarker(message.Payload.Span);
                    if (lastMarkerSequence is null || (int)(injected - lastMarkerSequence.Value) > 0)
                    {
                        lastMarkerSequence = injected;
                        if (armedSendTicks is null &&
                            pendingInputSends.TryPeek(out var pending) &&
                            (int)(injected - pending.Sequence) >= 0)
                        {
                            pendingInputSends.Dequeue();
                            armedSendTicks = pending.Ticks;
                        }
                    }
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
                    $"incomplete={session.IncompleteFrames}, dropped-incomplete={session.DroppedIncompleteFrames}, " +
                    $"skipped-for-reordering={session.SkippedForReordering}, " +
                    $"skipped-for-stale-present={session.SkippedForStalePresent}, " +
                    $"reorder-window-ended={session.ReorderWindowFrames}" +
                    $"{(inputCapture is not null ? $", input-events-captured={inputEventsCaptured}, input-events-sent={inputEventsSent}" : "")}" +
                    $"{(dropInputPercent > 0 ? $", input-events-dropped-simulated={inputEventsDroppedSimulated}" : "")}.");
                if (inputToPresentMs.Count > 0)
                {
                    logger.Info(
                        $"[lan-client] input-to-present avg={inputToPresentMs.Average():0.###}ms " +
                        $"min={inputToPresentMs.Min():0.###}ms max={inputToPresentMs.Max():0.###}ms " +
                        $"(n={inputToPresentMs.Count}, discrete input events only, present queued not scanned out).");
                }
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
}
