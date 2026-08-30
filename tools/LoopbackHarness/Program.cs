using System.Diagnostics;
using RemoteControl.Codec;
using RemoteControl.Common;
using Vortice.Direct3D11;

namespace RemoteControl.Tools.LoopbackHarness;

/// <summary>
/// Phase 0 entry point (see docs/PHASE-0.md): eventually wires
/// RemoteControl.Capture -> RemoteControl.Codec (encode) -> RemoteControl.Codec
/// (decode) -> RemoteControl.Render on one machine, no networking, and measures
/// whether the pipeline sustains 60fps 1080p with zero CPU-side texture copies.
///
/// Step 0 (MFT probe) and Step 1 (codec against a synthetic D3D11 texture,
/// isolated from capture/render) are implemented. DesktopDuplicator and
/// SwapChainPresenter are Step 2.
/// </summary>
internal static class Program
{
    private const uint Width = 1920;
    private const uint Height = 1080;
    private const uint FpsNumerator = 30;
    private const uint FpsDenominator = 1;
    private const int FrameCount = 90; // 3s at 30fps -- enough to warm up and get a stable latency average.

    private static int Main(string[] args)
    {
        var logger = new ConsoleLogger("LoopbackHarness");

        if (!RunStep0(logger))
            return 2;

        // MftProbe.Enumerate (Step 0) pairs its own MFStartup with an
        // unconditional MFShutdown in a finally block -- correct for Step 0
        // used standalone, but it leaves Media Foundation's DXVA/hardware
        // subsystem torn down for Step 1, which runs in the same process
        // immediately after. Every basic MF call (enumeration, type
        // negotiation, attribute get/set) kept working anyway, which is what
        // made this so hard to isolate -- only starting a real hardware
        // encode session actually needed the subsystem, and failed with the
        // misleading MF_E_UNSUPPORTED_D3D_TYPE ("input type is not
        // supported for D3D device") regardless of what the input sample
        // actually was. See docs/PHASE-0.md.
        Vortice.MediaFoundation.MediaFactory.MFStartup(false).CheckError();
        try
        {
            RunStep1(logger, verifyFrame: !args.Contains("--no-verify-frame"));
        }
        catch (Exception ex)
        {
            logger.Error("Step 1 failed.", ex);
            return 1;
        }
        finally
        {
            Vortice.MediaFoundation.MediaFactory.MFShutdown();
        }

        return 0;
    }

