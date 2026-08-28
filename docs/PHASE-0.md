# Phase 0: capture -> encode -> decode -> render

The go/no-go gate for the whole rewrite (see `ARCHITECTURE.md`, "Phased
build order" and "Highest-risk pieces"). This file is the working plan for
actually building it: what exists today, what has to be written, the order
to write it in, and the specific places this is known to go wrong.

`ARCHITECTURE.md` says *what* Phase 0 must prove and why it comes first.
This says *how* to approach it, and is the file to update as the phase
lands.

## Current state

`RemoteControl.Capture`, `RemoteControl.Codec`, and `RemoteControl.Render`
contain no source files at all -- only a `.csproj` each. There is nothing
to fill in; this is greenfield. `tools/LoopbackHarness/Program.cs` exists
but only prints that the pipeline is unimplemented.

What is already done and correct: the NuGet references (`Vortice.DXGI`,
`Vortice.Direct3D11`, `Vortice.MediaFoundation`, all 3.8.3) and the
project reference graph. Those don't need revisiting.

## What has to be built

Four components, plus one the original plan omitted:

- **`DesktopDuplicator`** (`Capture`) -- `IDXGIOutputDuplication`,
  `AcquireNextFrame` -> `ID3D11Texture2D`. Also `DisplayEnumerator` for
  picking an output.
- **Color conversion** (`Capture` or `Codec`) -- **this step is missing
  from `ARCHITECTURE.md` entirely.** Desktop Duplication produces BGRA
  (`B8G8R8A8_UNORM`); the H.264 encoder MFT wants NV12. Something must
  convert, and it must stay on the GPU: either `VideoProcessorMFT` or a
  pixel/compute shader. The obvious CPU-side conversion (read back,
  convert, re-upload) silently destroys the zero-copy property that is the
  entire point of this phase's gate. Treat it as a real component, not a
  detail.
- **`HardwareEncoder`** (`Codec`) -- H.264 encoder MFT, D3D11-aware,
  configured for low latency (see below).
- **`HardwareDecoder`** (`Codec`) -- H.264 decoder MFT, output as D3D11
  textures, no CPU readback.
- **`SwapChainPresenter`** (`Render`) -- present decoded textures via a
  D3D11 swap chain. Owns `DXGI_STATUS_OCCLUDED` and `ResizeBuffers`
  handling (`ARCHITECTURE.md` lesson #4).

## Build order

`ARCHITECTURE.md`'s "Next step" lists these capture-first, which is the
wrong order for a go/no-go gate: it puts the one genuinely unknown piece
in the middle, so the gate's answer arrives only after everything else is
built. Capture and swap-chain presentation are well-trodden with plenty of
C# precedent; the Media Foundation pipeline is the actual unknown. Prove
that first.

**Step 0 -- enumerate the MFTs. Written, not yet run on real hardware.**
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

**Step 1 -- codec against a synthetic source.** Feed a hand-made D3D11
texture (solid colour, or a moving rectangle so successive frames differ)
straight into encode -> decode. No Desktop Duplication, no swap chain.
Isolates the risky link with nothing else able to be at fault.

**Step 2 -- real capture in, real presentation out.** Add
`DesktopDuplicator` and `SwapChainPresenter` around a codec that is
already trusted.

## Exit criteria are two questions, not one

`ARCHITECTURE.md` states the gate as sustained 60fps 1080p with zero
CPU-side texture copies. That is two separate claims needing two different
tools, and conflating them is how a gate gets declared passed when it
wasn't:

1. **Is the output correct?** Read one frame back to CPU and write a PNG.
   A readback is fine here -- it is a one-off check, not part of the
   pipeline.
2. **Are there CPU copies?** Only answerable in a GPU debugger (PIX).
   Step 1's readback scaffold would mask the answer, so remove it before
   measuring.

Also required by the gate: that zero-B-frames/IPPP + `MF_LOW_LATENCY`
measurably beats defaults. That is a *comparison*, so keep the default
configuration runnable rather than deleting it once low-latency mode
works.

## Known landmines

**Hardware MFTs are asynchronous.** They cannot be driven with the
synchronous `ProcessInput` -> `ProcessOutput` loop shown in most samples
and tutorials -- that is the software-MFT model. Hardware MFTs need
`MF_TRANSFORM_ASYNC_UNLOCK` and an event-driven loop on
`METransformNeedInput` / `METransformHaveOutput`. Getting this wrong does
not fail cleanly; it hangs or starves. This is the most common way to lose
days on Media Foundation.

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
four.

## Testing notes

Desktop Duplication only produces frames when the screen actually changes,
so **a static desktop will not generate 60fps to measure.** Have video
playing or something animating on the captured output, or the throughput
numbers mean nothing.

None of this is verifiable off a real Windows machine with a real GPU,
which is why it could not be written during scaffolding. The pure-logic
projects (`Protocol`, `Net`) stay independently testable anywhere and
should keep their coverage as they grow.
