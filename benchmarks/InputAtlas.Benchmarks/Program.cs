using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using InputAtlas.Core;

namespace InputAtlas.Benchmarks;

internal static class Program
{
    public static void Main()
    {
        // 保留用户当前的电源设置，禁用 BenchmarkDotNet 默认的高性能计划切换。
        var config = ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(Job.Default.DontEnforcePowerPlan());
        BenchmarkRunner.Run<InputHotPathBenchmarks>(config);
    }
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
