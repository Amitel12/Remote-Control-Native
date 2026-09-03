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

## Real asymmetric-punch bug found and fixed (different network, same session)

Same session, a third network: the client on a phone hotspot (public IP
`141.226.89.188`, different phone/carrier than the earlier successful
phone-hotspot test in this doc). Local and reflexive ports matched on the
client's side, suggesting a lenient (not obviously symmetric) NAT -- yet the
first attempt still failed in a new way: **the host's own log declared the
punch successful (it received the client's probe in ~5s), while the client's
own log independently timed out after the full 30s waiting for a probe that
never arrived.**

Root cause, found in `HolePunchCoordinator.PunchAsync`
(`src/RemoteControl.Net/Stun/HolePunchCoordinator.cs`): as soon as a side
received *any* probe from the peer, it cancelled its own outbound probe loop
and returned immediately. That's fine when both directions open at the same
rate, but here the client's NAT needed several more seconds of the host's
probes to finish opening its own inbound mapping -- probes the host had
already stopped sending the moment its own side succeeded, starving the
client for the rest of its 30s window. One side "succeeding" is not proof
the path is open in both directions.

**Fix**: the send loop no longer stops when the local receive succeeds; it
keeps sending probes for the full `timeout` budget in the background
(fire-and-forget, so it doesn't delay the caller's own return once its side
is confirmed). Rebuilt and retried immediately after: **punch succeeded in
~9s, and this time video and input actually streamed** -- 2511 frames
encoded/sent over ~100s at ~25fps (cellular-limited, not a target), RTT avg
267.9ms (min 38.8ms, max 2234ms, real cellular jitter), 1196 real input
events received with 606 correctly deduped as redundant copies. This is the
first real confirmation of both Phase 3 reliability fixes
(`InputStateSync`/`ReconcileHeldState` and the redundant-send/
`InputSequenceDedup` pair) surviving actual internet jitter/loss end to end,
not just the simulated `LossyProxy`/`--drop-input-percent` harness paths.

## Automated candidate exchange

Every punch recorded above exchanged candidates by hand -- one person
reading an `ip:port` out of a terminal and typing it into another. That is
also where most of the failures came from: of the six attempts in the
restrictive-NAT test, five failed for pasting reasons (a candidate gone
stale after a restart, a transcription mismatch, `dotnet run`'s build time
eating one side's punch window) and only the sixth was a real NAT result.
The mechanism was never in doubt; the manual step around it was.

`RemoteControl.Net.Peering.SignaledPeerConnector` removes that step. It
registers with the signaling server under a pairing code, advertises its own
candidates, waits for the peer's, and punches -- returning the same kind of
confirmed-reachable `IPEndPoint` the manual path returned, so the streaming
code behind it is untouched. `tools/LoopbackHarness` uses it when both
`--signaling-server ws://host:port` and `--pairing-code CODE` are given:

```
LoopbackHarness --p2p-host 47000   --signaling-server ws://<server>:8080 --pairing-code ABC123 ...
LoopbackHarness --p2p-client 47001 --signaling-server ws://<server>:8080 --pairing-code ABC123 ...
```

Two details that are easy to get wrong, both settled in
`docs/WIRE-PROTOCOL.md`'s "Exchange order":

- **The first peer into the room must re-send its candidates on
  `peer-joined`.** The server relays to whoever is present and never
  replays, so the candidates the first peer sends on registration reach
  nobody. Without the re-send, exactly one side ends up with the other's
  candidates and both sides wait.
- **Host candidates are advertised alongside the server-reflexive one**, so
  two peers on the same LAN can connect without their traffic leaving it.
  Loopback addresses are deliberately excluded: a probe to `127.0.0.1` goes
  to the prober's own machine, which is useless at best and a false "path is
  open" at worst.

**A real bug the first version of these tests could not catch.** Completing
the local-candidates source releases the parked `peer-joined` re-send onto
the thread pool, and the statement immediately after it began the initial
send -- two sends in flight at once on a `ClientWebSocket`, which permits
exactly one and throws `InvalidOperationException` on the second. If the
re-send won the race it was the *initial* send that threw, aborting an
otherwise fine exchange. The original fake channel completed sends
instantly, so no overlap was ever observable in a test. Fixed by serializing
writes in both places that can know about the constraint: the connector no
longer overlaps its own sends, and `SignalingClient` serializes socket
writes for any other caller. The fake now holds each send open for a
measurable interval and fails the test if two are ever in flight -- the same
lesson as the reorder bug in `docs/PHASE-4.md`, that a test whose fake is
faster than reality cannot see a race.

**What this is verified against**: an in-process fake of the server's relay
logic plus real UDP sockets doing a real punch over loopback
(`SignaledPeerConnectorTests`) -- both registration orders, a rejected
registration, a peer that leaves, an unreachable candidate, and a peer whose
candidates are all unusable. That covers the choreography, which is the part
with the failure modes. It does **not** cover the real server: nothing here
has spoken to a deployed `packages/signaling-server`, and until it has, the
JSON on the wire is only as correct as `docs/WIRE-PROTOCOL.md` says it is.

## TURN relay fallback

The restrictive network above is the case this exists for: a clean punch
attempt, byte-matched candidates, ~25s of genuine overlap, and it still timed
out. Until now this app had nothing to offer that network.

`RemoteControl.Net.Turn` implements the client side of RFC 5766, scoped by
the coturn deployment that already exists in `amitel12/tests`
(`deploy/turnserver.conf.example`): the long-term credential mechanism
(`lt-cred-mech`), one static user, plain UDP on 3478, relay ports
49152-49352, no TLS. Short-term credentials and TLS are therefore not
implemented, and neither is ChannelBind -- it saves four bytes per packet in
exchange for a second framing format on the same socket and channel-lifetime
bookkeeping, a bad trade on what is by definition the slow path.

- **`TurnClient`** -- Allocate (including the 401 challenge, where the
  rejection *is* the handshake: the server answers with the REALM and NONCE
  the real request must carry), Refresh, CreatePermission, and the Send/Data
  indications that carry media. A 438 Stale Nonce is absorbed rather than
  surfaced, because coturn rotates nonces on its own schedule and treating
  that as an error would fail allocations at random.
- **`TurnRelayTransport`** -- an `IUdpTransport`, so FEC, the packetizer,
  congestion control, the reorder buffer and the input channel all run over
  the relay unmodified, exactly as they already do over a hole-punched
  socket. Costs 36 bytes per datagram (20-byte STUN header, 12-byte
  XOR-PEER-ADDRESS, 4-byte DATA header).
- **`SignaledPeerConnector`** allocates during candidate gathering -- it has
  to, since the relayed address is only useful if it goes into the one
  candidate exchange -- advertises it as a `relay` candidate, and falls back
  to it when the punch times out. If only the *peer* has a relay, we send
  directly to their relayed address instead; one-sided TURN is enough.
- Harness: `--turn-server host:port --turn-user U --turn-password P`
  alongside `--signaling-server`/`--pairing-code`. The relay needs the
  signaling path, since manual candidate entry cannot carry a second
  candidate.

**The permission trap, worth knowing before debugging this at 2am**: TURN
permissions expire after five minutes and the server reports nothing when one
lapses -- it silently stops relaying that peer. From upstream that looks like
video simply stopping, with no error at any layer. `TurnRelayTransport`
therefore re-sends the refresh and every permission every two minutes, driven
off the same calls that move media rather than a background thread.

**What this is verified against**: a fake TURN server that is a real UDP
socket speaking the real message format -- the 401 challenge, a rotated
nonce, a rejected permission, an echo path that follows a datagram out
through the wrapper and back in through the unwrapper -- plus a byte-exact
message vector generated by a separate Python implementation written from the
RFCs (hashlib and zlib, nothing from this codebase), which is what pins the
MESSAGE-INTEGRITY length fix-ups, the long-term key derivation and the
hand-written CRC-32.

**What it is not verified against: real coturn.** RFC 5769 publishes vectors
for Binding only, so there is no official vector for the parts most likely to
be wrong here, and the cross-implementation check cannot catch both
implementations misreading the same sentence. Running
`docker compose -f deploy/docker-compose.yml up -d` in `amitel12/tests` and
pointing both harness sides at it -- ideally from the restrictive network in
this document -- is what would actually close Phase 2.

## What's still manual / open

- **The candidate exchange is now automated in code, but has never run
  against a real server.** `RemoteControl.Net.Peering.SignaledPeerConnector`
  drives the whole `register` -> `peer-joined` -> `stun-candidates` ->
  `hole-punch-ready` flow and hands back a punched endpoint; the harness
  uses it when given `--signaling-server`/`--pairing-code` and falls back to
  the manual prompt otherwise. See "Automated candidate exchange" above for
  what is and isn't proven. **No signaling server is deployed**, so the only
  evidence it works is a test against an in-process fake of the server's
  relay behaviour -- deploying `packages/signaling-server` from
  `amitel12/tests` and running the two harness sides against it is the
  remaining step.
- **TURN relay fallback is implemented but has never spoken to real
  coturn.** See "TURN relay fallback" above for what exists and what that
  leaves unproven. The restrictive network in this document is still
  unconnectable until someone runs it against the deployed relay.
- **`CandidateKind.Host`/`Relay` are now both wired** -- host candidates are
  advertised so two peers on one LAN never leave it, and a relay candidate is
  advertised whenever TURN is configured. What is not wired is IPv6: every
  candidate path here is IPv4-only, matching the rest of the transport.
