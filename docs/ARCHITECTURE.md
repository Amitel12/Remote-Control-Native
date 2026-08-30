# Architecture

This is the full plan behind this rewrite: why it exists, what was decided
and researched, the phased build order, and the risk register. It was
written during the planning/scaffolding session that created this repo and
is kept here (rather than only in that session's chat history) so a fresh
session -- or anyone else picking this up -- has the complete picture
without depending on that conversation. Update it as phases land; where a
section describes something now done, it says so rather than leaving the
plan looking unstarted.

## Context

The Electron+WebRTC app this rewrite replaces
([`amitel12/tests`](https://github.com/amitel12/tests)) works and is
feature-complete for general remote-desktop use, but its whole pipeline --
Chromium's software/GPU compositing, WebRTC's general-purpose media stack,
Node-native-module input injection -- puts a ceiling on latency and
smoothness that's fine for "check email on my PC" but not for actually
playing games through it. The goal here is that ceiling gone: a genuinely
low-latency, Parsec/Moonlight-tier experience. That requires leaving
Electron and WebRTC behind entirely and going closer to the metal: native
GPU-resident capture, hardware encode/decode, and a custom UDP transport
tuned for real-time video instead of general reliability.

This is a from-scratch rewrite, not an incremental port -- decided and
researched in the planning session:

- **Language/framework: C#/.NET** (WPF), chosen over Rust/C++ for much
  faster solid development with full access to the same native Windows
  APIs (Desktop Duplication, Media Foundation, DirectX) via
  actively-maintained bindings, and no more Node-ABI native-module rebuild
  pain (the exact class of problem `nut-js`/`uiohook-napi` caused in the
  Electron app).
- **Transport: a fully custom UDP protocol** (not WebRTC) -- the "real
  Parsec" approach: own packet framing, forward error correction, and a
  congestion controller tuned for real-time video rather than throughput,
  in the spirit of Moonlight/Sunshine's and Parsec's own protocols.
- **Repo: a new, separate GitHub repository** -- this one. Cleaner
  per-language separation (dotnet SDK vs Node tooling never mix),
  independent history. `amitel12/tests`'s `packages/signaling-server` and
  `deploy/` (coturn) stay right where they are and keep running -- only
  their JSON message *payloads* gained new variants (see "Signaling
  protocol changes" below).
- Platform scope: Windows host + Windows client only, same as the
  Electron app.
- Same v1 feature baseline as the Electron app (screen mirror,
  mouse/keyboard control, then system+mic audio, multi-monitor swap,
  input arbitration, host overlay, disconnect handling) -- now built for
  real gaming-grade latency instead of Chromium's ceiling.

Researched and settled during planning:

- **Capture**: `Vortice.Windows` (NuGet: `Vortice.Direct3D11` +
  `Vortice.DXGI`) -- the actively-maintained SharpDX successor -- for DXGI
  Desktop Duplication API (`IDXGIOutputDuplication.AcquireNextFrame` ->
  `ID3D11Texture2D`, GPU-resident, no CPU copy).
- **Encode/decode**: `Vortice.MediaFoundation` remains the vendor-agnostic
  decoder path and the intended AMD/Intel encoder path, but the NVIDIA
  encoder MFT proved unusable on the RTX 3070. NVIDIA encoding therefore
  uses the implemented native `NvencEncoder` (`Lennox.NvEncSharp`), which
  accepts the same D3D11 NV12 texture directly. The Microsoft H.264 decoder
  MFT supports D3D11-aware output straight into textures presented via a
  swap chain. See `docs/PHASE-0.md` for the real-hardware and Nsight findings.
- **Transport**: hand-rolled STUN client + simultaneous-open UDP
  hole-punching (~200-400 lines, the same approach Parsec's own BUD
  protocol uses, ~97% P2P success without TURN) over the existing
  signaling WebSocket; coturn stays reusable unchanged as a
  relay-of-last-resort (TURN is transport-agnostic at the
  relay-allocation layer). `ENet-CSharp` (the same library
  Moonlight/Sunshine use for their control channel) for reliable/
  unreliable UDP channels. No library ships FEC -- Reed-Solomon erasure
  coding (RS(N,K) over GF(2^8)) is hand-built on top for video, following
  Moonlight's `VideoDepacketizer.c`/`reedsolomon/rs.c` design. Jitter
  buffering stays near-zero (~1 frame) and adaptive, not deep -- both
  Moonlight and Sunshine found deep smoothing buffers make stutter
  *worse*.
- **No existing C#/.NET Parsec/Moonlight-equivalent project exists** to
  build on -- this is assembled from primitives, same as Sunshine's C++
  team had to do.

### Lessons from the Electron app, baked in from day one

Real bugs hit and fixed in the Electron app -- this rewrite's
input/capture layer accounts for these from the start rather than
rediscovering them:

1. **DPI scaling**: Electron's `screen.getAllDisplays()` returned bounds
   in DIPs, not physical pixels, while injection needed physical pixels --
   caused cursor undershoot, worst at edges. This app must pick one
   coordinate space (physical pixels, matching DXGI/D3D11's native space)
   and use it consistently end-to-end, from display enumeration through
   to `SendInput`.
2. **Keyboard layout independence**: typed characters must use
   `SendInput`+`KEYEVENTF_UNICODE` unconditionally -- never VK/scan-code
   translation for plain character input (that's layout-dependent and was
   found to silently fail for English text on an English-layout host, the
   opposite of the intent of the code that did it that way). Real
   shortcuts (Ctrl+C, Alt+Tab, arrows) still need real
   VK+scancode+modifier-hold semantics. `RemoteControl.Protocol.KeyKind`
   already encodes this split (`Character` vs `Named`) at the wire-format
   level so the host-side injector can't accidentally blur the two.
3. **Mouse capture during drag**: a pointer leaving the input-capture
   surface mid-drag (fast overshoot, alt-tab) must never lose the
   eventual button-release signal, or the host ends up with a stuck
   virtual button. Needs an equivalent to Pointer Capture plus a
   blur/disconnect force-release safety net.
4. **Swap-chain lifecycle on minimize/occlusion**: the Electron-specific
   GPU-teardown-during-live-decode hang generalizes to "handle
   `DXGI_STATUS_OCCLUDED` on `Present` and `ResizeBuffers` on
   restore/resize correctly" for this app's D3D11 presentation code.
5. **Non-resizable window `SetSize`**: if the host overlay is ported with
   the same collapse/expand UX as the Electron app's, watch for the same
   resizable-toggle-only-worked-once bug class in WPF.

## Repo & module structure

```
remote-control-native/
  RemoteControl.sln
  src/
    RemoteControl.App/       # WPF shell: host/client mode UI, tray, overlay, settings
    RemoteControl.Capture/   # Vortice.DXGI desktop duplication (host)
    RemoteControl.Codec/     # Vortice.MediaFoundation encode (host) + decode (client)
    RemoteControl.Render/    # D3D11 swap-chain presentation (client)
    RemoteControl.Net/       # STUN client, hole-punch, ENet wrapper, FEC, packetizer, congestion control
    RemoteControl.Input/     # SendInput injection, raw input capture, DPI/coordinate mapping
    RemoteControl.Signaling/ # WebSocket client, pairing flow
    RemoteControl.Protocol/  # wire message types: JSON signaling shapes + binary UDP hot-path structs
    RemoteControl.Common/    # logging, config
  tests/
    RemoteControl.Net.Tests/       # STUN parsing, FEC round-trip, packetizer, jitter buffer (headless, CI-friendly)
    RemoteControl.Protocol.Tests/
  tools/
    LoopbackHarness/          # Phase 0: capture->encode->decode->render, no networking
docs/
  WIRE-PROTOCOL.md            # the two protocols this app speaks, kept in sync with amitel12/tests
  ARCHITECTURE.md             # this file
```

`Capture`/`Codec`/`Render` are kept separate from `Net` so Phase 0 (no
networking) and later phases can be developed/tested independently, and
so `Net`/`Protocol` unit tests don't need a GPU -- this paid off: those two
projects, plus `Signaling` and `Common`, build and run their full test
suite on plain Linux, no Windows or GPU required. `Capture`/`Codec`/
`Render`/`Input` also *compile* cross-platform (the `net8.0-windows` TFM
ships as reference assemblies), just can't run off Windows. Only
`RemoteControl.App` (WPF) needs an actual Windows machine even to build,
since the Windows Desktop workload isn't available cross-platform at all.

### Signaling protocol changes (done, additive rather than a replacement)

The original plan for this section said to *replace* the SDP/ICE
`ClientMessage`/`ServerMessage` payload variants in `amitel12/tests` with
the new STUN-candidate/hole-punch shapes. That turned out to conflict with
another constraint from the same plan -- `packages/desktop-app` (the
Electron app) stays in that repo untouched and still deployable -- since
it still constructs and matches exactly those SDP/ICE variants. What
actually shipped is additive instead:

- `packages/shared/src/signaling-protocol.ts` -- added `stun-candidates`/
  `hole-punch-ready`/`CandidateInit` **alongside** the existing
  `sdp-offer`/`sdp-answer`/`ice-candidate` shapes, not replacing them.
  `InputEvent`/`ControlMessage`/`HostDisplayInfo` untouched -- this app's
  input/control traffic rides the custom ENet channels
  (`RemoteControl.Protocol`), never that JSON signaling channel.
- `packages/signaling-server/src/server.ts` -- the relay `switch` case
  that used to match only `sdp-offer`/`sdp-answer`/`ice-candidate` now
  also matches `stun-candidates`/`hole-punch-ready`, doing the identical
  generic forward-to-peer relay for all five. Nothing else changed.
- `packages/signaling-server/src/rooms.ts` -- genuinely payload-agnostic
  (only inspects `role`/`pairingCode` on register), reused with **zero**
  changes, as originally planned.
- `deploy/docker-compose.yml`, `deploy/turnserver.conf.example` -- **zero
  changes**, as originally planned. A TURN relay allocation is
  payload-agnostic; coturn works as the fallback relay for this app's
  custom UDP protocol exactly as it did for WebRTC.

See `docs/WIRE-PROTOCOL.md` for the resulting message shapes.

## Phased build order

Ordered so the two highest-risk unknowns surface first, not last.

0. **Repo + protocol/net core -- done.** This repo was created and
   scaffolded, the signaling protocol changes above shipped, and
   everything that doesn't depend on Windows-only APIs was implemented
   *and tested* ahead of its nominal phase, since none of it needs real
   hardware to verify: `RemoteControl.Protocol` (wire types, 19 tests),
   `RemoteControl.Net`'s Reed-Solomon FEC (16 tests, including exhaustive
   K-of-N reconstruction across every loss combination for small block
   sizes), video packetizer/depacketizer (7 tests, simulated loss/
   reordering/duplicates/interleaved frames), STUN client (7 tests,
   including a real RFC 5769 reference vector and a genuine UDP loopback
   round trip), and the adaptive frame pacer (5 tests). `RemoteControl.
   Signaling`'s WebSocket client is implemented but not separately unit
   tested (straightforward `ClientWebSocket` plumbing). 58 tests total,
   all passing, none of them exercised against real hardware or a real
   network yet -- that's still ahead.
1. **Phase 0 -- Capture -> Encode -> Decode -> Render loopback, single
   machine, no networking. Steps 0-2 done and real-hardware verified.** See
   `docs/PHASE-0.md` for the full working plan and results. Step 1 (codec
   against a synthetic D3D11 texture) hit its go/no-go gate for
   real: **decode-side D3D11 zero-copy is confirmed working** end to end
   (the Microsoft H264 Video Decoder MFT genuinely hands back D3D11
   textures, verified by PNG readback matching the source); **encode-side
   zero-copy is not usable on this hardware** -- the vendor (NVIDIA) H.264
   encoder MFT rejects every D3D11 sample and never becomes ready to
   accept input at all via any documented driving mechanism, confirmed
   after eliminating every pipeline-side cause. `HardwareEncoder` falls
   back to the software H.264 encoder MFT (proven correct against the same
   pipeline) for comparison. The named contingency has now also been proven:
   **`NvencEncoder` drives NVIDIA's native NVENCODE API successfully with
   D3D11 NV12 input on the RTX 3070**, producing pixel-correct output and
   encoding 180/180 frames at 1080p60. The first Release run averaged
   2.583ms encode with P1 ultra-low-latency IPPP versus 2.942ms with P4
   high-quality IPPP. An Nsight Systems trace confirms zero steady-state
   device-to-system or system-to-device pixel transfers and shows dedicated
   NVENC/NVDEC H.264 engine work for every frame. Step 2 then completed the
   real desktop path. With capture and presentation separated across two
   displays, 316 frames were captured/encoded/decoded and 301 presented,
   with 2.923ms average steady capture-to-`Present` callback latency. A
   separate run exercised live
   `ResizeBuffers` plus minimize/restore and continued to completion. The
   one necessary capture bridge is a GPU `CopyResource` from the bindless
   Desktop Duplication surface into a reusable render-target texture; no
   CPU pixel copy is introduced.
2. **Phase 1 -- LAN UDP streaming, two machines, no NAT traversal.**
   `EnetTransport` with a hardcoded LAN address, `VideoPacketizer`/
   `VideoDepacketizer` (already implemented, see above) wired in, no FEC
   yet (LAN loss is near-zero). **Milestone**: live cross-machine LAN
   streaming with measured glass-to-glass latency as the baseline every
   later phase is compared against.
3. **Phase 2 -- STUN + hole-punch + TURN fallback.** `StunClient`
   (already implemented, see above) against the deployed coturn VPS; C#
   `SignalingClient` (already implemented) implements register -> exchange
   `stun-candidates` -> `HolePunchCoordinator` simultaneous-open, falling
   back to TURN relay on timeout. `HolePunchCoordinator` itself doesn't
   exist yet. **Milestone**: two machines on genuinely different home
   networks connect and stream direct/STUN, plus a restrictive-network
   (mobile hotspot) test confirming TURN actually carries the custom
   protocol end-to-end.
4. **Phase 3 -- Input capture + injection, lessons baked in.**
   `InputInjector`/`RawInputCapture` (neither implemented yet -- only
   `RemoteControl.Input`'s empty project scaffold exists).
   `RemoteControl.Protocol.InputEvent`/`InputEventCodec` (already
   implemented and tested) are the wire format they'll produce/consume.
   Explicit regression checks required before moving on: (a) click/drag
   accuracy at screen edges on 125%/150% DPI scaling, (b) typing English
   text with *both* an English and a non-English host layout, (c) fast
   drag overshoot + alt-tab mid-drag causing no stuck button on the host.
5. **Phase 4 -- FEC + congestion control + adaptive bitrate.** The FEC
   math itself (`RemoteControl.Net.Fec.ReedSolomonCodec`) and the
   packetizer/depacketizer built on it are already implemented and tested
   against synthetic loss (see Phase 0 above) -- what's left for this
   phase is validating them against *real* network loss/jitter (not just
   synthetic `Random`-driven loss in a unit test) via a lossy/reordering
   UDP proxy, plus building `CongestionController` (doesn't exist yet)
   to degrade encoder quality before frame delivery under constrained
   bandwidth. **Milestone**: watchable stream under injected 1-5% loss
   and constrained bandwidth, measured against the Phase 1 baseline.
6. **Phase 5 -- Feature parity pass.** Multi-monitor swap; input
   arbitration (C# low-level hooks *do* reliably expose
   `LLMHF_INJECTED`/`LLKHF_INJECTED`, a real improvement over the
   Electron app's `uiohook-napi` fingerprint-matching workaround); host
   overlay (watch lesson #5 above); disconnect handling mirroring the
   Electron app's three-cause taxonomy; audio (WASAPI loopback ->
   encode/packetize/FEC, mic passthrough via the same VB-CABLE approach
   as before). None of this implemented yet; the control-channel message
   shapes it'll need aren't defined yet either -- see the TODO in
   `docs/WIRE-PROTOCOL.md`'s "Control channel" section.
7. **Phase 6 -- Packaging/installer.** MSIX or signed NSIS/Inno Setup.
   Decide the Administrator/elevation story explicitly this time
   (`SendInput` into elevated/UAC contexts needs host elevation -- leave
   it explicit, not implicit; `RemoteControl.App/app.manifest` currently
   requests `asInvoker`, i.e. no forced elevation, with a comment flagging
   this as the deferred decision point). Code signing stays a deferrable
   "later".

## Highest-risk pieces (go in with eyes open)

1. **The Media Foundation D3D11 zero-copy pipeline itself (Phase 0).**
   Was the single biggest unknown; **now partially resolved, and the
   picture is worse on the encode side than "fiddly and
   sparsely-documented" suggested.** `Vortice.MediaFoundation` binds the
   APIs correctly -- driving them took real trial and error (see
   `docs/PHASE-0.md`'s Step 1 findings) but is not itself the blocker
   anymore. The actual finding: decode-side D3D11 zero-copy genuinely
   works on this hardware; encode-side does not -- the vendor (NVIDIA)
   H.264 encoder MFT never becomes usable via Media Foundation on this
   machine, independent of how carefully the D3D11 interop is done. That
   risk happened on the Media Foundation path, but its contingency is now
   proven: native NVENC accepts the same GPU-resident D3D11 NV12 texture
   (including the real subresource index), encodes pixel-correct H.264 at
   1080p60, and feeds the already-proven D3D11 decoder.
   `SwapChainPresenter` now also consumes the decoder's real texture-array
   slice directly through the D3D11 video processor. Remaining work for this
   risk is longer `DXGI_ERROR_ACCESS_LOST` soak testing and later input/output
   resource pooling; do not reopen the failed NVIDIA encoder MFT path.
2. **Hand-rolled Reed-Solomon FEC (Phase 4). Substantially de-risked on
   the algorithmic side, not yet on the network side.** The concern was
   "easy to get subtly wrong in ways that pass casual testing but fail
   under exactly the loss patterns it exists to handle" -- addressed by
   exhaustive C(N,K)-combination testing (every possible K-of-N loss
   pattern reconstructs correctly, not just one convenient case) plus a
   real external reference vector for the STUN half of the transport
   work. What's *not* yet tested: real network jitter/loss patterns
   (bursty loss, reordering at real-world scale, actual UDP MTU behavior)
   versus a unit test's synthetic `Random`-driven loss -- that's Phase 4's
   remaining work.
3. **Hand-rolled STUN + hole-punching (Phase 2). Partially de-risked.**
   The STUN message codec itself is solid (RFC 5769 reference vector,
   real UDP loopback round trip). What's unverified: real-world NAT
   behavior is genuinely varied (full-cone/restricted-cone/
   port-restricted/symmetric), and "works on the networks I tested" is a
   weaker guarantee than for the codec pipeline -- `HolePunchCoordinator`
   itself doesn't exist yet, and TURN fallback bounds the downside but
   validating a real P2P success rate needs testing across more than one
   home network.
4. **Solo-scope reality check.** Sunshine represents years of accumulated
   tuning compressed here into a handful of phases. Treat Phase 0 and
   Phase 2 as genuine checkpoints where "this is taking much longer than
   estimated" is expected signal to re-scope (accept higher-than-Parsec
   latency, or narrow to one GPU vendor first) rather than a sign of
   doing something wrong.
5. **DXGI Desktop Duplication's sharp edges.** `AcquireNextFrame`
   re-acquisition after `DXGI_ERROR_ACCESS_LOST` (UAC prompts, display
   mode changes, driver resets, fullscreen-exclusive game transitions --
   exactly what gaming triggers most) is a known rough edge in every
   project using this API, Sunshine included. Budget real testing time
   for it, don't assume "acquire once, loop forever."

## Verification

Same real-hardware constraint as everything above -- none of the
Windows-only pieces can be meaningfully exercised in a sandbox, which is
exactly why this repo was scaffolded on one but Phase 0 onward has to
happen on a real Windows dev machine with a real GPU. Each phase has its
own concrete milestone/exit-criteria stated above; Phase 0 and Phase 2 in
particular are meant as go/no-go checkpoints on real hardware before
continuing, not just items to check off.

The pure-logic pieces (`RemoteControl.Protocol`, `RemoteControl.Net`) are
the exception -- they're fully verifiable anywhere the .NET SDK runs, and
already are (`dotnet test` from the repo root, 58 passing tests). Keep
extending that test coverage as those modules grow; don't let "this needs
real hardware" become an excuse to skip testing the parts that don't.

## Next step

Run a longer resilience soak that forces display-mode/fullscreen transitions
through `DXGI_ERROR_ACCESS_LOST`, then start Phase 1 by wiring the
already-implemented video packetizer and depacketizer between two LAN
machines. Phase 0's correctness, throughput, hardware-engine use, and
driver-visible GPU residency gates are now answered. Keep native NVENC as
the NVIDIA encode path; the failed Media Foundation vendor encoder and PIX
investigations are closed.
