using System.Runtime.InteropServices;
using RemoteControl.Protocol;

namespace RemoteControl.Input;

/// <summary>
/// Captures real local mouse/keyboard input on a given window (via classic
/// Win32 subclassing -- <c>SetWindowLongPtr(GWLP_WNDPROC)</c> -- so this
/// works with any window handle: WinForms today, a future WPF
/// <c>HwndSource</c> tomorrow) and raises it as normalized
/// <see cref="InputEvent"/>s ready for <see cref="InputEventCodec"/>.
///
/// Despite the name, this deliberately does NOT use the Win32 "Raw Input"
/// API (relative mouse deltas, meant for FPS-style look controls) -- the
/// wire format's <see cref="InputEvent.MouseMove"/> is an absolute
/// normalized position (see InputEvent.cs), so the right primitive is the
/// ordinary WM_MOUSEMOVE/WM_*BUTTON* messages, which already report
/// absolute client-area coordinates.
///
/// Lesson #3 from docs/ARCHITECTURE.md (mouse capture during drag) is
/// load-bearing here: <c>SetCapture</c> on button-down keeps WM_MOUSEMOVE/
/// WM_*BUTTONUP arriving even once the cursor leaves the window (fast
/// overshoot), and losing focus altogether (WM_KILLFOCUS -- e.g. alt-tab
/// mid-drag) force-releases every currently-held button/modifier so a
/// dropped local button-up can never leave the remote host with a stuck
/// virtual button.
///
/// Lesson #2 (keyboard layout independence) is why plain character typing
/// is read from WM_CHAR (Windows already resolves the correct localized/
/// shifted Unicode character for us) rather than translating WM_KEYDOWN's
/// VK ourselves -- except when Ctrl/Alt is held: Windows delivers a
/// C0 control code for Ctrl+&lt;letter&gt; via WM_CHAR (Ctrl+C is 0x03, not
/// 'c'), not the plain letter, so that specific case reads the letter
/// straight off WM_KEYDOWN's VK instead (VK_A..VK_Z equal their ASCII
/// values by convention) and marks it with the held modifier -- see
/// InputInjector's matching VkKeyScanEx-based shortcut path.
/// </summary>
public sealed class RawInputCapture : IDisposable
{
    private readonly nint _hWnd;
    private readonly nint _originalWndProc;
    private readonly Win32Native.WndProc _wndProcDelegate; // kept alive: native code holds a pointer to this.
    private readonly HashSet<MouseButton> _heldButtons = [];
    private readonly HashSet<NamedKey> _heldNamedKeys = [];
    private readonly Dictionary<ushort, uint> _lastCharacterForVk = [];
    private ModifierKeys _heldModifiers;
    private char? _pendingHighSurrogate;
    private bool _disposed;

    public event Action<InputEvent>? Captured;

    public RawInputCapture(nint hWnd)
    {
        _hWnd = hWnd;
        _wndProcDelegate = WndProc;
        var newWndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _originalWndProc = Win32Native.SetWindowLongPtr(hWnd, Win32Native.GwlpWndProc, newWndProcPtr);
    }

    /// <summary>Force-releases every button/named-key this instance believes is currently held -- see class remarks on lesson #3.</summary>
    public void ForceReleaseAll()
    {
        foreach (var button in _heldButtons.ToArray())
        {
            _heldButtons.Remove(button);
            Captured?.Invoke(new InputEvent.MouseUp(button, 0.5f, 0.5f));
        }

        foreach (var namedKey in _heldNamedKeys.ToArray())
        {
            _heldNamedKeys.Remove(namedKey);
            Captured?.Invoke(new InputEvent.KeyUp(KeyKind.Named, (uint)namedKey, ModifierKeys.None));
        }

        _heldModifiers = ModifierKeys.None;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ForceReleaseAll();
        Win32Native.SetWindowLongPtr(_hWnd, Win32Native.GwlpWndProc, _originalWndProc);
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32Native.WmMouseMove:
                Captured?.Invoke(new InputEvent.MouseMove(NormalizedX(lParam), NormalizedY(lParam)));
                return 0;

            case Win32Native.WmLButtonDown: return HandleButtonDown(MouseButton.Left, lParam);
            case Win32Native.WmLButtonUp: return HandleButtonUp(MouseButton.Left, lParam);
            case Win32Native.WmRButtonDown: return HandleButtonDown(MouseButton.Right, lParam);
            case Win32Native.WmRButtonUp: return HandleButtonUp(MouseButton.Right, lParam);
            case Win32Native.WmMButtonDown: return HandleButtonDown(MouseButton.Middle, lParam);
            case Win32Native.WmMButtonUp: return HandleButtonUp(MouseButton.Middle, lParam);

            case Win32Native.WmMouseWheel:
                Captured?.Invoke(new InputEvent.Wheel(0, WheelDelta(wParam)));
                return 0;

            case Win32Native.WmMouseHWheel:
                Captured?.Invoke(new InputEvent.Wheel(WheelDelta(wParam), 0));
                return 0;

            case Win32Native.WmKeyDown:
                HandleKeyDown((ushort)wParam);
                return 0; // consumed -- don't let typed content also affect whatever's locally behind this window.

            case Win32Native.WmKeyUp:
                HandleKeyUp((ushort)wParam);
                return 0;

            case Win32Native.WmSysKeyDown:
                HandleKeyDown((ushort)wParam);
                break; // NOT consumed: Alt itself (and combos like Alt+Tab) route through WM_SYSKEY*, and the
                       // local user should still be able to actually alt-tab away from this window normally.

            case Win32Native.WmSysKeyUp:
                HandleKeyUp((ushort)wParam);
                break;

            case Win32Native.WmChar:
                HandleChar((char)wParam);
                return 0;

            case Win32Native.WmKillFocus:
                ForceReleaseAll();
                break;
        }

