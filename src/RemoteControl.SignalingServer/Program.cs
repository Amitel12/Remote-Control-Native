using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteControl.Common;

namespace RemoteControl.SignalingServer;

/// <summary>
/// The pairing/room-relay server for the signaling protocol in
/// docs/WIRE-PROTOCOL.md -- see SignalingHub for the relay logic itself.
/// Replaces amitel12/tests's packages/signaling-server so this repo doesn't
/// need that one checked out to run for real.
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

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{host}:{port}/");
        try
        {
            listener.Start();
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

        var hub = new SignalingHub();
        logger.Info($"Listening on ws://{host}:{port}/. Ctrl+C to stop.");

        var stopCts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; stopCts.Cancel(); };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            while (!stopCts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(stopCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                _ = HandleClientAsync(context, hub, logger, stopCts.Token);
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            listener.Stop();
        }

        return 0;
    }

    private static async Task HandleClientAsync(HttpListenerContext context, SignalingHub hub, ILogger logger, CancellationToken cancellationToken)
    {
        HttpListenerWebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(null);
        }
        catch (Exception ex)
        {
            logger.Warn($"WebSocket handshake failed: {ex.Message}");
            context.Response.StatusCode = 500;
            context.Response.Close();
            return;
        }

        var connection = new Connection(wsContext.WebSocket);
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                RemoteControl.Protocol.ClientMessage? message;
                try
                {
                    message = await connection.ReceiveOneAsync(buffer, cancellationToken);
                }
                catch (WebSocketException)
                {
                    break;
                }
                catch (JsonException ex)
                {
                    logger.Warn($"Discarding malformed signaling message: {ex.Message}");
                    continue;
                }

                if (message is null) break;
                await hub.HandleAsync(connection, message, cancellationToken);
            }
        }
        finally
        {
            await hub.DisconnectAsync(connection, CancellationToken.None);
            await connection.CloseAsync();
        }
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
