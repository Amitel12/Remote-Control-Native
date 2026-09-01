using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using RemoteControl.Net.Turn;

namespace RemoteControl.Net.Tests.Turn;

/// <summary>
/// A real UDP socket speaking the real TURN message format, standing in for coturn: it
/// challenges an unauthenticated Allocate with a 401 the way lt-cred-mech does, hands out a
/// relayed address once credentials arrive, and (for the transport tests) echoes Send
/// indications straight back as Data indications so a datagram can be followed all the way
/// out through the wrapper and back in through the unwrapper.
///
/// Shared by TurnClientTests and TurnRelayTransportTests rather than duplicated, so both are
/// provably talking to the same idea of a server.
/// </summary>
internal sealed class FakeTurnServer : IDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _cts = new();
    private int _authenticatedAllocates;

    private const string RealmValue = "remote-control.local";

    public FakeTurnServer() => _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

    public IPEndPoint Endpoint => (IPEndPoint)_socket.LocalEndPoint!;
    public required IPEndPoint RelayedEndpoint { get; init; }

    /// <summary>Answer the first authenticated Allocate with 438 Stale Nonce and a fresh nonce.</summary>
    public bool StaleNonceOnce { get; init; }

    /// <summary>Reject every authenticated Allocate with this error code instead of allocating.</summary>
    public int? RejectAuthenticatedWith { get; init; }

    /// <summary>Answer CreatePermission with this error code instead of success.</summary>
    public int? PermissionError { get; init; }

    /// <summary>Echo Send indications back as Data indications, so a relayed datagram can be followed out and back.</summary>
    public bool EchoRelayedTraffic { get; init; }

    public int AllocateRequests { get; private set; }
    public int RefreshRequests { get; private set; }
    public int PermissionRequests { get; private set; }
    public int SendIndications { get; private set; }
    public bool FirstAllocateWasAuthenticated { get; private set; }
    public bool LastAllocateWasAuthenticated { get; private set; }
    public string? LastNonceSeen { get; private set; }

    public void Start() => _ = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var buffer = new byte[2048];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var received = await _socket.ReceiveFromAsync(
                    buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _cts.Token);
                var datagram = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
                if (!TurnMessage.TryParse(datagram, out var request)) continue;

                var reply = BuildReply(datagram, request);
                if (reply is not null)
                    await _socket.SendToAsync(reply, SocketFlags.None, received.RemoteEndPoint, _cts.Token);
            }
        }
        catch (OperationCanceledException) { /* disposed */ }
        catch (ObjectDisposedException) { /* disposed */ }
    }

    private byte[]? BuildReply(byte[] datagram, TurnParsedMessage request)
    {
        var authenticated = HasAttribute(datagram, TurnMessage.UsernameAttribute)
                            && HasAttribute(datagram, TurnMessage.MessageIntegrityAttribute);

        switch (request.Method)
        {
            case TurnMethod.Allocate:
                AllocateRequests++;
                if (AllocateRequests == 1) FirstAllocateWasAuthenticated = authenticated;
                LastAllocateWasAuthenticated = authenticated;
                if (request.Nonce is not null) LastNonceSeen = request.Nonce;

                if (!authenticated)
                    return Challenge(request, 401, "nonce-one");

                if (StaleNonceOnce && ++_authenticatedAllocates == 1)
                    return Challenge(request, 438, "nonce-two");

                if (RejectAuthenticatedWith is { } rejection)
                    return Challenge(request, rejection, "nonce-one");

                return TurnMessage.Build(
                    TurnMethod.Allocate, StunClass.SuccessResponse, request.TransactionId,
                    [
                        TurnMessage.BuildXorAddress(TurnMessage.XorRelayedAddressAttribute, RelayedEndpoint),
                        TurnMessage.BuildLifetime(600),
                    ]);

            case TurnMethod.CreatePermission:
                PermissionRequests++;
                return PermissionError is { } permissionError
                    ? Challenge(request, permissionError, "nonce-one")
                    : TurnMessage.Build(TurnMethod.CreatePermission, StunClass.SuccessResponse, request.TransactionId, []);

            case TurnMethod.Refresh:
                RefreshRequests++;
                return TurnMessage.Build(
                    TurnMethod.Refresh, StunClass.SuccessResponse, request.TransactionId, [TurnMessage.BuildLifetime(600)]);

            case TurnMethod.Send when request.Class == StunClass.Indication:
                SendIndications++;
                if (!EchoRelayedTraffic || request.PeerEndpoint is null || request.Data is null) return null;
                // What a peer sending back through the relay looks like from this side.
                return TurnMessage.Build(
                    TurnMethod.Data, StunClass.Indication, TurnMessage.NewTransactionId(),
                    [
                        TurnMessage.BuildXorAddress(TurnMessage.XorPeerAddressAttribute, request.PeerEndpoint),
                        TurnMessage.BuildAttribute(TurnMessage.DataAttribute, request.Data),
                    ]);

            default:
                return null;
        }
    }

    private static byte[] Challenge(TurnParsedMessage request, int errorCode, string nonce)
    {
        var value = new byte[] { 0, 0, (byte)(errorCode / 100), (byte)(errorCode % 100) };
        return TurnMessage.Build(
            request.Method, StunClass.ErrorResponse, request.TransactionId,
            [
                TurnMessage.BuildAttribute(TurnMessage.ErrorCodeAttribute, value),
                TurnMessage.BuildStringAttribute(TurnMessage.RealmAttribute, RealmValue),
                TurnMessage.BuildStringAttribute(TurnMessage.NonceAttribute, nonce),
            ]);
    }

    /// <summary>Walks the attribute list looking for one type -- the client's own parser doesn't surface USERNAME or MESSAGE-INTEGRITY, and this needs to see that they were actually sent.</summary>
    private static bool HasAttribute(byte[] message, ushort wanted)
    {
        var attributesLength = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2, 2));
        var offset = 20;
        var end = Math.Min(message.Length, 20 + attributesLength);
        while (offset + 4 <= end)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset, 2));
            var length = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 2, 2));
            if (type == wanted) return true;
            offset += 4 + ((length + 3) & ~3);
        }
        return false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket.Dispose();
        _cts.Dispose();
    }
}
