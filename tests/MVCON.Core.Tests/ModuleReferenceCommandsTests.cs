using MVCON.Core.Drivers;
using MVCON.Core.Engines;
using MVCON.Core.Execution;
using MVCON.Core.Model;
using Xunit;
using ExecutionContext = MVCON.Core.Execution.ExecutionContext;

namespace MVCON.Core.Tests;

public class ModuleReferenceCommandsTests
{
    private (ExecutionContext Ctx, CommandExecutor Executor, BasicEngines Engines) CreateHarness(string script)
    {
        var io = new IoEngine();
        var varEng = new VariableEngine();
        var pt = new PointEngine(io);
        var img = new ImageEngine(io);
        var daq = new DaqEngine(io);
        var timer = new TimerEngine(io);

        var engines = new BasicEngines(io, varEng, pt, img, daq, timer);
        var drivers = new DriverManager(engines);
        var program = ProgramTable.FromText(script, 0, "RefTest");
        var ctx = new ExecutionContext(0, "ThreadMain", program, engines, drivers);
        var executor = new CommandExecutor(ModuleRegistry.CreateDefault());

        return (ctx, executor, engines);
    }

    private void RunToEnd(ExecutionContext ctx, CommandExecutor executor, int maxSteps = 500)
    {
        int steps = 0;
        while (ctx.IsRunning && steps++ < maxSteps)
        {
            var res = executor.ExecuteStep(ctx);
            if (res.Status == CommandStatus.Stop) break;
            if (res.Status == CommandStatus.Waiting)
            {
                Thread.Sleep(5);
            }
        }
    }

    [Fact]
    public void SystemModule_RecordAndCycleTime_CalculatesCorrectly()
    {
        string script = @"
System@Record::TEST,Start;
System@Delay::0.05;
System@Record::TEST,Middle;
System@LogRecordCycleTime::TEST,Elapsed:;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        double elapsed = engines.Timer.GetRecordElapsedSeconds("TEST");
        Assert.True(elapsed >= 0.04);
        Assert.Contains(ctx.SysLogEntries, l => l.Contains("Elapsed:"));
    }

    [Fact]
    public void SystemModule_TimerAndCounter_SetIoFlags()
    {
        string script = @"
System@TimerReset::T1,0.01;
System@Delay::0.02;
System@TimerCheck::T1;
System@CounterReset::C1,2;
System@CounterCheck::C1;
System@CounterCheck::C1;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.True(engines.Io.GetPort("T1#Timeout?"));
        Assert.True(engines.Io.GetPort("C1#Over?"));
    }

    [Fact]
    public void ThreadModule_RunNextAndSkipNextCMD_ControlFlow()
    {
        string script = @"
Variable@Set::A,1;
Thread@RunNextCMD::$A==2;
Variable@Set::Executed1,True;
Thread@SkipNextCMD::$A==1;
Variable@Set::Executed2,True;
Variable@Set::Final,Done;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        // Executed1 should be skipped because A==2 is false
        Assert.Throws<KeyNotFoundException>(() => engines.Variable.Get("Executed1"));
        // Executed2 should be skipped because SkipNextCMD was true
        Assert.Throws<KeyNotFoundException>(() => engines.Variable.Get("Executed2"));
        // Final should be executed
        Assert.Equal("Done", engines.Variable.Get("Final"));
    }

    [Fact]
    public void ThreadModule_KeySectionLock_AcquiresAndReleases()
    {
        string script = @"
Thread@KeySectionLock::MyLock,1,Timeout Msg;
Variable@Set::InsideLock,Yes;
Thread@KeySectionUnLock::MyLock;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.Equal("Yes", engines.Variable.Get("InsideLock"));
    }

    [Fact]
    public void CallModule_ParsesModesAndSetsReturnVar()
    {
        string script = @"
Call@CheckSensor.vi&::Param1,100,$RetVal;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.Equal("OK", engines.Variable.Get("RetVal"));
    }

    [Fact]
    public void IoModule_SetPortWithDelayAndFlash()
    {
        string script = @"
IO@SetPort::P0.0,0.02;
IO@AddVirtualIO::V1,ON;
IO@SetPortName::P0.0,Cylinder1;
IO@SetPortName::P1.0,Sensor1?;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.True(engines.Io.GetPort("P0.0"));
        Assert.True(engines.Io.GetPort("V1"));
        Assert.True(engines.Io.GetPort("Cylinder1"));
        Assert.False(engines.Io.GetPort("Sensor1?"));
    }

    [Fact]
    public void CaptureModule_SnapAndPatternMatch_SetsOKAndFindFlags()
    {
        string script = @"
Capture@Snap::Cam1;
Capture@PatternMatchOne::Cam1,target.png,ScoreVar,MatchPt;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.True(engines.Io.GetPort("Cam1#OK?"));
        Assert.True(engines.Io.GetPort("MatchPt#Find?"));
        Assert.Equal("98.5", engines.Variable.Get("ScoreVar"));
        var pt = engines.Point.GetPoint("MatchPt");
        Assert.Equal(100.0, pt.X);
    }

    [Fact]
    public void MotionModule_GoPallet_AdvancesIndexAndSetsLast()
    {
        string script = @"
Point&Path@Calculate::P=(10,20,30);
Point&Path@Calculate::P<=(40,50,60);
Motion@GoPallet::Motor1,100,P;
Motion@GoPallet::Motor1,100,P;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.True(engines.Io.GetPort("P#Last?"));
    }

