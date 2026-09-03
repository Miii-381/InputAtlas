using InputAtlas.Core;

namespace InputAtlas.App.Pet;

public enum PetInputAction
{
    None,
    KeyboardLeft,
    KeyboardRight,
    KeyboardBoth,
    MouseLeft,
    MouseRight,
    MouseMiddle,
    MouseBack,
    MouseForward,
}

public static class PetInputMapper
{
    private static readonly HashSet<ushort> LeftHandKeys =
    [
        0x14, 0x1A, 0x08, 0x15, 0x17,       // Q, W, E, R, T
        0x04, 0x16, 0x07, 0x09, 0x0A,       // A, S, D, F, G
        0x1D, 0x1B, 0x06, 0x19, 0x05,       // Z, X, C, V, B
        0x1E, 0x1F, 0x20, 0x21, 0x22,       // 1-5
        0x29, 0x2B, 0x35, 0x39,             // Esc, Tab, `, CapsLock
        0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, // F1-F6
        0xE0, 0xE1, 0xE2, 0xE3,             // left Ctrl/Shift/Alt/GUI
    ];

    private static readonly HashSet<ushort> RightHandKeys =
    [
        0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, // H-M
        0x11, 0x12, 0x13, 0x18, 0x1C,       // N, O, P, U, Y
        0x23, 0x24, 0x25, 0x26, 0x27,       // 6-0
        0x2A, 0x28, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, // editing/punctuation
        0x36, 0x37, 0x38,
        0x40, 0x41, 0x42, 0x43, 0x44, 0x45, // F7-F12
        0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, // navigation
        0x4F, 0x50, 0x51, 0x52,             // arrows
        0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B,
        0x5C, 0x5D, 0x5E, 0x5F, 0x60, 0x61, 0x62, 0x63, 0x67, // numpad
        0x65,                               // application/menu
        0xE4, 0xE5, 0xE6, 0xE7,            // right Ctrl/Shift/Alt/GUI
    ];

    public static PetInputAction Map(InputId input)
    {
        if (input == InputId.MouseLeft)
        {
            return PetInputAction.MouseLeft;
        }

        if (input == InputId.MouseRight)
        {
            return PetInputAction.MouseRight;
        }

        if (input == InputId.MouseMiddle)
        {
            return PetInputAction.MouseMiddle;
        }

        if (input == InputId.MouseBack)
        {
            return PetInputAction.MouseBack;
        }

        if (input == InputId.MouseForward)
        {
            return PetInputAction.MouseForward;
        }

        if (input.Value is >= 1000)
        {
            return PetInputAction.None;
        }

        if (input.Value == 0x2C || input == InputId.OtherKeyboard)
        {
            return PetInputAction.KeyboardBoth;
        }

        if (LeftHandKeys.Contains(input.Value))
        {
            return PetInputAction.KeyboardLeft;
        }

        return RightHandKeys.Contains(input.Value)
            ? PetInputAction.KeyboardRight
            : PetInputAction.KeyboardBoth;
    }
}
