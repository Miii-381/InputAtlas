using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using InputAtlas.Core;

namespace InputAtlas.Benchmarks;

internal static class Program
{
    public static void Main() => BenchmarkRunner.Run<InputHotPathBenchmarks>();
}

[MemoryDiagnoser]
public class InputHotPathBenchmarks
{
    private readonly InputCounterEngine _engine = new(1_800_000_000);
    private readonly RawKeyboardSample _down = new(0x11, 0x57, false, false, false);
    private readonly RawKeyboardSample _up = new(0x11, 0x57, false, false, true);
    private readonly RawMouseSample _wheel = new(RawMouseButtons.VerticalWheel, 120);

    [Benchmark]
    public void KeyboardDownUp()
    {
        _engine.HandleKeyboard(_down, 1_800_000_000);
        _engine.HandleKeyboard(_up, 1_800_000_000);
    }

    [Benchmark]
    public void WheelStep() => _engine.HandleMouse(_wheel, 1_800_000_000);

    [Benchmark]
    public InputId ScanCodeMapping() => KeyboardScanCodeMapper.Map(_down);
}
