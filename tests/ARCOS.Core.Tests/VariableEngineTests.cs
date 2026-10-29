using ARCOS.Core.Engines;
using Xunit;

namespace ARCOS.Core.Tests;

public class VariableEngineTests
{
    [Fact]
    public void ScopeResolution_CurrentSectionAndGlobalFallback_ShouldWork()
    {
        var vars = new VariableEngine();
        vars.Set("SharedVar", "GlobalValue", "Global");
        vars.Set("LocalVar", "Thread1Value", "Thread1");

        // 當前分區在 Thread1：LocalVar 取 Thread1，SharedVar 回退取 Global
        Assert.Equal("Thread1Value", vars.Get("LocalVar", "Thread1"));
        Assert.Equal("GlobalValue", vars.Get("SharedVar", "Thread1"));

        // 明確指定分區
        Assert.Equal("GlobalValue", vars.Get("SharedVar@Global", "Thread1"));
    }

    [Fact]
    public void ArrayMode_CursorNavigation_ShouldReturnCurrentElement()
    {
        var vars = new VariableEngine();
        vars.Set("Items", "[Apple;Banana;Cherry]", "Global");

        // 預設游標在 0 (Apple)
        Assert.Equal("Apple", vars.Get("Items"));

        // 游標前進
        bool hasNext = vars.LMoveNext("Items");
        Assert.True(hasNext);
        Assert.Equal("Banana", vars.Get("Items"));

        hasNext = vars.LMoveNext("Items");
        Assert.True(hasNext);
        Assert.Equal("Cherry", vars.Get("Items"));

        // 到達末端
        hasNext = vars.LMoveNext("Items");
        Assert.False(hasNext);
    }

    [Fact]
    public void FilterParameters_ShouldSubstituteVariables()
    {
        var vars = new VariableEngine();
        vars.Set("Speed", "1500", "Global");
        vars.Set("Target", "Tray1", "Thread2");

        string template = "MOVE POS=$(Target) SPEED=$Speed@Global TIMEOUT=5";
        string result = vars.FilterParameters(template, "Thread2");

        Assert.Equal("MOVE POS=Tray1 SPEED=1500 TIMEOUT=5", result);
    }

    [Fact]
    public void SliceAndJoint_ShouldConvertBetweenStringAndArray()
    {
        var vars = new VariableEngine();
        vars.Set("CSV", "10,20,30", "Global");

        vars.Slice("CSV", ",", "Global");
        Assert.Equal("[10;20;30]", vars.GetRaw("CSV"));

        vars.Joint("CSV", "-", "Global");
        Assert.Equal("10-20-30", vars.GetRaw("CSV"));
    }

    [Fact]
    public void LFindIndexSet_ShouldSynchronizeLinkedArrays()
    {
        var vars = new VariableEngine();
        vars.Set("Barcodes", "[BC_A;BC_B;BC_C]", "Global");
        vars.Set("Weights", "[10.5;20.3;30.8]", "Global");
        vars.Set("Statuses", "[PASS;FAIL;PASS]", "Global");

        bool found = vars.LFindIndexSet("Barcodes", "BC_B", new[] { "Weights", "Statuses" });
        Assert.True(found);

        Assert.Equal("BC_B", vars.Get("Barcodes"));
        Assert.Equal("20.3", vars.Get("Weights"));
        Assert.Equal("FAIL", vars.Get("Statuses"));
    }

    [Fact]
    public void IncAndDec_ShouldPerformArithmetic()
    {
        var vars = new VariableEngine();
        vars.Set("Counter", "10", "Global");

        vars.Inc("Counter", 5);
        Assert.Equal("15", vars.Get("Counter"));

        vars.Dec("Counter", 2);
        Assert.Equal("13", vars.Get("Counter"));
    }
}
