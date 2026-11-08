using System.Threading;
using ARCOS.Core.Engines;
using Xunit;

namespace ARCOS.Core.Tests;

public class RecordEngineTests
{
    [Fact]
    public void RecordEngine_DefaultChannel_HasSystemLogWith2000Capacity()
    {
        var engine = new RecordEngine();
        var names = engine.GetAllRecordNames();

        Assert.Contains("SYSTEMLOG", names);
        var sysLog = engine.GetChannel("SYSTEMLOG");
        Assert.NotNull(sysLog);
        Assert.Equal(2000, sysLog.Capacity);
        Assert.Equal(2000, sysLog.Entries.Count);
        Assert.Equal(0, sysLog.Index);
    }

    [Fact]
    public void RecordEngine_Record_WritesEntriesAndIncrementsIndex()
    {
        var engine = new RecordEngine();
        engine.Record("cycletime", "start");
        engine.Record("cycletime", "Step1_Inspect");

        var ch = engine.GetChannel("cycletime");
        Assert.NotNull(ch);
        Assert.Equal(2, ch.Index);
        Assert.Equal("start", ch.Entries[0].Message);
        Assert.Equal("Step1_Inspect", ch.Entries[1].Message);
        Assert.NotEqual("00:00:00.000", ch.Entries[0].Timestamp);
    }

    [Fact]
    public void RecordEngine_RingBuffer_WrapsAroundAt2000Slots()
    {
        var engine = new RecordEngine();
        // 寫入 2050 筆，驗證環狀槽位覆蓋
        for (int i = 0; i < 2050; i++)
        {
            engine.Record("TestRing", $"Msg_{i}");
        }

        var ch = engine.GetChannel("TestRing");
        Assert.NotNull(ch);
        Assert.Equal(2050, ch.Index);
        Assert.Equal(2000, ch.Entries.Count); // 槽位數量恆為 2000
        Assert.Equal("Msg_2049", ch.Entries[49].Message); // 2049 % 2000 = 49
    }

    [Fact]
    public void RecordEngine_Clear_ResetsAllSlotsAndIndex()
    {
        var engine = new RecordEngine();
        engine.Record("Station1", "start");
        engine.Record("Station1", "inspect");
        Assert.Equal(2, engine.GetChannel("Station1")!.Index);

        engine.Record("Station1", "CLEAR");
        var ch = engine.GetChannel("Station1")!;
        Assert.Equal(0, ch.Index);
        Assert.Equal(2000, ch.Entries.Count);
        Assert.Equal("00:00:00.000", ch.Entries[0].Timestamp);
        Assert.Equal(string.Empty, ch.Entries[0].Message);
    }

    [Fact]
    public void RecordEngine_CycleTime_CalculatesCorrectly()
    {
        var engine = new RecordEngine();
        engine.Record("Benchmark", "Start");
        Thread.Sleep(50);
        engine.Record("Benchmark", "End");

        double elapsed = engine.GetRecordElapsedSeconds("Benchmark");
        Assert.True(elapsed >= 0.04);
    }
}
