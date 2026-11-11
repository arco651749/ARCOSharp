using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using ARCOS.Core.Execution;
using Xunit;

namespace ARCOS.Core.Tests;

public class ArcosThreadVmTests
{
    private (ArcosThreadVM VM, BasicEngines Engines) CreateVM()
    {
        var io = new IoEngine();
        var varEng = new VariableEngine();
        var pt = new PointEngine(io);
        var img = new ImageEngine(io);
        var daq = new DaqEngine(io);
        var timer = new TimerEngine(io);

        var engines = new BasicEngines(io, varEng, pt, img, daq, timer);
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        return (vm, engines);
    }

    [Fact]
    public void ArcosThreadVM_Initialization_SetsUp13ThreadsAndIoFlags()
    {
        var (vm, engines) = CreateVM();

        Assert.Equal(13, vm.MainThreads.Count);
        Assert.Equal("ThreadMain", vm.MainThreads[0].ThreadName);
        Assert.Equal("Thread1", vm.MainThreads[1].ThreadName);
        Assert.Equal("Thread12", vm.MainThreads[12].ThreadName);

        Assert.False(engines.Io.GetPort("ThreadMain#Run?"));
        Assert.False(engines.Io.GetPort("Thread1#Run?"));
    }

    [Fact]
    public void ArcosThreadVM_DispatchWorker_ExecutesAndSetsRunFlag()
    {
        var (vm, engines) = CreateVM();

        // 設定 Program 1 給 Worker 1
        string workerScript = """
            IO@SetPort::WorkerDone;
            """;
        vm.LoadProgram(1, ProgramTable.FromText(workerScript, 1, "Worker1"));

        // 設定 Program 0 (Manager) 派發 Program 1 到 Thread 1
        string managerScript = """
            Thread@Run::1,1;
            """;
        vm.LoadProgram(0, ProgramTable.FromText(managerScript, 0, "Manager"));

        // 啟動 Manager
        vm.StartThread(0, 0);
        Assert.True(engines.Io.GetPort("ThreadMain#Run?"));

        // Tick 1: Manager 執行 Thread@Run::1,1
        vm.Tick();
        Assert.True(engines.Io.GetPort("Thread1#Run?"));

        // Tick 2: Worker 1 執行 SetPort::WorkerDone
        vm.Tick();
        Assert.True(engines.Io.GetPort("WorkerDone"));

        // Tick 3: Worker 1 完成結束，#Run? 歸 false
        vm.Tick();
        Assert.False(engines.Io.GetPort("Thread1#Run?"));
    }

    [Fact]
    public void ArcosThreadVM_PrivilegeAndPause_BypassesPauseAndSelfHeals()
    {
        var (vm, engines) = CreateVM();

        // Worker 普通執行緒 (受暫停卡控)
        string workerScript = """
            IO@SetPort::Step1;
            IO@SetPort::Step2;
            """;
        vm.LoadProgram(1, ProgramTable.FromText(workerScript, 1, "NormalWorker"));
        vm.StartThread(1, 1);

        // 執行 Step 1
        vm.Tick();
        Assert.True(engines.Io.GetPort("Step1"));
        Assert.False(engines.Io.GetPort("Step2"));

        // 啟動全機暫停 SystemPause
        engines.Timer.SetSystemPause(true);
        Assert.True(engines.Timer.IsSystemPaused);

        // 普通執行緒在暫停期間執行 Tick -> 被 Pause 閘門阻擋，不推進 Step 2
        vm.Tick();
        Assert.False(engines.Io.GetPort("Step2"));

        // 啟動帶 & 特權的看門狗 SubThread (如 Watchdog&，豁免暫停)
        string watchdogScript = """
            IO@SetPort::WatchdogRan;
            System@SystemResume::;
            """;
        vm.StartSubThread("Watchdog&", ProgramTable.FromText(watchdogScript, 90, "Watchdog&"));

        // Tick: Watchdog& 具備特權，可在全機暫停下繼續執行，並呼叫 SystemResume
        vm.Tick();
        Assert.True(engines.Io.GetPort("WatchdogRan"));
        vm.Tick(); // 執行 SystemResume
        Assert.False(engines.Timer.IsSystemPaused); // 成功自動解除全機暫停！

        // 解除暫停後，普通 Worker 恢復推進 Step 2
        vm.Tick();
        Assert.True(engines.Io.GetPort("Step2"));
    }

