using System.Linq;
using RemoteControl.Protocol;

namespace RemoteControl.Input;

/// <summary>Single source of truth for NamedKey&lt;-&gt;VK, shared by InputInjector (NamedKey -> VK) and RawInputCapture (VK -> NamedKey) so the two can never drift apart.</summary>
internal static class NamedKeyMapping
{
    public static readonly IReadOnlyDictionary<NamedKey, (ushort Vk, bool Extended)> ByNamedKey = new Dictionary<NamedKey, (ushort, bool)>
    {
        [NamedKey.Enter] = (0x0D, false),
        [NamedKey.Backspace] = (0x08, false),
        [NamedKey.Tab] = (0x09, false),
        [NamedKey.Escape] = (0x1B, false),
        [NamedKey.Space] = (0x20, false),
        [NamedKey.ArrowUp] = (0x26, true),
        [NamedKey.ArrowDown] = (0x28, true),
        [NamedKey.ArrowLeft] = (0x25, true),
        [NamedKey.ArrowRight] = (0x27, true),
        [NamedKey.Control] = (0x11, false),
        [NamedKey.Alt] = (0x12, false),
        [NamedKey.Shift] = (0x10, false),
    };

    public static readonly IReadOnlyDictionary<ushort, NamedKey> ByVk =
        ByNamedKey.ToDictionary(kv => kv.Value.Vk, kv => kv.Key);
}
