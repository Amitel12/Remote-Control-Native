using RemoteControl.Common;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace RemoteControl.Codec;

/// <summary>
/// A decoded frame: the D3D11 texture the decoder actually wrote into, and
/// which array slice within it. D3D11-aware decoders commonly manage their
/// output as one texture *array* (a rotating reference-picture pool, 6
/// slices on this hardware/build) rather than one texture per frame --
/// confirmed on real hardware after a readback bug where ignoring this and
/// blindly CopyResource-ing the whole array into a single-slice staging
/// texture silently copied nothing (mismatched array size is a silent
/// no-op, not an error). Every consumer must target
/// <see cref="SubresourceIndex"/> specifically -- via CopySubresourceRegion,
/// never CopyResource -- see docs/PHASE-0.md.
/// </summary>
public readonly record struct DecodedFrame(ID3D11Texture2D Texture, uint SubresourceIndex);

/// <summary>
/// H.264 Annex-B bytes -&gt; D3D11 NV12 texture, via the Microsoft H264 Video
/// Decoder MFT. docs/PHASE-0.md Step 0 found no standalone hardware H.264
/// decoder MFT on the test machine -- hardware decode is reached through
/// this software-*enumerated* MFT via DXVA2/D3D11VA once handed a D3D device
/// manager. Confirming that is real, not assumed, is half of this phase's
/// gate: the constructor checks MF_SA_D3D11_AWARE and throws immediately
/// with a clear diagnostic if it's absent, and <see cref="Decode"/> verifies
/// every output sample is actually D3D11-backed (via IMFDXGIBuffer), not
/// silently falling back to system memory.
/// </summary>
public sealed class HardwareDecoder : IDisposable
{
    private readonly AsyncTransform _transform;
    private long _nextSampleTime;
    private readonly long _sampleDuration;

    public HardwareDecoder(MfDevice mfDevice, uint width, uint height, uint fpsNumerator, uint fpsDenominator, ILogger? logger = null)
    {
        var log = logger ?? new ConsoleLogger(nameof(HardwareDecoder));
        _sampleDuration = (long)(10_000_000L * fpsDenominator / fpsNumerator);

        var mft = MftFinder.ActivateFirst(
            TransformCategoryGuids.VideoDecoder,
            hardware: false, // Confirmed by Step 0: the only H.264 decoder here is software-enumerated; it reaches hardware decode via DXVA once given the device manager below.
            inputType: new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 },
            outputType: null,
            what: "H.264 decode");

        var isD3D11Aware = mft.Attributes.GetUInt32(TransformAttributeKeys.D3D11Aware, out var aware).Success && aware != 0;
        if (!isD3D11Aware)
        {
            mft.Dispose();
            throw new InvalidOperationException(
                "Microsoft H264 Video Decoder MFT does not report MF_SA_D3D11_AWARE on this machine. " +
                "Zero-copy D3D11 decode is not available here -- stopping before building the rest of the " +
                "pipeline on a broken assumption (see docs/PHASE-0.md, Step 0's decoder findings).");
        }
        log.Info("Decoder reports MF_SA_D3D11_AWARE. Verifying it actually returns D3D11 textures on first decoded frame.");

        _transform = new AsyncTransform(mft, log);
        log.Info(_transform.IsAsync
            ? "Decoder reports MF_TRANSFORM_ASYNC -- driving it via the async event loop."
            : "Decoder does not report MF_TRANSFORM_ASYNC -- it is a synchronous MFT despite D3D11/DXVA " +
              "acceleration happening internally. Driving it with the plain ProcessInput/ProcessOutput loop.");
        _transform.TrySetD3DManager(mfDevice.DeviceManager);

        using (var inputType = MediaFactory.MFCreateMediaType())
        {
            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, width, height);
            MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, fpsNumerator, fpsDenominator);
            inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            mft.SetInputType(0, inputType, 0);
        }

        // Output type isn't ours to construct: ask the decoder what it can
        // produce and pick the NV12 one, rather than assuming index/format.
        IMFMediaType? nv12Type = null;
        for (var i = 0; ; i++)
        {
            IMFMediaType candidate;
            try { candidate = mft.GetOutputAvailableType(0, i); }
            catch { break; }

            if (candidate.GetGUID(MediaTypeAttributeKeys.Subtype) == VideoFormatGuids.NV12)
            {
                nv12Type = candidate;
                break;
            }
            candidate.Dispose();
        }

        if (nv12Type is null)
            throw new InvalidOperationException("H.264 decoder MFT offered no NV12 output type.");

        using (nv12Type)
        {
            mft.SetOutputType(0, nv12Type, 0);
        }

        _transform.BeginStreaming();
        log.Info($"Hardware H.264 decoder ready ({width}x{height}).");
    }

    /// <summary>Decodes one Annex-B access unit. Calls <paramref name="onOutput"/> for every decoded frame produced (usually 0 or 1 per call, due to decode pipelining).</summary>
    public void Decode(byte[] annexBUnit, Action<DecodedFrame> onOutput)
    {
        var sampleTime = _nextSampleTime;
        _nextSampleTime += _sampleDuration;

        using var sample = MediaFactory.MFCreateSample();
        using (var buffer = MediaFactory.MFCreateMemoryBuffer(annexBUnit.Length))
        {
            buffer.Lock(out var ptr, out _, out _);
            System.Runtime.InteropServices.Marshal.Copy(annexBUnit, 0, ptr, annexBUnit.Length);
            buffer.Unlock();
            buffer.CurrentLength = annexBUnit.Length;
            sample.AddBuffer(buffer);
        }
        sample.SampleTime = sampleTime;
        sample.SampleDuration = _sampleDuration;

        _transform.ProcessSample(0, 0, sample, output => onOutput(ExtractDecodedFrame(output)));
    }

    public void Drain(Action<DecodedFrame> onOutput) => _transform.Drain(0, output => onOutput(ExtractDecodedFrame(output)));

    private static DecodedFrame ExtractDecodedFrame(IMFSample sample)
    {
        using var buffer = sample.GetBufferByIndex(0);
        var dxgiBuffer = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
        if (dxgiBuffer is null)
        {
            throw new InvalidOperationException(
                "Decoded sample's buffer does not implement IMFDXGIBuffer -- the decoder produced a " +
                "system-memory buffer instead of a D3D11 texture despite MF_SA_D3D11_AWARE. Zero-copy decode " +
                "is not actually happening; see docs/PHASE-0.md.");
        }

        using (dxgiBuffer)
        {
            var texturePtr = dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID);
            return new DecodedFrame(new ID3D11Texture2D(texturePtr), dxgiBuffer.SubresourceIndex);
        }
    }

    public void Dispose() => _transform.Dispose();
}
