using Lennox.NvEncSharp;
using RemoteControl.Common;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Lennox.NvEncSharp.LibNvEnc;

namespace RemoteControl.Codec;

/// <summary>
/// GPU-resident NV12 -&gt; H.264 Annex-B encoding through NVIDIA's native
/// NVENCODE API. This bypasses the NVIDIA Media Foundation encoder MFT,
/// whose input path is confirmed unusable on the Phase 0 RTX 3070 machine
/// (see docs/PHASE-0.md), while keeping the same D3D11 device and the
/// already-proven Media Foundation decoder.
/// </summary>
public sealed class NvencEncoder : IDisposable
{
    private readonly ILogger _log;
    private readonly uint _width;
    private readonly uint _height;
    private readonly uint _fpsNumerator;
    private readonly uint _fpsDenominator;
    private readonly ulong _sampleDuration;
    private NvEncoder _encoder;
    private NvEncCreateBitstreamBuffer _bitstreamBuffer;
    private ulong _nextTimestamp;
    private uint _frameIndex;
    private bool _disposed;
    private uint _currentBitrateBps;
    private readonly bool _intraRefresh;

    public bool LowLatency { get; }
    public bool UsingHardware => true;
    public uint CurrentBitrateBps => _currentBitrateBps;

    public unsafe NvencEncoder(
        MfDevice mfDevice, uint width, uint height, uint fpsNumerator, uint fpsDenominator,
        bool lowLatency, uint bitrateBps = 8_000_000, bool intraRefresh = false, ILogger? logger = null)
    {
        if (width == 0 || height == 0 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "NV12 encoding requires non-zero even dimensions.");
        if (fpsNumerator == 0 || fpsDenominator == 0)
            throw new ArgumentOutOfRangeException(nameof(fpsNumerator), "Frame-rate numerator and denominator must be non-zero.");

        _log = logger ?? new ConsoleLogger(nameof(NvencEncoder));
        _width = width;
        _height = height;
        _fpsNumerator = fpsNumerator;
        _fpsDenominator = fpsDenominator;
        _sampleDuration = 10_000_000UL * fpsDenominator / fpsNumerator;
        LowLatency = lowLatency;
        _intraRefresh = intraRefresh;

        var initializeStatus = LibNvEnc.TryInitialize(out var failure);
        if (initializeStatus != LibNcEncInitializeStatus.Success)
        {
            throw new NotSupportedException(
                $"NVIDIA NVENCODE API initialization failed ({initializeStatus}): {failure}");
        }

        try
        {
            _encoder = LibNvEnc.OpenEncoderForDirectX(mfDevice.Device.NativePointer);

            var config = BuildEncodeConfig(bitrateBps);
            var configPointer = &config;
            var initialize = BuildInitializeParams(configPointer);

            _encoder.InitializeEncoder(ref initialize);
            _bitstreamBuffer = _encoder.CreateBitstreamBuffer();
            _currentBitrateBps = bitrateBps;

            _log.Info(
                $"Native NVENC H.264 encoder ready ({width}x{height}@{(double)fpsNumerator / fpsDenominator:0.##}fps, " +
                $"{bitrateBps / 1_000_000.0:0.#}Mbps CBR, {(lowLatency ? "P1 ultra-low-latency IPPP" : "P4 high-quality IPPP")}, " +
                $"{(intraRefresh ? "continuous intra-refresh (no periodic full IDR)" : "periodic full IDR")}, " +
                "D3D11 NV12 input).");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private unsafe NvEncConfig BuildEncodeConfig(uint bitrateBps)
    {
        var tuning = LowLatency ? NvEncTuningInfo.UltraLowLatency : NvEncTuningInfo.HighQuality;
        var preset = LowLatency ? NvEncPresetGuids.P1 : NvEncPresetGuids.P4;
        var config = _encoder.GetEncodePresetConfigEx(NvEncCodecGuids.H264, preset, tuning).PresetCfg;

        // IPPP only: FrameIntervalP=1 disables B-frames. A short periodic
        // IDR interval bounds recovery time after packet loss, and SPS/PPS
        // are repeated at each IDR so a receiver can join mid-stream.
        var gopLength = Math.Max(1u, _fpsNumerator * 2 / _fpsDenominator);
        config.ProfileGuid = NvEncProfileGuids.H264High;
        config.GopLength = gopLength;
        config.FrameIntervalP = 1;
        config.FrameFieldMode = NvEncParamsFrameFieldMode.Frame;

        var rc = config.RcParams;
        rc.RateControlMode = NvEncParamsRcMode.Cbr;
        rc.AverageBitRate = bitrateBps;
        rc.MaxBitRate = bitrateBps;
        rc.EnableLookahead = false;
        rc.ZeroReorderDelay = LowLatency;
        if (LowLatency)
        {
            var oneFrameBits = (uint)Math.Max(1UL, (ulong)bitrateBps * _fpsDenominator / _fpsNumerator);
            rc.VbvBufferSize = oneFrameBits;
            rc.VbvInitialDelay = oneFrameBits;
        }
        config.RcParams = rc;

        var codecConfig = config.EncodeCodecConfig;
        var h264 = codecConfig.H264Config;
        h264.RepeatSPSPPS = true;
        h264.OutputAUD = true;
        h264.ChromaFormatIDC = 1;
        if (_intraRefresh)
        {
            // Periodic full IDR frames are much larger than a regular P-frame even under a
            // tight VBV (docs/PHASE-4.md "avoid keyframe bitrate spikes") -- continuous intra
            // refresh spreads that same recovery-point guarantee (every macroblock gets
            // refreshed once per IntraRefreshPeriod) evenly across every frame instead of
            // bursting it into one, at the cost of a receiver needing up to one full period
            // to become fully clean after joining mid-stream (not a real cost here -- this
            // app always starts a session at the first frame, never mid-GOP).
            // NVENC_INFINITE_GOPLENGTH (0xFFFFFFFF) -- no named constant in this wrapper --
            // turns off NVENC's own periodic IDR/GOP boundary so intra-refresh is the only
            // recovery mechanism instead of the two overlapping.
            config.GopLength = uint.MaxValue;
            h264.IdrPeriod = uint.MaxValue;
            h264.EnableIntraRefresh = true;
            h264.IntraRefreshPeriod = gopLength;
            h264.IntraRefreshCnt = gopLength;
        }
        else
        {
            h264.IdrPeriod = gopLength;
        }
        codecConfig.H264Config = h264;
        config.EncodeCodecConfig = codecConfig;

        return config;
    }

    private unsafe NvEncInitializeParams BuildInitializeParams(NvEncConfig* configPointer) => new()
    {
        Version = NV_ENC_INITIALIZE_PARAMS_VER,
        EncodeGuid = NvEncCodecGuids.H264,
        PresetGuid = LowLatency ? NvEncPresetGuids.P1 : NvEncPresetGuids.P4,
        EncodeWidth = _width,
        EncodeHeight = _height,
        MaxEncodeWidth = _width,
        MaxEncodeHeight = _height,
        DarWidth = _width,
        DarHeight = _height,
        FrameRateNum = _fpsNumerator,
        FrameRateDen = _fpsDenominator,
        EnableEncodeAsync = 0,
        EnablePTD = 1,
        EncodeConfig = configPointer,
        TuningInfo = LowLatency ? NvEncTuningInfo.UltraLowLatency : NvEncTuningInfo.HighQuality,
    };

    /// <summary>
    /// Live bitrate change via NVENC's own reconfigure path -- cheap, no
    /// dropped reference frames/forced IDR (<c>ResetEncoder</c>/<c>ForceIDR</c>
    /// both left false), unlike tearing down and recreating the encoder.
    /// The feed for RemoteControl.Net.Congestion.CongestionController
    /// (docs/PHASE-4.md) -- degrading quality under constrained bandwidth
    /// needs to change the running encoder's output, not just future ones.
    /// </summary>
    public unsafe void SetBitrate(uint bitrateBps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bitrateBps == _currentBitrateBps)
            return;

        var config = BuildEncodeConfig(bitrateBps);
        var configPointer = &config;
        var initialize = BuildInitializeParams(configPointer);
        var reconfigure = new NvEncReconfigureParams
        {
            Version = NV_ENC_RECONFIGURE_PARAMS_VER,
            ReInitEncodeParams = initialize,
            ResetEncoder = false,
            ForceIDR = false,
        };

        _encoder.ReconfigureEncoder(ref reconfigure);
        _log.Info($"NVENC bitrate reconfigured: {_currentBitrateBps / 1_000_000.0:0.##}Mbps -> {bitrateBps / 1_000_000.0:0.##}Mbps.");
        _currentBitrateBps = bitrateBps;
    }

    /// <summary>
    /// Encodes one D3D11 NV12 texture slice. Pixel data remains GPU-resident:
    /// the exact texture and subresource are registered with NVENC, mapped for
    /// the encode, and unregistered only after synchronous bitstream readback.
    /// </summary>
    public void Encode(ID3D11Texture2D nv12Texture, Action<byte[]> onOutput, uint subresourceIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var description = nv12Texture.Description;
        if (description.Format != Format.NV12)
            throw new ArgumentException($"NVENC input must be NV12, not {description.Format}.", nameof(nv12Texture));
        if (description.Width < _width || description.Height < _height)
            throw new ArgumentException(
                $"NVENC input is {description.Width}x{description.Height}; configured size is {_width}x{_height}.",
                nameof(nv12Texture));
        if (subresourceIndex >= description.ArraySize * description.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(subresourceIndex));

        var registration = new NvEncRegisterResource
        {
            Version = NV_ENC_REGISTER_RESOURCE_VER,
            ResourceType = NvEncInputResourceType.Directx,
            Width = _width,
            Height = _height,
            Pitch = 0,
            SubResourceIndex = subresourceIndex,
            ResourceToRegister = nv12Texture.NativePointer,
            BufferFormat = NvEncBufferFormat.Nv12,
            BufferUsage = NvEncBufferUsage.NvEncInputImage,
        };

        using var registered = _encoder.RegisterResource(ref registration);
        var mapped = new NvEncMapInputResource
        {
            Version = NV_ENC_MAP_INPUT_RESOURCE_VER,
            RegisteredResource = registration.RegisteredResource,
        };

        _encoder.MapInputResource(ref mapped);
        try
        {
            var picture = new NvEncPicParams
            {
                Version = NV_ENC_PIC_PARAMS_VER,
                InputWidth = _width,
                InputHeight = _height,
                InputPitch = _width,
                FrameIdx = _frameIndex++,
                InputTimeStamp = _nextTimestamp,
                InputDuration = _sampleDuration,
                InputBuffer = mapped.MappedResource,
                OutputBitstream = _bitstreamBuffer.BitstreamBuffer,
                BufferFmt = mapped.MappedBufferFmt,
                PictureStruct = NvEncPicStruct.Frame,
            };
            _nextTimestamp += _sampleDuration;

            _encoder.EncodePicture(ref picture);
            using var bitstream = _encoder.LockBitstreamAndCreateStream(ref _bitstreamBuffer);
            using var output = new MemoryStream(checked((int)bitstream.Length));
            bitstream.CopyTo(output);
            onOutput(output.ToArray());
        }
        finally
        {
            _encoder.UnmapInputResource(mapped.MappedResource);
        }
    }

    /// <summary>
    /// With FrameIntervalP=1 and synchronous submission, no reordered frame is
    /// retained after Encode returns. Kept for parity with HardwareEncoder.
    /// </summary>
    public void Drain(Action<byte[]> onOutput)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onOutput);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_encoder.Handle == IntPtr.Zero)
            return;

        try
        {
            if (_bitstreamBuffer.BitstreamBuffer.Handle != IntPtr.Zero)
                _encoder.DestroyBitstreamBuffer(_bitstreamBuffer.BitstreamBuffer);
        }
        finally
        {
            _encoder.DestroyEncoder();
            _encoder = default;
            _bitstreamBuffer = default;
        }
    }
}
