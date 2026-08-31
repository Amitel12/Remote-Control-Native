# Phase 2 -- NAT traversal

## Status

**Core milestone CONFIRMED on real hardware: two machines on genuinely
different networks connected directly (no relay) and streamed real video.**
A second, separate real test on a different network hit a genuinely
restrictive NAT where hole-punching failed outright (see "Real
restrictive-NAT failure" below) -- direct P2P is confirmed to work but is
**not sufficient on its own**; TURN relay fallback is a real requirement,
not a nice-to-have, for this feature to work on every network.

`StunClient` (Phase 0/1) and `RemoteControl.Net.Stun.HolePunchCoordinator`
(new) implement simultaneous-open UDP hole punching, the same approach
Parsec's own BUD protocol uses. No signaling server is deployed yet to
exchange candidates through (see "What's still manual" below), so
`tools/LoopbackHarness`'s `--p2p-host`/`--p2p-client` modes stand in for it
with a manual copy/paste exchange. Once the path is punched, the harness
hands the exact same socket into the unmodified Phase 1 host/client session
code -- video streaming does not know or care whether its transport arrived
via a known LAN IP or a hole-punched NAT mapping.

## Running the P2P test

Build once (`dotnet build tools\LoopbackHarness\LoopbackHarness.csproj -c
Release`), then on each machine:

```bat
dotnet run --project tools\LoopbackHarness\LoopbackHarness.csproj -c Release -- --p2p-client <local-port>
dotnet run --project tools\LoopbackHarness\LoopbackHarness.csproj -c Release -- --p2p-host <local-port> --frames 300
```

Use a *fixed* `<local-port>` on both sides, not `0` (ephemeral) -- an
ephemeral port picks a new value every process restart, and if a punch
attempt times out and you rerun, your candidate silently changes underneath
whatever you already told the other side. This cost real time during the
first live test (see below). `--stun-server host:port` overrides the
default (`stun.l.google.com:19302`); any RFC 5389-compliant STUN server
works, including coturn's.

Each side prints its own server-reflexive candidate (`ip:port`) and then
either waits for the peer's candidate on stdin, or -- for driving one side
from a non-interactive tool/script -- takes it upfront via
`--remote-candidate ip:port`. Exchange the two candidates out of band (chat,
voice call, whatever) and paste them in. This manual step is exactly what a
deployed signaling server (`stun-candidates` message, see
`docs/WIRE-PROTOCOL.md`) is meant to automate -- it doesn't exist yet
(`amitel12/tests`' signaling server wasn't running for this test), so this
stands in for it.

## Real cross-network result

Host: this RTX 3070 PC, wired Ethernet, home network (public IP
`176.229.223.217`). Client: a second Windows PC (Intel Iris Plus Graphics),
tethered to a phone's mobile data connection (public IP `46.19.85.29`) --
genuinely different networks, different NATs, no shared router.

- STUN discovery succeeded on both sides against `stun.l.google.com:19302`.
- **Hole punch succeeded in 68ms** (12:28:12.365 punch start ->
  12:28:12.434 probe received) -- effectively instant, and notably *not* the
  symmetric-NAT failure case flagged as a real risk going in (mobile
  carriers commonly run CGNAT with per-destination port allocation, which
  defeats this style of punching entirely; that risk didn't materialize
  here, but should still be assumed possible on other carriers/networks).
- Video streamed end to end through the punched socket with **zero code
  changes to the streaming path**: host 300/300 captured/encoded; client
  **295/300 completed/decoded/presented, zero malformed, zero incomplete**,
  5 dropped-incomplete (attributable to the host's own desktop-idle capture
  timeouts, the same artifact seen in Phase 1 testing, not the network path).
  The client also wrote a real decoded verification frame PNG, confirming
  actual pixel content crossed the connection, not just packet counts.
- Real cellular RTT: **avg 194.3ms, min 61.0ms, max 532.5ms** (n=18) -- much
  higher and far more variable than the ~50ms measured over home Wi-Fi in
  Phase 1, as expected for a cellular uplink. The clock-offset reading
  (avg 3089ms) closely matched the ~3041-3110ms readings from the earlier
  Phase 1 Wi-Fi runs on the *same* physical client machine -- good
  independent confirmation that the estimate tracks real clock skew between
  the two machines, not network conditions.

The first three punch attempts failed before this succeeded -- not a code
bug, a process one: this session's own `--p2p-host 0` (ephemeral port) had
already regenerated a new local port (and thus a new server-reflexive
candidate) on every retry, so the peer kept punching toward a candidate that
had gone stale, and one manual transcription of a candidate had a typo
(`.233.` instead of `.229.`). Fixed by pinning a stable `--p2p-host` port and
reading candidates back character-by-character before running. Both are real
lessons about manual candidate exchange specifically, not about the punch
mechanism itself -- a real signaling server exchanging exact JSON payloads
would not have either failure mode.

## Real restrictive-NAT failure (confirms the risk flagged above)

Host: same RTX 3070 PC, home network (public IP `176.229.223.217`, stable
across the whole session). Client: a Windows laptop on a residential network
at a relative's house (public IP `5.29.18.5`), unrelated to and untested
before this session.

- STUN discovery succeeded on both sides every time -- the client's
  server-reflexive **port varied between process restarts** (`36097` ->
  `36099` -> `36107` -> `36099` across five restarts) even though its local
  ephemeral port also changed each time, consistent with a NAT that
  allocates a fresh external port per new local socket rather than reusing
  one.
- Six punch attempts total. The first five failed for mundane process
  reasons matching the exact lessons already written up above -- stale
  candidates pasted after a restart, a transcription mismatch between what
  was said and what was typed into the actual `dotnet run` command, and (twice)
  one side's 30s punch window fully elapsing before the other side had even
  finished building/starting, because `dotnet run`'s build+startup time ate
  most or all of the window. None of these are new findings; they're the
  same "manual exchange is fragile" class already documented, just worse
  here because `dotnet run` (not a prebuilt binary) adds ~30-60s of variable
  startup latency on the slower machine.
- **The sixth attempt was clean**: both sides had byte-for-byte matching
  candidates (confirmed by pasting exact terminal output rather than
  recalling it), and the client began punching only 5 seconds into the
  host's 30s window, giving ~25s of real overlap. **It still timed out.**
- This is a genuine restrictive-NAT (or firewall) result, not a process
  error: simultaneous-open punching depends on each side's NAT accepting an
  inbound packet from a peer it has itself just sent an outbound packet to,
  even though that specific peer never received a prior packet on its own.
  Some NATs/firewalls (commonly ones with per-session or per-destination
  port allocation, or strict stateful firewalls) refuse this regardless of
  timing. The varying external port per restart above is consistent with,
  though not conclusive proof of, that kind of allocation.
- Not IPv6-related: both server-reflexive candidates involved (`5.29.18.5`,
  `176.229.223.217`) are plain IPv4; STUN never returned an IPv6 candidate
  on either side.
- **This is exactly the gap already flagged**: "TURN relay fallback is not
  implemented" below, and the "mobile hotspot" restrictive-network risk in
  `docs/ARCHITECTURE.md`'s Phase 2 milestone. The phone-hotspot test above
  got lucky (non-symmetric NAT); this network is the real case TURN exists
  for. Direct P2P alone cannot be assumed sufficient for Phase 2 -- a relay
  fallback is required for production use, not just a nice-to-have.

## What's still manual / open

- **No deployed signaling server yet** to exchange `stun-candidates`
  automatically -- `SignalingClient` (Phase 0) is implemented and tested
  against the WebSocket protocol shape, but nothing in this repo drives it
  end to end with `HolePunchCoordinator` yet. That wiring, plus the
  `register` -> `peer-joined` -> `stun-candidates` -> `hole-punch-ready`
  flow from `docs/WIRE-PROTOCOL.md`, is the next real piece of Phase 2 work.
- **TURN relay fallback is not implemented.** The original test's NAT
  happened not to be symmetric; the restrictive-NAT test above (see "Real
  restrictive-NAT failure") confirms this is not a hypothetical risk -- a
  real network was hit where direct hole-punching genuinely cannot succeed,
  and the coturn relay path is the only fix. Nothing here exercises it yet.
- **`CandidateKind.Host`/`Relay` are unused by `HolePunchCoordinator`** --
  only the STUN-discovered server-reflexive candidate was tried. Host
  candidates (useful when both peers happen to share a LAN) and relay
  candidates (the TURN fallback) both need real wiring.
