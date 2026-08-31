using System.Net;
using System.Net.Sockets;
using RemoteControl.Common;

namespace RemoteControl.Tools.LossyProxy;

/// <summary>
/// A real UDP relay that sits between the LAN host and client and
/// deliberately impairs traffic in both directions -- see
/// docs/ARCHITECTURE.md Phase 4. Learns the host's address from the first
/// packet it sees that isn't from the configured client address (the host's
/// local port is ephemeral, unlike the client's, which is given on the
/// command line), then relays everything both ways, applying loss/
/// reordering/jitter to each hop independently.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var logger = new ConsoleLogger("LossyProxy");
        IPEndPoint listenOn, forwardTo;
        int lossPercent, reorderPercent;
        bool burstLoss;
        (int Min, int Max) reorderDelayMs, jitterMs;

        try
        {
            listenOn = ParseEndpoint(RequireOption(args, "--listen"));
            forwardTo = ParseEndpoint(RequireOption(args, "--forward-to"));
            lossPercent = ReadPercent(args, "--loss-percent", 0);
            burstLoss = args.Contains("--burst-loss");
            reorderPercent = ReadPercent(args, "--reorder-percent", 0);
            reorderDelayMs = ReadRange(args, "--reorder-delay-ms", (20, 80));
            jitterMs = ReadRange(args, "--jitter-ms", (0, 0));
        }
        catch (ArgumentException ex)
        {
            logger.Error(ex.Message);
            logger.Error("Usage: --listen ip:port --forward-to ip:port [--loss-percent N] [--burst-loss] " +
                         "[--reorder-percent N] [--reorder-delay-ms MIN-MAX] [--jitter-ms MIN-MAX]");
            return 2;
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(listenOn);
        logger.Info($"Relaying {listenOn} <-> {forwardTo}" +
                    $"{(lossPercent > 0 ? $", {lossPercent}% loss ({(burstLoss ? "bursty/correlated" : "independent per-packet")})" : "")}" +
                    $"{(reorderPercent > 0 ? $", {reorderPercent}% reordered (+{reorderDelayMs.Min}-{reorderDelayMs.Max}ms)" : "")}" +
                    $"{(jitterMs.Max > 0 ? $", {jitterMs.Min}-{jitterMs.Max}ms jitter on everything" : "")}.");
        logger.Info("Ctrl+C to stop and print a summary.");

        var impairment = new PacketImpairment(lossPercent, burstLoss, reorderPercent, reorderDelayMs, jitterMs);
        IPEndPoint? learnedHostEndpoint = null;
        var stopRequested = false;
        var stats = new RelayStats();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; stopRequested = true; };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var buffer = new byte[ushort.MaxValue + 1];
            while (!stopRequested)
            {
                if (!socket.Poll(200_000, SelectMode.SelectRead))
                    continue;

                EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                var received = socket.ReceiveFrom(buffer, ref source);
                var sender = (IPEndPoint)source;
                var payload = buffer[..received];

                IPEndPoint destination;
                if (sender.Equals(forwardTo))
                {
                    if (learnedHostEndpoint is null)
                    {
                        stats.DroppedBeforeHostKnown++;
                        continue; // a client reply before we've ever seen the host -- nothing to relay it to yet.
                    }
                    destination = learnedHostEndpoint;
                }
                else
                {
                    learnedHostEndpoint = sender;
                    destination = forwardTo;
                }

                stats.Seen++;
                var decision = impairment.Decide();
                if (decision.Drop)
                {
                    stats.Dropped++;
                    continue;
                }
                if (decision.DelayMs > 0)
                    stats.Delayed++;

                _ = RelayOneAsync(socket, payload, destination, decision.DelayMs, stats, logger);
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        logger.Info($"Stopped. seen={stats.Seen}, forwarded={stats.Forwarded}, dropped={stats.Dropped}, " +
                    $"delayed(reordered)={stats.Delayed}, dropped-before-host-known={stats.DroppedBeforeHostKnown}, " +
                    $"send-errors={stats.SendErrors}.");
        return 0;
    }

    private static async Task RelayOneAsync(Socket socket, byte[] payload, IPEndPoint destination, int delayMs, RelayStats stats, ILogger logger)
    {
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs);
            await socket.SendToAsync(payload, SocketFlags.None, destination);
            Interlocked.Increment(ref stats.Forwarded);
        }
        catch (SocketException ex)
        {
            Interlocked.Increment(ref stats.SendErrors);
            logger.Warn($"Relay send to {destination} failed: {ex.Message}");
        }
    }

    private static string RequireOption(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0 || index + 1 >= args.Length)
            throw new ArgumentException($"{option} is required.");
        return args[index + 1];
    }

    private static IPEndPoint ParseEndpoint(string value)
    {
        if (!IPEndPoint.TryParse(value, out var endpoint))
            throw new ArgumentException($"Expected ip:port, got '{value}'.");
        return endpoint;
    }

    private static int ReadPercent(string[] args, string option, int defaultValue)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0) return defaultValue;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value) || value is < 0 or > 100)
            throw new ArgumentException($"{option} requires an integer from 0 through 100.");
        return value;
    }

    private static (int Min, int Max) ReadRange(string[] args, string option, (int Min, int Max) defaultValue)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0) return defaultValue;
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{option} requires MIN-MAX (e.g. 20-80).");
        var parts = args[index + 1].Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var min) || !int.TryParse(parts[1], out var max) || min < 0 || max < min)
            throw new ArgumentException($"{option} requires MIN-MAX with 0 <= MIN <= MAX, got '{args[index + 1]}'.");
        return (min, max);
    }

    private sealed class RelayStats
    {
        public int Seen;
        public int Forwarded;
        public int Dropped;
        public int Delayed;
        public int DroppedBeforeHostKnown;
        public int SendErrors;
    }
}
