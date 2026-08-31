using System.Runtime.InteropServices;

namespace RemoteControl.Input;

/// <summary>
/// Direct P/Invoke against user32.dll -- no package needed for this small a
/// surface. Struct layouts match the Win32 SDK exactly (see MSDN for
/// INPUT/MOUSEINPUT/KEYBDINPUT); do not reorder fields.
/// </summary>
internal static class Win32Native
{
    internal const uint InputMouse = 0;
    internal const uint InputKeyboard = 1;

    internal const uint MouseEventFMove = 0x0001;
    internal const uint MouseEventFAbsolute = 0x8000;
    internal const uint MouseEventFVirtualDesk = 0x4000;
    internal const uint MouseEventFLeftDown = 0x0002;
    internal const uint MouseEventFLeftUp = 0x0004;
    internal const uint MouseEventFRightDown = 0x0008;
    internal const uint MouseEventFRightUp = 0x0010;
    internal const uint MouseEventFMiddleDown = 0x0020;
    internal const uint MouseEventFMiddleUp = 0x0040;
    internal const uint MouseEventFWheel = 0x0800;
    internal const uint MouseEventFHWheel = 0x1000;

    internal const uint KeyEventFExtendedKey = 0x0001;
    internal const uint KeyEventFKeyUp = 0x0002;
    internal const uint KeyEventFUnicode = 0x0004;
    internal const uint KeyEventFScanCode = 0x0008;

    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public nint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public nint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mi;
        [FieldOffset(0)] public KeyboardInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    /// <summary>Layout-dependent by design -- only used for Ctrl/Alt-held letter/digit shortcuts (e.g. Ctrl+C), never for plain character typing. See InputInjector's remarks.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern short VkKeyScanEx(char ch, nint dwhkl);

    [DllImport("user32.dll")]
    internal static extern nint GetKeyboardLayout(uint idThread);

    internal const uint MapvkVkToVsc = 0;

    /// <summary>Fills in a hardware scan code alongside the VK for games/apps that read raw scan codes instead of virtual keys.</summary>
    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKey(uint uCode, uint uMapType);

    // -- Capture side: window subclassing + mouse capture --

    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLButtonDown = 0x0201;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmRButtonDown = 0x0204;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmMButtonDown = 0x0207;
    internal const uint WmMButtonUp = 0x0208;
    internal const uint WmMouseWheel = 0x020A;
    internal const uint WmMouseHWheel = 0x020E;
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmKeyUp = 0x0101;
    internal const uint WmSysKeyDown = 0x0104;
    internal const uint WmSysKeyUp = 0x0105;
    internal const uint WmChar = 0x0102;
    internal const uint WmKillFocus = 0x0008;

    internal const int GwlpWndProc = -4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    internal delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    internal static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern nint SetCapture(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();
}