    private static bool RunStep0(ILogger logger)
    {
        logger.Info("Phase 0 / Step 0 -- probing Media Foundation transforms.");
        logger.Info("See docs/PHASE-0.md for the build order, exit criteria and known landmines.");
        Console.WriteLine();

        IReadOnlyList<MftProbe.MftInfo> transforms;
        try
        {
            transforms = MftProbe.Enumerate(logger);
        }
        catch (Exception ex)
        {
            logger.Error("MFT probe failed outright. Media Foundation may be unavailable on this machine.", ex);
            return false;
        }

        if (transforms.Count == 0)
        {
            logger.Error("No video transforms found at all, hardware or software.");
            logger.Error("That is a genuine blocker for Phase 0 -- do not build the pipeline on this machine.");
            return false;
        }

        // "264" marks a name that looks like H.264. It is a hint for the reader,
        // not a load-bearing check -- Step 1 pins the format via IMFMediaType.
        Console.WriteLine($"  {"CATEGORY",-16} {"TYPE",-4} {"H264?",-6} NAME");
        Console.WriteLine($"  {new string('-', 16)} {new string('-', 4)} {new string('-', 6)} {new string('-', 40)}");
        foreach (var t in transforms)
        {
            Console.WriteLine($"  {t.Category,-16} {(t.IsHardware ? "HW" : "SW"),-4} " +
                              $"{(t.LooksLikeH264 ? "yes" : ""),-6} {t.FriendlyName}");
        }
        Console.WriteLine();

        var hwH264Encoders = transforms.Count(t => t.IsHardware && t.Category == "VideoEncoder" && t.LooksLikeH264);
        var hwH264Decoders = transforms.Count(t => t.IsHardware && t.Category == "VideoDecoder" && t.LooksLikeH264);
        var swH264Decoders = transforms.Count(t => !t.IsHardware && t.Category == "VideoDecoder" && t.LooksLikeH264);

        logger.Info($"Hardware H.264 encoders: {hwH264Encoders}");
        logger.Info($"Hardware H.264 decoders: {hwH264Decoders}");
        logger.Info($"Software H.264 decoders: {swH264Decoders}");
        Console.WriteLine();

        // The verdict deliberately keys on H.264 specifically, not on "any
        // hardware transform". A machine can expose a hardware MJPEG or HEVC
        // decoder and no hardware H.264 one at all, and counting those as a
        // pass is precisely the false green a go/no-go gate must never give.
        if (hwH264Encoders == 0)
        {
            logger.Warn("NO-GO on the encode side -- no hardware H.264 encoder MFT.");
            logger.Warn("Software encoding cannot meet Phase 0's latency goal. See docs/PHASE-0.md.");
            return false;
        }

        if (hwH264Decoders > 0)
        {
            logger.Info("PASS -- hardware H.264 encoder and decoder MFTs both present.");
            logger.Info("Step 1 (codec against a synthetic texture) is unblocked. This proves");
            logger.Info("presence, not that either transform can actually be driven.");
            return true;
        }

        if (swH264Decoders > 0)
        {
            // Expected on Windows rather than a fault: GPU vendors generally do
            // not ship a standalone hardware H.264 *decoder* MFT. Hardware
            // decode is reached through the Microsoft H264 Video Decoder MFT,
            // which uses DXVA2/D3D11VA internally once it is handed a D3D
            // device manager via MFT_MESSAGE_SET_D3D_MANAGER.
            logger.Info("PARTIAL -- hardware H.264 encoder present; no hardware H.264 decoder MFT.");
            logger.Info("That is normal on Windows. Vendors rarely expose a standalone hardware H.264");
            logger.Info("decoder; hardware decode goes through the Microsoft H264 Video Decoder MFT");
            logger.Info("with DXVA2/D3D11VA, enabled by MFT_MESSAGE_SET_D3D_MANAGER.");
            logger.Info("");
            logger.Info("Step 1 must therefore confirm that decoder reports MF_SA_D3D11_AWARE and");
            logger.Info("really returns D3D11 textures. Until then zero-copy decode is unproven.");
            return true;
        }

        logger.Warn("NO-GO on the decode side -- no H.264 decoder at all, hardware or software.");
        return false;
    }

    private static void RunStep1(ILogger logger, bool verifyFrame)
    {
        Console.WriteLine();
        logger.Info("Phase 0 / Step 1 -- codec against a synthetic D3D11 texture (no capture, no swap chain).");
        Console.WriteLine();

        using var mfDevice = MfDevice.Create(logger);
        using var source = new SyntheticSource(mfDevice.Device, mfDevice.ImmediateContext, Width, Height);

        RunConfiguration(logger, mfDevice, source, lowLatency: true, verifyFrame);
        RunConfiguration(logger, mfDevice, source, lowLatency: false, verifyFrame: false);
    }

