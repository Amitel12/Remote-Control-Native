using System.Net;
using System.Net.Sockets;
using RemoteControl.Common;

namespace RemoteControl.Net.Turn;

/// <summary>A live TURN allocation: the relayed address peers should send to, plus how long the server will keep it.</summary>
public sealed record TurnAllocation(IPEndPoint RelayedEndpoint, IPEndPoint? MappedEndpoint, uint LifetimeSeconds);

/// <summary>
/// A TURN client (RFC 5766) covering exactly the relay-of-last-resort path
/// docs/PHASE-2.md calls for: allocate a relayed address, permit the peer to
/// reach it, and carry media through it. This is what makes the restrictive
/// residential network in that document -- where a clean simultaneous-open
/// punch still timed out -- connectable at all.
///
/// Takes the same already-bound <see cref="Socket"/> as
/// <see cref="Stun.StunClient"/> and <see cref="Stun.HolePunchCoordinator"/>,
/// for the same reason: the allocation belongs to a specific local
/// (address, port), so discovery, punching, relaying and the media that
/// follows all have to be the one socket.
///
/// Deliberately not implemented: ChannelBind. It saves 4 bytes of overhead
/// per packet over Send/Data indications, at the cost of a second framing
/// format on the same socket and channel-lifetime bookkeeping. On a path
/// that is already the slow fallback, that trade is not worth the failure
/// modes -- revisit only if relayed streaming proves bandwidth-bound.
/// </summary>
public sealed class TurnClient
{
    private const uint RequestedLifetimeSeconds = 600;

    private readonly Socket _socket;
    private readonly IPEndPoint _server;
    private readonly string _username;
    private readonly string _password;
    private readonly ILogger _logger;

    private string? _realm;
    private string? _nonce;
    private byte[]? _key;

    public TurnClient(Socket socket, IPEndPoint server, string username, string password, ILogger? logger = null)
    {
        _socket = socket;
        _server = server;
        _username = username;
        _password = password;
        _logger = logger ?? new ConsoleLogger(nameof(TurnClient));
    }

    public IPEndPoint ServerEndpoint => _server;

    /// <summary>
    /// Allocates a relayed transport address. The first Allocate is deliberately sent
    /// unauthenticated: RFC 5766 has the server answer 401 with the REALM and NONCE the real
    /// request must carry, so the rejection *is* the handshake rather than an error to avoid.
    /// </summary>
    public async Task<TurnAllocation> AllocateAsync(CancellationToken cancellationToken = default)
    {
        var unauthenticated = await ExchangeAsync(
            TurnMethod.Allocate,
            [TurnMessage.BuildRequestedTransportUdp(), TurnMessage.BuildLifetime(RequestedLifetimeSeconds)],
            authenticate: false,
            cancellationToken);

        if (unauthenticated is null)
            throw new TimeoutException($"TURN server {_server} did not answer the initial Allocate request.");

        if (unauthenticated.Class == StunClass.SuccessResponse && unauthenticated.RelayedEndpoint is not null)
        {
            // An open relay -- not what coturn with lt-cred-mech does, but harmless to accept.
            _logger.Warn($"TURN server {_server} allocated without asking for credentials.");
            return ToAllocation(unauthenticated);
        }

        if (unauthenticated.ErrorCode != 401 || unauthenticated.Realm is null || unauthenticated.Nonce is null)
        {
            throw new InvalidOperationException(
                $"TURN allocation failed: expected a 401 challenge carrying REALM and NONCE, got " +
                $"{DescribeFailure(unauthenticated)}.");
        }

        _realm = unauthenticated.Realm;
        _nonce = unauthenticated.Nonce;
        _key = TurnMessage.LongTermKey(_username, _realm, _password);
        _logger.Info($"TURN server {_server} challenged with realm '{_realm}' -- authenticating as '{_username}'.");

        var authenticated = await ExchangeAsync(
            TurnMethod.Allocate,
            [TurnMessage.BuildRequestedTransportUdp(), TurnMessage.BuildLifetime(RequestedLifetimeSeconds)],
            authenticate: true,
            cancellationToken);

        if (authenticated is null)
            throw new TimeoutException($"TURN server {_server} did not answer the authenticated Allocate request.");
        if (authenticated.Class != StunClass.SuccessResponse || authenticated.RelayedEndpoint is null)
            throw new InvalidOperationException($"TURN allocation was rejected: {DescribeFailure(authenticated)}.");

        var allocation = ToAllocation(authenticated);
        _logger.Info($"TURN allocation granted: relayed address {allocation.RelayedEndpoint}, lifetime {allocation.LifetimeSeconds}s.");
        return allocation;
    }

