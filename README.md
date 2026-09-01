# remote-control-native

Native Windows C#/.NET rewrite of the remote-desktop/game-streaming app
prototyped in [`amitel12/tests`](https://github.com/amitel12/tests)
(Electron + WebRTC). This rewrite exists because the Electron/Chromium/
WebRTC pipeline has a latency ceiling that's fine for general remote
desktop use but not for actually playing games through it -- the goal here
is Parsec/Moonlight-tier smoothness: GPU-resident capture, hardware
encode/decode, and a custom low-latency UDP transport instead of a
browser's general-purpose media stack.

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the full plan --
context, researched decisions, phased build order, and the risk register,
kept up to date as phases land -- and
[`docs/WIRE-PROTOCOL.md`](docs/WIRE-PROTOCOL.md) for the two protocols
this app speaks.

## Status

Not feature-complete. Phases 0-4 are implemented and verified on real
hardware (RTX 3070 / Windows 11 host plus a second Windows PC): GPU-resident
capture, native NVENC encode, D3D11 decode and presentation, a custom UDP
transport with Reed-Solomon FEC, UDP hole-punched P2P across genuinely
different networks, remote mouse/keyboard, and loss-driven adaptive bitrate.
What's implemented now:

- **`RemoteControl.Protocol`** -- the wire message types (both the JSON
  signaling messages and the binary UDP hot-path structs).
- **`RemoteControl.Net`** -- Reed-Solomon FEC over GF(256) (the single
  highest-risk piece in the whole rewrite -- see the risk register),
  video packetizer/depacketizer, STUN client (RFC 5389, verified against
  a real RFC 5769 reference vector), UDP hole-punch coordinator, AIMD
  congestion controller, jitter-buffer frame pacer, and LAN-session
  framing. Its tests include exhaustive K-of-N FEC reconstruction and a
  real UDP loopback STUN round trip.
- **`RemoteControl.Signaling`** -- WebSocket client speaking the updated
  signaling protocol against `amitel12/tests`'s (unchanged) signaling
  server, driven by `RemoteControl.Net.Peering.SignaledPeerConnector` to
  exchange candidates and hole-punch automatically. Verified against an
  in-process fake of the server plus a real loopback punch; no server is
  deployed yet, so it has not run against the real one.
- **`RemoteControl.Capture` / `Codec` / `Render`** -- real D3D11 Desktop
  Duplication, native NVIDIA NVENC (including live bitrate reconfigure and
  optional continuous intra-refresh), D3D11-backed H.264 decode, and
  swap-chain presentation, including display-mode and Win+L recovery.
- **`RemoteControl.Input`** -- `SendInput` injection and raw-input capture,
  with the DPI/coordinate, Unicode-typing and stuck-button lessons from the
  Electron app baked in, plus held-state resync and redundant send for
  lossy links.
- **`tools/LoopbackHarness`** -- every mode the above has been proven
  through: Phase 0 loopback, LAN host/client, P2P host/client, and the
  input demos. **`tools/LossyProxy`** -- a real impairing UDP relay (bursty
  loss, reordering, jitter) used to validate FEC and the decode path.

Standing between this and a usable app: a deployed signaling server to run
the now-automated candidate exchange against, and TURN relay fallback (a
real restrictive NAT has been found where direct punching cannot succeed --
see `docs/PHASE-2.md`). `RemoteControl.App` (WPF) is still a placeholder;
nothing above runs through it yet. See `docs/ARCHITECTURE.md`'s "Next step"
for the full ordered list.

## Building

Requires the .NET 8 SDK. `RemoteControl.App` (WPF) can only be built on
Windows -- it needs the Windows Desktop workload, which isn't available
cross-platform even just for compiling. Every other project targets
`net8.0` or `net8.0-windows`; the latter compiles fine on any OS (the
Windows API surface ships as reference assemblies), it just can't *run*
off Windows.

On Windows, this builds all 13 projects and runs the tests:

```
dotnet build RemoteControl.sln
dotnet test RemoteControl.sln
```

Note that a bare `dotnet build` resolves to `RemoteControl.sln` too, since
that's the only solution in the root -- so it is *not* a way to skip the
WPF app. Off Windows, exclude that one project explicitly:

```
find src tools tests -name '*.csproj' ! -name 'RemoteControl.App.csproj' \
  -exec dotnet build {} \;
dotnet test tests/RemoteControl.Net.Tests/RemoteControl.Net.Tests.csproj
dotnet test tests/RemoteControl.Protocol.Tests/RemoteControl.Protocol.Tests.csproj
```

## Running

The Windows projects pin `<Platforms>x64</Platforms>`, so the build output
lands under an `x64` path rather than the default one:

```
.\src\RemoteControl.App\bin\x64\Debug\net8.0-windows\RemoteControl.App.exe
```

`dotnet run --project src\RemoteControl.App` needs the platform passed
explicitly to match, i.e. `-p:Platform=x64`.

The WPF shell is still a placeholder. Run the proven pipeline, LAN and P2P
modes through `tools/LoopbackHarness`; see `docs/PHASE-0.md` through
`docs/PHASE-4.md`.

## Gotchas

**Never write `--` inside an XML comment.** It is illegal in XML, and both
`.xaml` and `app.manifest` have been broken by it once already. The two
failure modes look nothing alike:

- In a `.xaml` file it fails the build loudly, with `error MC3000`.
- In `app.manifest` it does *not* fail the build. `mt.exe` embeds the
  malformed manifest as-is, and the exe then dies at launch with "the
  application has failed to start because its side-by-side configuration
  is incorrect" -- an error that points nowhere near the actual cause.

Since the second one builds clean, the only way to catch it is to launch
the app. Worth doing after touching the manifest.

CI (`.github/workflows/ci.yml`) now validates XML inputs and smoke-tests
that `RemoteControl.App` actually launches, specifically to catch both bugs
above. It only runs on `push`/`pull_request`/manual dispatch, though --
`RemoteControl.App` is still only ever compiled by whoever builds on
Windows locally between CI runs, which is how both bugs above reached
`main` from a machine that could not build it.
