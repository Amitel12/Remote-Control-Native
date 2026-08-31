# Phase 1 -- LAN video streaming

## Status

**Step 1, two-process localhost stream: CONFIRMED working on real hardware.**

`tools/LoopbackHarness` now has separate LAN roles:

- `--lan-host <IPv4:port>` captures output 0, converts BGRA to NV12, encodes
  with native NVENC, packetizes each H.264 access unit, and sends UDP datagrams.
- `--lan-client <port>` waits for stream configuration, creates the D3D11
  decoder and swap-chain presenter, reassembles frames, decodes, and presents.

The first slice uses a direct UDP socket so the capture/encode process and the
decode/present process are genuinely separated before adding NAT traversal or
control channels. The planned `EnetTransport` abstraction is not implemented
yet. Video uses the existing `VideoPacketizer`/`VideoDepacketizer`, with parity
off by default (`--parity-percent 0`) for the low-loss LAN baseline above --
see "FEC parity recovery" below for turning it on and what it actually buys.

## Running the localhost test

Build once:

```bat
dotnet build tools\LoopbackHarness\LoopbackHarness.csproj -c Release
```

Start the client first in one terminal:

```bat
dotnet run --project tools\LoopbackHarness\LoopbackHarness.csproj -c Release -- --lan-client 47998 --frames 300 --no-verify-frame
```

Then start the host in another terminal:

```bat
dotnet run --project tools\LoopbackHarness\LoopbackHarness.csproj -c Release -- --lan-host 127.0.0.1:47998 --frames 300
```

Omit `--no-verify-frame` on the client to read back one decoded frame and write
`phase1-lan-client-verify-frame.png`. That one-off diagnostic readback is not
part of the steady-state path. Use `--frames 0` to run until the client window
is closed (client) or Ctrl+C is pressed (host).

The host sends configuration repeatedly and does not encode its first IDR
until the client has created its decoder/presenter and returned `Ready`. This
prevents startup packet loss from stranding the decoder until the next IDR.
Both roles wait indefinitely for this manual startup handshake; the client
window is intentionally black until configuration arrives. Close the client
window or press Ctrl+C in the host terminal to cancel before connection.
Each display-mode rebuild receives a new random session ID, allowing the client
to discard stale datagrams and recreate its video session at the new size.

## FEC parity recovery

`VideoPacketizer`/`VideoDepacketizer` already fully implement Reed-Solomon
parity shards and reconstruction (`RemoteControl.Net.Fec.ReedSolomonCodec`,
16 unit tests including exhaustive K-of-N reconstruction) -- that work
predates Phase 1. What Phase 1 hadn't done until now is turn it on anywhere
the real LAN host runs, or verify it actually recovers real loss over the
real runtime socket path rather than just in an isolated unit test.

Two new host-only flags:

- `--parity-percent N` (0-100, default 0): sets `VideoPacketizer`'s
  `parityRatio`. `N`% of a frame's data-shard count is added as recoverable
  parity shards (e.g. 10 data shards + 25% parity = 3 parity shards; any 10
  of the resulting 13 are enough to reconstruct the frame).
- `--drop-percent N` (0-100, default 0): **diagnostic only, not a real
  network's loss.** Before sending each video shard, the host rolls the dice
  and silently skips sending it N% of the time. This is the only way to
  prove FEC recovery works over the real socket path without a second,
  genuinely lossy network -- it exists to exercise the pipeline, not to
  model real Wi-Fi loss statistics (real loss is bursty and correlated,
  this is independent per-shard).

**Real localhost A/B result** (RTX 3070, 1920x1080@60, 300 frames, 15%
simulated per-shard loss both runs):

| | `--parity-percent 0` (control) | `--parity-percent 25` |
|---|---|---|
| completed | 162 | 266 |
| decoded | **0** | **266** |
| presented | **0 / 300** | **266 / 300** |
| dropped-incomplete | 129 | 33 |
| incomplete (still in flight at end) | 4 | 1 |

(Exact completed/dropped/incomplete counts vary run to run -- 15% is a random
per-shard roll, so which specific shards are lost differs each time. The
decisive, stable result across every run tried is `decoded`/`presented`:
always 0 without parity, reliably in the 260s/300 with it.)

Without parity, 15% independent per-shard loss didn't just drop a few
frames -- it wedged the entire stream at 0 decoded/presented. The likely
cause: NVENC's initial IDR (keyframe) is one large frame spread across many
shards, so it has the highest chance of losing at least one shard to
independent per-shard loss, and this harness sends exactly one keyframe at
stream start (no periodic re-keyframing yet) -- lose that one frame
irrecoverably and every subsequent P-frame has nothing to decode against.
With 25% parity, the same loss recovers to 266/300 presented (88.7%).

