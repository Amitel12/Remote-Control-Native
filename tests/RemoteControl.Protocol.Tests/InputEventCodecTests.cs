using RemoteControl.Protocol;
using Xunit;

namespace RemoteControl.Protocol.Tests;

public class InputEventCodecTests
{
    [Theory]
    [InlineData(0.5f, 0.25f)]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    public void MouseMove_RoundTrips(float x, float y)
    {
        AssertRoundTrips(new InputEvent.MouseMove(x, y));
    }

    [Fact]
    public void MouseDown_RoundTrips_WithButton()
    {
        AssertRoundTrips(new InputEvent.MouseDown(MouseButton.Right, 0.1f, 0.9f));
    }

    [Fact]
    public void MouseUp_RoundTrips_WithButton()
    {
        AssertRoundTrips(new InputEvent.MouseUp(MouseButton.Middle, 0.5f, 0.5f));
    }

    [Fact]
    public void Wheel_RoundTrips()
    {
        AssertRoundTrips(new InputEvent.Wheel(-3.5f, 12.0f));
    }

    [Fact]
    public void KeyDown_Character_RoundTrips_WithCodePointAndModifiers()
    {
        // 'd' with no modifiers -- the plain-typing path per lesson #2.
        AssertRoundTrips(new InputEvent.KeyDown(KeyKind.Character, (uint)'d', ModifierKeys.None));
    }

    [Fact]
    public void KeyDown_Named_RoundTrips_WithModifiers()
    {
        // Ctrl+C shortcut path: Named 'C' equivalent would actually be Character 'c' with Control held --
        // here exercising a genuinely Named key (Enter) held with a modifier to prove the modifier bits survive.
        AssertRoundTrips(new InputEvent.KeyDown(KeyKind.Named, (uint)NamedKey.Enter, ModifierKeys.Control | ModifierKeys.Shift));
    }

    [Fact]
    public void KeyUp_RoundTrips()
    {
        AssertRoundTrips(new InputEvent.KeyUp(KeyKind.Character, (uint)'x', ModifierKeys.Alt));
    }

    [Fact]
    public void Encode_ReturnsSizeWithinDeclaredMaxSize()
    {
        Span<byte> buffer = stackalloc byte[InputEventCodec.MaxSize];
        var written = InputEventCodec.Encode(new InputEvent.MouseDown(MouseButton.Left, 0.5f, 0.5f), buffer);
        Assert.True(written <= InputEventCodec.MaxSize);
    }

    [Fact]
    public void Encode_ThrowsOnUndersizedBuffer()
    {
        var tooSmall = new byte[2];
        Assert.Throws<ArgumentException>(() => InputEventCodec.Encode(new InputEvent.MouseMove(0.5f, 0.5f), tooSmall));
    }

    private static void AssertRoundTrips(InputEvent original)
    {
        Span<byte> buffer = stackalloc byte[InputEventCodec.MaxSize];
        var written = InputEventCodec.Encode(original, buffer);
        var decoded = InputEventCodec.Decode(buffer[..written]);
        Assert.Equal(original, decoded);
    }
}
