using RemoteControl.Protocol;

namespace RemoteControl.Input;

/// <summary>
/// Bit layout for the held-button/held-key snapshot sent as
/// <c>LanDatagramCodec.CreateInputStateSync</c>'s payload -- shared between
/// <see cref="RawInputCapture"/> (which builds it) and <see cref="InputInjector"/>
/// (which reconciles against it) so the two can't disagree on which bit means
/// what. Bits 0-2: MouseButton. Bits 3-14: NamedKey, offset by 3.
/// </summary>
internal static class InputHeldStateMask
{
    public static ushort SetButton(ushort mask, MouseButton button) => (ushort)(mask | (1 << (int)button));

    public static bool HasButton(ushort mask, MouseButton button) => (mask & (1 << (int)button)) != 0;

    public static ushort SetNamedKey(ushort mask, NamedKey namedKey) => (ushort)(mask | (1 << (3 + (int)namedKey)));

    public static bool HasNamedKey(ushort mask, NamedKey namedKey) => (mask & (1 << (3 + (int)namedKey))) != 0;
}
