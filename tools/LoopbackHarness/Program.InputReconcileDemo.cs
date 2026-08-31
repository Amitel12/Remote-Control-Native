using System.Runtime.InteropServices;
using RemoteControl.Common;
using RemoteControl.Input;
using RemoteControl.Protocol;

namespace RemoteControl.Tools.LoopbackHarness;

/// <summary>
/// Real-hardware smoke test for InputInjector.ReconcileHeldState (docs/PHASE-3.md's
/// reliability fix) -- entirely in-process, no network or second machine needed, since
/// this specifically isolates the reconciliation logic from network conditions: presses
/// a real button/key, deliberately never sends the matching release (simulating a lost
/// MouseUp/KeyUp), then reconciles against an empty held-mask (what the capture side
/// would report once the user's real button/key is actually up) and checks via
/// GetAsyncKeyState whether the OS agrees it's released.
/// </summary>
internal static partial class Program
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VkLButton = 0x01;
    private const int VkControl = 0x11;

    private static void RunInputReconcileDemo(ILogger logger)
    {
        var injector = new InputInjector();
        var pass = true;

        logger.Info("Pressing left mouse button (held, no release sent) -- simulating a lost MouseUp...");
        injector.Inject(new InputEvent.MouseDown(MouseButton.Left, 0.5f, 0.5f), 0, 0, 1920, 1080);
        Thread.Sleep(50);
        pass &= Expect(logger, IsPhysicallyDown(VkLButton), "left mouse button reads as held after MouseDown");

        logger.Info("Reconciling against an empty held-mask (capture side reports nothing held)...");
        injector.ReconcileHeldState(0);
        Thread.Sleep(50);
        pass &= Expect(logger, !IsPhysicallyDown(VkLButton), "left mouse button released by ReconcileHeldState alone");

        logger.Info("Pressing Control (held, no release sent) -- simulating a lost KeyUp...");
        injector.Inject(new InputEvent.KeyDown(KeyKind.Named, (uint)NamedKey.Control, ModifierKeys.None), 0, 0, 1920, 1080);
        Thread.Sleep(50);
        pass &= Expect(logger, IsPhysicallyDown(VkControl), "Control reads as held after KeyDown");

        logger.Info("Reconciling against an empty held-mask again...");
        injector.ReconcileHeldState(0);
        Thread.Sleep(50);
        pass &= Expect(logger, !IsPhysicallyDown(VkControl), "Control released by ReconcileHeldState alone");

        logger.Info(pass
            ? "PASS -- ReconcileHeldState correctly self-healed both a stuck button and a stuck modifier."
            : "FAIL -- see the unmet expectation(s) above.");
        if (!pass)
        {
            injector.ReleaseAllHeld(); // don't leave a real stuck button/key behind on a failed run.
            throw new InvalidOperationException("Input reconcile demo failed one or more expectations -- see log.");
        }
    }

    private static bool IsPhysicallyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    // Expect(ILogger, bool, string) is shared from Program.InputCaptureDemo.cs.
}
