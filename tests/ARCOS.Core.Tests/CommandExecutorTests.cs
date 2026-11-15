using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using ARCOS.Core.Execution;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;
using Xunit;

namespace ARCOS.Core.Tests;

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
        string distModules = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dist", "ARCOS_Runtime", "MODULES");
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
        Assert.Equal(CommandStatus.Completed, resJump.Status);
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

        // Line 1: 第二次執行觸發逾時異常，應自動攔截 [[TimeoutHealing]]、壓入 ErrorHandler Stack 幀並跳躍至 Line 3
        var resFail = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, resFail.Status);
        Assert.Equal(3, ctx.ProgIndex); // 成功自癒跳至 ErrorHandler
        Assert.Equal(1, ctx.FailedPC);  // 記錄出錯點為 Line 1
        Assert.Single(ctx.CallStack);
        Assert.Equal(StackFrameKind.ErrorHandler, ctx.CallStack.Peek().Kind);
        Assert.Equal(1, ctx.CallStack.Peek().ReturnLine);
        Assert.False(engines.Io.GetPort($"{ctx.ThreadName}#Error?")); // 已由 ErrorHandler 接住，不設 Thread#Error?
        Assert.False(engines.Io.GetPort("P0.0")); // Line 2 未被執行

        // Line 3: ErrorHandler (no-op)
        executor.ExecuteStep(ctx);
        Assert.Equal(4, ctx.ProgIndex);

        // Line 4: 執行自癒處置 SetPort HealedFlag
        executor.ExecuteStep(ctx);
        Assert.True(engines.Io.GetPort("HealedFlag"));
        Assert.Equal(5, ctx.ProgIndex);

        // Line 5: 執行 Thread@ErrorRetry:: -> 應從 Stack 彈回 Line 1
        var resRetry = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, resRetry.Status);
        Assert.Empty(ctx.CallStack);
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
            System@IncludeModule::Python;
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

    [Fact]
    public void ExecuteStep_MissingErrorLabel_PopsMessageBoxAndLogsWithoutEmgStop()
    {
        string script = """
            IO@WaitPortOn::NonExistingPort?,0.01,[[NoSuchLabel]]Cylinder Down Timeout;
            IO@SetPort::P0.0;
            """;

        var (ctx, executor, engines, drivers) = CreateTestHarness(script);
        var vm = new ArcosThreadVM(engines, drivers, executor);
        ctx.VM = vm;

        string? capturedMsgBox = null;
        vm.OnMessageBoxRequested = (threadName, msg) =>
        {
            capturedMsgBox = msg;
            return Task.CompletedTask;
        };

        // Step 1: 進入 Wait
        executor.ExecuteStep(ctx);
        Thread.Sleep(25);

        // Step 2: 超時觸發錯誤，因 [[NoSuchLabel]] 不存在，應彈出 MessageBox、寫入 Log，且不觸發 EMGStop& (Prog 99)
        var res = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Error, res.Status);

        // 1. 確認沒有觸發 99: EMG Stop，只有設 Thread#Error? 與停止該執行緒
        Assert.False(vm.IsEmgStopped);
        Assert.False(vm.SubThreads.ContainsKey("EMGStop&"));
        Assert.True(engines.Io.GetPort($"{ctx.ThreadName}#Error?"));
        Assert.False(engines.Io.GetPort("System#Error?"));
        Assert.False(ctx.IsRunning);

        // 2. 確認彈出了 MessageBox，且同一視窗訊息同時包含原錯誤與「[[NoSuchLabel]] 沒有找到」
        Assert.NotNull(capturedMsgBox);
        Assert.Contains("Cylinder Down Timeout", capturedMsgBox);
        Assert.Contains("[[NoSuchLabel]]", capturedMsgBox);

        // 3. 確認視窗內容已同步寫入 SYSTEMLOG
        var sysLog = engines.Record.GetOrCreateChannel("SYSTEMLOG");
        var logs = sysLog.Entries.Take(sysLog.Index).Select(e => e.Message).ToList();
        Assert.Contains(logs, m => m.Contains("[MessageBox]") && m.Contains("Cylinder Down Timeout") && m.Contains("[[NoSuchLabel]]"));
    }

    [Fact]
    public void ManagerWorkers_ThreadMainMonitorsWorkerErrorFlag_AndProgramIsolationDoesNotLeakCallStackHandlers()
    {
        var engines = new BasicEngines();
        var drivers = new DriverManager();
        var vm = new ArcosThreadVM(engines, drivers) { AutoSpawnBackgroundLoop = false };

        // Prog 1 (Worker): 第二次才不超時的假失敗情境 -> 先故意觸發未處理錯誤設 Thread1#Error?
        string workerScript = """
            IO@WaitPortOn::NonExistingWorkerPort?,0.01,[[MissingTag]]WorkerFault;
            IO@SetPort::WorkerDone,True;
            """;
        // Prog 0 (Manager / ThreadMain): 監控 Thread1#Error? 並進行處置 (設定 ManagerHandled 旗標)
        string managerScript = """
            Thread@If::Thread1#Error?;
            IO@SetPort::ManagerHandled,True;
            Thread@EndIf::;
            """;
        // Prog 2 + Prog 3: 驗證獨立程式區原則 + ErrorHandler 內部再 Call 副程式後 ErrorRetry 仍能精準彈回
        string callerWithOwnHandler = """
            Thread@Call::3;
            Thread@Stop::;
            Thread@ErrorHandler::[[SubFault]];
            IO@SetPort::CallerHandlerShouldNotRun,True;
            """;
        string subWithoutHandler = """
            IO@WaitPortOn::NonExistingSubPort?,0.01,[[SubFault]]SubFaultMsg;
            """;

        vm.LoadProgram(1, ProgramTable.FromText(workerScript, 1, "Worker1"));
        vm.LoadProgram(0, ProgramTable.FromText(managerScript, 0, "Manager"));
        vm.LoadProgram(2, ProgramTable.FromText(callerWithOwnHandler, 2, "Caller"));
        vm.LoadProgram(3, ProgramTable.FromText(subWithoutHandler, 3, "Sub"));

        // 1. Worker 執行與逾時
        vm.StartThread(1, 1);
        vm.Tick();
        Thread.Sleep(25);
        vm.Tick();

        Assert.True(engines.Io.GetPort("Thread1#Error?"));
        Assert.False(vm.MainThreads[1].IsRunning);
        Assert.False(vm.IsEmgStopped);

        // 2. Manager (ThreadMain) 檢查 Thread1#Error? 後處置
        vm.StartThread(0, 0);
        for (int i = 0; i < 5 && vm.MainThreads[0].IsRunning; i++)
        {
            vm.Tick();
        }
        Assert.True(engines.Io.GetPort("ManagerHandled"));

        // 3. 驗證程式區獨立性：Prog 3 出錯不會借用呼叫端 Prog 2 的 [[SubFault]] ErrorHandler
        vm.StartThread(2, 2);
        vm.Tick(); // Call 3 -> 進 Prog 3
        vm.Tick(); // WaitPortOn 進 Wait
        Thread.Sleep(25);
        vm.Tick(); // 逾時：Prog 3 無 [[SubFault]]，應設 Thread2#Error? 並停機，不可跑到 Prog 2 的 CallerHandlerShouldNotRun
        Assert.True(engines.Io.GetPort("Thread2#Error?"));
        Assert.False(engines.Io.GetPort("CallerHandlerShouldNotRun"));
        Assert.False(vm.MainThreads[2].IsRunning);
    }

    [Fact]
    public void ExecuteStep_NestedCallInsideErrorHandler_ErrorRetryRestoresOriginalFailedLineFromStack()
    {
        string script = """
            IO@WaitPortOn::RetryablePort?,0.01,[[FixAndRetry]]NeedHeal;
            Thread@Stop::;
            Thread@ErrorHandler::[[FixAndRetry]];
            Thread@Call::HealSub;
            Thread@ErrorRetry::;
            Thread@Label::HealSub;
            IO@SetPort::RetryablePort?,True;
            Thread@Return::;
            """;

        var (ctx, executor, engines, _) = CreateTestHarness(script);

        executor.ExecuteStep(ctx); // Line 1 -> Wait
        Thread.Sleep(25);
        executor.ExecuteStep(ctx); // Line 1 fail -> Jump Line 3, push ErrorHandler(Line 1)
        Assert.Equal(3, ctx.ProgIndex);

        executor.ExecuteStep(ctx); // Line 3 ErrorHandler -> Line 4
        executor.ExecuteStep(ctx); // Line 4 Call HealSub -> push Call(Line 5), jump Line 6
        Assert.Equal(6, ctx.ProgIndex);
        Assert.Equal(2, ctx.CallStack.Count);

        executor.ExecuteStep(ctx); // Line 6 Label -> Line 7
        executor.ExecuteStep(ctx); // Line 7 SetPort RetryablePort?=True -> Line 8
        executor.ExecuteStep(ctx); // Line 8 Return -> pop Call(Line 5), resume ErrorHandler at Line 5
        Assert.Equal(5, ctx.ProgIndex);
        Assert.Single(ctx.CallStack);

        executor.ExecuteStep(ctx); // Line 5 ErrorRetry -> pop ErrorHandler(Line 1) -> back to Line 1
        Assert.Equal(1, ctx.ProgIndex);
        Assert.Empty(ctx.CallStack);

        executor.ExecuteStep(ctx); // Line 1 WaitPortOn now succeeds! -> Line 2
        Assert.Equal(2, ctx.ProgIndex);
    }

    private sealed class DirectPcFlowHandler : IModuleHandler
    {
        public string ModuleName => "FlowTest";
        public IReadOnlyList<string> GetSupportedCommands() => new[] { "DirectJump", "SelfLoopOnce", "CtxCall", "CtxReturn" };

        public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
        {
            switch (action)
            {
                case "DirectJump":
                    if (int.TryParse(filteredParam, out int target))
                    {
                        ctx.ProgIndex = target;
                    }
                    else
                    {
                        ctx.Jump(filteredParam);
                    }
                    return CommandResult.Done();

                case "SelfLoopOnce":
                    ctx.State.Value = 99;
                    if (!ctx.Engines.Io.GetPort("SelfLoopVisited"))
                    {
                        ctx.Engines.Io.SetPort("SelfLoopVisited", true);
                        ctx.ProgIndex = ctx.ProgIndex; // 同行自我跳轉，仍不應被 Done() +1
                        return CommandResult.Done();
                    }
                    return CommandResult.Done();

                case "CtxCall":
                    ctx.Call(filteredParam);
                    return CommandResult.Done();

                case "CtxReturn":
                    ctx.Return();
                    return CommandResult.Done();

                default:
                    return CommandResult.Done();
            }
        }
    }

    [Fact]
    public void ExecuteStep_DirectPcAndStackMutationWithDone_DoesNotDoubleAdvanceAndResetsState()
    {
        var io = new IoEngine();
        var varEng = new VariableEngine();
        var engines = new BasicEngines(io, varEng, new PointEngine(io), new ImageEngine(io), new DaqEngine(io), new TimerEngine(io));
        var drivers = new DriverManager(engines);
        var registry = ModuleRegistry.CreateDefault();
        registry.RegisterHandler(new DirectPcFlowHandler());
        var executor = new CommandExecutor(registry);
        var vm = new ArcosThreadVM(engines, drivers, executor);

        // Prog 0: 呼叫副程式 Prog 1 -> 跳過 Line 3 -> 自我跳轉一次後繼續
        vm.LoadProgram(0, ProgramTable.FromText("""
            FlowTest@CtxCall::1;
            FlowTest@DirectJump::SkipTarget;
            IO@SetPort::ShouldBeSkipped,True;
            Thread@Label::SkipTarget;
            FlowTest@SelfLoopOnce::;
            IO@SetPort::Finished,True;
            """, 0, "Main"));

        // Prog 1: 副程式直接用 ctx.Return() + Done() 返回 Prog 0 第 2 行
        vm.LoadProgram(1, ProgramTable.FromText("""
            IO@SetPort::SubRan,True;
            FlowTest@CtxReturn::;
            """, 1, "Sub"));

        var ctx = vm.MainThreads[0];
        ctx.Program = vm.GetProgram(0);
        ctx.ProgIndex = 1;
        ctx.IsRunning = true;

        // 1. Line 1: CtxCall -> 切換到 Prog 1 第 1 行 (不額外 +1)
        var r1 = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, r1.Status);
        Assert.Equal(1, ctx.Program.ProgramNumber);
        Assert.Equal(1, ctx.ProgIndex);
        Assert.Single(ctx.CallStack);

        // 2. Prog 1 Line 1: SetPort SubRan -> 正常 +1 到 Line 2
        executor.ExecuteStep(ctx);
        Assert.True(io.GetPort("SubRan"));
        Assert.Equal(2, ctx.ProgIndex);

        // 3. Prog 1 Line 2: CtxReturn -> 返回 Prog 0 第 2 行 (不額外 +1)
        executor.ExecuteStep(ctx);
        Assert.Equal(0, ctx.Program.ProgramNumber);
        Assert.Equal(2, ctx.ProgIndex);
        Assert.Empty(ctx.CallStack);

        // 4. Prog 0 Line 2: DirectJump SkipTarget -> 跳至 Line 4 (略過 Line 3)
        executor.ExecuteStep(ctx);
        Assert.Equal(4, ctx.ProgIndex);
        Assert.False(io.GetPort("ShouldBeSkipped"));

        // 5. Line 4 Label -> 正常 +1 到 Line 5
        executor.ExecuteStep(ctx);
        Assert.Equal(5, ctx.ProgIndex);

        // 6. Line 5 SelfLoopOnce 第一次：設定 ctx.ProgIndex = ctx.ProgIndex 並回 Done -> 留在本行且 State 仍重設為 0
        executor.ExecuteStep(ctx);
        Assert.True(io.GetPort("SelfLoopVisited"));
        Assert.Equal(5, ctx.ProgIndex);
        Assert.Equal(0, ctx.State.Value);

        // 7. Line 5 SelfLoopOnce 第二次：未改 PC 回 Done -> 正常 +1 到 Line 6
        executor.ExecuteStep(ctx);
        Assert.Equal(6, ctx.ProgIndex);
        Assert.Equal(0, ctx.State.Value);

        // 8. Line 6 Finished
        executor.ExecuteStep(ctx);
        Assert.True(io.GetPort("Finished"));
    }
}
