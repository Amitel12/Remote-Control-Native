using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteControl.Common;
using RemoteControl.Net.Peering;
using RemoteControl.Protocol;

namespace RemoteControl.Signaling;

/// <summary>
/// Speaks the signaling WebSocket protocol against packages/signaling-server
/// in the TS repo (unchanged room/relay logic, new payload shapes -- see
/// docs/WIRE-PROTOCOL.md). This is the C# equivalent of the old Electron
/// app's SignalingClient (shared-webrtc/signaling-client.ts): connect,
/// register with a pairing code, and surface incoming ServerMessages to the
/// caller (SignaledPeerConnector, which drives the Phase 2 exchange) via an
/// event rather than owning any NAT-traversal logic itself -- this class
/// only knows how to move JSON messages across the WebSocket. That split is
/// what ISignalingChannel names: the connector is testable against a fake
/// channel precisely because none of the choreography lives here.
/// </summary>
public sealed class SignalingClient : ISignalingChannel, IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    // ClientWebSocket allows exactly one outstanding SendAsync and throws
    // InvalidOperationException on a second -- and this class is the one that knows that,
    // so it serializes rather than making every caller do it. SignaledPeerConnector also
    // avoids overlapping its own sends; this covers anyone who doesn't.
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ILogger _logger;
    private readonly Uri _serverUri;
    private CancellationTokenSource? _receiveLoopCts;
    private Task? _receiveLoopTask;

    public event Action<ServerMessage>? MessageReceived;
    public event Action? Closed;

    public SignalingClient(Uri serverUri, ILogger? logger = null)
    {
        _serverUri = serverUri;
        _logger = logger ?? new ConsoleLogger(nameof(SignalingClient));
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _socket.ConnectAsync(_serverUri, cancellationToken);
        _receiveLoopCts = new CancellationTokenSource();
        _receiveLoopTask = RunReceiveLoopAsync(_receiveLoopCts.Token);
    }

    public Task RegisterAsync(Role role, string pairingCode, CancellationToken cancellationToken = default) =>
        SendAsync(new ClientMessage.Register(role, pairingCode), cancellationToken);

    public async Task SendAsync(ClientMessage message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(message, ProtocolJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var messageStream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    messageStream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;

                var json = Encoding.UTF8.GetString(messageStream.ToArray());
                try
                {
                    var message = JsonSerializer.Deserialize<ServerMessage>(json, ProtocolJson.Options);
                    if (message is not null) MessageReceived?.Invoke(message);
                }
                catch (JsonException ex)
                {
                    // Untrusted network input -- log and keep the connection alive rather than tearing it down over one bad frame.
                    _logger.Warn($"Discarding malformed signaling message: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on DisposeAsync/shutdown.
        }
        catch (WebSocketException ex)
        {
            _logger.Error("Signaling connection failed.", ex);
        }
        finally
        {
            Closed?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _receiveLoopCts?.Cancel();
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch
            {
                // Best-effort close -- the socket is going away regardless.
            }
        }
        if (_receiveLoopTask is not null)
        {
            try { await _receiveLoopTask; } catch { /* already logged in the loop */ }
        }
        _socket.Dispose();
        _receiveLoopCts?.Dispose();
        _sendGate.Dispose();
    }
}
