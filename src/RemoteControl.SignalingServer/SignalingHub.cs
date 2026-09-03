using RemoteControl.Protocol;

namespace RemoteControl.SignalingServer;

/// <summary>
/// Room-relay logic, partitioned by pairing code: register into a room
/// (creating it if empty), ack the sender, announce PeerJoined to whoever
/// was already there, and relay everything else verbatim to the other
/// member. Mirrors what
/// RemoteControl.Net.Tests.Peering.SignaledPeerConnectorTests.FakeSignalingServer
/// already models and this repo's tests exercise against -- this is that
/// behaviour for real, so <see cref="RemoteControl.Signaling.SignalingClient"/>
/// needs no changes to talk to it. See docs/WIRE-PROTOCOL.md.
/// </summary>
internal sealed class SignalingHub
{
    private const int MaxRoomSize = 2;
    private readonly Dictionary<string, List<Connection>> _rooms = new();
    private readonly object _gate = new();

    public Task HandleAsync(Connection connection, ClientMessage message, CancellationToken cancellationToken) => message switch
    {
        ClientMessage.Register register => RegisterAsync(connection, register, cancellationToken),
        ClientMessage.StunCandidates candidates => RelayAsync(connection, new ServerMessage.StunCandidates(candidates.Candidates), cancellationToken),
        ClientMessage.HolePunchReady => RelayAsync(connection, new ServerMessage.HolePunchReady(), cancellationToken),
        _ => Task.CompletedTask,
    };

    public async Task DisconnectAsync(Connection connection, CancellationToken cancellationToken)
    {
        List<Connection> remaining;
        lock (_gate)
        {
            if (connection.PairingCode.Length == 0 || !_rooms.TryGetValue(connection.PairingCode, out var room))
            {
                remaining = [];
            }
            else
            {
                room.Remove(connection);
                remaining = [.. room];
                if (room.Count == 0) _rooms.Remove(connection.PairingCode);
            }
        }

        foreach (var peer in remaining)
            await peer.SendAsync(new ServerMessage.PeerLeft(), cancellationToken);
    }

    private async Task RegisterAsync(Connection connection, ClientMessage.Register register, CancellationToken cancellationToken)
    {
        List<Connection> existingMembers;
        bool full;
        lock (_gate)
        {
            if (!_rooms.TryGetValue(register.PairingCode, out var room))
                _rooms[register.PairingCode] = room = [];

            full = room.Count >= MaxRoomSize;
            existingMembers = full ? [] : [.. room];
            if (!full)
            {
                room.Add(connection);
                connection.Role = register.Role;
                connection.PairingCode = register.PairingCode;
            }
        }

        if (full)
        {
            await connection.SendAsync(new ServerMessage.Error("register-failed", "Room is full."), cancellationToken);
            return;
        }

        await connection.SendAsync(new ServerMessage.Registered(register.PairingCode, register.Role), cancellationToken);
        foreach (var peer in existingMembers)
            await peer.SendAsync(new ServerMessage.PeerJoined(), cancellationToken);
    }

    private async Task RelayAsync(Connection sender, ServerMessage message, CancellationToken cancellationToken)
    {
        List<Connection> peers;
        lock (_gate)
        {
            peers = _rooms.TryGetValue(sender.PairingCode, out var room)
                ? room.Where(member => member != sender).ToList()
                : [];
        }

        foreach (var peer in peers)
            await peer.SendAsync(message, cancellationToken);
    }
}
