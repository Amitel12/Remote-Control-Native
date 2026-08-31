using RemoteControl.Protocol;

namespace RemoteControl.Input;

/// <summary>
/// Replays <see cref="InputEvent"/>s on this machine via <c>SendInput</c> --
/// see docs/ARCHITECTURE.md's baked-in lessons #1 and #2, both load-bearing
/// here:
///
/// #1 (physical pixels end-to-end): callers pass the target display's
/// bounds in physical pixels (matching <c>RemoteControl.Capture.DisplayInfo</c>);
/// this class denormalizes the wire format's 0..1 coordinates against those
/// bounds, then re-normalizes to the 0..65535 range <c>MOUSEEVENTF_ABSOLUTE</c>
/// expects across the *full virtual desktop* (not just the target display),
/// so multi-monitor setups with a secondary display at negative/offset
/// coordinates land correctly.
///
/// #2 (keyboard layout independence): a <see cref="KeyKind.Character"/> key
/// with no modifiers held is plain typed text -- injected via
/// <c>KEYEVENTF_UNICODE</c> unconditionally, never VK/scan-code translation
/// (layout-dependent, and the Electron app's bug was exactly this: VK
/// translation silently failing for English text on an English-layout
/// host). A <see cref="KeyKind.Character"/> key WITH a modifier held (e.g.
/// Ctrl+C) is different: Unicode-injected characters bypass the OS's normal
/// shortcut processing entirely (Ctrl+C typed as Unicode does not copy), so
/// that specific case routes through <c>VkKeyScanEx</c> layout-dependent
/// translation on purpose -- the modifier itself was already pressed by a
/// separate <see cref="NamedKey"/> event, so this only needs to resolve
/// which VK the plain letter/digit maps to on the live keyboard layout.
/// <see cref="KeyKind.Named"/> keys always use real VK+scancode injection.
/// </summary>
public sealed class InputInjector
{
    private readonly HashSet<MouseButton> _heldButtons = [];
    private readonly HashSet<NamedKey> _heldNamedKeys = [];

    /// <param name="displayLeft">Target display's left edge, physical pixels (matches DisplayInfo.Left).</param>
    /// <param name="displayTop">Target display's top edge, physical pixels.</param>
    /// <param name="displayWidth">Target display's width, physical pixels.</param>
    /// <param name="displayHeight">Target display's height, physical pixels.</param>
    public void Inject(InputEvent inputEvent, int displayLeft, int displayTop, int displayWidth, int displayHeight)
    {
        switch (inputEvent)
        {
            case InputEvent.MouseMove(var x, var y):
                {
                    var (dx, dy) = ToVirtualDesktopCoordinates(x, y, displayLeft, displayTop, displayWidth, displayHeight);
                    SendMouse(dx, dy, Win32Native.MouseEventFMove | Win32Native.MouseEventFAbsolute | Win32Native.MouseEventFVirtualDesk);
                }
                break;

            case InputEvent.MouseDown(var button, var x, var y):
                {
                    _heldButtons.Add(button);
                    var (dx, dy) = ToVirtualDesktopCoordinates(x, y, displayLeft, displayTop, displayWidth, displayHeight);
                    SendMouse(dx, dy, Win32Native.MouseEventFMove | Win32Native.MouseEventFAbsolute | Win32Native.MouseEventFVirtualDesk | ButtonFlag(button, down: true));
                }
                break;

            case InputEvent.MouseUp(var button, var x, var y):
                {
                    _heldButtons.Remove(button);
                    var (dx, dy) = ToVirtualDesktopCoordinates(x, y, displayLeft, displayTop, displayWidth, displayHeight);
                    SendMouse(dx, dy, Win32Native.MouseEventFMove | Win32Native.MouseEventFAbsolute | Win32Native.MouseEventFVirtualDesk | ButtonFlag(button, down: false));
                }
                break;

            case InputEvent.Wheel(var dx, var dy):
                InjectWheel(dx, dy);
                break;

            case InputEvent.KeyDown(var keyKind, var code, var modifiers):
                InjectKey(keyKind, code, modifiers, down: true);
                break;

            case InputEvent.KeyUp(var keyKind, var code, var modifiers):
                InjectKey(keyKind, code, modifiers, down: false);
                break;
        }
    }

