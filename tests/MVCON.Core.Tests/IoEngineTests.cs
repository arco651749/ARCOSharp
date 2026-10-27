using MVCON.Core.Engines;
using Xunit;

namespace MVCON.Core.Tests;

public class IoEngineTests
{
    [Fact]
    public void PhysicalPort_ReadWrite_ShouldWork()
    {
        var io = new IoEngine();
        Assert.False(io.GetPort("P0.0"));

        io.SetPort("P0.0", true);
        Assert.True(io.GetPort("P0.0"));

        // 動態擴展點位
        io.SetPort("P100.5", true);
        Assert.True(io.GetPort("P100.5"));
    }

    [Fact]
    public void AliasMapping_ShouldSynchronizeBidirectionally()
    {
        var io = new IoEngine();
        io.SetPortName("P1.2", "StartBtn?");

        io.SetPort("StartBtn?", true);
        Assert.True(io.GetPort("P1.2"));
        Assert.True(io.GetPort("StartBtn?"));

        io.SetPort("P1.2", false);
        Assert.False(io.GetPort("StartBtn?"));
    }

    [Fact]
    public void Constants_TrueAndFalse_ShouldBeReadOnly()
    {
        var io = new IoEngine();
        Assert.True(io.GetPort("True"));
        Assert.False(io.GetPort("False"));

        // 寫入常數應被忽略
        io.SetPort("True", false);
        Assert.True(io.GetPort("True"));
    }

    [Fact]
    public void VirtualIO_AutoCreationAndSystemFlag_ShouldWork()
    {
        var io = new IoEngine();
        io.SetPort("Cam0#OK?", true);
        Assert.True(io.GetPort("Cam0#OK?"));

        io.AddVirtualIO("CustomFlag?", true);
        Assert.True(io.GetPort("CustomFlag?"));
    }

    [Fact]
    public void Shift_ShouldShiftValuesAlongList()
    {
        var io = new IoEngine();
        var ports = new[] { "P0.0", "P0.1", "P0.2", "P0.3" };

        io.SetPort("P0.0", true);
        io.SetPort("P0.1", false);
        io.SetPort("P0.2", true);
        io.SetPort("P0.3", false);

        // 正向移位 1 位 (P0.1 接收 P0.0 的值)
        io.Shift(ports, 1);

        Assert.False(io.GetPort("P0.0")); // 邊界補 false
        Assert.True(io.GetPort("P0.1"));
        Assert.False(io.GetPort("P0.2"));
        Assert.True(io.GetPort("P0.3"));
    }

    [Fact]
    public void BatchTransport_GetAndSet_ShouldMatchContinuousBits()
    {
        var io = new IoEngine();
        var inputs = new bool[] { true, false, true, true, false, false, true, false, true, true };

        io.SetBatch("P0.0", inputs);

        var outputs = io.GetBatch("P0.0", 10);
        Assert.Equal(inputs, outputs);
    }

    [Fact]
    public void IoChanged_Event_ShouldTriggerOnActualChange()
    {
        var io = new IoEngine();
        int changeCount = 0;
        string lastPort = "";
        bool lastVal = false;

        io.IoChanged += (port, alias, val) =>
        {
            changeCount++;
            lastPort = port;
            lastVal = val;
        };

        io.SetPort("P2.0", true);
        Assert.Equal(1, changeCount);
        Assert.Equal("P2.0", lastPort);
        Assert.True(lastVal);

        // 寫入相同值不應觸發事件
        io.SetPort("P2.0", true);
        Assert.Equal(1, changeCount);
    }
}
