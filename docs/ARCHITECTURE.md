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
  independent history. The signaling protocol itself started as an
  additive change to `amitel12/tests`'s `packages/signaling-server` (see
  "Signaling protocol changes" below), but this repo now has its own
  implementation, `src/RemoteControl.SignalingServer` -- same wire
  protocol, no dependency on the old repo to run. `deploy/` (coturn) in
  `amitel12/tests` is still the reference TURN config; any standalone
  coturn instance works.
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

See `docs/WIRE-PROTOCOL.md` for the resulting message shapes. This section
records that history; the server this repo actually runs against today is
its own `src/RemoteControl.SignalingServer`, a from-scratch implementation
of the same relay behaviour (see the Next-step list below), not the
`amitel12/tests` one described above.

## Phased build order

Ordered so the two highest-risk unknowns surface first, not last.

0. **Repo + protocol/net core -- done.** This repo was created and
   scaffolded, the signaling protocol changes above shipped, and
   everything that doesn't depend on Windows-only APIs was implemented
   *and tested* ahead of its nominal phase, since none of it needs real
   hardware to verify: `RemoteControl.Protocol` (wire types, 19 tests),
   `RemoteControl.Net`'s Reed-Solomon FEC (16 tests, including exhaustive
   K-of-N reconstruction across every loss combination for small block
   sizes), video packetizer/depacketizer (9 tests, simulated loss/
   reordering/duplicates/interleaved frames), STUN client (7 tests,
   including a real RFC 5769 reference vector and a genuine UDP loopback
   round trip), LAN session framing (6 tests), and the adaptive frame pacer
   (5 tests). `RemoteControl.
   Signaling`'s WebSocket client is implemented but not separately unit
   tested (straightforward `ClientWebSocket` plumbing). 67 tests total,
   all passing. The LAN framing is exercised across a real localhost UDP
   socket and real GPU processes; a physical two-machine network is still
   ahead.
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
   `DXGI_ERROR_ACCESS_LOST` path was also forced with live 1920x1080 ->
   1680x1050 -> 1920x1080 mode changes and rebuilt the complete GPU pipeline at
   each size. A Win+L secure-desktop transition paused capture and resumed after
   unlock without terminating the process; both acquire-time and
   release-time access loss are handled. The one necessary capture bridge is a
   GPU `CopyResource` from the bindless
   Desktop Duplication surface into a reusable render-target texture; no
   CPU pixel copy is introduced.
2. **Phase 1 -- LAN UDP streaming, two machines, no NAT traversal. Done,
   real-hardware verified.** A configuration/ready handshake and session
   envelope wire `VideoPacketizer`/`VideoDepacketizer` between separate host
   and client processes. FEC parity recovery, round-trip latency/
   clock-offset instrumentation, and a socket seam (`IUdpTransport`/
   `UdpTransport`) are implemented and real-hardware verified -- see
   "FEC parity recovery" and "Latency instrumentation" in `docs/PHASE-1.md`.
   `EnetTransport` itself stays deferred to Phase 3's input/control channel
   (see gate item 4 in `docs/PHASE-1.md` for why). The real two-machine run
   is done: host (RTX 3070, wired) streamed live to a second PC (Intel Iris
   Plus Graphics, Wi-Fi) over a real home network, 300/300 captured through
   presented, zero loss, avg 50.2ms glass-to-glass RTT. Host resolution-
   change and Win+L recovery were also verified with that client connected --
   see gate item 5 in `docs/PHASE-1.md`. **Milestone met**: live
   cross-machine LAN streaming with measured glass-to-glass latency as the
   baseline every later phase is compared
   against.
