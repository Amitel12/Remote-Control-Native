# Phase 4 -- FEC + congestion control + adaptive bitrate

## Status

**Real lossy/reordering network validation done, and it found (and fixed) a
genuine correctness bug the earlier synthetic-loss testing couldn't have
caught. `CongestionController` (adaptive bitrate under constrained
bandwidth) is not started.**

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

## What's not done

- **`CongestionController` does not exist.** Nothing currently degrades
  encoder bitrate/quality in response to observed loss or rising latency --
  the stream either keeps its configured 8Mbps CBR or the connection just
  gets worse. This is the actual "adaptive bitrate" half of Phase 4's name
  and hasn't been started.
- **No constrained-bandwidth test.** The proxy currently only does loss/
  reorder/jitter, not bandwidth capping/shaping -- the "watchable stream
  under... constrained bandwidth" half of the Phase 4 milestone needs that
  too, plus something to measure "watchable" against (the Phase 1 baseline
  numbers).
- Only tested on loopback so far (both host and client processes on this
  machine, proxy in between) -- not yet combined with a real two-machine
  network the way Phase 1/2 were.