    [Fact]
    public void ArcosThreadVM_SubThreadLifecycle_RunsConcurrentlyAndSyncs()
    {
        var (vm, engines) = CreateVM();

        string script = """
            Thread@SubThreadStart::ParallelTask;
            IO@SetPort::SubDone;
            Thread@SubThreadEnd::ParallelTask;
            Thread@SubThreadWait::ParallelTask;
            IO@SetPort::AllDone;
            """;

        vm.LoadProgram(0, ProgramTable.FromText(script, 0, "Main"));
        vm.StartThread(0, 0);

        // Tick 1: 啟動 SubThread 並跳過內部定義
        vm.Tick();
        Assert.True(engines.Io.GetPort("ParallelTask#Run?"));

        // Tick 2: SubThread 執行 SetPort SubDone，Main 進入 SubThreadWait
        vm.Tick();
        Assert.True(engines.Io.GetPort("SubDone"));

        // Tick 3: SubThread 結束歸零，Main 等待完成前進至 AllDone
        vm.Tick();
        Assert.False(engines.Io.GetPort("ParallelTask#Run?"));

        vm.Tick();
        Assert.True(engines.Io.GetPort("AllDone"));
    }

    [Fact]
    public void SystemPauseAndResume_LocalThreadPauseWithCondition_FreezesOnlyTargetStationThread()
    {
        var (vm, engines) = CreateVM();

        // Station 1 (Thread 1): 2 步動作
        vm.LoadProgram(1, ProgramTable.FromText("""
            IO@SetPort::St1_Step1;
            IO@SetPort::St1_Step2;
            """, 1, "Station1"));

        // Station 2 (Thread 2): 2 步動作
        vm.LoadProgram(2, ProgramTable.FromText("""
            IO@SetPort::St2_Step1;
            IO@SetPort::St2_Step2;
            """, 2, "Station2"));

        // Controller (Thread 0): 測試帶 CONDITION 的 Local Pause 與 Local Resume
        vm.LoadProgram(0, ProgramTable.FromText("""
            System@Pause::Thread1,CONDITION=Sensor_Fault;
            System@Pause::Thread1,CONDITION=Sensor_BlockSt1;
            System@Resume::Thread1,CONDITION=Sensor_ClearSt1;
            """, 0, "MainController"));

        // 預設 Sensor_Fault = false, Sensor_BlockSt1 = true, Sensor_ClearSt1 = true
        engines.Io.SetPort("Sensor_Fault", false);
        engines.Io.SetPort("Sensor_BlockSt1", true);
        engines.Io.SetPort("Sensor_ClearSt1", true);

        vm.StartThread(0, 0);
        vm.StartThread(1, 1);
        vm.StartThread(2, 2);

        // Tick 1:
        // Thread 0 執行第 1 行 System@Pause::Thread1,CONDITION=Sensor_Fault (因 Sensor_Fault=false 不生效)
        // Thread 1 執行 St1_Step1
        // Thread 2 執行 St2_Step1
        vm.Tick();
        Assert.False(vm.MainThreads[1].IsLocalPaused);
        Assert.False(engines.Io.GetPort("Thread1#Pause?"));
        Assert.True(engines.Io.GetPort("St1_Step1"));
        Assert.True(engines.Io.GetPort("St2_Step1"));

        // Tick 2:
        // Thread 0 執行第 2 行 System@Pause::Thread1,CONDITION=Sensor_BlockSt1 (Sensor_BlockSt1=true -> Thread 1 進入 Local Pause!)
        // Thread 1 因剛被 Thread 0 設為 LocalPaused，此輪被凍結，不執行 St1_Step2！
        // Thread 2 完全不受影響，順利執行 St2_Step2！
        vm.Tick();
        Assert.True(vm.MainThreads[1].IsLocalPaused);
        Assert.True(engines.Io.GetPort("Thread1#Pause?"));
        Assert.False(engines.Io.GetPort("St1_Step2")); // Station 1 確實被凍結！
        Assert.True(engines.Io.GetPort("St2_Step2"));  // Station 2 照常運作完成！

        // Tick 3:
        // Thread 0 執行第 3 行 System@Resume::Thread1,CONDITION=Sensor_ClearSt1 -> 解除 Thread 1 的 Local Pause!
        // Thread 1 同輪立即恢復並執行 St1_Step2！
        vm.Tick();
        Assert.False(vm.MainThreads[1].IsLocalPaused);
        Assert.False(engines.Io.GetPort("Thread1#Pause?"));
        Assert.True(engines.Io.GetPort("St1_Step2"));
    }

