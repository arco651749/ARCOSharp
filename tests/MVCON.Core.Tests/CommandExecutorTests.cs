using MVCON.Core.Drivers;
using MVCON.Core.Engines;
using MVCON.Core.Execution;
using ExecutionContext = MVCON.Core.Execution.ExecutionContext;
using Xunit;

namespace MVCON.Core.Tests;

public class CommandExecutorTests
{
    private (ExecutionContext Ctx, CommandExecutor Executor, BasicEngines Engines, DriverManager Drivers) CreateTestHarness(string script)
    {
        var io = new IoEngine();
        var varEng = new VariableEngine();
        var pt = new PointEngine(io);
        var img = new ImageEngine(io);
        var daq = new DaqEngine(io);
        var timer = new TimerEngine(io);

        var engines = new BasicEngines(io, varEng, pt, img, daq, timer);
        var drivers = new DriverManager(engines);
        var program = ProgramTable.FromText(script, 0, "TestProgram");
        var ctx = new ExecutionContext(0, "ThreadMain", program, engines, drivers);
        var executor = new CommandExecutor(ModuleRegistry.CreateDefault());

        return (ctx, executor, engines, drivers);
    }

    [Fact]
    public void ModuleRegistry_GetAllCommands_ReturnsAllAndFilteredCommands()
    {
        var registry = ModuleRegistry.CreateDefault();

        var allCmds = registry.GetAllCommands("*");
        Assert.NotEmpty(allCmds);
        Assert.Contains("IO@SetPort", allCmds);
        Assert.Contains("Motion@GoPoint", allCmds);
        Assert.Contains("System@Delay", allCmds);
        Assert.Contains("Thread@Jump", allCmds);

        var ioCmds = registry.GetAllCommands("IO");
        Assert.All(ioCmds, cmd => Assert.StartsWith("IO@", cmd));
        Assert.Contains("IO@SetPort", ioCmds);
        Assert.Contains("IO@WaitPortOn", ioCmds);
    }

    [Fact]
    public void ModuleRegistry_LoadExternalModules_LoadsConventionDlls()
    {
        var reg = new ModuleRegistry();
        string distModules = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dist", "MVCON_Runtime", "MODULES");
        if (Directory.Exists(distModules))
        {
            reg.LoadExternalModules(distModules);
            Assert.True(reg.HasModule("IO"));
            Assert.True(reg.HasModule("Motion"));
            Assert.True(reg.HasModule("Capture"));
            Assert.True(reg.HasModule("DAQ"));
            Assert.True(reg.HasModule("System"));
            Assert.True(reg.HasModule("Thread"));
            Assert.True(reg.HasModule("Variable"));
        }
    }

    [Fact]
    public void ExecuteStep_SequentialExecution_AdvancesPcAndUpdatesState()
    {
        string script = """
            IO@SetPort::P0.0;
            Variable@Set::Count,42;
            IO@ClrPort::P0.0;
            """;

        var (ctx, executor, engines, _) = CreateTestHarness(script);

        // Step 1: SetPort
        var res1 = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, res1.Status);
        Assert.Equal(2, ctx.ProgIndex);
        Assert.True(engines.Io.GetPort("P0.0"));

