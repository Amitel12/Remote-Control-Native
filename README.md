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
  a real RFC 5769 reference vector), UDP hole-punch coordinator, a TURN
  client and relay transport (RFC 5766) for the networks punching cannot
  cross, AIMD congestion controller, jitter-buffer frame pacer, and
  LAN-session framing. Its tests include exhaustive K-of-N FEC reconstruction and a
  real UDP loopback STUN round trip.
- **`RemoteControl.Signaling`** -- WebSocket client speaking the signaling
  protocol against `src/RemoteControl.SignalingServer` (this repo's own
  pairing/relay server, replacing a dependency on `amitel12/tests`'s
  signaling server), driven by `RemoteControl.Net.Peering.SignaledPeerConnector`
  to exchange candidates and hole-punch automatically. Verified against an
  in-process fake of the server, a real loopback punch, and a real-WebSocket
  smoke test of the server itself; no instance is deployed for real use yet.
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

**`RemoteControl.App` (WPF) is now a real host/client control panel**, not a
placeholder: it starts `src/RemoteControl.SignalingServer` in-process on the
host, generates a pairing code, and drives `SignaledPeerConnector` on both
ends so a LAN link, a hole-punched P2P link, and a TURN relay are all tried
automatically -- the user never picks one. Video and remote input stream
through the session layer lifted into `src/RemoteControl.Session` (see
`docs/PHASE-5.md`), rendered in a separate session window rather than
embedded in the WPF shell. TURN fallback is wired the same way the harness
always used it, but has still only been proven against fakes and a manual
P2P run (see `docs/PHASE-2.md`) -- a real coturn instance is still item 3 on
`docs/ARCHITECTURE.md`'s "Next step" list.

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

On the host PC, click **Start Hosting** and read out the pairing code and
"others connect to" address. On the client, pick **Connect to a PC**, enter
that address and code, and click **Connect**. First run on a machine that
has never hosted before will likely hit Windows' HTTP.sys permission check
(`HttpListenerException`, access denied) -- the app falls back to
localhost-only and logs the one-time fix:
`netsh http add urlacl url=http://+:7777/ user=Everyone` (run elevated,
once per machine). A published self-contained exe:

```
dotnet publish src\RemoteControl.App -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:PublishSingleFile=true
```

For lower-level manual testing (no signaling, explicit LAN/P2P mode
selection, loss/FEC simulation), `tools/LoopbackHarness` still exposes every
flag directly; see `docs/PHASE-0.md` through `docs/PHASE-5.md`.

`src/RemoteControl.SignalingServer` runs cross-platform (`dotnet run
--project src/RemoteControl.SignalingServer`). It binds all interfaces
(`http://+:7777/`) by default, which needs either an elevated process or a
one-time `netsh http add urlacl url=http://+:7777/ user=Everyone`; pass
`--host localhost` for local-only testing (no elevation needed) or
`--port` to change the port.

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
