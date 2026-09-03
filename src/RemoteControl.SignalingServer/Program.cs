using System.Net;
using RemoteControl.Common;

namespace RemoteControl.SignalingServer;

/// <summary>
/// CLI entry point over <see cref="SignalingServerHost"/> -- see that class for
/// the actual server. Replaces amitel12/tests's packages/signaling-server so
/// this repo doesn't need that one checked out to run for real.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var logger = new ConsoleLogger("SignalingServer");

        int port;
        string host;
        try
        {
            port = ReadPort(args, defaultValue: 7777);
            host = ReadOption(args, "--host", defaultValue: "+");
        }
        catch (ArgumentException ex)
        {
            logger.Error(ex.Message);
            logger.Error("Usage: [--port N] [--host +|*|<address>]");
            return 2;
        }

        using var server = new SignalingServerHost(host, port, logger);
        try
        {
            server.Start();
        }
        catch (HttpListenerException ex)
        {
            // Binding "+"/"*"/a non-loopback address reserves a URL ACL, which on Windows needs
            // either an elevated process or a one-time
            // `netsh http add urlacl url=http://+:PORT/ user=Everyone` (admin, once). "localhost"
            // needs neither -- use --host localhost for local testing.
            logger.Error($"Failed to bind {host}:{port} (try running elevated, reserving the URL " +
                         "with netsh http add urlacl, or passing --host localhost).", ex);
            return 1;
        }

        logger.Info($"Listening on {server.ListenerDescription}. Ctrl+C to stop.");

        var stopCts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; stopCts.Cancel(); };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            await server.RunAsync(stopCts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        return 0;
    }

    private static int ReadPort(string[] args, int defaultValue)
    {
        var index = Array.IndexOf(args, "--port");
        if (index < 0) return defaultValue;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var port) || port is <= 0 or > 65535)
            throw new ArgumentException("--port requires an integer 1-65535.");
        return port;
    }

    private static string ReadOption(string[] args, string option, string defaultValue)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0) return defaultValue;
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{option} requires a value.");
        return args[index + 1];
    }
}
