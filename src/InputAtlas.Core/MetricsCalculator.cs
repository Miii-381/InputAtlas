namespace InputAtlas.Core;

public static class MetricsCalculator
{
    public static InputMetrics Calculate(IEnumerable<BucketSnapshot> snapshots)
    {
        long keyboard = 0;
        long mouse = 0;
        long wheel = 0;

        foreach (var snapshot in snapshots)
        {
            foreach (var item in snapshot.Counts)
            {
                switch (GetCategory(item.Key))
                {
                    case InputCategory.Keyboard:
                        keyboard = SaturatingAdd(keyboard, item.Value);
                        break;
                    case InputCategory.MouseButton:
                        mouse = SaturatingAdd(mouse, item.Value);
                        break;
                    case InputCategory.Wheel:
                        wheel = SaturatingAdd(wheel, item.Value);
                        break;
                }
            }
        }

        var activityUnits = SaturatingAdd(
            SaturatingMultiply(SaturatingAdd(keyboard, mouse), 10),
            wheel);
        return new InputMetrics(keyboard, mouse, wheel, activityUnits);
    }

    public static InputCategory GetCategory(InputId input)
    {
        if (input == InputId.OtherKeyboard || input.Value is > 0 and <= 0xE7)
        {
            return InputCategory.Keyboard;
        }

        if (input.Value is >= 1001 and <= 1005)
        {
            return InputCategory.MouseButton;
        }

        if (input.Value is >= 1011 and <= 1014)
        {
            return InputCategory.Wheel;
        }

        return InputCategory.Other;
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long SaturatingMultiply(long value, long multiplier) =>
        value > long.MaxValue / multiplier ? long.MaxValue : value * multiplier;
}