    /// <summary>
    /// Synthesizes release for every button/named-key this instance
    /// believes is currently held -- call on disconnect/blur so a dropped
    /// connection mid-drag or mid-shortcut can never leave the host with a
    /// stuck virtual button or held modifier (lesson #3's safety net,
    /// host-side complement to the capture-side one -- see
    /// docs/ARCHITECTURE.md).
    /// </summary>
    public void ReleaseAllHeld()
    {
        foreach (var button in _heldButtons.ToArray())
            ReleaseButton(button);
        foreach (var namedKey in _heldNamedKeys.ToArray())
            ReleaseNamedKey(namedKey);
    }

    /// <summary>
    /// Releases anything this instance believes is held that
    /// <paramref name="remoteHeldMask"/> (the capture side's own
    /// <see cref="RawInputCapture.GetHeldMask"/>, arrived via
    /// InputStateSync) says is *not* actually held -- self-heals a lost
    /// MouseUp/KeyUp within one sync interval instead of leaving it stuck
    /// until session end. Deliberately one-directional: never presses
    /// something the mask claims is held that this instance doesn't have on
    /// record -- a stale/reordered sync packet causing a phantom press would
    /// be a worse failure than waiting for the user's next real input.
    /// </summary>
    public void ReconcileHeldState(ushort remoteHeldMask)
    {
        foreach (var button in _heldButtons.ToArray())
        {
            if (!InputHeldStateMask.HasButton(remoteHeldMask, button))
                ReleaseButton(button);
        }

        foreach (var namedKey in _heldNamedKeys.ToArray())
        {
            if (!InputHeldStateMask.HasNamedKey(remoteHeldMask, namedKey))
                ReleaseNamedKey(namedKey);
        }
    }

    // No MOVE/ABSOLUTE flags here -- release in place, don't yank the cursor somewhere arbitrary just to unstick a button.
    private void ReleaseButton(MouseButton button)
    {
        SendMouse(0, 0, ButtonFlag(button, down: false));
        _heldButtons.Remove(button);
    }

    private void ReleaseNamedKey(NamedKey namedKey)
    {
        if (NamedKeyMapping.ByNamedKey.TryGetValue(namedKey, out var mapping))
            SendKeyboard(mapping.Vk, mapping.Extended, down: false);
        _heldNamedKeys.Remove(namedKey);
    }

    private static (int Dx, int Dy) ToVirtualDesktopCoordinates(float x, float y, int left, int top, int width, int height)
    {
        var virtualLeft = Win32Native.GetSystemMetrics(Win32Native.SmXVirtualScreen);
        var virtualTop = Win32Native.GetSystemMetrics(Win32Native.SmYVirtualScreen);
        var virtualWidth = Win32Native.GetSystemMetrics(Win32Native.SmCxVirtualScreen);
        var virtualHeight = Win32Native.GetSystemMetrics(Win32Native.SmCyVirtualScreen);

        var physicalX = left + x * width;
        var physicalY = top + y * height;

        // MOUSEEVENTF_ABSOLUTE|VIRTUALDESK expects 0..65535 across the whole virtual desktop, not the target display alone.
        var normalizedX = (physicalX - virtualLeft) * 65535.0 / Math.Max(1, virtualWidth - 1);
        var normalizedY = (physicalY - virtualTop) * 65535.0 / Math.Max(1, virtualHeight - 1);
        return ((int)Math.Clamp(normalizedX, 0, 65535), (int)Math.Clamp(normalizedY, 0, 65535));
    }

    private static uint ButtonFlag(MouseButton button, bool down) => button switch
    {
        MouseButton.Left => down ? Win32Native.MouseEventFLeftDown : Win32Native.MouseEventFLeftUp,
        MouseButton.Right => down ? Win32Native.MouseEventFRightDown : Win32Native.MouseEventFRightUp,
        MouseButton.Middle => down ? Win32Native.MouseEventFMiddleDown : Win32Native.MouseEventFMiddleUp,
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown mouse button."),
    };

