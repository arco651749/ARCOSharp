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
}
