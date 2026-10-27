using MVCON.Core.Engines;
using MVCON.Core.Model;
using Xunit;

namespace MVCON.Core.Tests;

public class DaqEngineTests
{
    [Fact]
    public void SetAndAppendSamples_ShouldAccumulateWaveformData()
    {
        var io = new IoEngine();
        var daq = new DaqEngine(io);

        daq.SetWaveform("Force1", DateTime.Now, 0.001, new double[] { 1.0, 2.0, 3.0 });
        Assert.Equal(3.0, daq.GetInstantValue("Force1"));

        daq.AppendSamples("Force1", new double[] { 4.0, 5.0 }.AsSpan());
        Assert.Equal(5.0, daq.GetInstantValue("Force1"));

        var wf = daq.GetWaveform("Force1");
        Assert.Equal(5, wf.GetOrCreateChannel(0).Y.Length);
    }

    [Fact]
    public void CompareMaxMinInRange_ShouldVerifyTolerances()
    {
        var io = new IoEngine();
        var daq = new DaqEngine(io);

        daq.SetWaveform("PressCurve", DateTime.Now, 0.001, new double[] { 10.0, 50.0, 90.0, 20.0 });

        // 範圍 0 ~ 100：Pass
        bool ok = daq.CompareMaxMinInRange("PressCurve", max: 100.0, min: 0.0);
        Assert.True(ok);
        Assert.True(io.GetPort("PressCurve#OK?"));

        // 範圍 0 ~ 80：Fail (最大值 90 超出上限 80)
        bool fail = daq.CompareMaxMinInRange("PressCurve", max: 80.0, min: 0.0);
        Assert.False(fail);
        Assert.False(io.GetPort("PressCurve#OK?"));
    }

    [Fact]
    public void CompareCoverBy_ShouldCheckEnvelope()
    {
        var io = new IoEngine();
        var daq = new DaqEngine(io);

        // 實際波形
        daq.SetWaveform("Actual", DateTime.Now, 0.001, new double[] { 10, 20, 30 });
        // 上限包絡線
        daq.SetWaveform("UpperSpec", DateTime.Now, 0.001, new double[] { 15, 25, 35 });
        // 下限包絡線
        daq.SetWaveform("LowerSpec", DateTime.Now, 0.001, new double[] { 5, 15, 25 });

        bool withinEnvelope = daq.CompareCoverBy("Actual", "UpperSpec", "LowerSpec");
        Assert.True(withinEnvelope);
        Assert.True(io.GetPort("Actual#OK?"));

        // 修改實際波形使其超出上限
        daq.SetWaveform("Actual", DateTime.Now, 0.001, new double[] { 10, 40, 30 });
        bool exceeded = daq.CompareCoverBy("Actual", "UpperSpec", "LowerSpec");
        Assert.False(exceeded);
        Assert.False(io.GetPort("Actual#OK?"));
    }

    [Fact]
    public void ApplyFilter_MovingAverage_ShouldSmoothData()
    {
        var daq = new DaqEngine();
        // 帶毛刺的資料：[10, 100, 10]
        daq.SetWaveform("NoisySignal", DateTime.Now, 0.001, new double[] { 10.0, 100.0, 10.0 });

        daq.ApplyFilter("NoisySignal", DaqFilterType.MovingAverage, param: 3);

        var wf = daq.GetWaveform("NoisySignal");
        var filteredY = wf.GetOrCreateChannel(0).Y;
        // 中間毛刺 100 應被平滑化為 (10 + 100 + 10) / 3 = 40
        Assert.Equal(40.0, filteredY[1], precision: 1);
    }
}
