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

Not feature-complete. Phase 0's GPU pipeline is proven on the RTX 3070 / Windows
11 test machine, and Phase 1 has begun with a proven two-process localhost UDP
stream. What's implemented now:

- **`RemoteControl.Protocol`** -- the wire message types (both the JSON
  signaling messages and the binary UDP hot-path structs). 19 tests.
- **`RemoteControl.Net`** -- Reed-Solomon FEC over GF(256) (the single
  highest-risk piece in the whole rewrite -- see the risk register),
  video packetizer/depacketizer, STUN client (RFC 5389, verified against
  a real RFC 5769 reference vector), jitter-buffer frame pacer, and
  LAN-session framing. Its 48 tests include exhaustive K-of-N FEC
  reconstruction and a real UDP loopback STUN round trip.
- **`RemoteControl.Signaling`** -- WebSocket client speaking the updated
  signaling protocol against `amitel12/tests`'s (unchanged) signaling
  server.
- **`RemoteControl.Capture` / `Codec` / `Render`** -- real D3D11 Desktop
  Duplication, native NVIDIA NVENC, D3D11-backed H.264 decode, and swap-chain
  presentation, including display-mode and Win+L recovery.
- **`tools/LoopbackHarness`** -- Phase 0 loopback plus Phase 1 LAN host/client
  modes. A 300-frame localhost run passed 300/300 end to end with no incomplete
  frames; see [`docs/PHASE-1.md`](docs/PHASE-1.md).

`RemoteControl.Input` and the WPF application remain scaffolds. The real
two-machine LAN run and latency baseline are the next gate.

## Building

Requires the .NET 8 SDK. `RemoteControl.App` (WPF) can only be built on
Windows -- it needs the Windows Desktop workload, which isn't available
cross-platform even just for compiling. Every other project targets
`net8.0` or `net8.0-windows`; the latter compiles fine on any OS (the
Windows API surface ships as reference assemblies), it just can't *run*
off Windows.

On Windows, this builds all 12 projects and runs the tests:

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

The WPF shell is still a placeholder. Run the proven pipeline and LAN modes
through `tools/LoopbackHarness`; see `docs/PHASE-0.md` and `docs/PHASE-1.md`.

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
