# Phase 4 -- FEC + congestion control + adaptive bitrate

## Status

**Real lossy/reordering network validation done, and it found (and fixed) a
genuine correctness bug the earlier synthetic-loss testing couldn't have
caught. `CongestionController` is implemented and real-hardware verified on
both loopback and a real two-machine network, including multiple live NVENC
bitrate reconfigures mid-stream. Only bandwidth-capping (as opposed to
loss-driven adaptation) remains untested.**

## The lossy/reordering proxy

`tools/LossyProxy` is a real UDP relay (`--listen`/`--forward-to`) that sits
between the LAN host and client and deliberately impairs traffic in both
directions:

- `--loss-percent N [--burst-loss]`: drop N% of packets. Without
  `--burst-loss`, independent per-packet (a coin flip per datagram). With
  it, a Gilbert-Elliott two-state model (mean burst length a few packets in
  the "bad" state) -- real network loss is correlated, not independent, and
  Phase 1's `--drop-percent` flag was explicitly documented as *not*
  modeling that.
- `--reorder-percent N [--reorder-delay-ms MIN-MAX]`: hold N% of packets
  back by an extra random delay before forwarding, so they arrive after
  packets sent moments later without delay -- genuine out-of-order arrival,
  not simulated.
- `--jitter-ms MIN-MAX`: baseline random delay applied to everything.

It learns the host's address from the first packet it sees that isn't from
the configured `--forward-to` (client) address, since the host's local port
is ephemeral; from then on it relays both directions, applying the
configured impairment independently to each hop.

## A real bug found by real reordering

First run through the proxy (20% bursty loss, 10% reordered ±20-80ms, 25%
FEC parity) produced `completed=300, decoded=293, presented=293` -- all 300
frames fully reassembled (FEC recovery worked completely), but 7 silently
never decoded, with **no** malformed/incomplete/dropped-incomplete count to
explain it.

