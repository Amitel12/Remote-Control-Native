# Phase 0: capture -> encode -> decode -> render

The go/no-go gate for the whole rewrite (see `ARCHITECTURE.md`, "Phased
build order" and "Highest-risk pieces"). This file is the working plan for
actually building it: what exists today, what has to be written, the order
to write it in, and the specific places this is known to go wrong.

`ARCHITECTURE.md` says *what* Phase 0 must prove and why it comes first.
This says *how* to approach it, and is the file to update as the phase
lands.

## Current state

Phase 0 Steps 0-2 are implemented and have run successfully on the real
Windows 11 / RTX 3070 machine. `RemoteControl.Codec` has Step 0
(`MftProbe`) and Step 1's codec components (`MfDevice`, `AsyncTransform`, `ColorConverter`,
`HardwareEncoder`, `NvencEncoder`, `HardwareDecoder`, `D3DSample`,
`Nv12Readback`, `Interop/CodecApi.cs`) -- see the Step 1 write-up below for
what each does and what real hardware forced them to become.
`NvencEncoder` is the working encode-side hardware path: it drives NVIDIA's
native NVENCODE API with the existing D3D11 NV12 texture and its real
subresource index, bypassing the unusable NVIDIA encoder MFT without a CPU
pixel readback. `RemoteControl.Capture` now provides `DisplayEnumerator`
and `DesktopDuplicator`; `RemoteControl.Render` provides
`SwapChainPresenter`. `tools/LoopbackHarness/Program.cs` runs the complete
live Step 2 loop by default. `--step1` selects the synthetic codec test and
`--mf-encoder` retains its Media Foundation comparison. `--frames N`
changes the live presentation target; `--frames 0` runs until the window is
closed, which is the mode intended for an interactive Nsight capture.

What is already done and correct: the NuGet references (`Vortice.DXGI`,
`Vortice.Direct3D11`, `Vortice.MediaFoundation`, all 3.8.3, plus
`Lennox.NvEncSharp` 2.1.1) and the project reference graph. Those don't
need revisiting.

## Implemented pipeline

The complete Phase 0 path is now present:

- **`DesktopDuplicator`** (`Capture`) -- `IDXGIOutputDuplication`,
  `AcquireNextFrame` -> `ID3D11Texture2D`. Also `DisplayEnumerator` for
  picking an output.
- **Color conversion** (`Codec`, done) -- **this step was missing from
  `ARCHITECTURE.md` entirely.** Desktop Duplication produces BGRA
  (`B8G8R8A8_UNORM`); the H.264 encoder MFT wants NV12. `ColorConverter`
  does this via the Video Processor MFT, GPU-resident end to end -- see
  Step 1 finding 6 for the real bug this step's implementation hit.
- **`HardwareEncoder`** (`Codec`, done, with caveats) -- H.264 encoder
  MFT. Tries the hardware (NVIDIA) encoder first as designed; falls back
  to the software H.264 encoder MFT on this machine, where the hardware
  one never becomes usable -- see Step 1 findings 1-3.
- **`NvencEncoder`** (`Codec`, done) -- native NVIDIA NVENCODE API fallback.
  Registers and maps the exact D3D11 NV12 texture slice produced by the
  color converter, encodes synchronously with zero B-frames, reads back only
  the compressed H.264 bitstream, then unmaps/unregisters the texture. Proven
  on the RTX 3070 at 1080p60; see "Native NVENC fallback" below.
- **`HardwareDecoder`** (`Codec`, done) -- H.264 decoder MFT, output as
  D3D11 textures, no CPU readback. Zero-copy confirmed for real -- see
  Step 1 findings 5 and "Decode-side zero-copy" below.
