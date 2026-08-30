using RemoteControl.Common;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace RemoteControl.Codec;

/// <summary>
/// BGRA -&gt; NV12 on the GPU via the Video Processor MFT (see docs/PHASE-0.md:
/// "this step is missing from ARCHITECTURE.md entirely"). Desktop Duplication
/// (and this phase's synthetic source) produce B8G8R8A8_UNorm; the H.264
/// encoder MFT wants NV12. Reading the BGRA texture back to CPU to convert
/// and re-uploading would silently destroy the zero-copy property this whole
/// phase exists to prove, so this stays D3D11 texture in, D3D11 texture out.
///
/// Driven through <see cref="AsyncTransform"/> like the encoder/decoder --
/// the Video Processor MFT turns out to be a synchronous MFT (no
/// MF_TRANSFORM_ASYNC attribute), and AsyncTransform picks the matching
/// drive loop automatically rather than needing a second hand-rolled one here.
/// </summary>
public sealed class ColorConverter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly AsyncTransform _transform;
    private readonly uint _width;
    private readonly uint _height;

    private readonly BindFlags _outputBindFlags;
    private readonly ResourceUsage _outputUsage;

    /// <param name="outputBindFlags">
    /// BindFlags to create the output NV12 texture with. Must match what the
    /// downstream consumer (the hardware encoder's input stream) actually
    /// requires -- see <see cref="HardwareEncoder.RequiredInputBindFlags"/>.
    /// BindFlags.None fails ProcessInput on that consumer with
    /// MF_E_UNSUPPORTED_D3D_TYPE (confirmed on real hardware, docs/PHASE-0.md).
    /// </param>
    public ColorConverter(
        MfDevice mfDevice, uint width, uint height,
        BindFlags outputBindFlags = BindFlags.RenderTarget, ResourceUsage outputUsage = ResourceUsage.Default,
        ILogger? logger = null)
    {
        var log = logger ?? new ConsoleLogger(nameof(ColorConverter));
        _device = mfDevice.Device;
        _width = width;
        _height = height;
        _outputBindFlags = outputBindFlags;
        _outputUsage = outputUsage;

        var mft = MftFinder.ActivateFirst(
            TransformCategoryGuids.VideoProcessor,
            hardware: false, // Video Processor MFT is not registered under EnumFlagHardware; it's a builtin MFT that uses D3D11/DXVA internally once given a device manager.
            inputType: null,
            outputType: null,
            what: "BGRA->NV12 color conversion (Video Processor MFT)");

        _transform = new AsyncTransform(mft, log);
        log.Info($"Video Processor MFT is {(_transform.IsAsync ? "async" : "synchronous")}.");
        _transform.TrySetD3DManager(mfDevice.DeviceManager);

        ConfigureTypes(mft);
        _transform.BeginStreaming();
    }

    private void ConfigureTypes(IMFTransform mft)
    {
        using var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32); // MFVideoFormat_ARGB32 == DXGI B8G8R8A8_UNorm memory layout.
        MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, _width, _height);
        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        mft.SetInputType(0, inputType, 0);

        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, _width, _height);
        outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        mft.SetOutputType(0, outputType, 0);
    }

    /// <summary>Converts one BGRA texture to a freshly-allocated NV12 texture, GPU-side only.</summary>
    public (ID3D11Texture2D Texture, uint SubresourceIndex) Convert(ID3D11Texture2D bgraTexture, long sampleTime, long sampleDuration)
    {
        using var inputSample = D3DSample.Wrap(bgraTexture, sampleTime, sampleDuration);

        var outputTexture = _device.CreateTexture2D(new Texture2DDescription(
            Format.NV12, _width, _height, arraySize: 1, mipLevels: 1,
            _outputBindFlags, _outputUsage, CpuAccessFlags.None, sampleCount: 1, sampleQuality: 0, ResourceOptionFlags.None));

        // Handing the transform a sample wrapping outputTexture (via
        // allocateOutput) does not guarantee the sample ProcessOutput hands
        // back to onOutput wraps that same texture -- confirmed on real
        // hardware: it silently substitutes its own sample even though this
        // MFT's OutputStreamProvidesSamples is false, leaving outputTexture
        // untouched (all zero) while the real converted data was in the
        // returned sample all along. Extract the texture (and its
        // subresource index -- see DecodedFrame) the callback actually
        // received instead of assuming it's outputTexture at subresource 0.
        ID3D11Texture2D? result = null;
        var resultSubresource = 0u;
        var produced = false;
        _transform.ProcessSample(0, 0, inputSample, output =>
        {
            produced = true;
            using var buffer = output.GetBufferByIndex(0);
            using var dxgiBuffer = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
            if (dxgiBuffer is not null)
            {
                result = new ID3D11Texture2D(dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID));
                resultSubresource = dxgiBuffer.SubresourceIndex;
            }
            else
            {
                result = outputTexture;
            }
        }, allocateOutput: () => D3DSample.Wrap(outputTexture, sampleTime, sampleDuration));

        if (!produced)
            throw new InvalidOperationException("Video Processor MFT produced no output for this input sample.");

        if (result != outputTexture)
            outputTexture.Dispose(); // Not the texture that was actually written; don't leak it.

        return (result!, resultSubresource);
    }

    public void Dispose() => _transform.Dispose();
}
