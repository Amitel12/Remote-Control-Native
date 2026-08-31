using System.Net;
using System.Net.Sockets;
using RemoteControl.Common;

namespace RemoteControl.Net.Stun;

/// <summary>
/// Simultaneous-open UDP hole punch (the Parsec-BUD-style approach --
/// see docs/ARCHITECTURE.md). Sends small probe datagrams to every
/// candidate remote endpoint on a loop while listening for the peer's own
/// probes; the NAT mapping opened by our outbound probe is usually enough
/// for the peer's inbound probe to reach us (and vice versa) for cone
/// NATs. Deliberately takes the same <see cref="Socket"/> <see cref="StunClient"/>
/// used for discovery -- NATs bind translations per local (address, port),
/// so reusing it (rather than opening a fresh socket) is what makes the
/// STUN-discovered candidate actually reachable.
/// </summary>
public sealed class HolePunchCoordinator
{
    private static ReadOnlySpan<byte> ProbeMagic => "RCNPUNCH"u8;

    private readonly Socket _socket;
    private readonly ILogger _logger;

    public HolePunchCoordinator(Socket socket, ILogger? logger = null)
    {
        _socket = socket;
        _logger = logger ?? new ConsoleLogger(nameof(HolePunchCoordinator));
    }

    /// <summary>
    /// Sends probes to every candidate every <paramref name="probeInterval"/>
    /// while listening for a matching probe from any of them. Returns the
    /// specific remote endpoint a probe was received from (the one that
    /// worked -- may differ in address/port from the intended candidate if a
    /// NAT rewrote it), or null if nothing arrived within <paramref name="timeout"/>.
    /// </summary>
    public async Task<IPEndPoint?> PunchAsync(
        IReadOnlyList<IPEndPoint> candidates,
        TimeSpan timeout,
        TimeSpan? probeInterval = null,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
            throw new ArgumentException("At least one candidate endpoint is required.", nameof(candidates));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var receiveTask = ReceiveProbeAsync(cts.Token);
        var sendTask = SendProbesAsync(candidates, probeInterval ?? TimeSpan.FromMilliseconds(200), cts.Token);

        try
        {
            return await receiveTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"Hole punch to {string.Join(", ", candidates)} timed out after {timeout}.");
            return null;
        }
        finally
        {
            cts.Cancel(); // stop the send loop now that we're done either way.
            try { await sendTask; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    private async Task SendProbesAsync(IReadOnlyList<IPEndPoint> candidates, TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    await _socket.SendToAsync(ProbeMagic.ToArray(), SocketFlags.None, candidate, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (SocketException ex)
                {
                    _logger.Warn($"Punch probe to {candidate} failed: {ex.Message}");
                }
            }

            await Task.Delay(interval, cancellationToken);
        }
    }

    private async Task<IPEndPoint> ReceiveProbeAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ProbeMagic.Length];
        EndPoint anySource = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, anySource, cancellationToken);
            }
            catch (SocketException)
            {
                // A prior probe to an unreachable/closed candidate can surface here as
                // WSAECONNRESET on Windows UDP sockets (ICMP port-unreachable, not a
                // real peer response) -- not fatal, keep listening within the budget.
                continue;
            }

            if (result.ReceivedBytes == ProbeMagic.Length && buffer.AsSpan().SequenceEqual(ProbeMagic))
            {
                var remote = (IPEndPoint)result.RemoteEndPoint;
                _logger.Info($"Hole-punch probe received from {remote} -- path is open.");
                return remote;
            }
        }
    }
}
