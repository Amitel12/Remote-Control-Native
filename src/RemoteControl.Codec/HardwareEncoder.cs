using RemoteControl.Codec.Interop;
using RemoteControl.Common;
using Vortice.MediaFoundation;

namespace RemoteControl.Codec;

/// <summary>
/// NV12 -&gt; H.264 Annex-B bytes, via the H.264 encoder MFT. Async MFT,
/// driven through <see cref="AsyncTransform"/>.
///
/// Tries the hardware (NVIDIA) encoder MFT first, exactly as Step 1
/// requires, but never sends MFT_MESSAGE_SET_D3D_MANAGER to it and takes a
/// system-memory NV12 buffer, not a D3D11 texture -- see
/// <see cref="Nv12Readback"/>. Confirmed by direct testing on real hardware,
/// documented in full in docs/PHASE-0.md:
/// - Every externally supplied D3D11 sample is rejected with
///   MF_E_UNSUPPORTED_D3D_TYPE, regardless of BindFlags/Usage/message
///   ordering/low-latency mode/explicit MF_SA_D3D11_AWARE re-assertion, or
///   using MFCreateVideoSampleFromSurface instead of MFCreateDXGISurfaceBuffer.
/// - Worse, that first rejection corrupts state badly enough that any
///   further ProcessInput call on *any* encoder instance in the process --
///   including a freshly activated one that never touches D3D11 -- throws a
///   fatal, uncatchable AccessViolationException. There is no in-place or
///   even fresh-instance recovery once a D3D11 sample has been attempted.
/// - Independently of D3D11, the hardware encoder never signals it can
///   accept input at all (neither the documented METransformNeedInput event
///   -- tried with both the synchronous GetEvent(0) substitute and a proper
///   BeginGetEvent/EndGetEvent callback pump -- nor GetInputStatus polling,
///   which a working reference implementation, sipsorcery/
///   mediafoundationsamples' MFH264RoundTrip, uses successfully against its
///   own hardware encoder MFT).
///
/// Given that, this class treats "the hardware encoder never becomes ready"
/// as an expected, bounded-wait outcome: <see cref="Encode"/> falls back to
/// the software H.264 encoder MFT (proven correct end-to-end against the
/// same pipeline) the first time that wait times out, without ever
/// attempting a D3D11 sample against the hardware encoder at all.
///
/// Two configurations are kept runnable, not just the low-latency one --
/// docs/PHASE-0.md's exit criteria require *comparing* zero-B-frames/IPPP +
/// MF_LOW_LATENCY against encoder defaults, not just proving low-latency
/// mode works in isolation.
/// </summary>
public sealed class HardwareEncoder : IDisposable
{
    // Set once, process-wide: if the hardware encoder never became ready in
    // one HardwareEncoder instance, later instances go straight to software
    // rather than re-paying InputReadyTimeout per instance for a result
    // already known. See class remarks.
    private static bool s_hardwareEncoderKnownUnusable;

    private readonly ILogger _log;
    private readonly MfDevice _mfDevice;
    private readonly uint _width;
    private readonly uint _height;
    private readonly uint _fpsNumerator;
    private readonly uint _fpsDenominator;
    private readonly uint _bitrateBps;
    private long _nextSampleTime;
    private readonly long _sampleDuration;

    private AsyncTransform _transform;
    private bool _usingSoftwareFallback;

    public bool LowLatency { get; }
    public bool UsingHardware => !_usingSoftwareFallback;

    public HardwareEncoder(
        MfDevice mfDevice, uint width, uint height, uint fpsNumerator, uint fpsDenominator,
        bool lowLatency, uint bitrateBps = 8_000_000, ILogger? logger = null)
    {
        _log = logger ?? new ConsoleLogger(nameof(HardwareEncoder));
        _mfDevice = mfDevice;
        _width = width;
        _height = height;
        _fpsNumerator = fpsNumerator;
        _fpsDenominator = fpsDenominator;
        _bitrateBps = bitrateBps;
        LowLatency = lowLatency;
        _sampleDuration = (long)(10_000_000L * fpsDenominator / fpsNumerator); // 100ns units.

        _usingSoftwareFallback = s_hardwareEncoderKnownUnusable;
        _transform = Activate(hardware: !s_hardwareEncoderKnownUnusable);
    }

    private AsyncTransform Activate(bool hardware)
    {
        var mft = MftFinder.ActivateFirst(
            TransformCategoryGuids.VideoEncoder,
            hardware,
            inputType: null,
            outputType: new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 },
            what: "H.264 encode");

