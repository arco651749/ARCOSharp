using MVCON.Core.Engines;
using Xunit;

namespace MVCON.Core.Tests;

public class TimerAndCounterEngineTests
{
    [Fact]
    public void Timer_BasicTimeoutAndFlag_ShouldWork()
    {
        var io = new IoEngine();
        var timer = new TimerEngine(io);

        // 設置 0.05 秒逾時
        timer.TimerReset("TestTimer", 0.05);
        Assert.False(timer.TimerCheck("TestTimer"));
        Assert.False(io.GetPort("TestTimer#Timeout?"));

        // 等待 70ms
        Thread.Sleep(70);

        Assert.True(timer.TimerCheck("TestTimer"));
        Assert.True(io.GetPort("TestTimer#Timeout?"));
        Assert.True(timer.GetElapsedMs("TestTimer") >= 50);
    }

    [Fact]
    public void SystemPause_NormalTimer_ShouldFreezeElapsedTime()
    {
        var io = new IoEngine();
        var timer = new TimerEngine(io);

        // 普通計時器設置 0.1 秒
        timer.TimerReset("NormalTimer", 0.1);

        // 走時 20ms
        Thread.Sleep(20);

        // 進入全機暫停
        timer.SetSystemPause(true);
        Assert.True(io.GetPort("System#Pause?"));

        // 暫停期間沉睡 100ms
        Thread.Sleep(100);

        // 普通計時器在暫停期間凍結，此時不應逾時！
        Assert.False(timer.TimerCheck("NormalTimer"));
        Assert.False(io.GetPort("NormalTimer#Timeout?"));

        // 解除暫停
        timer.SetSystemPause(false);

        // 恢復後等待其餘 90ms 達到總計 110ms 有效時間
        Thread.Sleep(90);

        Assert.True(timer.TimerCheck("NormalTimer"));
    }

    [Fact]
    public void SystemPause_BypassTimerWithAmpersand_ShouldKeepRunningDuringPause()
    {
        var io = new IoEngine();
        var timer = new TimerEngine(io);

        // & 特權計時器（設置 0.06 秒）
        timer.TimerReset("Watchdog&", 0.06);

        // 立即進入全機暫停
        timer.SetSystemPause(true);

        // 即使在暫停期間，& 計時器依然持續走時！
        Thread.Sleep(80);

        // 應成功檢測到超時並將虛擬 IO 設為 true
        Assert.True(timer.TimerCheck("Watchdog&"));
        Assert.True(io.GetPort("Watchdog&#Timeout?"));
    }

    [Fact]
    public void CounterEngine_DecrementAndOverFlag_ShouldWork()
    {
        var io = new IoEngine();
        var counter = new CounterEngine(io);

        counter.CounterReset("ProdCount", 2);
        Assert.Equal(2, counter.GetCount("ProdCount"));
        Assert.False(io.GetPort("ProdCount#Over?"));

        // 第 1 次遞減：剩餘 1，未結束
        bool over1 = counter.CounterCheck("ProdCount");
        Assert.False(over1);
        Assert.Equal(1, counter.GetCount("ProdCount"));
        Assert.False(io.GetPort("ProdCount#Over?"));

        // 第 2 次遞減：剩餘 0，已結束
        bool over2 = counter.CounterCheck("ProdCount");
        Assert.True(over2);
        Assert.Equal(0, counter.GetCount("ProdCount"));
        Assert.True(io.GetPort("ProdCount#Over?"));
    }
}
