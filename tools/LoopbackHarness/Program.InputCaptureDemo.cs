using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using RemoteControl.Common;
using RemoteControl.Input;
using RemoteControl.Protocol;

namespace RemoteControl.Tools.LoopbackHarness;

/// <summary>
/// Phase 3 real-hardware smoke test for <see cref="RawInputCapture"/> --
/// self-verifying: uses <see cref="InputInjector"/> to synthesize real OS
/// input into a capture window and checks that RawInputCapture reports it
/// back correctly, including the lesson #3 safety net (a real
/// <c>WM_KILLFOCUS</c>, triggered by shifting focus to a second window
/// rather than by injecting Alt+Tab, so this doesn't disrupt whatever the
/// user actually has open elsewhere).
/// </summary>
internal static partial class Program
{
    private static void RunInputCaptureDemo(ILogger logger)
    {
        using var window = new Form
        {
            Text = "Remote-Control-Native — input capture demo",
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(640, 360),
            BackColor = Color.DarkSlateBlue,
        };
        using var focusStealer = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-2000, -2000), // off-screen -- exists only to hold OS focus away from `window` on demand.
            ClientSize = new Size(1, 1),
            ShowInTaskbar = false,
        };
        window.Show();
        focusStealer.Show();
        window.Activate();
        Application.DoEvents();
        Thread.Sleep(300);

        var captured = new List<InputEvent>();
        using var capture = new RawInputCapture(window.Handle);
        capture.Captured += e => captured.Add(e);

        var windowOrigin = window.PointToScreen(Point.Empty);
        var width = window.ClientSize.Width;
        var height = window.ClientSize.Height;

        var injector = new InputInjector();
        void Inject(InputEvent e)
        {
            injector.Inject(e, windowOrigin.X, windowOrigin.Y, width, height);
            Application.DoEvents();
            Thread.Sleep(20);
        }

        logger.Info("Injecting a scripted input sequence into the capture window and checking what RawInputCapture reports back...");

        // 1) Plain move + click.
        Inject(new InputEvent.MouseMove(0.5f, 0.5f));
        Inject(new InputEvent.MouseDown(MouseButton.Left, 0.5f, 0.5f));
        Inject(new InputEvent.MouseUp(MouseButton.Left, 0.5f, 0.5f));

        // 2) Lesson #3: start a drag, then lose focus WITHOUT ever sending the button-up -- the safety
        // net (WM_KILLFOCUS -> ForceReleaseAll) should synthesize the MouseUp on its own.
        Inject(new InputEvent.MouseDown(MouseButton.Left, 0.3f, 0.3f));
        Inject(new InputEvent.MouseMove(0.7f, 0.7f));
        focusStealer.Activate();
        Application.DoEvents();
        Thread.Sleep(100);
        window.Activate();
        Application.DoEvents();
        Thread.Sleep(100);

        // 3) Wheel.
        injector.Inject(new InputEvent.Wheel(0, 3), 0, 0, 0, 0);
        Application.DoEvents();
        Thread.Sleep(20);

        // 4) Plain typing, including a surrogate-pair character -- exercises HandleChar's astral-codepoint path.
        const string text = "hi 🎉";
        foreach (var rune in text.EnumerateRunes())
        {
            injector.Inject(new InputEvent.KeyDown(KeyKind.Character, (uint)rune.Value, ModifierKeys.None), 0, 0, 0, 0);
            injector.Inject(new InputEvent.KeyUp(KeyKind.Character, (uint)rune.Value, ModifierKeys.None), 0, 0, 0, 0);
            Application.DoEvents();
            Thread.Sleep(20);
        }

        // 5) Ctrl+A shortcut -- exercises the VK-range/held-modifier path in HandleKeyDown, not WM_CHAR.
        injector.Inject(new InputEvent.KeyDown(KeyKind.Named, (uint)NamedKey.Control, ModifierKeys.None), 0, 0, 0, 0);
        Application.DoEvents();
        injector.Inject(new InputEvent.KeyDown(KeyKind.Character, 'a', ModifierKeys.Control), 0, 0, 0, 0);
        injector.Inject(new InputEvent.KeyUp(KeyKind.Character, 'a', ModifierKeys.Control), 0, 0, 0, 0);
        injector.Inject(new InputEvent.KeyUp(KeyKind.Named, (uint)NamedKey.Control, ModifierKeys.None), 0, 0, 0, 0);
        Application.DoEvents();
        Thread.Sleep(50);

        capture.Dispose(); // also force-releases anything still (correctly) held.
        window.Close();
        focusStealer.Close();

        logger.Info($"Captured {captured.Count} events:");
        foreach (var e in captured)
            logger.Info($"  {Describe(e)}");

        var pass = true;
        pass &= Expect(logger, captured.OfType<InputEvent.MouseDown>().Any(m => m.Button == MouseButton.Left), "a plain MouseDown was captured");
        pass &= Expect(logger, captured.OfType<InputEvent.MouseUp>().Count(m => m.Button == MouseButton.Left) >= 2,
            "two MouseUps were captured -- the plain click's, and the drag's synthesized release from the focus-loss safety net (lesson #3)");
        pass &= Expect(logger, captured.OfType<InputEvent.Wheel>().Any(w => w.DeltaY > 0), "the wheel scroll was captured");
        var emojiCodePoint = (uint)"🎉".EnumerateRunes().First().Value;
        pass &= Expect(logger, captured.OfType<InputEvent.KeyDown>().Any(k => k.KeyKind == KeyKind.Character && k.CodePointOrNamedKey == emojiCodePoint),
            "the surrogate-pair emoji round-tripped as one correct codepoint");
        pass &= Expect(logger, captured.OfType<InputEvent.KeyDown>().Any(k => k.KeyKind == KeyKind.Character && k.CodePointOrNamedKey == 'a' && k.HeldModifiers == ModifierKeys.Control),
            "Ctrl+A was captured as Character 'a' with Control held (not a raw 0x01 control code)");

        logger.Info(pass
            ? "PASS -- RawInputCapture correctly reported every injected event, including the focus-loss safety net."
            : "FAIL -- see the unmet expectation(s) above.");
        if (!pass)
            throw new InvalidOperationException("Input capture demo failed one or more expectations -- see log.");
    }

    private static bool Expect(ILogger logger, bool condition, string description)
    {
        logger.Info($"  [{(condition ? "OK" : "MISSING")}] {description}");
        return condition;
    }

    private static string Describe(InputEvent e) => e switch
    {
        InputEvent.MouseMove(var x, var y) => $"MouseMove ({x:0.000}, {y:0.000})",
        InputEvent.MouseDown(var button, var x, var y) => $"MouseDown {button} ({x:0.000}, {y:0.000})",
        InputEvent.MouseUp(var button, var x, var y) => $"MouseUp {button} ({x:0.000}, {y:0.000})",
        InputEvent.Wheel(var dx, var dy) => $"Wheel ({dx:0.##}, {dy:0.##})",
        InputEvent.KeyDown(var kind, var code, var mods) => $"KeyDown {kind} {DescribeCode(kind, code)} mods={mods}",
        InputEvent.KeyUp(var kind, var code, var mods) => $"KeyUp {kind} {DescribeCode(kind, code)} mods={mods}",
        _ => e.ToString() ?? "?",
    };

    private static string DescribeCode(KeyKind kind, uint code) =>
        kind == KeyKind.Named ? ((NamedKey)code).ToString() : $"U+{code:X4} '{char.ConvertFromUtf32((int)code)}'";
}
