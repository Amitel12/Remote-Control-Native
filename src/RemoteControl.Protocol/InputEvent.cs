namespace RemoteControl.Protocol;

public enum MouseButton : byte
{
    Left = 0,
    Right = 1,
    Middle = 2,
}

[Flags]
public enum ModifierKeys : byte
{
    None = 0,
    Control = 1 << 0,
    Alt = 1 << 1,
    Shift = 1 << 2,
}

/// <summary>
/// A key is either a literal Unicode character to type, or one of a small
/// fixed set of named keys that need real hold semantics / have no
/// character representation (Enter, arrows, modifiers themselves, etc).
/// This mirrors the old Electron app's KEY_MAP split -- see
/// docs/ARCHITECTURE.md lesson #2: the host must inject Character keys via
/// SendInput+KEYEVENTF_UNICODE unconditionally (never virtual-key
/// translation), and only use real VK+scancode injection for Named keys and
/// for modifier-held shortcuts.
/// </summary>
public enum KeyKind : byte
{
    Character = 0,
    Named = 1,
}

public enum NamedKey : byte
{
    Enter,
    Backspace,
    Tab,
    Escape,
    Space,
    ArrowUp,
    ArrowDown,
    ArrowLeft,
    ArrowRight,
    Control,
    Alt,
    Shift,
}

/// <summary>
/// Input events as sent over the custom UDP transport's input channels
/// (ENet reliable-ordered for everything except MouseMove, which -- like
/// the old app's separate unreliable "input-fast" datachannel -- rides an
/// unreliable/unordered channel since only the latest position matters).
/// Binary-encoded (see InputEventCodec), never JSON -- this is a
/// latency-sensitive hot path, unlike the signaling messages in
/// ClientMessage/ServerMessage.
///
/// X/Y are normalized 0..1 client-side (same convention as the old app);
/// the host denormalizes to physical pixels using the active display's
/// bounds -- see docs/ARCHITECTURE.md lesson #1: physical pixels
/// end-to-end, never DIPs.
/// </summary>
public abstract record InputEvent
{
    public sealed record MouseMove(float X, float Y) : InputEvent;

    public sealed record MouseDown(MouseButton Button, float X, float Y) : InputEvent;

    public sealed record MouseUp(MouseButton Button, float X, float Y) : InputEvent;

    public sealed record Wheel(float DeltaX, float DeltaY) : InputEvent;

    public sealed record KeyDown(KeyKind KeyKind, uint CodePointOrNamedKey, ModifierKeys HeldModifiers) : InputEvent;

    public sealed record KeyUp(KeyKind KeyKind, uint CodePointOrNamedKey, ModifierKeys HeldModifiers) : InputEvent;
}
