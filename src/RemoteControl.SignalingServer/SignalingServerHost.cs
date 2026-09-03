using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteControl.Common;

namespace RemoteControl.SignalingServer;

/// <summary>
/// The pairing/room-relay server, hostable in-process (a GUI can new one up
/// directly) instead of only through <c>dotnet run</c>. See <see cref="SignalingHub"/>
/// for the relay logic and docs/WIRE-PROTOCOL.md for the wire shapes.
/// </summary>
public sealed class SignalingServerHost : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger _logger;
    private readonly HttpListener _listener = new();
    private readonly SignalingHub _hub = new();

    public string ListenerDescription => $"ws://{_host}:{_port}/";

    public SignalingServerHost(string host, int port, ILogger? logger = null)
    {
        _host = host;
        _port = port;
        _logger = logger ?? new ConsoleLogger("SignalingServer");
    }

    /// <summary>
    /// Binds the HttpListener. Throws <see cref="HttpListenerException"/> if the prefix can't be
    /// reserved (binding "+"/"*"/a non-loopback address needs either an elevated process or a
    /// one-time `netsh http add urlacl`) -- callers decide how to surface that themselves (the CLI
    /// wrapper logs and exits, a GUI would show the netsh hint).
    /// </summary>
    public void Start()
    {
        _listener.Prefixes.Add($"http://{_host}:{_port}/");
        _listener.Start();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
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

                _ = HandleClientAsync(context, cancellationToken);
            }
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleClientAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        HttpListenerWebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(null);
        }
        catch (Exception ex)
        {
            _logger.Warn($"WebSocket handshake failed: {ex.Message}");
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
                    _logger.Warn($"Discarding malformed signaling message: {ex.Message}");
                    continue;
                }

                if (message is null) break;
                await _hub.HandleAsync(connection, message, cancellationToken);
            }
        }
        finally
        {
            await _hub.DisconnectAsync(connection, CancellationToken.None);
            await connection.CloseAsync();
        }
    }

    public void Dispose()
    {
        // Stop()/Close() throw ObjectDisposedException if Start() never succeeded -- HttpListener
        // tears itself down internally when AddPrefixCore fails (e.g. the access-denied case
        // Start()'s doc comment describes), so a caller that disposes after a failed Start() (as a
        // `using`/`try-finally` naturally does) must not treat that as a new error.
        if (!_listener.IsListening)
            return;
        _listener.Stop();
        ((IDisposable)_listener).Dispose();
    }
}
