using System.Collections.Generic;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using ARCOS.Core.Execution;
using ARCOS.Core.Execution.Handlers;
using Xunit;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Core.Tests;

public class VariableEngineTests
{
    private (BasicEngines Engines, ExecutionContext Context, VariableModuleHandler Handler) CreateTestEnv()
    {
        var io = new IoEngine();
        var vars = new VariableEngine();
        var point = new PointEngine(io);
        var image = new ImageEngine(io);
        var daq = new DaqEngine(io);
        var timer = new TimerEngine(io);

        var engines = new BasicEngines(io, vars, point, image, daq, timer);
        var prog = new ProgramTable();
        var drivers = new DriverManager();
        var ctx = new ExecutionContext(1, "Thread1", prog, engines, drivers) { CurrentSection = "1" };
        var handler = new VariableModuleHandler();

        return (engines, ctx, handler);
    }

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

    [Fact]
    public void Handler_Calculate_ShouldComputeMathFormulaCorrectly()
    {
        var (engines, ctx, handler) = CreateTestEnv();
        engines.Variable.Set("A", "10", "1");
        engines.Variable.Set("B", "25", "1");

        handler.Execute("Calculate", "Total=A+B*2", ctx);
        Assert.Equal("60", engines.Variable.Get("Total", "1"));
    }

    [Fact]
    public void Handler_Select_TernaryLogicEvaluation_ShouldChooseCorrectBranch()
    {
        var (engines, ctx, handler) = CreateTestEnv();
        engines.Variable.Set("Count", "15", "1");

        // 條件成立: Count > 10 為真，選擇 PASS
        handler.Execute("Select", "Result=Count>10 ? PASS : FAIL", ctx);
        Assert.Equal("PASS", engines.Variable.Get("Result", "1"));

        // 條件不成立: Count < 5 為假，選擇 FAIL
        handler.Execute("Select", "Result=Count<5 ? PASS : FAIL", ctx);
        Assert.Equal("FAIL", engines.Variable.Get("Result", "1"));
    }

    [Fact]
    public void Handler_Shift_ShouldShiftValuesAcrossVariables()
    {
        var (engines, ctx, handler) = CreateTestEnv();
        engines.Variable.Set("V1", "First", "1");
        engines.Variable.Set("V2", "Second", "1");
        engines.Variable.Set("V3", "Third", "1");

        handler.Execute("Shift", "V1,V2,V3,Shift=1", ctx);

        Assert.Equal("Third", engines.Variable.Get("V1", "1"));
        Assert.Equal("First", engines.Variable.Get("V2", "1"));
        Assert.Equal("Second", engines.Variable.Get("V3", "1"));
    }

    [Fact]
    public void Handler_ArrayQueue_PushAndPopOperations_ShouldOperateCorrectly()
    {
        var (engines, ctx, handler) = CreateTestEnv();
        engines.Variable.Set("Q", "[M1;M2]", "1");

        handler.Execute("LPush", "Q,M0", ctx);
        Assert.Equal("[M0;M1;M2]", engines.Variable.GetRaw("Q", "1"));

        handler.Execute("RPush", "Q,M3", ctx);
        Assert.Equal("[M0;M1;M2;M3]", engines.Variable.GetRaw("Q", "1"));

        handler.Execute("LPop", "Q", ctx);
        Assert.Equal("[M1;M2;M3]", engines.Variable.GetRaw("Q", "1"));

        handler.Execute("RPop", "Q", ctx);
        Assert.Equal("[M1;M2]", engines.Variable.GetRaw("Q", "1"));

        handler.Execute("LGetIndexSize", "Q", ctx);
        Assert.Equal("0", engines.Variable.Get("Q#Index", "1"));
        Assert.Equal("2", engines.Variable.Get("Q#Size", "1"));
    }

    [Fact]
    public void Handler_SetOperations_UnionDiffIntersection_ShouldOperateCorrectly()
    {
        var (engines, ctx, handler) = CreateTestEnv();
        engines.Variable.Set("Set1", "[A;B;C]", "1");
        engines.Variable.Set("Set2", "[B;C;D]", "1");

        // 聯集
        handler.Execute("SUnion", "UnionSet,Set1,Set2", ctx);
        string u = engines.Variable.GetRaw("UnionSet", "1");
        Assert.Contains("A", u);
        Assert.Contains("B", u);
        Assert.Contains("C", u);
        Assert.Contains("D", u);

        // 差集 Set1 - Set2 = A
        handler.Execute("SDiff", "DiffSet,Set1,Set2", ctx);
        Assert.Equal("[A]", engines.Variable.GetRaw("DiffSet", "1"));

        // 交集 Set1 ∩ Set2 = B, C
        handler.Execute("SIntersection", "InterSet,Set1,Set2", ctx);
        string inter = engines.Variable.GetRaw("InterSet", "1");
        Assert.Contains("B", inter);
        Assert.Contains("C", inter);
        Assert.DoesNotContain("A", inter);
    }

    [Fact]
    public void Csvi_Set_CanBeLoadedAndInstantiated()
    {
        string p = System.IO.Path.Combine(System.AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MODULES", "VARIABLE", "Set.csvi");
        p = System.IO.Path.GetFullPath(p);
        Assert.True(System.IO.File.Exists(p), $"File not found: {p}");
        byte[] bytes = System.IO.File.ReadAllBytes(p);
        var asm = System.Reflection.Assembly.Load(bytes);
        Assert.NotNull(asm);
        var t = asm.GetType("ARCOS.Module.Variable.VariableSetGuidance");
        Assert.NotNull(t);
        var obj = System.Activator.CreateInstance(t) as ARCOS.Core.Guidance.IParameterGuidance;
        Assert.NotNull(obj);
        Assert.Equal("Variable@Set", obj.CommandKey);
        var names = asm.GetManifestResourceNames();
        Assert.Contains("CSVI.RuntimeXaml.Wizard.axaml", names);
    }
}
