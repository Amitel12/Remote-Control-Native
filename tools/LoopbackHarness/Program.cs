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
            logger.Error("No H.264 transforms found at all, hardware or software.");
            logger.Error("That is a genuine blocker for Phase 0 -- do not build the pipeline on this machine.");
            return 1;
        }

        Console.WriteLine($"  {"CATEGORY",-16} {"TYPE",-4} NAME");
        Console.WriteLine($"  {new string('-', 16)} {new string('-', 4)} {new string('-', 40)}");
        foreach (var t in transforms)
        {
            Console.WriteLine($"  {t.Category,-16} {(t.IsHardware ? "HW" : "SW"),-4} {t.FriendlyName}");
        }
        Console.WriteLine();

        var hardwareEncoders = transforms.Count(t => t.IsHardware && t.Category == "VideoEncoder");
        var hardwareDecoders = transforms.Count(t => t.IsHardware && t.Category == "VideoDecoder");

        logger.Info($"Hardware H.264 encoders: {hardwareEncoders}");
        logger.Info($"Hardware H.264 decoders: {hardwareDecoders}");
        Console.WriteLine();

        // The whole point of Step 0 is to make this verdict explicit rather
        // than letting a missing transform surface three components later as a
        // confusing failure inside the pipeline.
        if (hardwareEncoders > 0 && hardwareDecoders > 0)
        {
            logger.Info("PASS -- both hardware transforms present. Step 1 (codec against a synthetic");
            logger.Info("texture) is unblocked. Note this proves presence, not that they can be driven.");
            return 0;
        }

        logger.Warn("INCOMPLETE -- hardware transforms are missing on at least one side.");
        logger.Warn("Software transforms cannot meet Phase 0's latency goal. Re-read the go/no-go");
        logger.Warn("gate in docs/PHASE-0.md before continuing; this may mean vendor-specific NVENC.");
        return 2;
    }
}