    /// <summary>
    /// Extends the allocation. Call well inside the lifetime the allocation reported -- when it
    /// expires the relayed address is gone and any stream through it stops dead.
    /// Passing zero releases the allocation instead.
    /// </summary>
    public async Task<uint> RefreshAsync(uint lifetimeSeconds = RequestedLifetimeSeconds, CancellationToken cancellationToken = default)
    {
        var response = await ExchangeAsync(
            TurnMethod.Refresh, [TurnMessage.BuildLifetime(lifetimeSeconds)], authenticate: true, cancellationToken);

        if (response is null)
            throw new TimeoutException($"TURN server {_server} did not answer a Refresh request.");
        if (response.Class != StunClass.SuccessResponse)
            throw new InvalidOperationException($"TURN refresh was rejected: {DescribeFailure(response)}.");

        return response.LifetimeSeconds ?? lifetimeSeconds;
    }

    /// <summary>
    /// Authorizes one peer address to reach the allocation. Without this the server silently
    /// drops that peer's packets -- permissions are per peer IP and expire after five minutes,
    /// so this is re-sent alongside refreshes rather than once at setup.
    /// </summary>
    public async Task CreatePermissionAsync(IPEndPoint peer, CancellationToken cancellationToken = default)
    {
        var response = await ExchangeAsync(
            TurnMethod.CreatePermission,
            [TurnMessage.BuildXorAddress(TurnMessage.XorPeerAddressAttribute, peer)],
            authenticate: true,
            cancellationToken);

        if (response is null)
            throw new TimeoutException($"TURN server {_server} did not answer a CreatePermission request for {peer}.");
        if (response.Class != StunClass.SuccessResponse)
            throw new InvalidOperationException($"TURN permission for {peer} was rejected: {DescribeFailure(response)}.");
    }

    /// <summary>
    /// Wraps an outbound datagram in a Send indication addressed to <paramref name="peer"/>.
    /// Indications carry no transaction and get no reply -- exactly right for media, which
    /// wants no retransmission anyway.
    /// </summary>
    public byte[] BuildSendIndication(IPEndPoint peer, ReadOnlySpan<byte> payload)
    {
        var transactionId = TurnMessage.NewTransactionId();
        return TurnMessage.Build(
            TurnMethod.Send,
            StunClass.Indication,
            transactionId,
            [
                TurnMessage.BuildXorAddress(TurnMessage.XorPeerAddressAttribute, peer),
                TurnMessage.BuildAttribute(TurnMessage.DataAttribute, payload),
            ]);
    }

    /// <summary>
    /// Builds an authenticated request without sending or awaiting it. Needed once media is
    /// flowing: the socket then belongs to the transport's own receive loop, so the
    /// send-and-wait-for-my-transaction shape used during setup would steal video datagrams
    /// out from under it. Callers hand the response back through
    /// <see cref="TryHandleResponse"/> instead.
    /// </summary>
    public byte[] BuildAuthenticatedRequest(TurnMethod method, IReadOnlyList<byte[]> attributes)
    {
        if (_key is null || _realm is null || _nonce is null)
            throw new InvalidOperationException("Authenticated TURN requests need a completed Allocate first.");

        var all = new List<byte[]>(attributes)
        {
            TurnMessage.BuildStringAttribute(TurnMessage.UsernameAttribute, _username),
            TurnMessage.BuildStringAttribute(TurnMessage.RealmAttribute, _realm),
            TurnMessage.BuildStringAttribute(TurnMessage.NonceAttribute, _nonce),
        };

        return TurnMessage.Build(method, StunClass.Request, TurnMessage.NewTransactionId(), all, _key);
    }

    /// <summary>
    /// Consumes a datagram that is a response to one of this client's own requests, returning
    /// true if it was one (and is therefore not media). A rotated nonce is absorbed here so the
    /// next periodic request carries it; anything else that failed is logged rather than thrown,
    /// because by this point a throw would tear down a live stream over a refresh that will be
    /// retried in seconds anyway.
    /// </summary>
    public bool TryHandleResponse(ReadOnlySpan<byte> datagram)
    {
        if (!TurnMessage.TryParse(datagram, out var message)) return false;
        if (message.Class is not (StunClass.SuccessResponse or StunClass.ErrorResponse)) return false;
        if (message.Method is not (TurnMethod.Refresh or TurnMethod.CreatePermission or TurnMethod.Allocate)) return false;

        if (message.ErrorCode == 438 && message.Nonce is not null)
        {
            _nonce = message.Nonce;
            if (message.Realm is not null) _realm = message.Realm;
            _key = TurnMessage.LongTermKey(_username, _realm!, _password);
            _logger.Info("TURN server rotated its nonce mid-session -- the next refresh will carry the new one.");
        }
        else if (message.Class == StunClass.ErrorResponse)
        {
            _logger.Warn($"TURN {message.Method} was rejected mid-session: {DescribeFailure(message)}.");
        }

        return true;
    }