        // Step 2: Set Variable
        var res2 = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, res2.Status);
        Assert.Equal(3, ctx.ProgIndex);
        Assert.Equal("42", engines.Variable.Get("Count"));

        // Step 3: ClrPort
        var res3 = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, res3.Status);
        Assert.Equal(4, ctx.ProgIndex);
        Assert.False(engines.Io.GetPort("P0.0"));
    }

    [Fact]
    public void ExecuteStep_ParamFilter_SubstitutesVariablesBeforeExecution()
    {
        string script = """
            Variable@Set::BaseX,100;
            Variable@Calculate::TargetX=$(BaseX)+25;
            """;

        var (ctx, executor, engines, _) = CreateTestHarness(script);

        executor.ExecuteStep(ctx);
        executor.ExecuteStep(ctx);

        Assert.Equal("125", engines.Variable.Get("TargetX"));
    }

    [Fact]
    public void ExecuteStep_JumpAndLabel_TransitionsExecutionPointer()
    {
        string script = """
            Thread@Jump::TargetLine;
            IO@SetPort::P0.0;
            Thread@Label::TargetLine;
            IO@SetPort::P0.1;
            """;

        var (ctx, executor, engines, _) = CreateTestHarness(script);

        // Step 1: Jump to TargetLine
        var resJump = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Jump, resJump.Status);
        Assert.Equal(3, ctx.ProgIndex); // 指向 TargetLine
        Assert.False(engines.Io.GetPort("P0.0")); // 略過了 Line 2

        // Step 2: Label (no-op)
        executor.ExecuteStep(ctx);
        Assert.Equal(4, ctx.ProgIndex);

        // Step 3: SetPort P0.1
        executor.ExecuteStep(ctx);
        Assert.True(engines.Io.GetPort("P0.1"));
    }

    [Fact]
    public void ExecuteStep_IfBranching_SkipsBlockWhenConditionIsFalse()
    {
        string script = """
            Variable@Set::TestVal,0;
            Thread@If::TestVal > 10;
            IO@SetPort::P0.0;
            Thread@Else;
            IO@SetPort::P0.1;
            Thread@EndIf;
            """;

        var (ctx, executor, engines, _) = CreateTestHarness(script);

        executor.ExecuteStep(ctx); // Line 1: Set TestVal=0
        executor.ExecuteStep(ctx); // Line 2: If (false) -> Jump to Else (Line 4)

        Assert.Equal(4, ctx.ProgIndex);
        executor.ExecuteStep(ctx); // Line 4: Else -> executes next line (Line 5)
        Assert.Equal(5, ctx.ProgIndex);

        executor.ExecuteStep(ctx); // Line 5: SetPort P0.1
        Assert.False(engines.Io.GetPort("P0.0"));
        Assert.True(engines.Io.GetPort("P0.1"));
    }

    [Fact]
    public void ExecuteStep_NonBlockingDelay_UsesCommandStateWithoutFreezing()
    {
        string script = """
            System@Delay::0.05;
            IO@SetPort::P0.0;
            """;

        var (ctx, executor, engines, _) = CreateTestHarness(script);

        // 第一步呼叫：進入等待狀態
        var res1 = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Waiting, res1.Status);
        Assert.Equal(1, ctx.ProgIndex); // PC 不推進
        Assert.True(ctx.State.HasData);

        // 模擬時間流逝 (給予 80ms 餘裕，避免 Windows 系統計時器 15.6ms 顆粒度邊界誤差)
        Thread.Sleep(80);

        // 第二步呼叫：時間已到完成
        var res2 = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, res2.Status);
        Assert.Equal(2, ctx.ProgIndex); // PC 推進至下一行
        Assert.False(ctx.State.HasData); // CommandState 自動 Reset

        // 執行下一行
        executor.ExecuteStep(ctx);
        Assert.True(engines.Io.GetPort("P0.0"));
    }

    [Fact]
    public void ExecuteStep_SelfHealing_InterceptsLabelAndAllowsErrorRetry()
    {
        string script = """
            IO@WaitPortOn::NonExistingPort?,0.01,[[TimeoutHealing]]Wait Failed;
            IO@SetPort::P0.0;
            Thread@ErrorHandler::[[TimeoutHealing]];
            IO@SetPort::HealedFlag;
            Thread@ErrorRetry::;
            """;

        var (ctx, executor, engines, _) = CreateTestHarness(script);

        // Line 1: WaitPortOn 第一次執行進入 Wait
        executor.ExecuteStep(ctx);
        Assert.Equal(1, ctx.ProgIndex);

        // 等待逾時
        Thread.Sleep(20);

        // Line 1: 第二次執行觸發逾時異常，應自動攔截 [[TimeoutHealing]] 並跳躍至 Line 3
        var resFail = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Jump, resFail.Status);
        Assert.Equal(3, ctx.ProgIndex); // 成功自癒跳至 ErrorHandler
        Assert.Equal(1, ctx.FailedPC);  // 記錄出錯點為 Line 1
        Assert.False(engines.Io.GetPort("P0.0")); // Line 2 未被執行

        // Line 3: ErrorHandler (no-op)
        executor.ExecuteStep(ctx);
        Assert.Equal(4, ctx.ProgIndex);

        // Line 4: 執行自癒處置 SetPort HealedFlag
        executor.ExecuteStep(ctx);
        Assert.True(engines.Io.GetPort("HealedFlag"));
        Assert.Equal(5, ctx.ProgIndex);

        // Line 5: 執行 Thread@ErrorRetry:: -> 應跳回 FailedPC (Line 1)
        var resRetry = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Retry, resRetry.Status);
        Assert.Equal(1, ctx.ProgIndex); // 成功還原至出錯指令重試！
    }

    [Fact]
    public void ExecuteStep_AiAgent_BypassSupervision()
    {
        string script = """
            AIAgent@SuperviseStart::~InspectPart;
            IO@SetPort::InspectOK;
            AIAgent@SuperviseEnd::;
            """;

        var (ctx, executor, engines, _) = CreateTestHarness(script);

        // Line 1: SuperviseStart with ~ (bypass)
        var res1 = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, res1.Status);
        Assert.True(ctx.State.Get<bool>("AIAgent_Bypass"));

        // Line 2: SetPort
        var res2 = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, res2.Status);
        Assert.True(engines.Io.GetPort("InspectOK"));

        // Line 3: SuperviseEnd (should bypass without blocking)
        var res3 = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, res3.Status);
        Assert.Equal(4, ctx.ProgIndex);
    }

    [Fact]
    public void ExecuteStep_Redis_SetAndGet()
    {
        string script = """
            Redis@CreateSession::Line1,127.0.0.1:6379;
            Redis@Set::Line1,Part_12345;
            Redis@Get::Line1,ReceivedSN;
            """;

        var (ctx, executor, engines, _) = CreateTestHarness(script);

        executor.ExecuteStep(ctx);
        Assert.Equal("OK", engines.Variable.Get("RedisErr#Line1"));

        executor.ExecuteStep(ctx);
        executor.ExecuteStep(ctx);
        Assert.Equal("Part_12345", engines.Variable.Get("ReceivedSN"));
    }

    [Fact]
    public void ExecuteStep_Python_VariableExchange()
    {
        string script = """
            Variable@Set::InputVal,100;
            Python@SetVariable::InputVal,PythonName=X;
            Python@Script::Y=200;
            Python@GetVariable::Y,ARCOSName=ResultVal;
            """;

        var (ctx, executor, engines, _) = CreateTestHarness(script);

        executor.ExecuteStep(ctx);
        executor.ExecuteStep(ctx);
        executor.ExecuteStep(ctx);
        executor.ExecuteStep(ctx);

        Assert.Equal("200", engines.Variable.Get("ResultVal"));
    }

    [Fact]
    public void ExecuteStep_MultiStation_StateAndShift()
    {
        string script = """
            MultiStation@SetStationON::1;
            MultiStation@SetStationOK::1;
            MultiStation@SetStationON::2;
            MultiStation@SetStationNG::2;
            MultiStation@StateShift::1;
            """;

        var (ctx, executor, engines, _) = CreateTestHarness(script);

        executor.ExecuteStep(ctx);
        Assert.True(engines.Io.GetPort("Station1#Run?"));

        executor.ExecuteStep(ctx);
        Assert.True(engines.Io.GetPort("Station1#OK?"));

        executor.ExecuteStep(ctx);
        executor.ExecuteStep(ctx);
        Assert.True(engines.Io.GetPort("Station2#NG?"));

        // StateShift::1
        executor.ExecuteStep(ctx);
        // After shifting by 1: Station 2 receives Station 1's state (OK), Station 1 receives Station 2's state (NG)
        Assert.True(engines.Io.GetPort("Station2#OK?"));
        Assert.True(engines.Io.GetPort("Station1#NG?"));
    }
}
