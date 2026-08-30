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

    private static void RunLanHost(ILogger logger, IPEndPoint clientEndpoint, int targetFrames)
    {
        while (true)
        {
            try
            {
                RunLanHostSession(logger, clientEndpoint, targetFrames);
                return;
            }
            catch (DesktopConfigurationChangedException ex)
            {
                logger.Warn($"{ex.Message} Starting a new LAN video session for the new mode.");
            }
        }
    }

    private static void RunLanHostSession(ILogger logger, IPEndPoint clientEndpoint, int targetFrames)
    {
        logger.Info($"Phase 1 LAN host: capture -> native NVENC -> UDP {clientEndpoint}.");

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
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = LanReceiveBufferSize,
            SendBufferSize = LanReceiveBufferSize,
        };
        socket.Connect(clientEndpoint);

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

        var packetizer = new VideoPacketizer(parityRatio: 0);
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
        var sendTimes = new List<double>();

        try
        {
            while (!stopRequested &&
                   (targetFrames == 0 || encoded < targetFrames) &&
                   (runLimit is null || run.Elapsed < runLimit.Value))
            {
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
            $"payload={encodedBytesSent / 1024.0:0.0}KiB, wire={wireBytesSent / 1024.0:0.0}KiB, timeouts={acquireTimeouts}.");
        if (sendTimes.Count > 5)
        {
            var steady = sendTimes.Skip(5).ToList();
            logger.Info(
                $"[lan-host] packetize+send avg={steady.Average():0.###}ms " +
                $"min={steady.Min():0.###}ms max={steady.Max():0.###}ms (n={steady.Count}, warmup skipped).");
        }

        if (targetFrames > 0 && encoded < targetFrames)
            throw new InvalidOperationException($"LAN host did not encode {targetFrames} frames within {runLimit!.Value.TotalSeconds:0}s (got {encoded}).");
        logger.Info("PASS -- LAN host completed its real capture/encode/send run.");
    }

    private static void WaitForLanClient(Socket socket, byte[] configuration, ulong sessionId, ILogger logger)
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

            if (!socket.Poll(50_000, SelectMode.SelectRead))
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

        using var mfDevice = MfDevice.Create(logger);
        var displays = DisplayEnumerator.Enumerate(mfDevice.Device);
        if (displays.Count == 0)
            throw new InvalidOperationException("The D3D11 adapter has no attached desktop outputs.");
        var presentationDisplay = displays.Count > 1 ? displays[1] : displays[0];
        using var window = new PresentationWindow(
            presentationDisplay,
            "Remote-Control-Native — Phase 1 LAN client");
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = LanReceiveBufferSize,
        };
        socket.Bind(new IPEndPoint(IPAddress.Any, listenPort));
        logger.Info($"LAN client bound to {socket.LocalEndPoint}; start the host with --lan-host <this-PC-ip>:{listenPort}.");
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

                if (!socket.Poll(10_000, SelectMode.SelectRead))
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
            }
        }
        finally
        {
            if (session is not null)
            {
                logger.Info(
                    $"[lan-client] datagrams={datagramsReceived}, completed={session.CompletedFrames}, " +
                    $"decoded={session.Decoded}, presented={session.Presented}, malformed={malformedDatagrams}, " +
                    $"incomplete={session.IncompleteFrames}.");
                session.Dispose();
            }
        }

        if (session is null)
        {
            logger.Warn("LAN client window was closed before a host connected.");
            return;
        }
        if (targetFrames > 0 && session.Presented < targetFrames)
            throw new InvalidOperationException($"LAN client expected {targetFrames} presented frames but received {session.Presented}.");
        if (!window.IsClosed)
            logger.Info("PASS -- LAN client received, reassembled, decoded, and presented the stream.");
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
