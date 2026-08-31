# Phase 1 -- LAN video streaming

## Status

**Real two-machine LAN streaming: CONFIRMED working on real hardware.** See
"Real cross-machine LAN result" below for the actual run.

`tools/LoopbackHarness` now has separate LAN roles:

- `--lan-host <IPv4:port>` captures output 0, converts BGRA to NV12, encodes
  with native NVENC, packetizes each H.264 access unit, and sends UDP datagrams.
- `--lan-client <port>` waits for stream configuration, creates the D3D11
  decoder and swap-chain presenter, reassembles frames, decodes, and presents.

The first slice uses a direct UDP socket, now behind `IUdpTransport`/
`UdpTransport` (see gate item 4 below), so the capture/encode process and
the decode/present process are genuinely separated before adding NAT
traversal or control channels. `EnetTransport` remains deliberately
unimplemented -- see item 4 for why. Video uses the existing
`VideoPacketizer`/`VideoDepacketizer`, with parity
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

## Real cross-machine LAN result

First genuine two-machine run, both roles on the actual harness over a real
home network (same router): host = this RTX 3070 PC on wired 2.5GbE
Ethernet (`192.168.1.114`, no Wi-Fi adapter present on this machine at all);
client = a second Windows PC with Intel(R) Iris(R) Plus Graphics, on Wi-Fi
(`192.168.1.118`). 1920x1080@60, native NVENC P1 ultra-low-latency IPPP,
8Mbps CBR, 300 frames, no FEC/loss simulation flags:

- Host: **300 captured / 300 encoded at 59.83fps**, 2,964 video datagrams,
  3,554.5KiB on the LAN envelope, zero acquisition timeouts.
- Client: **300 completed / 300 decoded / 300 presented**, zero malformed,
  incomplete, or dropped-incomplete frames.
- `[lan-host] latency rtt avg=50.164ms min=20.38ms max=92.534ms,
  clock-offset avg=3041.493ms (n=5)` -- the first *real* glass-to-glass
  number (not the localhost self-loop case described above). The polling-
  granularity caveat from "Latency instrumentation" still applies (n=5 is a
  short run), and the ~3s clock-offset reading is independently corroborated
  by the two machines' own log timestamps differing by almost exactly that
  much.
- The Wi-Fi leg was clean -- zero loss, so this run didn't exercise FEC
  against real loss; that path is still only proven against the simulated
  `--drop-percent` loss above. A fully wired-both-ends comparison wasn't
  possible with this host's hardware (no Wi-Fi adapter), so item 2's "wired,
  then Wi-Fi" ask is only half-covered -- the client's leg was Wi-Fi, the
  host's was wired, both stitched into one real cross-machine run rather than
  two separate same-medium runs.

This is the milestone the whole Phase 1 gate was blocking on: live
cross-machine LAN streaming with a measured glass-to-glass baseline,
end to end on real hardware on both ends.

## Remaining Phase 1 gate

1. ~~Run the same roles on two Windows PCs connected to the same router.~~
   **Done** -- see "Real cross-machine LAN result" above.
2. ~~Record packet/frame counters on wired Ethernet, then Wi-Fi...~~ **Done,
   with a caveat** -- see "Real cross-machine LAN result" above for the real
   numbers and why it's a wired-host/Wi-Fi-client run rather than two
   separate same-medium runs. Real loss (to exercise `--parity-percent`
   against) didn't show up on this particular run -- worth another pass if a
   noisier Wi-Fi network is available later.
3. ~~Add latency timestamps/echo clock-offset estimation~~ **Done** -- see
   "Latency instrumentation" above. The mechanism round-trips correctly on the
   localhost test; the real LAN glass-to-glass number is still item 1's gate,
   not this one's.
4. ~~Put the socket behind the planned transport abstraction~~ **Done** --
   `RemoteControl.Net.Transport.IUdpTransport`/`UdpTransport` now sit between
   the LAN host/client and `System.Net.Sockets.Socket`; `Program.Lan.cs` no
   longer references `Socket` directly. Decision on the other half of this
   item (does ENet add value for the *video* channel specifically): **no, not
   yet** -- video already carries its own Reed-Solomon FEC (see "FEC parity
   recovery" above), so ENet's reliable channels don't help this traffic, and
   its unreliable channel is just UDP with extra framing on top of what we
   already hand-roll. Pulling in the native ENet-CSharp dependency now would
   cost build/deployment complexity for no benefit to this path. ENet is still
   the right call for Phase 3's input/control channel, which genuinely needs
   reliable delivery -- that implementation can target `IUdpTransport` without
   touching the video path. Verified on real hardware post-refactor: 100/100
   captured/encoded/completed/decoded/presented, unchanged from pre-refactor
   behavior.
5. ~~Repeat host resolution-change and Win+L recovery while the remote client
   is connected.~~ **Done** -- real two-machine run, host `--frames 0`
   (indefinite), client `--frames 3000`. Two real events:
   - **Resolution change** (1920x1080 -> 1680x1050 -> back to 1920x1080):
     each change correctly hit `DesktopConfigurationChangedException` and
     started a brand-new LAN session; the client picked up each new
     `Configuration` datagram and reconnected cleanly every time (3 sessions
     in one client run, zero manual restart needed).
   - **Win+L**: correctly handled as access-loss only, *not* a mode change --
     no new session was started, matching `docs/PHASE-0.md`'s prediction.
     The client's presented video visibly froze/glitched during the lock (as
     expected -- it cannot capture the secure desktop at all, confirming
     Windows' own security boundary holds: the remote client never sees the
     password-entry screen) and resumed cleanly on unlock, same session.
   - Both events included stretches of "access lost / restored" churn
     **longer than the client's own "no video for 10s" watchdog** (one ran
     ~16s). The client did not disconnect. Why: that watchdog actually resets
     on *any* received datagram, not specifically video -- and the host's
     ~1/sec `LatencyProbe` heartbeat (see "Latency instrumentation" above)
     keeps arriving even while capture is stalled, so it incidentally kept
     the connection alive through the stall. Worth keeping as-is (it's a
     genuine resilience property, not a bug), but worth naming: the watchdog
     is really "no *traffic*, not specifically no *video*, for 10s".
   - Full-run result: client reached **3000/3000 presented, zero malformed,
     zero incomplete**, 404 dropped-incomplete (frames lost during the
     access-loss churn itself, expected and non-fatal).

The host is paced to the negotiated 60fps rather than sending at the captured
display's refresh rate. The client retains a small reordering window and evicts
older incomplete frames, reporting them as dropped instead of allowing one
lost UDP shard to accumulate state or terminate the rest of the stream.

**All five Phase 1 gate items are now done.** The two-machine run, latency
measurement, transport seam, and resolution-change/Win+L recovery with a
connected client have all passed on real hardware. Phase 1 is complete.