    [Fact]
    public async Task IndependentParallelThreadVM_AutoSpawnLoop_RunsIndependentlyWithZeroDelay()
    {
        var (vm, engines) = CreateVM();
        vm.AutoSpawnBackgroundLoop = true;

        vm.LoadProgram(1, ProgramTable.FromText("""
            System@Note::Fast1;
            System@Note::Fast2;
            IO@SetPort::ParallelWorker1Done;
            """, 1, "FastWorker1"));

        vm.StartThread(1, 1);

        // 等待幾毫秒讓獨立平行單元自己跑完
        for (int i = 0; i < 50 && !engines.Io.GetPort("ParallelWorker1Done"); i++)
        {
            await Task.Delay(5);
        }

        Assert.True(engines.Io.GetPort("ParallelWorker1Done"));
        Assert.False(engines.Io.GetPort("Thread1#Run?"));
    }

    [Fact]
    public void SystemPrograms_DefaultCommandsAndPrivilegedHooks_ShouldWork()
    {
        var (vm, engines) = CreateVM();

        // 1. 驗證預設系統程式區指令
        var p94 = vm.GetProgram(94);
        var p95 = vm.GetProgram(95);
        var p96 = vm.GetProgram(96);
        var p99 = vm.GetProgram(99);

        Assert.Equal(1, p94.Count);
        Assert.Equal("System@Resume", p94[1].Command);

        Assert.Equal(1, p95.Count);
        Assert.Equal("System@Pause", p95[1].Command);

        Assert.Equal(1, p96.Count);
        Assert.Equal("Thread@Run", p96[1].Command);
        Assert.Equal("0,0", p96[1].RawParameter);

        Assert.Equal(1, p99.Count);
        Assert.Equal("Motion@Stop", p99[1].Command);
        Assert.Equal("", p99[1].RawParameter);

        // 2. 觸發 95: Pause On -> 產生 PauseOn& 並執行 System@Pause 進入全機暫停
        var ctx95 = vm.TriggerSystemHook(95);
        Assert.NotNull(ctx95);
        Assert.Equal("PauseOn&", ctx95!.ThreadName);
        Assert.True(ctx95.IsBypassPause);
        vm.Tick();
        Assert.True(engines.Timer.IsSystemPaused);

        // 3. 在全機暫停期間，觸發 94: Pause Off -> PauseOff& 因尾端帶 "&" 可穿透暫停並執行 System@Resume 解除全機暫停
        var ctx94 = vm.TriggerSystemHook(94);
        Assert.NotNull(ctx94);
        Assert.Equal("PauseOff&", ctx94!.ThreadName);
        Assert.True(ctx94.IsBypassPause);
        vm.Tick();
        Assert.False(engines.Timer.IsSystemPaused);
    }

