using RemoteControl.Common;

namespace RemoteControl.Tools.LoopbackHarness;

/// <summary>
/// Phase 0 entry point (see docs/ARCHITECTURE.md): wire up
/// RemoteControl.Capture -> RemoteControl.Codec -> RemoteControl.Codec
/// (decode) -> RemoteControl.Render on one machine, no networking, and
/// measure whether the pipeline sustains 60fps 1080p with zero CPU-side
/// texture copies. This is currently a stub -- the actual DesktopDuplicator/
/// HardwareEncoder/HardwareDecoder/SwapChainPresenter classes it will call
/// into don't exist yet (those projects contain no source files at all yet,
/// only their .csproj); writing and validating them against real GPU
/// hardware is the next concrete step, and can only happen on a real Windows
/// dev machine, not in this repo's Linux-sandboxed scaffolding pass.
///
/// See docs/PHASE-0.md for the working plan -- build order, exit criteria,
/// and the known Media Foundation landmines to expect.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var logger = new ConsoleLogger("LoopbackHarness");
        logger.Info("Phase 0 loopback harness -- capture/encode/decode/render pipeline not yet implemented.");
        logger.Info("See docs/PHASE-0.md for the build order, exit criteria and known landmines.");
        return 0;
    }
}