    private static void SendMouse(int dx, int dy, uint flags)
    {
        var input = new Win32Native.Input
        {
            Type = Win32Native.InputMouse,
            U = new Win32Native.InputUnion
            {
                Mi = new Win32Native.MouseInput { Dx = dx, Dy = dy, DwFlags = flags },
            },
        };
        Win32Native.SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<Win32Native.Input>());
    }

    private void InjectWheel(float dx, float dy)
    {
        var inputs = new List<Win32Native.Input>(2);
        if (dy != 0)
        {
            inputs.Add(MouseInput(Win32Native.MouseEventFWheel, unchecked((uint)(int)Math.Round(dy))));
        }
        if (dx != 0)
        {
            inputs.Add(MouseInput(Win32Native.MouseEventFHWheel, unchecked((uint)(int)Math.Round(dx))));
        }
        if (inputs.Count > 0)
            Win32Native.SendInput((uint)inputs.Count, inputs.ToArray(), System.Runtime.InteropServices.Marshal.SizeOf<Win32Native.Input>());
    }

    private static Win32Native.Input MouseInput(uint flags, uint mouseData) => new()
    {
        Type = Win32Native.InputMouse,
        U = new Win32Native.InputUnion { Mi = new Win32Native.MouseInput { DwFlags = flags, MouseData = mouseData } },
    };

    private void InjectKey(KeyKind keyKind, uint code, ModifierKeys modifiers, bool down)
    {
        if (keyKind == KeyKind.Named)
        {
            var namedKey = (NamedKey)code;
            if (down) _heldNamedKeys.Add(namedKey); else _heldNamedKeys.Remove(namedKey);
            if (!NamedKeyMapping.ByNamedKey.TryGetValue(namedKey, out var mapping))
                return;
            SendKeyboard(mapping.Vk, mapping.Extended, down);
            return;
        }

        // Character. Plain typing (no modifier) -> Unicode injection, never VK translation (lesson #2).
        if (modifiers == ModifierKeys.None)
        {
            foreach (var utf16Unit in char.ConvertFromUtf32(checked((int)code)))
                SendUnicodeChar(utf16Unit, down);
            return;
        }

        // A modifier is held: this is a shortcut (Ctrl+C etc), not typed text. Unicode injection would bypass
        // the OS's shortcut handling entirely, so resolve the live-layout VK for this one character instead --
        // deliberately layout-dependent here, unlike plain typing above (see class remarks).
        if (code > 0xFFFF) return; // astral characters are never real single-VK shortcuts.
        var scan = Win32Native.VkKeyScanEx((char)code, Win32Native.GetKeyboardLayout(0));
        if (scan == -1) return; // this character has no VK on the live layout.
        SendKeyboard((ushort)(scan & 0xFF), extended: false, down);
    }

    private static void SendUnicodeChar(char utf16Unit, bool down)
    {
        var input = new Win32Native.Input
        {
            Type = Win32Native.InputKeyboard,
            U = new Win32Native.InputUnion
            {
                Ki = new Win32Native.KeyboardInput
                {
                    WVk = 0,
                    WScan = utf16Unit,
                    DwFlags = Win32Native.KeyEventFUnicode | (down ? 0 : Win32Native.KeyEventFKeyUp),
                },
            },
        };
        Win32Native.SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<Win32Native.Input>());
    }

    private static void SendKeyboard(ushort vk, bool extended, bool down)
    {
        var scanCode = (ushort)Win32Native.MapVirtualKey(vk, Win32Native.MapvkVkToVsc);
        var flags = (extended ? Win32Native.KeyEventFExtendedKey : 0u) | (down ? 0u : Win32Native.KeyEventFKeyUp);
        var input = new Win32Native.Input
        {
            Type = Win32Native.InputKeyboard,
            U = new Win32Native.InputUnion
            {
                Ki = new Win32Native.KeyboardInput { WVk = vk, WScan = scanCode, DwFlags = flags },
            },
        };
        Win32Native.SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<Win32Native.Input>());
    }
}
