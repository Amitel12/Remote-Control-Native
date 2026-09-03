using System.Net;
using System.Net.Sockets;
using RemoteControl.Common;
using RemoteControl.Net.Transport;
using RemoteControl.Session;

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

    /// <summary>
    /// Sets up Ctrl+C to cancel <paramref name="cancellationToken"/>'s source, for the CLI-only
    /// entry points below -- RemoteControl.Session itself has no console dependency, since it also
    /// runs headless inside a future GUI.
    /// </summary>
    private static CancellationTokenSource CreateCancelKeySource()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };
        return cts;
    }

    private static void RunLanHost(ILogger logger, IPEndPoint clientEndpoint, int targetFrames, int parityPercent, int dropPercent, bool adaptiveBitrate, bool adaptiveFec, bool intraRefresh, bool remoteInput)
    {
        using IUdpTransport socket = new UdpTransport(LanReceiveBufferSize, LanReceiveBufferSize);
        socket.Connect(clientEndpoint);
        using var cts = CreateCancelKeySource();
        var options = new SessionOptions
        {
            TargetFrames = targetFrames,
            ParityPercent = parityPercent,
            DropPercent = dropPercent,
            AdaptiveBitrate = adaptiveBitrate,
            AdaptiveFec = adaptiveFec,
            IntraRefresh = intraRefresh,
            RemoteInput = remoteInput,
        };
        HostSession.Run(logger, socket, clientEndpoint.ToString(), options, onStats: null, cts.Token);
    }

    private static void RunLanClient(ILogger logger, int listenPort, int targetFrames, bool verifyFrame, bool remoteInput, int dropInputPercent)
    {
        logger.Info($"Phase 1 LAN client: listening for UDP video on 0.0.0.0:{listenPort}.");
        using IUdpTransport socket = new UdpTransport(LanReceiveBufferSize, sendBufferSize: 0);
        socket.Bind(new IPEndPoint(IPAddress.Any, listenPort));
        logger.Info($"LAN client bound to {socket.LocalEndPoint}; start the host with --lan-host <this-PC-ip>:{listenPort}.");
        using var cts = CreateCancelKeySource();
        var options = new SessionOptions
        {
            TargetFrames = targetFrames,
            VerifyFrame = verifyFrame,
            RemoteInput = remoteInput,
            DropInputPercent = dropInputPercent,
        };
        ClientSession.Run(logger, socket, options, onStats: null, cts.Token);
    }
}