    [Fact]
    public void MotionStop_WithoutArguments_ShouldStopAllMotionDevices()
    {
        var (vm, engines) = CreateVM();

        var ctx = vm.MainThreads[0];
        vm.Executor.ExecuteCommandLine(ctx, "Motion@AddMotor::AxisX,MockMotion");
        vm.Executor.ExecuteCommandLine(ctx, "Motion@AddMotor::AxisY,MockMotion");

        engines.Io.SetPort("AxisX#Busy?", true);
        engines.Io.SetPort("AxisX#Done?", false);
        engines.Io.SetPort("AxisY#Busy?", true);
        engines.Io.SetPort("AxisY#Done?", false);

        // 執行 99: EMG Stop 預設的無參數 Motion@Stop
        var res = vm.Executor.ExecuteCommandLine(ctx, "Motion@Stop::");
        Assert.Equal(CommandStatus.Completed, res.Status);

        Assert.False(engines.Io.GetPort("AxisX#Busy?"));
        Assert.True(engines.Io.GetPort("AxisX#Done?"));
        Assert.False(engines.Io.GetPort("AxisY#Busy?"));
        Assert.True(engines.Io.GetPort("AxisY#Done?"));
    }

    [Fact]
    public void SystemCsviCommands_SessionLogFlush_ClearError_And_MissingCsviFails()
    {
        var (vm, engines) = CreateVM();
        var ctx = vm.MainThreads[0];

        // 1. 驗證已刪除 CSVI 的廢棄指令直接回傳 Error (絕不退回內建保底)
        var resRemoved1 = vm.Executor.ExecuteCommandLine(ctx, "System@ParameterSet::A,123");
        Assert.Equal(CommandStatus.Error, resRemoved1.Status);
        var resRemoved2 = vm.Executor.ExecuteCommandLine(ctx, "System@AddExternalDriver::Motion,Mock");
        Assert.Equal(CommandStatus.Error, resRemoved2.Status);
        var resRemoved3 = vm.Executor.ExecuteCommandLine(ctx, "System@UserPanelCMD::Refresh");
        Assert.Equal(CommandStatus.Error, resRemoved3.Status);

        // 2. 驗證 Log Message -> Clear Message 會自動將訊息寫入當次 Session Log 檔 (LOGS/yyyyMMdd-HHmmss.log)
        string sessionLogPath = ARCOS.Core.Common.ArcosPaths.SessionLogFilePath;
        Assert.True(System.IO.File.Exists(sessionLogPath));

        string uniqueMarker = $"SESSION_TEST_MSG_{Guid.NewGuid():N}";
        vm.Executor.ExecuteCommandLine(ctx, $"System@Log Message::{uniqueMarker}");
        var sysChannel = engines.Record.GetOrCreateChannel("SYSTEMLOG");
        Assert.Contains(sysChannel.Entries, e => e.Message.Contains(uniqueMarker));

        // 執行 Clear Message.csvi -> 清空記憶體並寫入 SessionLogFilePath
        vm.Executor.ExecuteCommandLine(ctx, "System@Clear Message::");
        Assert.Equal(0, sysChannel.Index);
        string fileContent = System.IO.File.ReadAllText(sessionLogPath);
        Assert.Contains(uniqueMarker, fileContent);

        // 3. 驗證 Log Error.csvi 觸發 99: EMG Stop 並設 System#Error? = True，以及 Clear Error.csvi 復歸
        vm.LoadProgram(0, ProgramTable.FromText("System@Log Error::CriticalServoAlarm;", 0, "Main"));
        vm.StartThread(0, 0);
        vm.Tick(); // 執行 Log Error -> 觸發 HandleError -> 啟動 EMGStop& (Prog 99)

        Assert.True(engines.Io.GetPort("System#Error?"));
        Assert.True(vm.SubThreads.ContainsKey("EMGStop&"));

        // 執行 Clear Error.csvi -> 復歸 System#Error? = False 並清除 [ERROR] 紀錄
        vm.Executor.ExecuteCommandLine(ctx, "System@Clear Error::");
        Assert.False(engines.Io.GetPort("System#Error?"));
        Assert.DoesNotContain(sysChannel.Entries, e => e.Message.Contains("[ERROR]"));
    }