The first pass at this table had a real bug, not just a confusing number:
`completed` + `dropped-incomplete` summed to more than 300 frames sent, and
`decoded` was consistently lower than `completed`. Cause:
`VideoDepacketizer` tracked eviction only by the newest frame index seen, not
which indices were permanently resolved -- a shard that merely arrived
*late* (reordered behind a burst of newer frames, not actually lost) for an
already-evicted frame silently reopened a brand-new `FrameAssembly` for that
same index. That's not just a bookkeeping quirk: if that reopened frame then
completed, it got decoded and presented *after* newer frames had already
displayed -- a real stale-frame-out-of-order bug, not only a stats mismatch.
Fixed by tracking a `_lastResolvedFrameIndex` watermark and discarding any
shard at or below it for a frame no longer in progress (see
`VideoDepacketizer.cs` and the `LateShardForAlreadyEvictedFrame_IsDiscarded_NotReopened`
regression test). The table above is the corrected, self-consistent result --
`completed` now exactly equals `decoded`, and `completed + dropped-incomplete
+ incomplete` sums to 300 on both runs.

## Real-hardware localhost result

RTX 3070 / Windows 11, 1920x1080, native NVENC P1 ultra-low-latency IPPP,
8Mbps CBR, two separate processes over `127.0.0.1`:

- Host: **300 captured / 300 encoded at 59.99fps**, 3,060 video datagrams,
  3,407.5KiB encoded payload, 3,669.6KiB on the LAN envelope, three normal
  unchanged-desktop acquisition timeouts.
- Host packetize + socket send: **0.181ms average** (0.070ms min, 0.755ms
  max, 5-frame warmup skipped).
- Client: **300 completed / 300 decoded / 300 presented**, zero malformed,
  incomplete, or dropped-incomplete frames.
- A separate 30-frame correctness run wrote a coherent decoded desktop PNG.
- `[lan-host] latency rtt avg=22.707ms min=4.898ms max=59.981ms, clock-offset
  avg=-11.201ms (n=5)` -- see "Latency instrumentation" below for what this
  does and doesn't prove.

This proves the process split, handshake, UDP framing, exact H.264 frame
reassembly, D3D11 decode, and presentation on the real GPU. It does not prove
two-machine LAN behavior or glass-to-glass latency.

## Latency instrumentation

The host sends a `LanDatagramKind.LatencyProbe` (its own `Stopwatch.GetTimestamp()`
and `DateTime.UtcNow.Ticks`) roughly once a second; the client echoes it back
immediately via `LatencyEcho`, appending its own `DateTime.UtcNow.Ticks`. RTT is
computed entirely in the host's own `Stopwatch` clock domain (send time vs. the
same clock when the echo arrives), so it needs no cross-machine clock sync at
all. The wall-clock fields only feed a clock-offset *estimate*
(`clientWall - hostWall - RTT/2`, the standard symmetric-latency approximation),
useful later for correlating host/client log timestamps once this runs across
two real machines with unsynchronized clocks. See
`RemoteControl.Net.Transport.LanDatagramCodec`'s `CreateLatencyProbe`/
`CreateLatencyEcho`/`ReadLatencyProbe`/`ReadLatencyEcho`, and
`RunLanHostSession`/`RunLanClient` in `tools/LoopbackHarness/Program.Lan.cs`.

**This is the measurement mechanism working correctly, not a real latency
number.** The localhost RTT above (avg 22.7ms) is *not* network latency --
this machine has essentially zero network latency to itself. It's dominated by
how often the host loop checks for a pending echo: that check only happens once
per outer loop iteration, which is paced to the 60fps capture/encode cadence
(~16.7ms), so up to roughly one frame period of polling latency is baked into
every RTT sample by construction. That same polling granularity will still
apply on a real two-machine run -- true network RTT will be entangled with it,
not measured in isolation -- which is worth knowing before reading a real
cross-machine number, not a problem introduced by testing over localhost.
Tightening that (e.g. draining echoes on a separate thread/timer instead of
once per captured frame) is a reasonable follow-up once real network numbers
make it worth the precision.

## Remaining Phase 1 gate

1. Run the same roles on two Windows PCs connected to the same router.
2. Record packet/frame counters on wired Ethernet, then Wi-Fi: read the new
   `[lan-host] latency rtt/clock-offset` line for the first real
   cross-machine numbers (see "Latency instrumentation" above for its actual
   precision floor before trusting the number), and try `--parity-percent`
   against whatever real loss shows up there (see "FEC parity recovery"
   above for what it fixed against simulated loss).
3. ~~Add latency timestamps/echo clock-offset estimation~~ **Done** -- see
   "Latency instrumentation" above. The mechanism round-trips correctly on the
   localhost test; the real LAN glass-to-glass number is still item 1's gate,
   not this one's.
4. Put the socket behind the planned transport abstraction (and decide whether
   ENet adds value for the unreliable video-only baseline before adding reliable
   control/input channels).
5. Repeat host resolution-change and Win+L recovery while the remote client is
   connected.

The host is paced to the negotiated 60fps rather than sending at the captured
display's refresh rate. The client retains a small reordering window and evicts
older incomplete frames, reporting them as dropped instead of allowing one
lost UDP shard to accumulate state or terminate the rest of the stream.

Do not claim the Phase 1 milestone until the two-machine run and latency
measurement pass.
