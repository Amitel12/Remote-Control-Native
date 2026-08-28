using System.Buffers.Binary;

namespace RemoteControl.Protocol;

/// <summary>Fixed-size little-endian binary encoding for InputEvent -- see InputEvent.cs for why this isn't JSON.</summary>
public static class InputEventCodec
{
    private enum Tag : byte
    {
        MouseMove = 0,
        MouseDown = 1,
        MouseUp = 2,
        Wheel = 3,
        KeyDown = 4,
        KeyUp = 5,
    }

    /// <summary>Largest single encoded event -- callers can size a stack/pooled buffer to this instead of guessing.</summary>
    public const int MaxSize = 10;

    public static int Encode(InputEvent inputEvent, Span<byte> destination)
    {
        switch (inputEvent)
        {
            case InputEvent.MouseMove(var x, var y):
                RequireSize(destination, 9);
                destination[0] = (byte)Tag.MouseMove;
                BinaryPrimitives.WriteSingleLittleEndian(destination[1..5], x);
                BinaryPrimitives.WriteSingleLittleEndian(destination[5..9], y);
                return 9;

            case InputEvent.MouseDown(var button, var x, var y):
                RequireSize(destination, 10);
                destination[0] = (byte)Tag.MouseDown;
                destination[1] = (byte)button;
                BinaryPrimitives.WriteSingleLittleEndian(destination[2..6], x);
                BinaryPrimitives.WriteSingleLittleEndian(destination[6..10], y);
                return 10;

            case InputEvent.MouseUp(var button, var x, var y):
                RequireSize(destination, 10);
                destination[0] = (byte)Tag.MouseUp;
                destination[1] = (byte)button;
                BinaryPrimitives.WriteSingleLittleEndian(destination[2..6], x);
                BinaryPrimitives.WriteSingleLittleEndian(destination[6..10], y);
                return 10;

            case InputEvent.Wheel(var dx, var dy):
                RequireSize(destination, 9);
                destination[0] = (byte)Tag.Wheel;
                BinaryPrimitives.WriteSingleLittleEndian(destination[1..5], dx);
                BinaryPrimitives.WriteSingleLittleEndian(destination[5..9], dy);
                return 9;

            case InputEvent.KeyDown(var keyKind, var code, var modifiers):
                RequireSize(destination, 7);
                destination[0] = (byte)Tag.KeyDown;
                destination[1] = (byte)keyKind;
                BinaryPrimitives.WriteUInt32LittleEndian(destination[2..6], code);
                destination[6] = (byte)modifiers;
                return 7;

            case InputEvent.KeyUp(var keyKind, var code, var modifiers):
                RequireSize(destination, 7);
                destination[0] = (byte)Tag.KeyUp;
                destination[1] = (byte)keyKind;
                BinaryPrimitives.WriteUInt32LittleEndian(destination[2..6], code);
                destination[6] = (byte)modifiers;
                return 7;

            default:
                throw new ArgumentOutOfRangeException(nameof(inputEvent), inputEvent, "Unknown InputEvent subtype.");
        }
    }

    public static InputEvent Decode(ReadOnlySpan<byte> source)
    {
        if (source.Length < 1) throw new ArgumentException("Source is empty.", nameof(source));
        var tag = (Tag)source[0];
        return tag switch
        {
            Tag.MouseMove => new InputEvent.MouseMove(
                BinaryPrimitives.ReadSingleLittleEndian(source[1..5]),
                BinaryPrimitives.ReadSingleLittleEndian(source[5..9])),

            Tag.MouseDown => new InputEvent.MouseDown(
                (MouseButton)source[1],
                BinaryPrimitives.ReadSingleLittleEndian(source[2..6]),
                BinaryPrimitives.ReadSingleLittleEndian(source[6..10])),

            Tag.MouseUp => new InputEvent.MouseUp(
                (MouseButton)source[1],
                BinaryPrimitives.ReadSingleLittleEndian(source[2..6]),
                BinaryPrimitives.ReadSingleLittleEndian(source[6..10])),

            Tag.Wheel => new InputEvent.Wheel(
                BinaryPrimitives.ReadSingleLittleEndian(source[1..5]),
                BinaryPrimitives.ReadSingleLittleEndian(source[5..9])),

            Tag.KeyDown => new InputEvent.KeyDown(
                (KeyKind)source[1],
                BinaryPrimitives.ReadUInt32LittleEndian(source[2..6]),
                (ModifierKeys)source[6]),

            Tag.KeyUp => new InputEvent.KeyUp(
                (KeyKind)source[1],
                BinaryPrimitives.ReadUInt32LittleEndian(source[2..6]),
                (ModifierKeys)source[6]),

            _ => throw new ArgumentOutOfRangeException(nameof(source), tag, "Unknown wire tag byte."),
        };
    }

    private static void RequireSize(Span<byte> destination, int required)
    {
        if (destination.Length < required)
            throw new ArgumentException($"Destination must be at least {required} bytes, got {destination.Length}.", nameof(destination));
    }
}
