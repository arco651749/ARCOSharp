using MVCON.Core.Drivers;
using MVCON.Core.Engines;
using MVCON.Core.Execution;
using Xunit;

namespace MVCON.Core.Tests;

public class MvconThreadVmTests
{
    private (MvconThreadVM VM, BasicEngines Engines) CreateVM()
    {
        var io = new IoEngine();
        var varEng = new VariableEngine();
        var pt = new PointEngine(io);
        var img = new ImageEngine(io);
        var daq = new DaqEngine(io);
        var timer = new TimerEngine(io);

        var engines = new BasicEngines(io, varEng, pt, img, daq, timer);
        var drivers = new DriverManager(engines);
        var vm = new MvconThreadVM(engines, drivers);

        return (vm, engines);
    }

    [Fact]
    public void MvconThreadVM_Initialization_SetsUp13ThreadsAndIoFlags()
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
    public void MvconThreadVM_DispatchWorker_ExecutesAndSetsRunFlag()
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
    public void MvconThreadVM_PrivilegeAndPause_BypassesPauseAndSelfHeals()
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
    public void MvconThreadVM_SubThreadLifecycle_RunsConcurrentlyAndSyncs()
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
}