    private static void RunConfiguration(ILogger logger, MfDevice mfDevice, SyntheticSource source, bool lowLatency, bool verifyFrame)
    {
        Console.WriteLine();
        logger.Info($"--- Configuration: {(lowLatency ? "low-latency (IPPP, MF_LOW_LATENCY)" : "encoder defaults")} ---");

        using var encoder = new HardwareEncoder(mfDevice, Width, Height, FpsNumerator, FpsDenominator, lowLatency, logger: logger);
        using var colorConverter = new ColorConverter(mfDevice, Width, Height, logger: logger);
        using var decoder = new HardwareDecoder(mfDevice, Width, Height, FpsNumerator, FpsDenominator, logger);

        var encodeLatenciesMs = new List<double>();
        var decodeLatenciesMs = new List<double>();
        var compressedSizes = new List<int>();
        var framesDecoded = 0;
        var savedVerificationFrame = false;
        var sw = new Stopwatch();

        for (var i = 0; i < FrameCount; i++)
        {
            using var bgra = source.NextFrame();
            var (nv12, nv12Subresource) = colorConverter.Convert(
                bgra, i * 10_000_000L * FpsDenominator / FpsNumerator, 10_000_000L * FpsDenominator / FpsNumerator);
            using var nv12Owned = nv12;

            sw.Restart();
            encoder.Encode(mfDevice, nv12, encoded =>
            {
                sw.Stop();
                encodeLatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
                compressedSizes.Add(encoded.Length);

                var decodeSw = Stopwatch.StartNew();
                decoder.Decode(encoded, decoded =>
                {
                    decodeSw.Stop();
                    decodeLatenciesMs.Add(decodeSw.Elapsed.TotalMilliseconds);
                    framesDecoded++;

                    if (verifyFrame && !savedVerificationFrame)
                    {
                        savedVerificationFrame = true;
                        var path = Path.Combine(AppContext.BaseDirectory, "step1-verify-frame.png");
                        FrameVerifier.SaveNv12FrameAsPng(
                            mfDevice.Device, mfDevice.ImmediateContext, decoded.Texture, path, decoded.SubresourceIndex);
                        logger.Info($"Wrote verification frame: {path}");
                    }

                    decoded.Texture.Dispose();
                });
            }, nv12Subresource);
        }

        encoder.Drain(encoded =>
        {
            compressedSizes.Add(encoded.Length);
            decoder.Decode(encoded, decoded => { framesDecoded++; decoded.Texture.Dispose(); });
        });
        decoder.Drain(decoded => { framesDecoded++; decoded.Texture.Dispose(); });

        Report(logger, lowLatency, encoder.UsingHardware, encodeLatenciesMs, decodeLatenciesMs, compressedSizes, framesDecoded);
    }

    private static void Report(
        ILogger logger, bool lowLatency, bool usingHardwareEncoder,
        List<double> encodeLatenciesMs, List<double> decodeLatenciesMs, List<int> compressedSizes, int framesDecoded)
    {
        // Skip the first few frames: encoder/decoder pipelining means early
        // calls measure setup cost, not steady-state per-frame latency.
        const int warmup = 5;
        var steadyEncode = encodeLatenciesMs.Skip(warmup).ToList();
        var steadyDecode = decodeLatenciesMs.Skip(warmup).ToList();

        logger.Info($"[{(lowLatency ? "low-latency" : "defaults")}] encoder: {(usingHardwareEncoder ? "HARDWARE" : "SOFTWARE FALLBACK -- hardware encoder never accepted input, see docs/PHASE-0.md")}");
        logger.Info($"[{(lowLatency ? "low-latency" : "defaults")}] frames encoded: {encodeLatenciesMs.Count}, decoded: {framesDecoded}");
        if (steadyEncode.Count > 0)
            logger.Info($"[{(lowLatency ? "low-latency" : "defaults")}] encode latency avg={steadyEncode.Average():0.###}ms " +
                        $"min={steadyEncode.Min():0.###}ms max={steadyEncode.Max():0.###}ms (n={steadyEncode.Count}, warmup skipped)");
        if (steadyDecode.Count > 0)
            logger.Info($"[{(lowLatency ? "low-latency" : "defaults")}] decode latency avg={steadyDecode.Average():0.###}ms " +
                        $"min={steadyDecode.Min():0.###}ms max={steadyDecode.Max():0.###}ms (n={steadyDecode.Count}, warmup skipped)");
        if (compressedSizes.Count > 0)
            logger.Info($"[{(lowLatency ? "low-latency" : "defaults")}] avg compressed frame size: {compressedSizes.Average():0}B " +
                        $"({compressedSizes.Average() * 8 * FpsNumerator / FpsDenominator / 1_000_000.0:0.##}Mbps @ {FpsNumerator}fps)");
    }
}
