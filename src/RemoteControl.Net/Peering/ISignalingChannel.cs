using RemoteControl.Protocol;

namespace RemoteControl.Net.Peering;

/// <summary>
/// The signaling WebSocket, reduced to the four operations
/// <see cref="SignaledPeerConnector"/> actually needs. Exists for the same
/// reason <c>IUdpTransport</c> does on the video path (docs/PHASE-1.md gate
/// item 4): the interesting, easy-to-get-wrong part is the *choreography*
/// -- who sends candidates when, and what happens when they arrive in the
/// wrong order -- and that is worth testing without a WebSocket, a server,
/// or a network in the way.
///
/// <c>RemoteControl.Signaling.SignalingClient</c> is the real implementation.
/// It lives in a separate project (it owns the actual
/// <c>ClientWebSocket</c>), which is why this interface is declared here, in
/// the project that consumes it, rather than next to it.
/// </summary>
public interface ISignalingChannel : IAsyncDisposable
{
    /// <summary>Raised for every message the server sends, on the channel's own receive loop.</summary>
    event Action<ServerMessage>? MessageReceived;

    /// <summary>Raised once when the connection ends, for any reason.</summary>
    event Action? Closed;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task SendAsync(ClientMessage message, CancellationToken cancellationToken = default);
}