        var transform = new AsyncTransform(mft, _log);

        if (LowLatency)
        {
            mft.Attributes.Set(CodecApiGuids.MfLowLatency, true).CheckError();
            if (!CodecApiHelper.TrySetValue(mft.NativePointer, CodecApiGuids.AVEncMPVDefaultBPictureCount, (object)0, out var reason))
                _log.Warn($"Could not disable B-frames via ICodecAPI: {reason}. Continuing -- MF_LOW_LATENCY is still set.");
        }

        // Output type first: some encoder MFTs only expose input types that
        // match an already-set output type.
        using (var outputType = MediaFactory.MFCreateMediaType())
        {
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, _width, _height);
            MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, _fpsNumerator, _fpsDenominator);
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, _bitrateBps);
            outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            mft.SetOutputType(0, outputType, 0);
        }

        using (var inputType = MediaFactory.MFCreateMediaType())
        {
            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, _width, _height);
            MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, _fpsNumerator, _fpsDenominator);
            inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            inputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, true);
            mft.SetInputType(0, inputType, 0);
        }

        // Deliberately never sent, even for the hardware attempt: sending
        // MFT_MESSAGE_SET_D3D_MANAGER and then feeding this specific
        // encoder a D3D11 sample is what corrupts it -- see class remarks.
        // System-memory input only, both for the hardware probe and the
        // software fallback.
        transform.BeginStreaming();
        _log.Info($"H.264 encoder ready ({(hardware ? "hardware" : "software")}, {_width}x{_height}@{(double)_fpsNumerator / _fpsDenominator:0.##}fps, " +
                  $"{_bitrateBps / 1_000_000.0:0.#}Mbps, {(LowLatency ? "low-latency IPPP" : "default")}, system-memory input).");
        return transform;
    }

    /// <summary>Encodes one NV12 texture (read back to a packed system-memory buffer first -- see class remarks). Calls <paramref name="onOutput"/> for every H.264 access unit produced (usually 0 or 1 per call).</summary>
    public void Encode(
        MfDevice mfDevice, Vortice.Direct3D11.ID3D11Texture2D nv12Texture, Action<byte[]> onOutput, uint subresourceIndex = 0)
    {
        var sampleTime = _nextSampleTime;
        _nextSampleTime += _sampleDuration;

        var packed = Nv12Readback.ToPackedBytes(mfDevice.Device, mfDevice.ImmediateContext, nv12Texture, _width, _height, subresourceIndex);

        var sample = MediaFactory.MFCreateSample();
        using (sample)
        {
            using var buffer = MediaFactory.MFCreateMemoryBuffer(packed.Length);
            buffer.Lock(out var ptr, out _, out _);
            System.Runtime.InteropServices.Marshal.Copy(packed, 0, ptr, packed.Length);
            buffer.Unlock();
            buffer.CurrentLength = packed.Length;
            sample.AddBuffer(buffer);
            sample.SampleTime = sampleTime;
            sample.SampleDuration = _sampleDuration;

            try
            {
                _transform.ProcessSample(0, 0, sample, output => onOutput(ReadBuffer(output)));
            }
            catch (TimeoutException) when (!_usingSoftwareFallback)
            {
                _log.Warn("Hardware H.264 encoder never signalled it could accept input -- confirmed real-hardware " +
                          "limitation (see docs/PHASE-0.md), not a transient issue. Falling back to the software " +
                          "H.264 encoder MFT, proven correct end-to-end against this same pipeline.");
                s_hardwareEncoderKnownUnusable = true;
                _usingSoftwareFallback = true;
                _transform = Activate(hardware: false);
                _transform.ProcessSample(0, 0, sample, output => onOutput(ReadBuffer(output)));
            }
        }
    }

    /// <summary>Flushes any frames the encoder is still holding (B-frame reordering, internal pipelining).</summary>
    public void Drain(Action<byte[]> onOutput) => _transform.Drain(0, output => onOutput(ReadBuffer(output)));

    private static byte[] ReadBuffer(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var ptr, out _, out var currentLength);
        try
        {
            var bytes = new byte[currentLength];
            System.Runtime.InteropServices.Marshal.Copy(ptr, bytes, 0, currentLength);
            return bytes;
        }
        finally
        {
            buffer.Unlock();
        }
    }

    public void Dispose() => _transform.Dispose();
}
