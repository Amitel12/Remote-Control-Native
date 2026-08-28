using System.Net;
using System.Net.Sockets;
using RemoteControl.Common;

namespace RemoteControl.Net.Stun;

/// <summary>
/// Sends a Binding Request to a STUN server over an existing UDP socket and
/// waits for the matching response, with simple retry-with-timeout (STUN
/// runs over UDP, so a request or response can just be lost). Deliberately
/// takes an already-bound <see cref="Socket"/> rather than owning one --
/// the HolePunchCoordinator (Phase 2) needs to reuse the exact same local
/// socket/port for both the STUN discovery step and the later UDP
/// hole-punch/media traffic, since NATs bind translations per local
/// (address, port), not globally.
/// </summary>
public sealed class StunClient
{
    private readonly Socket _socket;
    private readonly ILogger _logger;

    public StunClient(Socket socket, ILogger? logger = null)
    {
        _socket = socket;
        _logger = logger ?? new ConsoleLogger(nameof(StunClient));
    }

    /// <summary>
    /// Discovers this socket's server-reflexive (public) endpoint as seen
    /// by <paramref name="stunServer"/>. Retries up to <paramref name="attempts"/>
    /// times with <paramref name="perAttemptTimeout"/> each (UDP request or
    /// response can simply be dropped); returns null if the server never
    /// answers within the budget.
    /// </summary>
    public async Task<IPEndPoint?> DiscoverReflexiveEndpointAsync(
        IPEndPoint stunServer,
        int attempts = 3,
        TimeSpan? perAttemptTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var timeout = perAttemptTimeout ?? TimeSpan.FromSeconds(1);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (request, transactionId) = StunMessage.BuildBindingRequest();

            try
            {
                await _socket.SendToAsync(request, SocketFlags.None, stunServer, cancellationToken);

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(timeout);

                var buffer = new byte[512];
                while (true)
                {
                    var receiveResult = await _socket.ReceiveFromAsync(
                        buffer, SocketFlags.None, stunServer, attemptCts.Token);

                    var endpoint = StunMessage.TryParseBindingResponse(
                        buffer.AsSpan(0, receiveResult.ReceivedBytes), transactionId);
                    if (endpoint is not null) return endpoint;
                    // Not our response (stray/duplicate/mismatched transaction) -- keep listening within this attempt's timeout.
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.Warn($"STUN attempt {attempt}/{attempts} to {stunServer} timed out after {timeout}.");
            }
            catch (SocketException ex)
            {
                _logger.Warn($"STUN attempt {attempt}/{attempts} to {stunServer} failed: {ex.Message}");
            }
        }

        return null;
    }
}