- **`SwapChainPresenter`** (`Render`, done) -- presents the decoded NV12
  texture directly through the D3D11 video processor into a flip-discard
  BGRA swap chain. Its input view uses the decoder's real array slice. It
  owns `DXGI_STATUS_OCCLUDED`, minimized-window skips, and `ResizeBuffers`
  handling (`ARCHITECTURE.md` lesson #4).

## Build order

`ARCHITECTURE.md`'s "Next step" lists these capture-first, which is the
wrong order for a go/no-go gate: it puts the one genuinely unknown piece
in the middle, so the gate's answer arrives only after everything else is
built. Capture and swap-chain presentation are well-trodden with plenty of
C# precedent; the Media Foundation pipeline is the actual unknown. Prove
that first.

**Step 0 -- enumerate the MFTs. Written and run on real hardware.**
`RemoteControl.Codec`'s `MftProbe`, invoked by `tools/LoopbackHarness`:
calls `MFTEnumEx` for hardware and software H.264 encoders and decoders
and prints what comes back, then states a pass/incomplete verdict. Answers
two questions in an afternoon: does this GPU expose what we assume, and
does `Vortice.MediaFoundation` actually surface the APIs needed to drive
it? Binding coverage varies, and a gap found here costs an afternoon
instead of three weeks.

It enumerates only -- it never activates or configures a transform, so a
pass means the pieces are *present*, not that they can be driven. That
is Step 1's job.

**Result on the first real machine (NVIDIA GPU, Windows 11):**

- `NVIDIA H.264 Encoder MFT` -- hardware. The encode side is real.
- No hardware H.264 *decoder*. The only hardware decoder exposed is
  `NVIDIA MJPEG Video Decoder MFT`; the H.264 decoder available is
  `Microsoft H264 Video Decoder MFT`, enumerated as software.

That asymmetry is expected on Windows, not a fault. GPU vendors generally
do not ship a standalone hardware H.264 decoder MFT: hardware decode is
reached *through* the Microsoft H264 Video Decoder MFT, which uses
DXVA2/D3D11VA internally once handed a D3D device manager via
`MFT_MESSAGE_SET_D3D_MANAGER`.

The consequence for Step 1 is concrete: the encoder and decoder are not
symmetric pieces. The encoder is a vendor hardware MFT; the decoder is a
Microsoft MFT that must be *told* to use the GPU. Confirming that decoder
reports `MF_SA_D3D11_AWARE` and genuinely returns D3D11 textures is the
first thing Step 1 has to establish -- until then, zero-copy on the decode
half of the pipeline is unproven, and it is half the gate.

**Step 1 -- codec against a synthetic source.** Feed a hand-made D3D11
texture (solid colour, or a moving rectangle so successive frames differ)
straight into encode -> decode. No Desktop Duplication, no swap chain.
Isolates the risky link with nothing else able to be at fault.

**Result on the same machine: PARTIAL GO. Decode-side D3D11 zero-copy is
real and verified. Encode-side zero-copy is not usable on this hardware --
the vendor encoder MFT never becomes ready to accept input, hardware or
software sample, and a broken assumption (that D3D11 input at least fails
cleanly) turned out to actively corrupt process state. The pipeline itself,
driven the way this section describes, is proven correct end to end
against the software H.264 encoder MFT -- same `ColorConverter`, same
`HardwareDecoder`, same `AsyncTransform` drive loop, same verification
PNG.**

Built in `RemoteControl.Codec`, driven from `tools/LoopbackHarness`:
`MfDevice` (D3D11 device against an explicitly chosen adapter +
`IMFDXGIDeviceManager`), `ColorConverter` (BGRA -> NV12 via the Video
Processor MFT), `HardwareEncoder` (NV12 -> H.264), `HardwareDecoder` (H.264
-> NV12 D3D11 texture), and `AsyncTransform`, a shared driver used by all
three MFTs. `tools/LoopbackHarness/SyntheticSource` renders the moving
BGRA rectangle via two `ID3D11DeviceContext1.ClearView` calls (no shader
needed); `FrameVerifier` is the one-off NV12 -> PNG readback, gated behind
`--no-verify-frame`.

Six things turned out wrong, in the order they were found. Each cost real
debugging time on real hardware, which is the entire point of doing this
before Phase 1:

1. **The hardware H.264 encoder MFT rejects every D3D11 sample.**
   `ProcessInput` returns `MF_E_UNSUPPORTED_D3D_TYPE` ("the input type is
   not supported for D3D device") regardless of the input texture's
   `BindFlags`/`Usage`/`ResourceOptionFlags`, regardless of whether
   `MFT_MESSAGE_SET_D3D_MANAGER` is sent before or after type negotiation,
   regardless of low-latency mode, and regardless of using
   `MFCreateVideoSampleFromSurface` instead of `MFCreateDXGISurfaceBuffer`
   to build the sample. The decoder's own D3D11 output (proven real, see
   below) rules out a `MFCreateDXGISurfaceBuffer`/RIID bug; the color
   converter accepting and producing D3D11 samples successfully on the
   *same* device rules out the device/adapter setup. This is specific to
   the encoder MFT's input path.
2. **That rejection is not recoverable, in-instance or fresh.** Retrying
   `ProcessInput` on the same transform after one D3D11 rejection throws
   `MF_E_NOTACCEPTING`; forcing a reset (`MFT_MESSAGE_SET_D3D_MANAGER`
   NULL, `MFT_MESSAGE_COMMAND_FLUSH`, restart streaming) upgrades that to a
   **fatal, uncatchable `AccessViolationException` that kills the
   process** -- not a managed exception `try`/`catch` can stop. Worse, the
   corruption outlives the instance: activating a brand-new encoder MFT in
   the same process, that has never touched a D3D11 sample, still throws
   the same AV on its first `ProcessInput`. Whatever broke is process- or
   driver-global, not per-object. `HardwareEncoder` now never sends
   `MFT_MESSAGE_SET_D3D_MANAGER` to this encoder at all -- system-memory
   input only, confirmed safe.
3. **Independent of D3D11: the hardware encoder never signals it can
   accept input, by any mechanism.** `MF_TRANSFORM_ASYNC` is genuinely set
   (unlike the decoder -- see Step 0 note below), so the documented
   contract is the event-driven one: unlock, then wait for
   `METransformNeedInput` before the first `ProcessInput`. That wait timed
   out with both the synchronous `GetEvent(0)` (MSDN's documented legal
   substitute for the callback pump) and a proper
   `BeginGetEvent`/`EndGetEvent` callback pump implementing
   `IMFAsyncCallback` -- neither ever received a single event, for up to
   20s. `GetInputStatus` polling (`MFT_INPUT_STATUS_ACCEPT_DATA`) -- the
   pattern a working reference implementation
   ([sipsorcery/mediafoundationsamples' `MFH264RoundTrip.cpp`](https://github.com/sipsorcery/mediafoundationsamples))
   uses successfully against its own hardware H.264 encoder MFT -- also
   never returned ready, for the full 5s bound `AsyncTransform` now uses.
   `AsyncTransform` was rewritten around `GetInputStatus`/`ProcessOutput`
   polling (matching that reference sample) instead of the event queue,
   since the event queue provably does nothing on this driver for *any*
   transform, not just this encoder -- but the encoder specifically still
   never becomes ready. `HardwareEncoder.Encode` treats that timeout as an
   expected, bounded outcome: it falls back to the software H.264 encoder
   MFT (same pipeline, proven correct -- see below) rather than surfacing
   an error, logs the finding once, and remembers process-wide so later
   `HardwareEncoder` instances (the low-latency vs. defaults comparison
   needs two) skip straight past the doomed hardware attempt.
4. **`MftProbe`'s `MFStartup`/`MFShutdown` pairing (Step 0) tears down
   Media Foundation for anything sharing its process afterward.**
   `MftProbe.Enumerate` calls `MFShutdown` in a `finally` block -- correct
   for Step 0 run alone, wrong once Step 1 runs immediately after it in
   the same `LoopbackHarness` invocation. Every basic MF call (enumerate,
   activate, negotiate types, get/set attributes) kept working regardless,
   which is what made this so hard to isolate -- only starting a real
   hardware encode session actually needed the torn-down subsystem, and it
   failed with the misleading `MF_E_UNSUPPORTED_D3D_TYPE`, sending the
   investigation down the D3D11 path above for real, separate reasons
   before this one was found. `Program.Main` now calls `MFStartup` again
   itself before Step 1 and matches it with its own `MFShutdown`, rather
   than relying on `MftProbe`'s internal pairing to leave MF in a state
   Step 1 can use.
5. **The decoder's D3D11 output is one slice of a texture *array*, not a
   standalone texture -- and copying the whole array with `CopyResource`
   against a mismatched single-slice staging texture is a silent no-op,
   not an error.** `IMFDXGIBuffer.SubresourceIndex` on the decoded sample's
   buffer cycled 0..5 against a 6-slice array (the decoder's internal
   reference-picture pool) -- ignoring it and blindly `CopyResource`-ing
   produced a staging texture that read back as all-zero Y/U/V on every
   frame, which decoded to a uniform dark green PNG that looked like a
   real (if wrong) image rather than an obvious failure. `HardwareDecoder`
   now returns a `DecodedFrame(Texture, SubresourceIndex)` record; every
   consumer (`Nv12Readback`, `FrameVerifier`) uses
   `CopySubresourceRegion(dst, 0, 0,0,0, src, subresourceIndex, null)`,
   never `CopyResource`, against decoder output.
6. **The Video Processor MFT does not guarantee the output sample it hands
   back is the one you gave it**, even though its `OutputStreamProvidesSamples`
   flag is false (meaning *it shouldn't be allocating its own). Handing
   `ProcessOutput` a sample wrapping our own pre-created NV12 texture and
   then trusting that texture to hold the result left it untouched
   (all-zero) while the real converted frame was in the substituted sample
   the whole time -- same "looks like success, silently wrong" shape as
   finding 5, different mechanism. `ColorConverter.Convert` now extracts
   the texture actually referenced by the sample `ProcessOutput` returns
   (via `IMFDXGIBuffer`, same pattern as the decoder) instead of assuming
   it's the texture that was handed in.

One smaller Vortice/SharpGen binding issue, not an MF concept: the 2-arg
`ID3D11DeviceContext1.ClearView(view, color)` convenience overload
recurses into itself (stack overflow); the 3-arg form with `null` rects
throws `NullReferenceException` marshaling the array. Only the 3-arg form
with an explicit non-null rect array works. Affects `SyntheticSource` only.

**Decode-side zero-copy: CONFIRMED, not just assumed.** The Microsoft H264
Video Decoder MFT reports `MF_SA_D3D11_AWARE = true`; every decoded
sample's buffer implements `IMFDXGIBuffer` (`HardwareDecoder` throws
immediately, loudly, if one ever doesn't); the extracted `ID3D11Texture2D`
is genuinely GPU-resident, D3D11VA-backed decode output, confirmed
end-to-end by writing the decoded frame back to a PNG and visually
matching the synthetic source's moving rectangle. Also confirmed: this
decoder is a **synchronous** MFT (`MF_TRANSFORM_ASYNC` is absent) despite
doing real D3D11/DXVA hardware acceleration internally -- the "hardware
MFTs are async" landmine below turned out to describe the vendor encoder
specifically, not every D3D11-aware MFT.

**Encode-side zero-copy through Media Foundation: not usable on this
hardware.** Findings 1-3 above are the complete, direct answer for the
NVIDIA encoder MFT. Do not retry that API path. `ARCHITECTURE.md`'s
contingency for this outcome -- NVIDIA's native NVENCODE API -- has now
been implemented and proven separately below.

**Native NVENC fallback: CONFIRMED working on the RTX 3070.**
`NvencEncoder` uses `Lennox.NvEncSharp` 2.1.1 (NVENCODE API 12.2) to open a
DirectX encode session against the same D3D11 device used by the color
converter and decoder. For every frame it passes the converter's actual
`ID3D11Texture2D` plus its actual `SubResourceIndex` to
`NvEncRegisterResource`, maps it, submits it as NV12, locks the compressed
H.264 Annex-B output, then unmaps and unregisters it. There is no NV12
readback or upload in this path; the only CPU copy is the compressed
bitstream that must go to the network/decoder. The retained
`HardwareEncoder` path still does the deliberate `Nv12Readback` and remains
available with `--mf-encoder` for comparison.

Real Release run, 1920x1080 at 60fps, 180 synthetic frames, 5-frame warmup:

- P1 ultra-low-latency, CBR, IPPP, zero reorder delay: **2.583ms average
  encode** (2.112ms min, 5.029ms max); **0.112ms average decode**; 180/180
  frames encoded and decoded.
- P4 high-quality, CBR, still forced IPPP so resource lifetime stays
  synchronous: **2.942ms average encode** (2.092ms min, 3.446ms max);
  **0.108ms average decode**; 180/180 frames encoded and decoded.

The decoded PNG was read back once and visually matched the synthetic dark
background plus orange moving rectangle. The low-latency path is therefore
both faster than the P4 IPPP comparison on this run and pixel-correct. This
is not yet a true "NVENC defaults with B-frames" comparison: supporting
reordering requires a pool that keeps multiple registered input textures
and output buffers alive until delayed outputs are returned. The current
comparison deliberately keeps `FrameIntervalP = 1` in both modes so every
input texture can be released only after its synchronous output is locked.

**Low-latency vs. defaults, measured** (software H.264 encoder MFT, 1080p,
90 synthetic frames, steady-state after a 5-frame warmup): low-latency
(`MF_LOW_LATENCY` set; `CODECAPI_AVEncMPVDefaultBPictureCount` unsupported
by this MFT, see below) averaged ~6.3ms encode /
~0.12ms decode per frame; defaults averaged ~4.4-4.9ms encode / ~0.08-0.1ms
decode. The two configurations differ in the direction the *encoder's*
compressed output size does, not encode speed on this software path (low-
latency: ~840B/frame avg; defaults: ~375B/frame avg) -- consistent with
low-latency mode trading compression efficiency for latency, as intended,
though these specific numbers are a software-encoder artifact, not
representative of what the hardware path would show. `IsSupported` on
`CODECAPI_AVEncMPVDefaultBPictureCount` returns "not supported" for both
the hardware and software encoder MFTs on this machine -- B-frame count
isn't configurable via `ICodecAPI` here at all; `MF_LOW_LATENCY` is set
and doing the only enforceable part of the job.

**Step 2 -- real capture in, real presentation out: CONFIRMED working.**
`DesktopDuplicator` enumerates outputs on the same adapter as `MfDevice`,
acquires with `IDXGIOutputDuplication`, and recreates the duplication
session after `DXGI_ERROR_ACCESS_LOST`. The acquired desktop surface has no
bind flags and the Video Processor MFT rejected it with
`DXGI_ERROR_INVALID_CALL`; the required bridge is one reusable BGRA
render-target texture populated with D3D11 `CopyResource`. This is a
GPU-to-GPU copy, not a CPU readback/upload. `SwapChainPresenter` uses the
D3D11 video processor to convert the decoder's actual NV12 texture-array
slice to a BGRA flip-discard swap chain.

On multi-monitor systems, the harness places the presentation window on the
first output other than the captured output. This prevents the swap chain
from being captured recursively as a visual hall of mirrors, which keeps
content complexity and performance measurements representative. A
single-monitor system falls back to the captured output and logs a warning.

Feedback-free Release run, output 0 captured at 1920x1080 and the window on
output 1, native NVENC P1, 300 presentation target: **316 captured / 316
encoded / 316 decoded / 301 presented**, 10 acquisition timeouts and zero
occlusion/minimize skips. Steady callback latency from acquired desktop
frame to `Present(syncInterval: 0)` averaged **2.923ms** (2.399ms min,
3.943ms max, 5-frame warmup skipped). The one-off decoded PNG from the
earlier correctness run is a coherent copy of the live desktop.

A second Release run with `--exercise-window-state` resized the client from
1280x720 to 960x540, minimized it for 30 decoded frames, restored it, and
continued to completion: **346 captured / 346 encoded / 346 decoded / 301
presented**, 30 safe minimized skips, no crash or device teardown. The
`DXGI_ERROR_ACCESS_LOST` recovery path was then forced on the RTX 3070 / Windows
11 machine. Changing the captured display from 1920x1080 to 1680x1050 rebuilt
capture, conversion, NVENC, decode, and presentation at the new dimensions;
changing it back rebuilt the complete session at 1920x1080. Locking with
Win+L paused capture while Windows was on the secure desktop and unlocking
restored capture without terminating the harness. DXGI can report access loss
from either `AcquireNextFrame` or `ReleaseFrame` when the desktop switch races
an outstanding frame, so both calls enter the same recovery path. The latter
case was found only by this real lock/unlock test.

## Exit criteria are two questions, not one

`ARCHITECTURE.md` states the gate as sustained 60fps 1080p with zero
CPU-side texture copies. That is two separate claims needing two different
tools, and conflating them is how a gate gets declared passed when it
wasn't:

1. **Is the output correct?** Read one frame back to CPU and write a PNG.
   A readback is fine here -- it is a one-off check, not part of the
   pipeline. **Answered for Step 1: yes**, decode output visually matches
   the synthetic source (`FrameVerifier`, gated behind `--no-verify-frame`)
   -- and getting a genuinely correct-looking PNG took two real bugs to
   find first (Step 1 findings 5 and 6 above), since a wrong-but-plausible
   image is a worse failure mode than an obvious crash. **Answered for the
   complete Step 2 loop as well:** the one-off PNG is a coherent 1920x1080
   capture of the live desktop after native encode and D3D11 decode.
2. **Are there CPU copies? Answered at the driver/ETW-visible level: no
   steady-state pixel transfers.** An NVIDIA Nsight Systems trace of the
   Release loop recorded 316 native NVENC H.264 submissions, 632 NVENC
   engine workloads, 316 NVDEC H.264 engine workloads, and 300 DXGI
   presents. For `LoopbackHarness.exe`, it recorded zero device-to-system
   transfers. System-to-device transfers occurred only during initialization
   (0.707s through 1.873s) and stopped for the remaining frame loop, so there
   is no per-frame pixel upload. `nvEncLockBitstream` is the expected CPU-side
   compressed-bitstream access, not a pixel readback. PIX cannot capture this
   native D3D11/MF/NVENC path: native D3D11 is unsupported and forcing
   D3D11On12 terminates before GPU work with `E_PIX_CAPTURE_NO_GPU_WORK`.
   The `--mf-encoder` software fallback intentionally calls `Nv12Readback`
   and is not a zero-copy path.

Also required by the gate: that zero-B-frames/IPPP plus low-latency tuning
measurably beats defaults. That is a *comparison*, so keep comparison modes
runnable rather than deleting them once low latency works. **Native NVENC
P1 ultra-low-latency beats P4 high-quality with both forced to IPPP in the
first 1080p60 run (2.583ms vs. 2.942ms average).** A true default/B-frame
NVENC comparison remains open for the resource-pool reason above. The older
software MFT low-latency/default comparison is still runnable with
`--mf-encoder`.

## Known landmines

**Hardware MFTs are asynchronous -- except when the event queue doesn't
work anyway.** The textbook rule still applies in spirit: an MFT reporting
`MF_TRANSFORM_ASYNC` refuses most calls with `MF_E_TRANSFORM_ASYNC_LOCKED`
until you set `MF_TRANSFORM_ASYNC_UNLOCK`. But confirmed on real hardware
(Step 1): don't assume the event queue that unlocks access to
(`METransformNeedInput`/`METransformHaveOutput` via `GetEvent` or
`BeginGetEvent`/`EndGetEvent`) actually delivers anything -- on this
NVIDIA driver it never did, for either mechanism, on the one MFT here that
reports async at all. `GetInputStatus` polling before `ProcessInput` and a
`ProcessOutput` polling loop after -- the plain synchronous model, just
also legal to use post-unlock -- is what a working reference
implementation uses and what this codebase's `AsyncTransform` uses now.
Getting the unlock step wrong (or trusting the event queue blindly) does
not fail cleanly; it hangs or starves. Still the most common way to lose
days on Media Foundation, just not for the reason the API docs suggest.

**D3D11 device setup for MF.** Create the device with
`D3D11_CREATE_DEVICE_VIDEO_SUPPORT`; hand the MFT an
`IMFDXGIDeviceManager` via `MFT_MESSAGE_SET_D3D_MANAGER`; call
`ID3D11Multithread::SetMultithreadProtected(true)`, because MF touches the
device from its own threads. Missing any of these produces failures well
away from the actual cause.

**Encoder configuration.** `MF_LOW_LATENCY`, and B-frames explicitly off
(`CODECAPI_AVEncMPVDefaultBPictureCount` = 0) -- B-frames trade latency
for compression, which is exactly the wrong trade here.

**`AcquireNextFrame` returning `DXGI_ERROR_WAIT_TIMEOUT` is normal.** It
means nothing on screen changed. It is control flow, not an error. Release
every acquired frame. `DXGI_ERROR_ACCESS_LOST` needs real re-acquisition
handling (`ARCHITECTURE.md` risk #5) -- UAC prompts, mode changes, driver
resets and fullscreen transitions all trigger it, and gaming triggers all
four. A desktop switch may instead make `ReleaseFrame` return
`DXGI_ERROR_ACCESS_LOST`; discard that frame and recreate duplication in the
same way. `DuplicateOutput` returns `E_ACCESSDENIED` while Win+L has Windows on
the secure desktop, so retry until the interactive desktop returns rather than
treating it as fatal.

## Testing notes

Desktop Duplication only produces frames when the screen actually changes,
so **a static desktop will not generate 60fps to measure.** Have video
playing or something animating on the captured output, or the throughput
numbers mean nothing.

None of this is verifiable off a real Windows machine with a real GPU,
which is why it could not be written during scaffolding. The pure-logic
projects (`Protocol`, `Net`) stay independently testable anywhere and
should keep their coverage as they grow.