Root cause: `LanClientVideoSession.TryProcessVideoPacket` called
`_decoder.Decode()` the instant `VideoDepacketizer.AddPacket` returned a
completed frame -- in whatever order frames finished reassembling over the
network, with nothing checking that this was actually the *next* frame in
sequence. Independent per-shard loss (Phase 1's `--drop-percent`) rarely
exposes this, because losing shards from one frame doesn't usually let a
*later* frame's shards win the race to complete first. Real reordering does
exactly that: frame N+1's shards can all arrive intact while frame N is
still waiting on FEC-recoverable shards delayed by the proxy, so N+1
completes and gets decoded first. H.264 IPPP decoding depends on decoding
P-frames in temporal order (each references the immediately preceding
reconstructed picture); feeding the decoder a frame out of sequence
corrupted its reference state for the affected frames, and this specific
decoder (a synchronous MFT) simply never produced output for them --
silently, no error, no counter -- rather than throwing or visibly failing.

Fixed with a bounded reorder buffer in `LanClientVideoSession`
(`EnqueueForOrderedDecode`): completed frames are held in a
`SortedDictionary<uint, byte[]>` keyed by frame index and only released to
the decoder in strictly increasing order. If the buffer grows to
`MaxReorderWindowFrames` (8) while still missing the next expected index,
that index is presumed unrecoverably lost and skipped -- the same "prefer
skipping a stuck frame over stalling" philosophy `RemoteControl.Net.Jitter.FramePacer`
already applies to *timing*, now also applied to *order*. Skips are counted
explicitly (`SkippedForReordering`, surfaced as `skipped-for-reordering` in
the client's summary log) instead of silently vanishing.

**Real-hardware result, same proxy settings, before and after the fix**
(RTX 3070, 1920x1080@60, 25% FEC parity, 20% bursty loss, 10% reordered):

| | Before | After |
|---|---|---|
| completed | 300 | 297 |
| decoded | **293** | **297** |
| presented | **293** | **297** |
| dropped-incomplete | 0 | 2 |
| skipped-for-reordering (new counter) | n/a (silent) | 3 |

`completed == decoded == presented` again, exactly, the same invariant the
Phase 1 depacketizer bugfix established for loss -- now holding under real
reordering too. (Exact counts will vary run to run, same caveat as Phase 1's
FEC table -- the loss/reorder decisions are randomized.)

## Adaptive bitrate

`RemoteControl.Net.Congestion.CongestionController` is a classic AIMD
controller (the same shape TCP congestion control uses): back off
multiplicatively the instant either signal looks bad, climb back up
additively only after several consecutive clean samples. Two independent
signals, either enough to trigger a decrease:

- **Client-reported frame loss** -- a new `LanDatagramCodec.QualityReport`
  datagram, sent by the client back to the host roughly once a second,
  carrying its own windowed fraction of frames that either never
  reassembled (`DroppedIncompleteFrames`) or reassembled but got skipped by
  the reorder buffer above (`SkippedForReordering`) -- both are visible
  glitches from the viewer's side, so both count.
- **RTT rising well above its own recent baseline** -- reuses the existing
  latency-probe RTT samples from `docs/PHASE-1.md`; classic queueing/
  bufferbloat congestion shows up here before loss usually does.

Bounded to the encoder's own starting bitrate as the ceiling -- it only ever
backs off and recovers, never pushes past the configured target quality.
Applying a bitrate change to the *running* encoder needed a real NVENC
capability that had never been used here: `NvEncoder.ReconfigureEncoder`
(`Lennox.NvEncSharp`), wrapped as `NvencEncoder.SetBitrate()`, with
`ResetEncoder`/`ForceIDR` both left false so a bitrate change doesn't force
a fresh keyframe or drop reference frames -- just changes what the running
session encodes next.

Opt-in via `--adaptive-bitrate` on `--lan-host`/`--p2p-host` (existing
runs without it are completely unaffected).

**Real-hardware result, loopback** (RTX 3070, 1920x1080@60, 25% FEC parity,
15% bursty loss, `--adaptive-bitrate`, 600 frames): the controller detected
the loss and cut bitrate exactly once, `8Mbps -> 6.8Mbps` (the configured
0.85 decrease factor), via a genuinely live `NvEncReconfigureEncoder` call
mid-stream -- untested before this. Client result: `completed=599,
decoded=599, presented=599`, zero dropped/skipped -- the encoder transition
produced no visible glitch or decode corruption at all, not even at the
exact frame where the bitrate changed.

**Real-hardware result, real two-machine network** (same settings, this
RTX 3070 PC as host, a second PC over real home Wi-Fi as client,
`tools/LossyProxy` relaying between them with 15% bursty loss injected):
the controller reacted to genuinely real, changing conditions, not a single
canned response -- **four live reconfigures** over one run,
`8 -> 6.8 -> 5.78Mbps` (backing off twice as conditions stayed bad) then
`6.07 -> 6.37Mbps` (recovering twice as they improved). Client result:
`completed=597, decoded=597, presented=597` -- invariant holds exactly
again -- `dropped-incomplete=3` (real loss FEC couldn't recover, a strong
recovery ratio against 15% injected loss) and `skipped-for-reordering=3`,
this time catching *genuine* real-network reordering rather than the
proxy's simulated kind (no `--reorder-percent` was even set for this run).
This is the whole Phase 1-4 stack -- LAN handshake, FEC, the reorder fix,
latency probing, and adaptive bitrate -- proven working together over a
real network for the first time.

One real setup mistake worth recording: the proxy's first attempt at this
bound to `--listen 127.0.0.1:<port>` (loopback-only, copied straight from
the earlier loopback test) and every relay to the real client failed with
"a socket operation was attempted to an unreachable network" -- a
loopback-bound socket cannot route packets to a real LAN address at all.
Fixed by binding `0.0.0.0` instead; the host still dials the proxy via
`127.0.0.1` fine (a 0.0.0.0-bound socket accepts traffic addressed to any
local IP), only the *outbound* leg to the real remote peer needed the
wider bind.

## What's not done

- **No constrained-bandwidth test.** The proxy currently only does loss/
  reorder/jitter, not bandwidth capping/shaping -- the "watchable stream
  under... constrained bandwidth" half of the Phase 4 milestone needs that
  too, plus something to measure "watchable" against (the Phase 1 baseline
  numbers). The loss-driven half of adaptive bitrate is proven; the
  bandwidth-driven half isn't tested at all yet.
- **`CongestionController`'s AIMD constants (loss threshold, decrease/
  increase factors, clean-sample count) are untuned** -- chosen for
  plausibility, not measured against real perceptual quality/stutter.
