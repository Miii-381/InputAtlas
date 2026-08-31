using System.Collections.ObjectModel;

namespace InputAtlas.Core;

public sealed class InputCounterEngine
{
    private const int CounterCapacity = 1100;
    private readonly long[] _counts = new long[CounterCapacity];
    private readonly bool[] _pressed = new bool[CounterCapacity];
    private int _verticalPositiveRemainder;
    private int _verticalNegativeRemainder;
    private int _horizontalPositiveRemainder;
    private int _horizontalNegativeRemainder;
    private int _coverageSeconds;
    private long _bucketStartUtc;

    public InputCounterEngine(long initialUnixSeconds)
    {
        _bucketStartUtc = TimeBuckets.AlignFiveMinutes(initialUnixSeconds);
    }

    public event Action<InputId>? Counted;

    public event Action<InputId, bool>? StateChanged;

    public event Action<BucketSnapshot>? BucketCompleted;

    public long BucketStartUtc => Volatile.Read(ref _bucketStartUtc);

    public void HandleKeyboard(in RawKeyboardSample sample, long unixSeconds)
    {
        EnsureBucket(unixSeconds);
        var input = KeyboardScanCodeMapper.Map(sample);
        var index = input.Value;

        if (sample.IsBreak)
        {
            if (Volatile.Read(ref _pressed[index]))
            {
                Volatile.Write(ref _pressed[index], false);
                StateChanged?.Invoke(input, false);
            }

            return;
        }

        if (Volatile.Read(ref _pressed[index]))
        {
            return;
        }

        Volatile.Write(ref _pressed[index], true);
        StateChanged?.Invoke(input, true);
        Increment(input);
    }

    public void HandleMouse(in RawMouseSample sample, long unixSeconds)
    {
        EnsureBucket(unixSeconds);
        UpdateButton(sample.Buttons, RawMouseButtons.LeftDown, RawMouseButtons.LeftUp, InputId.MouseLeft);
        UpdateButton(sample.Buttons, RawMouseButtons.RightDown, RawMouseButtons.RightUp, InputId.MouseRight);
        UpdateButton(sample.Buttons, RawMouseButtons.MiddleDown, RawMouseButtons.MiddleUp, InputId.MouseMiddle);
        UpdateButton(sample.Buttons, RawMouseButtons.Button4Down, RawMouseButtons.Button4Up, InputId.MouseForward);
        UpdateButton(sample.Buttons, RawMouseButtons.Button5Down, RawMouseButtons.Button5Up, InputId.MouseBack);

        if ((sample.Buttons & RawMouseButtons.VerticalWheel) != 0)
        {
            ProcessWheel(sample.ButtonData, true);
        }

        if ((sample.Buttons & RawMouseButtons.HorizontalWheel) != 0)
        {
            ProcessWheel(sample.ButtonData, false);
        }
    }

    public void AddCoverage(int seconds, long unixSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        EnsureBucket(unixSeconds);
        var updated = Math.Min(300, checked(Volatile.Read(ref _coverageSeconds) + seconds));
        Volatile.Write(ref _coverageSeconds, updated);
    }

    public BucketSnapshot Snapshot(long updatedUtc)
    {
        var counts = new SortedDictionary<InputId, long>();
        for (ushort index = 1; index < _counts.Length; index++)
        {
            var value = Volatile.Read(ref _counts[index]);
            if (value > 0)
            {
                counts[new InputId(index)] = value;
            }
        }

        return new BucketSnapshot(
            Volatile.Read(ref _bucketStartUtc),
            Volatile.Read(ref _coverageSeconds),
            new ReadOnlyDictionary<InputId, long>(counts),
            updatedUtc);
    }

    public void ResetPressedStates()
    {
        for (ushort index = 1; index < _pressed.Length; index++)
        {
            if (!Volatile.Read(ref _pressed[index]))
            {
                continue;
            }

            Volatile.Write(ref _pressed[index], false);
            StateChanged?.Invoke(new InputId(index), false);
        }
    }

    private void EnsureBucket(long unixSeconds)
    {
        var desired = TimeBuckets.AlignFiveMinutes(unixSeconds);
        if (desired == _bucketStartUtc)
        {
            return;
        }

        BucketCompleted?.Invoke(Snapshot(unixSeconds));
        Array.Clear(_counts);
        Volatile.Write(ref _coverageSeconds, 0);
        Volatile.Write(ref _bucketStartUtc, desired);
    }

    private void UpdateButton(
        RawMouseButtons flags,
        RawMouseButtons down,
        RawMouseButtons up,
        InputId input)
    {
        var index = input.Value;
        if ((flags & up) != 0 && Volatile.Read(ref _pressed[index]))
        {
            Volatile.Write(ref _pressed[index], false);
            StateChanged?.Invoke(input, false);
        }

        if ((flags & down) == 0 || Volatile.Read(ref _pressed[index]))
        {
            return;
        }

        Volatile.Write(ref _pressed[index], true);
        StateChanged?.Invoke(input, true);
        Increment(input);
    }

    private void ProcessWheel(short delta, bool vertical)
    {
        if (delta == 0)
        {
            return;
        }

        if (vertical)
        {
            if (delta > 0)
            {
                _verticalPositiveRemainder += delta;
                ConsumeWheel(ref _verticalPositiveRemainder, InputId.WheelUp);
            }
            else
            {
                _verticalNegativeRemainder += -delta;
                ConsumeWheel(ref _verticalNegativeRemainder, InputId.WheelDown);
            }
        }
        else if (delta > 0)
        {
            _horizontalPositiveRemainder += delta;
            ConsumeWheel(ref _horizontalPositiveRemainder, InputId.WheelRight);
        }
        else
        {
            _horizontalNegativeRemainder += -delta;
            ConsumeWheel(ref _horizontalNegativeRemainder, InputId.WheelLeft);
        }
    }

    private void ConsumeWheel(ref int remainder, InputId input)
    {
        while (remainder >= 120)
        {
            remainder -= 120;
            StateChanged?.Invoke(input, true);
            Increment(input);
            StateChanged?.Invoke(input, false);
        }
    }

    private void Increment(InputId input)
    {
        var index = input.Value;
        var current = Volatile.Read(ref _counts[index]);
        if (current == long.MaxValue)
        {
            return;
        }

        Interlocked.Increment(ref _counts[index]);
        Counted?.Invoke(input);
    }
}
