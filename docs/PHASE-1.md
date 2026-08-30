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
yet. Video uses the existing `VideoPacketizer`/`VideoDepacketizer` with parity
disabled for the initial low-loss LAN baseline.

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

This proves the process split, handshake, UDP framing, exact H.264 frame
reassembly, D3D11 decode, and presentation on the real GPU. It does not prove
two-machine LAN behavior or glass-to-glass latency.

## Remaining Phase 1 gate

1. Run the same roles on two Windows PCs connected to the same router.
2. Record packet/frame counters on wired Ethernet, then Wi-Fi.
3. Add latency timestamps/echo clock-offset estimation and measure the LAN
   glass-to-glass baseline.
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