3. **Phase 2 -- STUN + hole-punch + TURN fallback. Core milestone met,
   real-hardware verified.** `HolePunchCoordinator` (simultaneous-open UDP
   hole punch) is implemented and tested; `tools/LoopbackHarness`'s
   `--p2p-host`/`--p2p-client` modes proved it for real: this RTX 3070 PC
   (home Ethernet) and a second PC tethered to mobile data (genuinely
   different networks/NATs) connected directly in 68ms with no relay, then
   streamed 300 real frames through the punched socket -- see
   `docs/PHASE-2.md`. Two further networks have been tested since. On a third
   network (a different carrier's phone hotspot) the punch exposed a real
   bug -- each side stopped sending probes the moment its *own* receive
   succeeded, starving a peer whose NAT needed several more seconds of
   inbound probes to finish opening; fixed by sending for the full timeout
   budget regardless, after which the punch succeeded and both video and
   remote input streamed over real cellular. On a fourth (a residential
   network at a relative's house) a clean attempt with byte-matched
   candidates and ~25s of genuine window overlap **still timed out** -- the
   restrictive-NAT case, confirmed for real rather than hypothetically.
   Candidate exchange is automated in code as of
   `RemoteControl.Net.Peering.SignaledPeerConnector`, which drives the full
   `register` -> `peer-joined` -> `stun-candidates` -> `hole-punch-ready`
   flow through `SignalingClient` and hands `HolePunchCoordinator` the
   result (`--signaling-server`/`--pairing-code` on the harness; the manual
   prompt remains the fallback). That closes the gap that caused most of the
   failed attempts above -- stale candidates after a restart, transcription
   typos, one side's punch window elapsing during the other's build -- but
   **no signaling server is deployed**, so it is verified only against an
   in-process fake of the server's relay behaviour plus a real loopback
   punch, never against the real thing. TURN relay fallback remains
   unimplemented. **Milestone**: two
   machines on genuinely different home networks connect and stream
   direct/STUN -- met. A restrictive-network test confirming TURN carries
   the protocol end-to-end -- still open, and no longer optional: a real
   network has now been found where direct punching genuinely cannot
   succeed.
4. **Phase 3 -- Input capture + injection, lessons baked in. Both halves
   implemented and real-hardware verified.** `InputInjector` (`SendInput`,
   lessons #1/#2 baked in) and `RawInputCapture` (window subclassing,
   lesson #3's `SetCapture`/`WM_KILLFOCUS` safety net) are both implemented
   and verified via `tools/LoopbackHarness --input-demo`/
   `--input-capture-demo` -- mouse movement, Unicode typing (including a
   surrogate-pair emoji round-tripping through real capture), a real
   `Enter` keypress, a working Ctrl+A shortcut, and a real focus-loss
   mid-drag correctly synthesizing the missing button-release, all
   confirmed on real hardware; see `docs/PHASE-3.md`.
   `RemoteControl.Protocol.InputEvent`/`InputEventCodec` (already
   implemented and tested) are the wire format they produce/consume. The
   end-to-end loop is now wired and real-hardware verified too: capture ->
   `LanDatagramKind.Input` over the existing UDP socket -> injection,
   opt-in via `--remote-input`, with matching sent/received counts on a real
   two-machine run. Best-effort UDP surfaced two real reliability gaps,
   both fixed and verified: lost releases leaving a stuck button/key
   (`InputStateSync` + `ReconcileHeldState`, a periodic held-state resync)
   and silently dropped typed characters (redundant send plus
   `InputSequenceDedup`, measured ~70% -> ~90% character recovery at 30%
   loss). Both then survived a real cellular P2P session -- 1196 input
   events, 606 correctly deduped. ENet input channels are still not used;
   the same "defer until genuinely needed" call as the video channel.
   Explicit regression checks required before moving on -- **none of these
   three have been run yet**: (a) click/drag accuracy at screen edges on
   125%/150% DPI scaling, (b) typing English text with *both* an English and
   a non-English host layout, (c) fast drag overshoot + alt-tab mid-drag
   causing no stuck button on the host.
5. **Phase 4 -- FEC + congestion control + adaptive bitrate. Loss-driven
   half real-hardware verified on a real two-machine network; only
   bandwidth-capping remains untested.**
   `tools/LossyProxy` (real UDP relay, bursty Gilbert-Elliott loss + genuine
   packet reordering + jitter) validated FEC/the depacketizer against real
   network impairment instead of just synthetic per-shard loss -- and found
   a real bug: completed frames were decoded in network-arrival order, not
   frame-index order, so real reordering (which independent per-shard loss
   rarely triggers) could feed the H.264 decoder out of temporal sequence
   and silently corrupt/drop frames with no error or counter. Fixed with a
   bounded reorder buffer in the client session, the same "skip a stuck
   frame rather than stall" philosophy `FramePacer` already applies to
   timing, now applied to decode order too. `CongestionController` (AIMD,
   reacting to client-reported loss via a new `QualityReport` datagram and
   to RTT spikes) is implemented and real-hardware verified on both
   loopback and a real two-machine network (this PC and a second PC over
   real home Wi-Fi, `tools/LossyProxy` relaying between them): on the real
   network it reacted to genuinely changing conditions with four live
   reconfigures in one run (two decreases, two recoveries) via
   `NvEncReconfigureEncoder` (never used here before), with the
   `completed == decoded == presented` invariant holding throughout --
   zero decode corruption at any transition. See `docs/PHASE-4.md` for
   both results. Bandwidth capping/shaping itself isn't tested at all
   yet -- only the loss-driven half of "adaptive bitrate" is proven.
   A later latency pass, prompted by a real cellular P2P session that
   streamed fine by the counters but looked laggy to a viewer, added five
   more adaptations (`docs/PHASE-4.md`, "The latency-improvement list"):
   skipping the *presentation* of backlogged stale frames while still
   decoding them, adaptive FEC (`--adaptive-fec`, parity scaled off measured
   loss instead of a fixed ratio), continuous intra-refresh
   (`--intra-refresh`, no periodic IDR bitrate spike), an adaptive reorder
   window, and a TCP-`ssthresh`-shaped soft ceiling stopping the congestion
   controller from repeatedly climbing back to a bitrate that just failed.
   Four of those five were designed by inference from logs and have not yet
   been run on a real link; the fifth is an input-to-present latency
   measurement added specifically to give the others a number to be judged
   against.
   **Milestone**:
   watchable stream under injected 1-5% loss and constrained bandwidth,
   measured against the Phase 1 baseline -- loss/reordering half met, on
   both loopback and a real two-machine network; bandwidth-constrained half
   not attempted.
6. **Phase 5 -- Feature parity pass.** Starts with a structural debt worth
   naming: nearly all session logic built in Phases 1-4 lives in
   `tools/LoopbackHarness` (`LanClientVideoSession` is a private nested
   class in one 900-line file), not in `src/`. That was the right call while
   the harness was the only consumer, but `RemoteControl.App` cannot reuse
   any of it and neither can the unit tests -- so lifting the session layer
   into a real library is the prerequisite for everything below, not a
   cleanup to do afterwards. Multi-monitor swap; input
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
   slice directly through the D3D11 video processor. Display-mode and Win+L
   recovery are now proven on real hardware; later input/output resource
   pooling remains. Do not reopen the failed NVIDIA encoder MFT path.
2. **Hand-rolled Reed-Solomon FEC (Phase 4). Substantially de-risked on
   the algorithmic side, not yet on the network side.** The concern was
   "easy to get subtly wrong in ways that pass casual testing but fail
   under exactly the loss patterns it exists to handle" -- addressed by
   exhaustive C(N,K)-combination testing (every possible K-of-N loss
   pattern reconstructs correctly, not just one convenient case) plus a
   real external reference vector for the STUN half of the transport
   work. **Since resolved**: `tools/LossyProxy` put the FEC path under real
   bursty loss, real reordering and real jitter, and the codec itself held
   -- what broke was decode *ordering* around it (see Phase 4 above), not
   the erasure coding. Parity is now scaled off measured loss rather than
   fixed (`--adaptive-fec`). Genuine constrained-bandwidth behaviour is the
   one impairment still not exercised.
3. **Hand-rolled STUN + hole-punching (Phase 2). Partially de-risked.**
   The STUN message codec itself is solid (RFC 5769 reference vector,
   real UDP loopback round trip). `HolePunchCoordinator` now exists and has
   been tested across four real networks -- and the varied-NAT concern
   proved entirely justified: it succeeded on two (68ms and ~9s), needed a
   real bug fixed to work on a third (one side stopping its probes too
   early), and **failed outright on a fourth** despite a clean attempt.
   That last one is the case TURN exists for. TURN is now implemented and
   `CandidateKind.Host`/`Relay` are both wired, so the downside is bounded in
   code -- but only in code: nothing here has spoken to real coturn, and an
   untested fallback is not yet a bound. That test is what closes this risk.
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
already are (`dotnet test` from the repo root, 67 passing tests). Keep
extending that test coverage as those modules grow; don't let "this needs
real hardware" become an excuse to skip testing the parts that don't.

## Next step

Phases 0-4 are landed and real-hardware verified; the open work is in this
order.

1. **Verify the Phase 4 latency pass on a real link.** Four of its five
   changes are inferred from logs rather than measured. One real
   two-machine run (ideally the cellular P2P condition they were written
   for) recording input-to-present with each flag off and on turns them
   from plausible into justified -- or finds that one of them doesn't help.
2. **Deploy a signaling server and run the automated exchange against it.**
   `SignaledPeerConnector` now connects `SignalingClient` to
   `HolePunchCoordinator`, but the only server it has ever spoken to is an
   in-process fake. `src/RemoteControl.SignalingServer` (own implementation
   of the same relay protocol, replacing the `amitel12/tests` dependency
   this item used to name) has passed a real-WebSocket smoke test
   (register/ack, peer-joined, candidate relay both ways, hole-punch-ready
   relay, peer-left on disconnect, room-full rejection) but not yet an
   actual two-machine run. Running both harness sides against a deployed
   instance with `--signaling-server`/`--pairing-code` is what turns the
   manual copy/paste off for good -- and it comes before the TURN work
   because every NAT test after it gets cheaper.
3. **Run the TURN fallback against real coturn.** It is implemented
   (`docs/PHASE-2.md`, "TURN relay fallback") and tested against a fake
   server, which cannot catch a misread of the RFC that both implementations
   share. coturn itself is unrelated to `amitel12/tests` (any standalone
   instance works, e.g. via `deploy/turnserver.conf.example` there as a
   reference config, or a fresh `docker run coturn/coturn`), so this and
   step 2 no longer share a dependency -- but doing them in one session,
   ideally from the restrictive network that motivated the TURN work, is
   still the efficient path.
4. **Close out Phase 4's bandwidth half.** `tools/LossyProxy` shapes loss,
   reordering and jitter but has no bandwidth cap, so the "constrained
   bandwidth" half of the milestone is untested and the AIMD constants stay
   untuned guesses.
5. **Run Phase 3's three regression checks** (DPI-scaled edge accuracy,
   non-English host layout, real alt-tab mid-drag) -- all still unrun.
6. **Then Phase 5**, which starts by lifting the session layer out of
   `tools/LoopbackHarness` into `src/` (see the Phase 5 entry above -- today
   `RemoteControl.App` can reuse none of what Phases 1-4 built) and defining
   the reliable control-channel message shapes still marked TODO in
   `docs/WIRE-PROTOCOL.md`.

Phase 0's correctness, throughput, hardware-engine use, driver-visible GPU
residency, display-mode recovery, and secure-desktop recovery gates are all
answered. Keep native NVENC as the NVIDIA encode path; the failed Media
Foundation vendor encoder and PIX investigations are closed.
Fullscreen-exclusive and driver-reset recovery remain useful soak coverage.
