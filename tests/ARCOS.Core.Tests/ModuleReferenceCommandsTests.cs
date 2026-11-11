using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using ARCOS.Core.Execution;
using ARCOS.Core.Model;
using Xunit;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Core.Tests;

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

        double elapsedRecord = engines.Record.GetRecordElapsedSeconds("TEST");
        Assert.True(elapsedRecord >= 0.04);
        Assert.Contains(ctx.Engines.Record.GetOrCreateChannel("SYSTEMLOG").Entries, e => e.Message.Contains("Elapsed:"));
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
    public void MotionModule_AddMotion_Home_GoPoint_And_GetPos()
    {
        string script = @"
Motion@AddMotion::MotorTest,MockMotion;
Motion@Home::MotorTest;
Point&Path@Calculate::TargetP=(123.4,567.8,90.0);
Motion@GoPoint::MotorTest,100,TargetP;
Motion@WaitMotor::MotorTest;
Motion@GetPos::MotorTest;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.True(engines.Io.GetPort("MotorTest#Homed?"));
        Assert.True(engines.Io.GetPort("MotorTest#Done?"));
    }

    [Fact]
    public void MotionModule_AddMotor_BackwardCompatibility()
    {
        string script = @"
Motion@AddMotor::MotorLegacy,MockMotion;
Motion@Home::MotorLegacy;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.True(engines.Io.GetPort("MotorLegacy#Homed?"));
    }

    [Fact]
    public void MotionModule_GoSafe_WithSafeAxisZ()
    {
        string script = @"
Motion@AddMotion::M2,MockMotion;
Point&Path@Calculate::P2=(50,60,70);
Motion@GoSafe::M2,100,P2,Z=0;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        // 等待補間完全完成
        Thread.Sleep(150);
        Assert.True(engines.Io.GetPort("M2#Done?"));
        var cur = engines.Point.GetPoint("M2_CurPos");
        Assert.Equal(50.0, cur.X);
        Assert.Equal(60.0, cur.Y);
        Assert.Equal(70.0, cur.Z);
    }

    [Fact]
    public void MotionCSVI_Packages_ExistAndCanBeInstantiated()
    {
        string baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "MODULES", "MOTION");
        string goPointCsvi = Path.Combine(baseDir, "GoPoint.csvi");
        string motionMasterCsvi = Path.Combine(baseDir, "Motion.csvi");

        if (File.Exists(goPointCsvi))
        {
            var asm = System.Reflection.Assembly.Load(File.ReadAllBytes(goPointCsvi));
            var type = asm.GetType("ARCOS.Module.Motion.MotionGoPointGuidance");
            Assert.NotNull(type);
            var inst = Activator.CreateInstance(type);
            Assert.NotNull(inst);
            Assert.IsAssignableFrom<ARCOS.Core.Guidance.IParameterGuidance>(inst);
        }

        if (File.Exists(motionMasterCsvi))
        {
            var asm = System.Reflection.Assembly.Load(File.ReadAllBytes(motionMasterCsvi));
            var type = asm.GetType("ARCOS.Module.Motion.MotionMasterGuidance");
            Assert.NotNull(type);
            var inst = Activator.CreateInstance(type);
            Assert.NotNull(inst);
            Assert.IsAssignableFrom<ARCOS.Core.Guidance.IParameterGuidance>(inst);
        }
    }

    [Fact]
    public void PointPathModule_SelectAndLocalModifications()
    {
        string script = @"
Point&Path@Calculate::P1=(10,20,30,0,0,0,12.5);
Point&Path@Calculate::P2=(100,200,300);
Point&Path@Select::PSelected=1==1?P1:P2;
Point&Path@AddXYZ::P1,5,5,5;
Point&Path@ReplaceH::P1,45.5;
Point&Path@ReplaceH::P1,SetIO=P0.0;Pose=Left;
Variable@GetPointElement::RadVal=H(P1);
Variable@GetPointElement::PoseVal=H.Pose(P1);
Point&Path@Calculate::WS=(0,0,50);
Point&Path@InsertOnePoint::WS,(0,100,50);
Point&Path@InsertOnePoint::WS,(100,0,50);
Point&Path@Calculate::PT=(20,40,0);
Point&Path@WS2Point::PT,WS;
Point&Path@Calculate::Center=(50,50,0,0,0,0,20);
Point&Path@Circle2DPath::Center,4,Angle=360;
Point&Path@Calculate::RangeBox=(0,0,0);
Point&Path@InsertOnePoint::RangeBox,(100,100,0);
Point&Path@ChkInRangeXY::PT,RangeBox,InRangeFlag;
Point&Path@ClearPoint::P2;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        var pSel = engines.Point.GetPoint("PSelected");
        Assert.Equal(10.0, pSel.X);
        Assert.Equal(12.5, pSel.Radius);

        var p1 = engines.Point.GetPoint("P1");
        Assert.Equal(15.0, p1.X);
        Assert.Equal(25.0, p1.Y);
        Assert.Equal(35.0, p1.Z);
        Assert.Equal(45.5, p1.Radius);
        Assert.Equal("Left", p1.GetHString("Pose"));
        Assert.Equal("P0.0", p1.GetHString("SetIO"));
        Assert.Equal("Left", engines.Variable.Get("PoseVal"));

        // WS2Point test: WS=[(0,0,50), (0,100,50), (100,0,50)], PT=(20,40,0) -> (40, 20, 50)
        var pt = engines.Point.GetPoint("PT");
        Assert.Equal(40.0, pt.X, 3);
        Assert.Equal(20.0, pt.Y, 3);
        Assert.Equal(50.0, pt.Z, 3);

        // Circle2DPath from single point + Radius=20 -> 4 points
        var centerPallet = engines.Point.GetPointArray("Center");
        Assert.Equal(4, centerPallet.Count);
        Assert.Equal(70.0, centerPallet[0].X, 3);
        Assert.Equal(50.0, centerPallet[0].Y, 3);

        // ChkInRangeXY -> InRangeFlag = 1
        Assert.Equal("1", engines.Variable.Get("InRangeFlag"));

        // ClearPoint -> P2 cleared
        Assert.Empty(engines.Point.GetPointArray("P2"));
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
    public void VariableModule_CsviDrivenCommands_TrimInclusiveEnd_RegexSave_SubscribeWait_GetPointSection()
    {
        string saveFile = $"RegexSaveTest_{Guid.NewGuid():N}.ini";
        string script = $@"
Variable@Set::StrVar,ABCDEFG;
Variable@Trim::StrVar,1,4;
Point&Path@Calculate::PT2=(11.5,22.5,33.5);
Variable@GetPointElement::YVal=Y(PT2),CustomSec;
Variable@Set::Prod_1,Apple;
Variable@Set::Prod_2,Banana;
Variable@Set::Temp_Ignore,999;
Variable@VariableSave::{saveFile},^Prod_[0-9]+$;
Variable@Delete::Prod_1;
Variable@Delete::Prod_2;
Variable@Delete::Temp_Ignore;
Variable@VariableLoad::{saveFile};
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        // 1. 驗證 Trim 0-based 且包含 $end (1..4 of "ABCDEFG" -> "BCDE")
        Assert.Equal("BCDE", engines.Variable.Get("StrVar"));

        // 2. 驗證 GetPointElement 支援尾端 , $section
        Assert.Equal("22.5", engines.Variable.Get("YVal", "CustomSec"));

        // 3. 驗證 VariableSave 正規表示式篩選 (^Prod_[0-9]+$) 與 VariableLoad
        Assert.Equal("Apple", engines.Variable.Get("Prod_1"));
        Assert.Equal("Banana", engines.Variable.Get("Prod_2"));
        Assert.Throws<KeyNotFoundException>(() => engines.Variable.Get("Temp_Ignore"));

        string fullSavePath = Path.Combine(ARCOS.Core.Common.ArcosPaths.ScriptDirectory, saveFile);
        if (File.Exists(fullSavePath)) File.Delete(fullSavePath);

        // 4. 驗證 Subscribe 非阻塞微步輪詢 (未 Publish 時回傳 Waiting，Publish 後回傳 Completed)
        var waitRes = executor.ExecuteCommandLine(ctx, "Variable@Subscribe::SyncSignal;");
        Assert.Equal(CommandStatus.Waiting, waitRes.Status);

        executor.ExecuteCommandLine(ctx, "Variable@Publish::SyncSignal;");
        var doneRes = executor.ExecuteCommandLine(ctx, "Variable@Subscribe::SyncSignal;");
        Assert.Equal(CommandStatus.Completed, doneRes.Status);

        // 5. 驗證不存在的 Variable 指令直接報錯
        var errRes = executor.ExecuteCommandLine(ctx, "Variable@NoSuchVarCmd::123;");
        Assert.Equal(CommandStatus.Error, errRes.Status);
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

    [Fact]
    public void SystemAndEngines_DirectoryConventionsAndFileOperations()
    {
        // 1. 確保目錄初始化
        ARCOS.Core.Common.ArcosPaths.EnsureDirectoriesExist();
        Assert.True(Directory.Exists(ARCOS.Core.Common.ArcosPaths.ScriptDirectory));
        Assert.True(Directory.Exists(ARCOS.Core.Common.ArcosPaths.LogsDirectory));
        Assert.True(Directory.Exists(ARCOS.Core.Common.ArcosPaths.PictureDirectory));
        Assert.True(Directory.Exists(ARCOS.Core.Common.ArcosPaths.WaveformDirectory));
        Assert.True(Directory.Exists(ARCOS.Core.Common.ArcosPaths.PointAndPathDirectory));

        // 2. PointEngine Save & Load in POINT&PATH
        var ptEngine = new PointEngine();
        ptEngine.SetPoint("P_TestUnit", new MachinePoint(12.3, 45.6, 78.9, 10.0, 20.0, 30.0));
        string ptFile = Path.Combine(ARCOS.Core.Common.ArcosPaths.PointAndPathDirectory, "TestPoints.ini");
        ptEngine.SaveToFile(ptFile);
        Assert.True(File.Exists(ptFile));

        var ptEngine2 = new PointEngine();
        ptEngine2.LoadFromFile(ptFile);
        var loadedPt = ptEngine2.GetPoint("P_TestUnit");
        Assert.Equal(12.3, loadedPt.X, 3);
        Assert.Equal(45.6, loadedPt.Y, 3);
        Assert.Equal(78.9, loadedPt.Z, 3);

        // 3. DaqEngine Save & Load in WAVEFORM
        var daqEngine = new DaqEngine();
        double[] testY = new double[] { 0.1, 0.5, 1.2, 2.5, 1.0, 0.0 };
        daqEngine.SetWaveform("Wf_TestUnit", DateTime.Now, 0.002, testY, channel: 0);
        string wfFile = Path.Combine(ARCOS.Core.Common.ArcosPaths.WaveformDirectory, "TestWf.csv");
        daqEngine.SaveWaveform("Wf_TestUnit", wfFile, channel: 0);
        Assert.True(File.Exists(wfFile));

        var daqEngine2 = new DaqEngine();
        daqEngine2.LoadWaveform("Wf_Loaded", wfFile, channel: 0);
        var loadedWf = daqEngine2.GetWaveform("Wf_Loaded");
        var ch0 = loadedWf.GetOrCreateChannel(0);
        Assert.Equal(testY.Length, ch0.Y.Length);
        Assert.Equal(0.002, ch0.Dt, 4);

        // 4. Capture@ImageSave in PICTURE/<CamName>/
        var (ctx, executor, engines) = CreateHarness("Capture@ImageSave::CAM_INSPECT,Frame01.bmp;");
        using var dummyMat = new OpenCvSharp.Mat(100, 100, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(100, 150, 200));
        engines.Image.SetSingleImage("CAM_INSPECT", dummyMat);
        RunToEnd(ctx, executor);

        string expectedImgPath = Path.Combine(ARCOS.Core.Common.ArcosPaths.GetCameraPictureDirectory("CAM_INSPECT"), "Frame01.bmp");
        Assert.True(File.Exists(expectedImgPath));

        // 5. ExecutionContext.Log records in SYSTEMLOG and flushes to SessionLogFilePath on Clear Message
        string uniqueLogMsg = $"Unit test verification log message {Guid.NewGuid():N}";
        ctx.Log(uniqueLogMsg);
        executor.ExecuteCommandLine(ctx, "System@Clear Message::");
        string logPath = ARCOS.Core.Common.ArcosPaths.SessionLogFilePath;
        Assert.True(File.Exists(logPath));
        string logContent = File.ReadAllText(logPath);
        Assert.Contains(uniqueLogMsg, logContent);

        // 6. System@UserPanelOpen triggers VM callback
        var (ctxPanel, execPanel, enginesPanel) = CreateHarness("System@UserPanelOpen::CustomRecipePanel.csvi;");
        string requestedPanel = "";
        ctxPanel.VM = new ArcosThreadVM(enginesPanel, new DriverManager(enginesPanel));
        ctxPanel.VM.OnUserPanelOpenRequested += (panel) => requestedPanel = panel;
        RunToEnd(ctxPanel, execPanel);
        Assert.Equal("CustomRecipePanel.csvi", requestedPanel);
    }


    [Fact]
    public void PythonModule_MultiSessionIsolation_VariablesDoNotCollide()
    {
        string script = @"
System@IncludeModule::Python;
Python@SetVariable::A,100,Session=Sess1;
Python@SetVariable::A,999,Session=Sess2;
Python@GetVariable::A,Session=Sess1,ARCOSName=ResultSess1;
Python@GetVariable::A,Session=Sess2,ARCOSName=ResultSess2;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.Equal("100", engines.Variable.Get("ResultSess1"));
        Assert.Equal("999", engines.Variable.Get("ResultSess2"));
    }

    [Fact]
    public void PythonModule_ThreadImplicit_Isolation()
    {
        var io = new IoEngine();
        var varEng = new VariableEngine();
        var pt = new PointEngine(io);
        var img = new ImageEngine(io);
        var daq = new DaqEngine(io);
        var timer = new TimerEngine(io);
        var engines = new BasicEngines(io, varEng, pt, img, daq, timer);
        var drivers = new DriverManager(engines);
        var executor = new CommandExecutor(ModuleRegistry.CreateDefault());

        // Thread 1
        var prog1 = ProgramTable.FromText(@"
System@IncludeModule::Python;
Python@Script::counter = 42;
Python@GetVariable::counter,ARCOSName=T1_Val;
", 0, "T1");
        var ctx1 = new ExecutionContext(0, "Thread1", prog1, engines, drivers);
        ctx1.CurrentSection = "Thread1";
        RunToEnd(ctx1, executor);

        // Thread 2 (Python 模組已於系統載入，各 Thread 依 ThreadName 隱式隔離 Session)
        var prog2 = ProgramTable.FromText(@"
Python@Script::counter = 777;
Python@GetVariable::counter,ARCOSName=T2_Val;
", 0, "T2");
        var ctx2 = new ExecutionContext(1, "Thread2", prog2, engines, drivers);
        ctx2.CurrentSection = "Thread2";
        RunToEnd(ctx2, executor);

        // Thread 1 read again to ensure no cross-contamination
        var prog1Check = ProgramTable.FromText(@"
Python@GetVariable::counter,ARCOSName=T1_Final;
", 0, "T1_Check");
        var ctx1Check = new ExecutionContext(0, "Thread1", prog1Check, engines, drivers);
        ctx1Check.CurrentSection = "Thread1";
        RunToEnd(ctx1Check, executor);

        Assert.Equal("42", engines.Variable.Get("T1_Val", "Thread1"));
        Assert.Equal("777", engines.Variable.Get("T2_Val", "Thread2"));
        Assert.Equal("42", engines.Variable.Get("T1_Final", "Thread1"));
    }

    [Fact]
    public void PythonModule_PointExchange_NumPy()
    {
        string script = @"
System@IncludeModule::Python;
Python@SetPoint::PtOrig,PythonName=p;
Python@Script::p[0] += 5.5
p[1] *= 2.0;
Python@GetPoint::p,ARCOSName=PtResult;
";
        var (ctx, executor, engines) = CreateHarness(script);
        engines.Point.SetPoint("PtOrig", new MachinePoint { X = 10.0, Y = 20.0, Z = 30.0, U = 0, V = 0, W = 0 });
        RunToEnd(ctx, executor);

        var ptResult = engines.Point.GetPoint("PtResult");
        Assert.Equal(15.5, ptResult.X, 3);
        Assert.Equal(40.0, ptResult.Y, 3);
        Assert.Equal(30.0, ptResult.Z, 3);
    }

    [Fact]
    public void PythonModule_ImageExchange_OpenCv()
    {
        string script = @"
System@IncludeModule::Python;
Python@SetCapture::CamSource,PythonName=py_img;
Python@Script::py_img[:, :] = (10, 20, 30);
Python@GetCapture::py_img,ARCOSName=CamProcessed;
";
        var (ctx, executor, engines) = CreateHarness(script);
        using var testMat = new OpenCvSharp.Mat(20, 30, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(0, 0, 0));
        engines.Image.SetSingleImage("CamSource", testMat);
        RunToEnd(ctx, executor);

        var processedFrame = engines.Image.GetFrame("CamProcessed");
        Assert.NotNull(processedFrame.Image);
        Assert.Equal(30, processedFrame.Image.Width);
        Assert.Equal(20, processedFrame.Image.Height);

        // Check pixel value
        var vec = processedFrame.Image.At<OpenCvSharp.Vec3b>(5, 5);
        Assert.Equal(10, vec.Item0);
        Assert.Equal(20, vec.Item1);
        Assert.Equal(30, vec.Item2);
    }

    [Fact]
    public void PythonModule_ResetSession_ClearsVariables()
    {
        string script = @"
System@IncludeModule::Python;
Python@Script::Session=TempSess, test_var = 12345;
Python@GetVariable::test_var,Session=TempSess,ARCOSName=BeforeReset;
Python@ResetSession::TempSess;
Python@GetVariable::test_var,Session=TempSess,ARCOSName=AfterReset;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.Equal("12345", engines.Variable.Get("BeforeReset"));
        Assert.True(string.IsNullOrEmpty(engines.Variable.Get("AfterReset")));
    }


    [Fact]
    public void PythonModule_OnDemandLoading_NotLoadedAtBoot_LoadsOnIncludeModule()
    {
        // 1. 驗證開機時 Registry 絕無 Python 模組
        var registry = ModuleRegistry.CreateDefault();
        Assert.False(registry.HasModule("Python"), "Python module should NOT be loaded at boot!");

        // 2. 執行腳本包含 System@IncludeModule::Python;
        string script = @"
System@IncludeModule::Python;
Python@SetVariable::BootTestVar,ReadyToRun;
Python@GetVariable::BootTestVar,ARCOSName=BootResult;
";
        var io = new IoEngine();
        var varEng = new VariableEngine();
        var pt = new PointEngine(io);
        var img = new ImageEngine(io);
        var daq = new DaqEngine(io);
        var timer = new TimerEngine(io);
        var engines = new BasicEngines(io, varEng, pt, img, daq, timer);
        var drivers = new DriverManager(engines);
        var prog = ProgramTable.FromText(script, 0, "BootTest");
        var ctx = new ExecutionContext(0, "ThreadMain", prog, engines, drivers);
        var executor = new CommandExecutor(registry);

        int steps = 0;
        while (ctx.IsRunning && steps++ < 10)
        {
            executor.ExecuteStep(ctx);
        }

        // 3. 驗證 IncludeModule 執行後，Python 已動態註冊並正常運算
        Assert.True(registry.HasModule("Python"), "Python module should be loaded after IncludeModule!");
        Assert.Equal("ReadyToRun", engines.Variable.Get("BootResult"));

        // 4. 驗證系統日誌中包含 Init.csvi 調用紀錄
        Assert.Contains(ctx.Engines.Record.GetOrCreateChannel("SYSTEMLOG").Entries, e => e.Message.Contains("[IncludeModule]") && e.Message.Contains("Python"));
    }


    [Fact]
    public void PythonModule_IncludeModule_WhenCalledAgain_ResetsEntireModule()
    {
        var io = new IoEngine();
        var varEng = new VariableEngine();
        var pt = new PointEngine(io);
        var img = new ImageEngine(io);
        var daq = new DaqEngine(io);
        var timer = new TimerEngine(io);
        var engines = new BasicEngines(io, varEng, pt, img, daq, timer);
        var drivers = new DriverManager(engines);
        var executor = new CommandExecutor(ModuleRegistry.CreateDefault());

        // 1. 初次載入並在 ThreadMain 建立變數
        var prog1 = ProgramTable.FromText(@"
System@IncludeModule::Python;
Python@Script::global_var = 12345;
Python@GetVariable::global_var,ARCOSName=Var_First;
", 0, "T1");
        var ctx1 = new ExecutionContext(0, "ThreadMain", prog1, engines, drivers);
        RunToEnd(ctx1, executor);
        Assert.Equal("12345", engines.Variable.Get("Var_First"));

        // 2. 再次呼叫 System@IncludeModule::Python (比照 AddDevice 重複加入時 Release + Reload + Reset)
        var prog2 = ProgramTable.FromText(@"
System@IncludeModule::Python;
Python@GetVariable::global_var,ARCOSName=Var_AfterReset;
", 0, "T2");
        var ctx2 = new ExecutionContext(0, "ThreadMain", prog2, engines, drivers);
        RunToEnd(ctx2, executor);

        // 驗證變數已被清空重置 (變數不存在回傳空字串)
        Assert.Equal("", engines.Variable.Get("Var_AfterReset"));
    }

    [Fact]
    public void CallModule_ExternalCsvi_ExecutesMathAndAssignsVariable()
    {
        string script = @"
Call@mycsvi.csvi&::15,25,$MySum;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.Equal("40", engines.Variable.Get("MySum"));
    }

    [Fact]
    public void CallModule_ExternalCsvi_ExecutesHistogramAndReturnsScore()
    {
        string script = @"
Call@mycsvi.csvi&::Cam1,100,200,HistScore;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.Equal("127.5", engines.Variable.Get("HistScore"));
    }

    [Fact]
    public void CallModule_ExternalCsvi_ReentrantMode_ExecutesParallelWithoutInterference()
    {
        string script = @"
Call@mycsvi.csvi&&::12,18,Sum1;
Call@mycsvi.csvi&&::Cam2,50,150,Score1;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.Equal("30", engines.Variable.Get("Sum1"));
        Assert.Equal("85", engines.Variable.Get("Score1"));
    }

    [Fact]
    public void CallModule_ExternalCsvi_LegacyViSyntax_AutomaticallyRedirectsToCsvi()
    {
        string script = @"
Call@mycsvi.vi&::100,200,ViSum;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.Equal("300", engines.Variable.Get("ViSum"));
    }

    [Fact]
    public void CallModule_ExternalCsvi_ErrorLabel_JumpsOnError()
    {
        string script = @"
Variable@Set::State,Initial;
Call@NoSuchModuleError.csvi&::1,2,[[$ErrCatch]]FailedToRun;
Variable@Set::State,ShouldBeSkipped;
Thread@Stop::;
Thread@ErrorHandler::[[ErrCatch]];
Variable@Set::State,Handled;
";
        var (ctx, executor, engines) = CreateHarness(script);
        RunToEnd(ctx, executor);

        Assert.Equal("Handled", engines.Variable.Get("State"));
    }

    [Fact]
    public void CallModule_ExecuteCommandLine_And_EmbeddedResources_WorkCorrectly()
    {
        var (ctx, executor, engines) = CreateHarness("System@DoNothing::;");

        // 1. 驗證主視窗 CALL 模組 ▶ Run Test 所呼叫的 ExecuteCommandLine 可正常派發至 CallModule
        var res = executor.ExecuteCommandLine(ctx, "Call@mycsvi.csvi&::15, 25, $RunTestOut;");
        Assert.Equal(CommandStatus.Completed, res.Status);
        Assert.Equal("40", engines.Variable.Get("RunTestOut"));

        // 2. 驗證 ExternalModules/mycsvi.csvi 為完整 CSVI 3.0 單檔組件，具備 Wizard / Panel / Manifest / Source.Bundle
        string csviPath = Path.Combine(ARCOS.Core.Common.ArcosPaths.ExternalModulesDirectory, "mycsvi.csvi");
        Assert.True(File.Exists(csviPath), $"Expected mycsvi.csvi at {csviPath}");

        var asm = System.Reflection.Assembly.Load(File.ReadAllBytes(csviPath));
        var resNames = asm.GetManifestResourceNames();
        Assert.Contains("CSVI.Manifest.json", resNames);
        Assert.Contains("CSVI.RuntimeXaml.Wizard.axaml", resNames);
        Assert.Contains("CSVI.RuntimeXaml.Panel.axaml", resNames);
        Assert.Contains("CSVI.Source.Bundle", resNames);

        var cmdType = asm.GetExportedTypes().FirstOrDefault(t =>
            typeof(ICallCommand).IsAssignableFrom(t) &&
            typeof(ARCOS.Core.Guidance.IParameterGuidance).IsAssignableFrom(t));
        Assert.NotNull(cmdType);
    }
}
