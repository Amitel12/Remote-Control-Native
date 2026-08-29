using RemoteControl.Codec;
using RemoteControl.Common;

namespace RemoteControl.Tools.LoopbackHarness;

/// <summary>
/// Phase 0 entry point (see docs/PHASE-0.md): eventually wires
/// RemoteControl.Capture -> RemoteControl.Codec (encode) -> RemoteControl.Codec
/// (decode) -> RemoteControl.Render on one machine, no networking, and measures
/// whether the pipeline sustains 60fps 1080p with zero CPU-side texture copies.
///
/// Only Step 0 of that plan exists so far: the MFT probe, which reports whether
/// this machine exposes the hardware transforms everything else assumes. The
/// DesktopDuplicator/HardwareEncoder/HardwareDecoder/SwapChainPresenter classes
/// are still unwritten, and can only be built and verified against real GPU
/// hardware.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var logger = new ConsoleLogger("LoopbackHarness");

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
            return 1;
        }

        if (transforms.Count == 0)
        {
            logger.Error("No video transforms found at all, hardware or software.");
            logger.Error("That is a genuine blocker for Phase 0 -- do not build the pipeline on this machine.");
            return 1;
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
            return 2;
        }

        if (hwH264Decoders > 0)
        {
            logger.Info("PASS -- hardware H.264 encoder and decoder MFTs both present.");
            logger.Info("Step 1 (codec against a synthetic texture) is unblocked. This proves");
            logger.Info("presence, not that either transform can actually be driven.");
            return 0;
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
            return 0;
        }

        logger.Warn("NO-GO on the decode side -- no H.264 decoder at all, hardware or software.");
        return 2;
    }
}
