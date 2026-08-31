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
}
