namespace InputAtlas.Core;

public readonly record struct InputId(ushort Value) : IComparable<InputId>
{
    public static readonly InputId OtherKeyboard = new(900);
    public static readonly InputId UnobservableFn = new(901);
    public static readonly InputId MouseLeft = new(1001);
    public static readonly InputId MouseRight = new(1002);
    public static readonly InputId MouseMiddle = new(1003);
    public static readonly InputId MouseBack = new(1004);
    public static readonly InputId MouseForward = new(1005);
    public static readonly InputId WheelUp = new(1011);
    public static readonly InputId WheelDown = new(1012);
    public static readonly InputId WheelLeft = new(1013);
    public static readonly InputId WheelRight = new(1014);
    public static readonly InputId OtherMouse = new(1099);

    public int CompareTo(InputId other) => Value.CompareTo(other.Value);

    public static bool operator <(InputId left, InputId right) => left.Value < right.Value;

    public static bool operator <=(InputId left, InputId right) => left.Value <= right.Value;

    public static bool operator >(InputId left, InputId right) => left.Value > right.Value;

    public static bool operator >=(InputId left, InputId right) => left.Value >= right.Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