    [Fact]
    public void PointPathModule_SelectAndLocalModifications()
    {
        string script = @"
Point&Path@Calculate::P1=(10,20,30);
Point&Path@Calculate::P2=(100,200,300);
Point&Path@Select(X=A?B:C)::PSelected=1==1?P1:P2;
Point&Path@AddXYZ::P1,(5,5,5);
Point&Path@ReplaceH::P1,45.5;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        var pSel = engines.Point.GetPoint("PSelected");
        Assert.Equal(10.0, pSel.X);

        var p1 = engines.Point.GetPoint("P1");
        Assert.Equal(15.0, p1.X);
        Assert.Equal(45.5, p1.H["Radius"]?.GetValue<double>());
    }

    [Fact]
    public void VariableModule_AllReferenceFeatures_WorkCorrectly()
    {
        string script = @"
Variable@Set::List,[A;B;C;D];
Variable@LInsert::List,NewX,After[B];
Variable@SAdd::MySet,Item1;
Variable@SAdd::MySet,Item2;
Variable@SAdd::MySet,Item1;
Variable@Set::V1,Alpha;
Variable@Set::V2,Beta;
Variable@Select::Choice=1==1?$V1:$V2;
Variable@Shift::V1,V2,Shift=1;
Point&Path@Calculate::PT=(12.3,45.6,78.9);
Variable@GetPointElement::XVal=X(PT);
Variable@Publish::Ev1;
Variable@Flush::Ev1;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        // LInsert test
        string listRaw = engines.Variable.GetRaw("List");
        Assert.Equal("[A;B;NewX;C;D]", listRaw);

        // SAdd unique set test
        string setRaw = engines.Variable.GetRaw("MySet");
        Assert.Equal("[Item1;Item2]", setRaw);

        // Select ternary test
        Assert.Equal("Alpha", engines.Variable.Get("Choice"));

        // Shift test (V1 was Alpha, V2 was Beta -> shifted by 1: V1=Beta, V2=Alpha)
        Assert.Equal("Beta", engines.Variable.Get("V1"));
        Assert.Equal("Alpha", engines.Variable.Get("V2"));

        // GetPointElement test
        Assert.Equal("12.3", engines.Variable.Get("XVal"));

        // Dynamic $DATE and $TIME test
        string date = engines.Variable.FilterParameters("$DATE");
        Assert.Equal(DateTime.Now.ToString("yyyyMMdd"), date);
    }

    [Fact]
    public void VariableModule_LFindIndexSet_SynchronizesCursorsAcrossArrays()
    {
        // 驗證 Variable.md 中的經典範例：在 Item 中找 PartB，同步帶出 V, I, Range
        string script = @"
Variable@Set::Item,[PartA;PartB;PartC;PartD;PartE];
Variable@Set::V,[1.0;2.0;3.0;2.0;2.0];
Variable@Set::I,[1.0;2.0;3.0;1.0;3.0];
Variable@Set::Range,[0.95~1.05;1.95~2.05;2.95~3.05;0.95~1.05;2.95~3.05];
Variable@LFindIndexSet::Item,PartB,V,I,Range;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.Equal("PartB", engines.Variable.Get("Item"));
        Assert.Equal("2.0", engines.Variable.Get("V"));
        Assert.Equal("2.0", engines.Variable.Get("I"));
        Assert.Equal("1.95~2.05", engines.Variable.Get("Range"));
    }

    [Fact]
    public void VariableModule_SetOperations_UnionDiffIntersection()
    {
        string script = @"
Variable@Set::SetA,[A;B;C;D];
Variable@Set::SetB,[C;D;E;F];
Variable@SUnion::ResUnion,SetA,SetB;
Variable@SDiff::ResDiff,SetA,SetB;
Variable@SIntersection::ResInter,SetA,SetB;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.Equal("[A;B;C;D;E;F]", engines.Variable.GetRaw("ResUnion"));
        Assert.Equal("[A;B]", engines.Variable.GetRaw("ResDiff"));
        Assert.Equal("[C;D]", engines.Variable.GetRaw("ResInter"));
    }

    [Fact]
    public void PointPathModule_Pallet2DAndCircle2DPath_GeneratesCorrectPoints()
    {
        string script = @"
Point&Path@Calculate::WS=(0,0,0);
Point&Path@Calculate::WS<=(100,0,0);
Point&Path@Calculate::WS<=(0,100,0);
Point&Path@Pallet2D::WS,2,2;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        var pts = engines.Point.GetPointArray("WS");
        Assert.Equal(4, pts.Count);
        Assert.Equal(0.0, pts[0].X);
        Assert.Equal(100.0, pts[1].X);
        Assert.Equal(0.0, pts[2].X);
        Assert.Equal(100.0, pts[2].Y);
    }

    private class MockExtraCmdLaserHandler : IModuleHandler
    {
        public string ModuleName => "LaserMarking";
        public IReadOnlyList<string> GetSupportedCommands() => new[] { "StartMark" };

        public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
        {
            if (action.Equals("StartMark", StringComparison.OrdinalIgnoreCase))
            {
                // 驗證具備完整存取 BasicEngines 與 Variables 的能力
                ctx.Engines.Variable.Set("LaserStatus", "MarkingCompleted", ctx.CurrentSection);
                return CommandResult.Done();
            }
            return CommandResult.Fail($"Unknown action '{action}'");
        }
    }

    [Fact]
    public void ExtraCommand_CustomRoutine_ExecutesInProcessWithDirectEngineAccess()
    {
        string script = @"
LaserMarking@StartMark::Text=SN12345;
";
        var (ctx, executor, engines) = CreateHarness(script);
        executor.Registry.RegisterHandler(new MockExtraCmdLaserHandler());
        RunToEnd(ctx, executor);

        Assert.Equal("MarkingCompleted", engines.Variable.Get("LaserStatus", ctx.CurrentSection));
    }
}