    [Fact]
    public void SystemCsviCommands_MessageBoxAndWarningBox_NonBlockingMicrostepFlow()
    {
        var (vm, engines) = CreateVM();

        TaskCompletionSource? msgTcs = null;
        TaskCompletionSource<bool>? warnTcs = null;

        vm.OnMessageBoxRequested = (threadName, msg) =>
        {
            msgTcs = new TaskCompletionSource();
            return msgTcs.Task;
        };

        vm.OnWarningBoxRequested = (threadName, msg) =>
        {
            warnTcs = new TaskCompletionSource<bool>();
            return warnTcs.Task;
        };

        engines.Io.SetPort("DoorOpen", true);
        vm.LoadProgram(0, ProgramTable.FromText("""
            System@MessageBox::Please confirm fixture;
            System@WarningBox::DoorOpen,Door is open! Continue?;
            IO@SetPort::AfterWarningDone;
            """, 0, "Main"));

        vm.StartThread(0, 0);

        // Tick 1: 進入 MessageBox，處於 Waiting 狀態，PC 停在 1
        vm.Tick();
        Assert.NotNull(msgTcs);
        Assert.Equal(1, vm.MainThreads[0].ProgIndex);

        // Tick 2: 使用者尚未按確定，維持 Waiting
        vm.Tick();
        Assert.Equal(1, vm.MainThreads[0].ProgIndex);

        // 使用者按下確定 -> Tick 3 完成 MessageBox，推進至第 2 行 WarningBox
        msgTcs!.SetResult();
        vm.Tick();
        Assert.Equal(2, vm.MainThreads[0].ProgIndex);

        // Tick 4: 進入 WarningBox (DoorOpen=true)，處於 Waiting 狀態
        vm.Tick();
        Assert.NotNull(warnTcs);
        Assert.Equal(2, vm.MainThreads[0].ProgIndex);

        // 使用者選擇「停止執行 (false)」-> Tick 5 終止該執行緒，不執行第 3 行 AfterWarningDone
        warnTcs!.SetResult(false);
        vm.Tick();
        Assert.False(vm.MainThreads[0].IsRunning);
        Assert.False(engines.Io.GetPort("AfterWarningDone"));
    }

