using RemoteControl.Capture;
using RemoteControl.Codec;
using RemoteControl.Common;
using RemoteControl.Input;
using RemoteControl.Protocol;

namespace RemoteControl.Tools.LoopbackHarness;

/// <summary>
/// Phase 3 real-hardware smoke test for <see cref="InputInjector"/> (see
/// docs/ARCHITECTURE.md's baked-in lessons #1/#2) -- moves the real mouse
/// and types into whatever window has focus, so it waits for a countdown
/// before doing anything rather than firing immediately.
/// </summary>
internal static partial class Program
{
    private static void RunInputDemo(ILogger logger)
    {
        using var mfDevice = MfDevice.Create(logger);
        var displays = DisplayEnumerator.Enumerate(mfDevice.Device);
        if (displays.Count == 0)
            throw new InvalidOperationException("No attached displays found.");
        var display = displays[0];
        logger.Info($"Target display: {display.DeviceName}, {display.Width}x{display.Height} at ({display.Left},{display.Top}).");

        logger.Info("Starting in 5 seconds -- click into a text editor (Notepad etc.) now so it has focus.");
        for (var i = 5; i >= 1; i--)
        {
            logger.Info($"  {i}...");
            Thread.Sleep(1000);
        }

        var injector = new InputInjector();
        try
        {
            logger.Info("Moving the cursor through the four quadrants of the target display...");
            foreach (var (x, y) in new[] { (0.25f, 0.25f), (0.75f, 0.25f), (0.75f, 0.75f), (0.25f, 0.75f), (0.5f, 0.5f) })
            {
                injector.Inject(new InputEvent.MouseMove(x, y), display.Left, display.Top, display.Width, display.Height);
                Thread.Sleep(300);
            }

            logger.Info("Left-clicking at the current position (to focus whatever's under the cursor)...");
            injector.Inject(new InputEvent.MouseDown(MouseButton.Left, 0.5f, 0.5f), display.Left, display.Top, display.Width, display.Height);
            Thread.Sleep(50);
            injector.Inject(new InputEvent.MouseUp(MouseButton.Left, 0.5f, 0.5f), display.Left, display.Top, display.Width, display.Height);
            Thread.Sleep(200);

            const string text = "Hello from InputInjector -- unicode typing test: héllo, 日本語, emoji 🎉";
            logger.Info($"Typing (KEYEVENTF_UNICODE, layout-independent): \"{text}\"");
            foreach (var rune in text.EnumerateRunes())
            {
                injector.Inject(new InputEvent.KeyDown(KeyKind.Character, (uint)rune.Value, ModifierKeys.None), 0, 0, 0, 0);
                injector.Inject(new InputEvent.KeyUp(KeyKind.Character, (uint)rune.Value, ModifierKeys.None), 0, 0, 0, 0);
                Thread.Sleep(15);
            }

            logger.Info("Pressing Enter (named key, real VK+scancode)...");
            injector.Inject(new InputEvent.KeyDown(KeyKind.Named, (uint)NamedKey.Enter, ModifierKeys.None), 0, 0, 0, 0);
            injector.Inject(new InputEvent.KeyUp(KeyKind.Named, (uint)NamedKey.Enter, ModifierKeys.None), 0, 0, 0, 0);
            Thread.Sleep(200);

            logger.Info("Typing more text, then Ctrl+A (shortcut path: VkKeyScanEx layout-dependent translation, modifier stays held via a real Named key)...");
            const string moreText = "select-all should highlight everything above";
            foreach (var rune in moreText.EnumerateRunes())
            {
                injector.Inject(new InputEvent.KeyDown(KeyKind.Character, (uint)rune.Value, ModifierKeys.None), 0, 0, 0, 0);
                injector.Inject(new InputEvent.KeyUp(KeyKind.Character, (uint)rune.Value, ModifierKeys.None), 0, 0, 0, 0);
                Thread.Sleep(15);
            }
            Thread.Sleep(200);

            injector.Inject(new InputEvent.KeyDown(KeyKind.Named, (uint)NamedKey.Control, ModifierKeys.None), 0, 0, 0, 0);
            injector.Inject(new InputEvent.KeyDown(KeyKind.Character, 'a', ModifierKeys.Control), 0, 0, 0, 0);
            injector.Inject(new InputEvent.KeyUp(KeyKind.Character, 'a', ModifierKeys.Control), 0, 0, 0, 0);
            injector.Inject(new InputEvent.KeyUp(KeyKind.Named, (uint)NamedKey.Control, ModifierKeys.None), 0, 0, 0, 0);

            logger.Info("PASS -- input demo sequence sent. Check the focused window for the typed text and selection.");
        }
        finally
        {
            injector.ReleaseAllHeld();
        }
    }
}
