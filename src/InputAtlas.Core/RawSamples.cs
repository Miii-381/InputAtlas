namespace InputAtlas.Core;

public readonly record struct RawKeyboardSample(
    ushort MakeCode,
    ushort VirtualKey,
    bool IsExtended0,
    bool IsExtended1,
    bool IsBreak);

[Flags]
public enum RawMouseButtons : ushort
{
    None = 0,
    LeftDown = 0x0001,
    LeftUp = 0x0002,
    RightDown = 0x0004,
    RightUp = 0x0008,
    MiddleDown = 0x0010,
    MiddleUp = 0x0020,
    Button4Down = 0x0040,
    Button4Up = 0x0080,
    Button5Down = 0x0100,
    Button5Up = 0x0200,
    VerticalWheel = 0x0400,
    HorizontalWheel = 0x0800,
}

public readonly record struct RawMouseSample(RawMouseButtons Buttons, short ButtonData);