    /// <summary>
    /// Unwraps an inbound Data indication. Returns false for anything else on the socket, which
    /// on the relay path means anything that is not peer media -- the caller passes those on
    /// untouched rather than guessing.
    /// </summary>
    public static bool TryReadDataIndication(ReadOnlySpan<byte> datagram, out IPEndPoint? peer, out byte[]? payload)
    {
        peer = null;
        payload = null;
        if (!TurnMessage.TryParse(datagram, out var message)) return false;
        if (message.Method != TurnMethod.Data || message.Class != StunClass.Indication) return false;
        if (message.Data is null) return false;

        peer = message.PeerEndpoint;
        payload = message.Data;
        return true;
    }

    private static TurnAllocation ToAllocation(TurnParsedMessage message) =>
        new(message.RelayedEndpoint!, message.MappedEndpoint, message.LifetimeSeconds ?? RequestedLifetimeSeconds);

    private static string DescribeFailure(TurnParsedMessage message) =>
        message.ErrorCode is { } code ? $"error {code}" : $"a {message.Class} with no ERROR-CODE";

    /// <summary>
    /// One request/response exchange, retried because this is UDP and either direction can
    /// simply be dropped. A 438 (Stale Nonce) is not a failure: the server rotates nonces on its
    /// own schedule and expects the request replayed with the new one, so that is done here
    /// rather than surfacing to every caller.
    /// </summary>
    private async Task<TurnParsedMessage?> ExchangeAsync(
        TurnMethod method, IReadOnlyList<byte[]> attributes, bool authenticate, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var response = await SendOnceAsync(method, attributes, authenticate, cancellationToken);
            if (response is null) continue; // timed out -- retransmit.

            if (response.ErrorCode == 438 && response.Nonce is not null && authenticate)
            {
                _nonce = response.Nonce;
                if (response.Realm is not null) _realm = response.Realm;
                _key = TurnMessage.LongTermKey(_username, _realm!, _password);
                _logger.Info("TURN server rotated its nonce -- replaying the request with the new one.");
                continue;
            }

            return response;
        }

        return null;
    }

    private async Task<TurnParsedMessage?> SendOnceAsync(
        TurnMethod method, IReadOnlyList<byte[]> attributes, bool authenticate, CancellationToken cancellationToken)
    {
        var transactionId = TurnMessage.NewTransactionId();
        var all = new List<byte[]>(attributes);
        byte[]? key = null;

        if (authenticate)
        {
            if (_key is null || _realm is null || _nonce is null)
                throw new InvalidOperationException("Authenticated TURN requests need a completed Allocate first.");

            // Order matters: USERNAME/REALM/NONCE have to precede MESSAGE-INTEGRITY, which
            // covers everything before it.
            all.Add(TurnMessage.BuildStringAttribute(TurnMessage.UsernameAttribute, _username));
            all.Add(TurnMessage.BuildStringAttribute(TurnMessage.RealmAttribute, _realm));
            all.Add(TurnMessage.BuildStringAttribute(TurnMessage.NonceAttribute, _nonce));
            key = _key;
        }

        var request = TurnMessage.Build(method, StunClass.Request, transactionId, all, key);
        await _socket.SendToAsync(request, SocketFlags.None, _server, cancellationToken);

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(TimeSpan.FromSeconds(2));

        var buffer = new byte[2048];
        try
        {
            while (true)
            {
                var received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, _server, attemptCts.Token);
                if (!TurnMessage.TryParse(buffer.AsSpan(0, received.ReceivedBytes), out var message)) continue;
                if (message.Method != method) continue;
                if (!message.TransactionId.AsSpan().SequenceEqual(transactionId)) continue; // stale or someone else's.
                return message;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"TURN {method} request to {_server} timed out; retransmitting.");
            return null;
        }
        catch (SocketException ex)
        {
            // ICMP port-unreachable surfaces here on Windows UDP sockets; not fatal on its own.
            _logger.Warn($"TURN {method} request to {_server} failed: {ex.Message}");
            return null;
        }
    }
}
