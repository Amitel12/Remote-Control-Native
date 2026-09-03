using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteControl.Protocol;

namespace RemoteControl.SignalingServer;

/// <summary>
/// One socket in a room. Owns its own send serialization -- a WebSocket
/// allows exactly one outstanding SendAsync and throws on a second, same
/// constraint <see cref="RemoteControl.Signaling.SignalingClient"/> has on
/// the client side.
/// </summary>
internal sealed class Connection(WebSocket socket)
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public Role Role { get; set; }
    public string PairingCode { get; set; } = "";

    public async Task SendAsync(ServerMessage message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, ProtocolJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Null return means the socket sent a close frame (or a frame that failed to parse as JSON at all).</summary>
    public async Task<ClientMessage?> ReceiveOneAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        using var messageStream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            messageStream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        var json = Encoding.UTF8.GetString(messageStream.ToArray());
        return JsonSerializer.Deserialize<ClientMessage>(json, ProtocolJson.Options);
    }

    public async Task CloseAsync()
    {
        if (socket.State == WebSocketState.Open)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { /* best-effort; the socket is going away regardless */ }
        }
    }
}