        return Win32Native.CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    private nint HandleButtonDown(MouseButton button, nint lParam)
    {
        _heldButtons.Add(button);
        if (_heldButtons.Count == 1)
            Win32Native.SetCapture(_hWnd); // first button down -- start capturing so overshoot/leave doesn't lose the eventual up.
        Captured?.Invoke(new InputEvent.MouseDown(button, NormalizedX(lParam), NormalizedY(lParam)));
        return 0;
    }

    private nint HandleButtonUp(MouseButton button, nint lParam)
    {
        _heldButtons.Remove(button);
        Captured?.Invoke(new InputEvent.MouseUp(button, NormalizedX(lParam), NormalizedY(lParam)));
        if (_heldButtons.Count == 0)
            Win32Native.ReleaseCapture(); // last button released -- stop capturing, let the cursor behave normally again.
        return 0;
    }

    private void HandleKeyDown(ushort vk)
    {
        UpdateModifierState(vk, down: true);

        if (NamedKeyMapping.ByVk.TryGetValue(vk, out var namedKey))
        {
            _heldNamedKeys.Add(namedKey);
            Captured?.Invoke(new InputEvent.KeyDown(KeyKind.Named, (uint)namedKey, _heldModifiers));
            return;
        }

        // A modifier is held over a plain letter/digit -- this is a shortcut (Ctrl+C etc), and WM_CHAR would
        // deliver a C0 control code for it (Ctrl+C is 0x03), not the letter -- read the letter off the VK
        // directly instead (VK_A..VK_Z/VK_0..VK_9 equal their ASCII values by Win32 convention).
        if ((_heldModifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0 &&
            (vk is >= (ushort)'A' and <= (ushort)'Z' or >= (ushort)'0' and <= (ushort)'9'))
        {
            var codePoint = (uint)char.ToLowerInvariant((char)vk);
            _lastCharacterForVk[vk] = codePoint;
            Captured?.Invoke(new InputEvent.KeyDown(KeyKind.Character, codePoint, _heldModifiers));
        }
        // Otherwise: plain typing. Handled by WM_CHAR (HandleChar), which gets the layout/shift-correct
        // character from Windows instead of us hand-rolling that translation -- see class remarks.
    }

    private void HandleKeyUp(ushort vk)
    {
        UpdateModifierState(vk, down: false);

        if (NamedKeyMapping.ByVk.TryGetValue(vk, out var namedKey))
        {
            _heldNamedKeys.Remove(namedKey);
            Captured?.Invoke(new InputEvent.KeyUp(KeyKind.Named, (uint)namedKey, _heldModifiers));
            return;
        }

        if (_lastCharacterForVk.Remove(vk, out var codePoint))
            Captured?.Invoke(new InputEvent.KeyUp(KeyKind.Character, codePoint, _heldModifiers));
    }

    private void HandleChar(char utf16Unit)
    {
        // Control characters (Enter, Tab, Backspace, Escape, the Ctrl+<letter> C0 codes, ...) are already
        // handled via the NamedKey/shortcut paths in HandleKeyDown -- emitting them again here would double-inject.
        if (utf16Unit < 0x20 || utf16Unit == 0x7F)
            return;

        if (char.IsHighSurrogate(utf16Unit))
        {
            _pendingHighSurrogate = utf16Unit;
            return;
        }

        uint codePoint;
        if (_pendingHighSurrogate is { } high && char.IsLowSurrogate(utf16Unit))
        {
            codePoint = (uint)char.ConvertToUtf32(high, utf16Unit);
            _pendingHighSurrogate = null;
        }
        else
        {
            _pendingHighSurrogate = null;
            codePoint = utf16Unit;
        }

        Captured?.Invoke(new InputEvent.KeyDown(KeyKind.Character, codePoint, ModifierKeys.None));
        Captured?.Invoke(new InputEvent.KeyUp(KeyKind.Character, codePoint, ModifierKeys.None));
    }

    private void UpdateModifierState(ushort vk, bool down)
    {
        var flag = vk switch
        {
            0x11 => ModifierKeys.Control,
            0x12 => ModifierKeys.Alt,
            0x10 => ModifierKeys.Shift,
            _ => (ModifierKeys?)null,
        };
        if (flag is not { } modifier) return;
        _heldModifiers = down ? _heldModifiers | modifier : _heldModifiers & ~modifier;
    }

    private float NormalizedX(nint lParam) => Normalize(unchecked((short)((long)lParam & 0xFFFF)), ClientWidth());
    private float NormalizedY(nint lParam) => Normalize(unchecked((short)(((long)lParam >> 16) & 0xFFFF)), ClientHeight());

    private static float Normalize(short coordinate, int extent) =>
        extent <= 0 ? 0f : Math.Clamp(coordinate / (float)extent, 0f, 1f); // clamped: a captured drag can overshoot the client rect.

    private static float WheelDelta(nint wParam) => unchecked((short)(((long)wParam >> 16) & 0xFFFF)) / 120f; // WHEEL_DELTA notches.

    private int ClientWidth()
    {
        Win32Native.GetClientRect(_hWnd, out var rect);
        return rect.Right - rect.Left;
    }

    private int ClientHeight()
    {
        Win32Native.GetClientRect(_hWnd, out var rect);
        return rect.Bottom - rect.Top;
    }
}