    [Fact]
    public void ThreadCsviCommands_SubroutineCallReturn_NestedIf_MultiVarForeach_ConcurrentSubThread_ErrorRetryOffset_KeyLock()
    {
        var (vm, engines) = CreateVM();

        // 設定副程式 Program 10 (SubRoutine10)
        vm.LoadProgram(10, ProgramTable.FromText("""
            Variable@Inc::CallCounter,10;
            Thread@Return::;
            Variable@Set::CallCounter,9999;
            """, 10, "SubRoutine10"));

        // 設定 Worker Program 2 (供 Thread@Run 測試以 Label 作為起始點)
        vm.LoadProgram(2, ProgramTable.FromText("""
            Variable@Set::RunResult,SkippedLine;
            Thread@Label::EntryLabel;
            Variable@Set::RunResult,StartedFromLabel;
            """, 2, "WorkerProg2"));

        // 設定主程式 Program 0
        string mainScript = """
            Variable@Set::CallCounter,5;
            Thread@Call::10;
            Thread@Call::SubRoutine10;
            Variable@Set::OuterCond,1;
            Variable@Set::InnerCond,0;
            Thread@If::OuterCond == 1;
                Thread@If::InnerCond == 1;
                    Variable@Set::IfRes,WrongInner;
                Thread@Else::;
                    Variable@Set::IfRes,CorrectInnerElse;
                Thread@EndIf::;
            Thread@Else::;
                Variable@Set::IfRes,WrongOuterElse;
            Thread@EndIf::;
            Variable@Set::ArrA,[X1;X2;X3];
            Variable@Set::ArrB,[Y1;Y2;Y3];
            Variable@Set::Joined,;
            Thread@ForeachStart::LoopAB;
                Variable@Append::Joined,$(ArrA)$(ArrB)_;
                Thread@SubThreadStart::proc&&;
                    Variable@Set::Snap_$(ArrA),$(ArrB),Global;
                Thread@SubThreadEnd::proc&&;
            Thread@ForeachLoop::LoopAB,ArrA,ArrB;
            Thread@SubThreadWait::proc&&;
            Thread@SubThreadWait::NonExistentSub,SKIPNOTFIND=1;
            Thread@Run::2,1,EntryLabel;
            Thread@Wait::1;
            """;

        vm.LoadProgram(0, ProgramTable.FromText(mainScript, 0, "Main"));
        vm.StartThread(0, 0);

        for (int i = 0; i < 200 && (vm.MainThreads[0].IsRunning || vm.MainThreads[1].IsRunning || vm.IsSubThreadRunning("proc&&")); i++)
        {
            vm.Tick();
        }

        // 1. 驗證 Thread@Call 與 Thread@Return (5 + 10 + 10 = 25)
        Assert.Equal("25", engines.Variable.Get("CallCounter", vm.MainThreads[0].CurrentSection));

        // 2. 驗證巢狀 If...Else...EndIf
        Assert.Equal("CorrectInnerElse", engines.Variable.Get("IfRes", vm.MainThreads[0].CurrentSection));

        // 3. 驗證 ForeachLoop 同步推進 ArrA 與 ArrB
        Assert.Equal("X1Y1_X2Y2_X3Y3_", engines.Variable.Get("Joined", vm.MainThreads[0].CurrentSection));

        // 4. 驗證 SubThread proc&& 併發多實例各自快照迴圈當下的陣列元素值
        Assert.Equal("Y1", engines.Variable.Get("Snap_X1", "Global"));
        Assert.Equal("Y2", engines.Variable.Get("Snap_X2", "Global"));
        Assert.Equal("Y3", engines.Variable.Get("Snap_X3", "Global"));

        // 5. 驗證 Thread@Run 支援以 Label 名稱作為起始行
        Assert.Equal("StartedFromLabel", engines.Variable.Get("RunResult", vm.MainThreads[1].CurrentSection));

        // 6. 驗證 SubThreadWait 若未帶 SKIPNOTFIND=1 且目標不存在，應回傳 Error
        var waitErr = vm.Executor.ExecuteCommandLine(vm.MainThreads[0], "Thread@SubThreadWait::NoSuchSubThread;");
        Assert.Equal(CommandStatus.Error, waitErr.Status);

        // 7. 驗證 KeySectionLock.csvi 與 KeySectionUnLock.csvi 跨檔案共用鎖表
        var lock1 = vm.Executor.ExecuteCommandLine(vm.MainThreads[0], "Thread@KeySectionLock::SharedRes,0.1,BusyTimeout;");
        Assert.Equal(CommandStatus.Completed, lock1.Status);

        var lock2 = vm.Executor.ExecuteCommandLine(vm.MainThreads[1], "Thread@KeySectionLock::SharedRes,0.1,BusyTimeout;");
        Assert.Equal(CommandStatus.Waiting, lock2.Status);

        var unlock1 = vm.Executor.ExecuteCommandLine(vm.MainThreads[0], "Thread@KeySectionUnLock::SharedRes;");
        Assert.Equal(CommandStatus.Completed, unlock1.Status);

        var lock2After = vm.Executor.ExecuteCommandLine(vm.MainThreads[1], "Thread@KeySectionLock::SharedRes,0.1,BusyTimeout;");
        Assert.Equal(CommandStatus.Completed, lock2After.Status);
        vm.Executor.ExecuteCommandLine(vm.MainThreads[1], "Thread@KeySectionUnLock::SharedRes;");

        // 8. 驗證 ErrorRetry 支援 $offset (FailedPC + offset)
        vm.MainThreads[0].FailedPC = 5;
        var retryRes = vm.Executor.ExecuteCommandLine(vm.MainThreads[0], "Thread@ErrorRetry::1==1,-2;");
        Assert.Equal(CommandStatus.Retry, retryRes.Status);
        Assert.Equal(-2, retryRes.TargetLine);
    }
}
