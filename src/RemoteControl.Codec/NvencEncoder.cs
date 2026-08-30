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

    public bool LowLatency { get; }
    public bool UsingHardware => true;

    public unsafe NvencEncoder(
        MfDevice mfDevice, uint width, uint height, uint fpsNumerator, uint fpsDenominator,
        bool lowLatency, uint bitrateBps = 8_000_000, ILogger? logger = null)
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

        var initializeStatus = LibNvEnc.TryInitialize(out var failure);
        if (initializeStatus != LibNcEncInitializeStatus.Success)
        {
            throw new NotSupportedException(
                $"NVIDIA NVENCODE API initialization failed ({initializeStatus}): {failure}");
        }

        try
        {
            _encoder = LibNvEnc.OpenEncoderForDirectX(mfDevice.Device.NativePointer);

            var tuning = lowLatency ? NvEncTuningInfo.UltraLowLatency : NvEncTuningInfo.HighQuality;
            var preset = lowLatency ? NvEncPresetGuids.P1 : NvEncPresetGuids.P4;
            var config = _encoder.GetEncodePresetConfigEx(NvEncCodecGuids.H264, preset, tuning).PresetCfg;

            // IPPP only: FrameIntervalP=1 disables B-frames. A short periodic
            // IDR interval bounds recovery time after packet loss, and SPS/PPS
            // are repeated at each IDR so a receiver can join mid-stream.
            var gopLength = Math.Max(1u, fpsNumerator * 2 / fpsDenominator);
            config.ProfileGuid = NvEncProfileGuids.H264High;
            config.GopLength = gopLength;
            config.FrameIntervalP = 1;
            config.FrameFieldMode = NvEncParamsFrameFieldMode.Frame;

            var rc = config.RcParams;
            rc.RateControlMode = NvEncParamsRcMode.Cbr;
            rc.AverageBitRate = bitrateBps;
            rc.MaxBitRate = bitrateBps;
            rc.EnableLookahead = false;
            rc.ZeroReorderDelay = lowLatency;
            if (lowLatency)
            {
                var oneFrameBits = (uint)Math.Max(1UL, (ulong)bitrateBps * fpsDenominator / fpsNumerator);
                rc.VbvBufferSize = oneFrameBits;
                rc.VbvInitialDelay = oneFrameBits;
            }
            config.RcParams = rc;

            var codecConfig = config.EncodeCodecConfig;
            var h264 = codecConfig.H264Config;
            h264.IdrPeriod = gopLength;
            h264.RepeatSPSPPS = true;
            h264.OutputAUD = true;
            h264.ChromaFormatIDC = 1;
            codecConfig.H264Config = h264;
            config.EncodeCodecConfig = codecConfig;

            var configPointer = &config;
            var initialize = new NvEncInitializeParams
            {
                Version = NV_ENC_INITIALIZE_PARAMS_VER,
                EncodeGuid = NvEncCodecGuids.H264,
                PresetGuid = preset,
                EncodeWidth = width,
                EncodeHeight = height,
                MaxEncodeWidth = width,
                MaxEncodeHeight = height,
                DarWidth = width,
                DarHeight = height,
                FrameRateNum = fpsNumerator,
                FrameRateDen = fpsDenominator,
                EnableEncodeAsync = 0,
                EnablePTD = 1,
                EncodeConfig = configPointer,
                TuningInfo = tuning,
            };

            _encoder.InitializeEncoder(ref initialize);
            _bitstreamBuffer = _encoder.CreateBitstreamBuffer();

            _log.Info(
                $"Native NVENC H.264 encoder ready ({width}x{height}@{(double)fpsNumerator / fpsDenominator:0.##}fps, " +
                $"{bitrateBps / 1_000_000.0:0.#}Mbps CBR, {(lowLatency ? "P1 ultra-low-latency IPPP" : "P4 high-quality IPPP")}, " +
                "D3D11 NV12 input).");
        }
        catch
        {
            Dispose();
            throw;
        }
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
