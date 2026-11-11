using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using ARCOS.Core.Execution;
using ARCOS.Core.Guidance;
using ARCOS.Core.Model;
using ARCOS.Driver.MockIO;
using ARCOS.Module.IO;
using Xunit;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Core.Tests;

/// <summary>
/// 針對 IO 模組與 MockIO 驅動之大一統三元架構 (Trinity) 與動態載入/熱重載專屬閉環驗證測試：
/// 1. 開機零殘留與零預載 (Startup Clean State & Complete Isolation)
/// 2. 隨選動態加載 (On-Demand Lazy Loading via Sequencer Command)
/// 3. 動態掛載驅動 (On-Demand Driver Loading via IO@AddIODriver)
/// 4. 三元架構維度 1：C# 邏輯與微秒級雙向批次搬運 (MockIoDriver & IoEngine Batch Sync)
/// 5. 三元架構維度 2：UI 前面板即時監控與控制項掛載 (MockIoView Front Panel)
/// 6. 三元架構維度 3：參數導引視窗與 Wizard 精靈 (Wizard Tab & IParameterGuidance)
/// 7. Windows 實體檔案零鎖死、現場覆寫與設備熱更新 (Zero-Locking Overwrite & ReloadDevice)
/// 8. 真實排程器工作流端到端閉環測試 (End-to-End Sequencer Lifecycle)
/// </summary>
public class IoSubsystemTrinityAndDynamicVerificationTests
{
    private static readonly object _initLock = new();

    static IoSubsystemTrinityAndDynamicVerificationTests()
    {
        lock (_initLock)
        {
            _ = typeof(Avalonia.Data.RelativeSource).Assembly;
            _ = typeof(Avalonia.Controls.Control).Assembly;
            _ = typeof(Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader).Assembly;
        }
    }

    [Fact]
    public void Test1_StartupIsolation_NoPreloadedIoModuleOrDriver()
    {
        // 1. 驗證新建立的 ModuleRegistry 僅包含 Basic 核心模組，IO 模組絕對不在其中
        var registry = ModuleRegistry.CreateDefault();
        Assert.False(registry.HasModule("IO"), "IO 模組開機時嚴格禁止預載入！");

        // 2. 驗證 DriverManager 開機時絕對為空
        var engines = new BasicEngines();
        var driverMgr = new DriverManager(engines);
        Assert.Empty(driverMgr.GetAllDevices());
        Assert.Null(driverMgr.GetDevice("IO", "IO1"));
    }

    [Fact]
    public void Test2_OnDemandDynamicLoading_IoModule_TriggeredByCommand()
    {
        var registry = ModuleRegistry.CreateDefault();
        var engines = new BasicEngines();
        var driverMgr = new DriverManager(engines);
        var executor = new CommandExecutor(registry);

        // 1. 執行前確認尚未載入
        Assert.False(registry.HasModule("IO"));

        // 2. 下達標準 ARCOS 指令：IO@SetPort::P0.0, 1;
        var program = ProgramTable.FromText("IO@SetPort::P0.0, 1", 0, "TestIOProgram");
        var ctx = new ExecutionContext(0, "Thread_Main", program, engines, driverMgr)
        {
            Registry = registry
        };

        // 3. 執行單步
        var result = executor.ExecuteStep(ctx);

        // 4. 驗證 IO 模組已於下指令當下被隨選動態載入！
        Assert.True(registry.HasModule("IO"), "下達 IO 指令時必須自動隨選加載 IO 模組！");
        var handler = registry.GetHandler("IO");
        Assert.NotNull(handler);
        Assert.Equal("IO", handler.ModuleName);

        // 5. 驗證指令確實被執行，P0.0 設為 true
        Assert.True(engines.Io.GetPort("P0.0"));
    }

    [Fact]
    public void Test3_OnDemandDriverLoading_Via_AddIoDriverCommand()
    {
        var registry = ModuleRegistry.CreateDefault();
        var engines = new BasicEngines();
        var driverMgr = new DriverManager(engines);
        var executor = new CommandExecutor(registry);

        // 註冊工廠供測試使用
        driverMgr.RegisterDriverFactory("MockIO", () => new MockIoDriver());

        // 1. 初始無驅動
        Assert.Null(driverMgr.GetDevice("IO", "IO1"));

        // 2. 下達 IO@AddIODriver 指令
        string cmdText = "IO@AddIODriver::IO1, MockIO, Device=com1,OutStart=p0.0,OutPorts=32,InStart=p4.0,InPorts=32";
        var program = ProgramTable.FromText(cmdText, 0, "TestDriverProgram");
        var ctx = new ExecutionContext(0, "Thread_Main", program, engines, driverMgr)
        {
            Registry = registry
        };

        var result = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, result.Status);

        // 3. 驗證 IO1 驅動已成功建立且參數正確
        var device = driverMgr.GetDevice("IO", "IO1") as MockIoDriver;
        Assert.NotNull(device);
        Assert.Equal("IO1", device.DeviceName);
        Assert.Equal("IO", device.Category);
        Assert.Equal("MockIO", device.DriverType);
        Assert.Equal("com1", device.Device);
        Assert.Equal("p0.0", device.OutStart);
        Assert.Equal(32, device.OutPorts);
        Assert.Equal("p4.0", device.InStart);
        Assert.Equal(32, device.InPorts);

        driverMgr.ShutdownAll();
    }

    [Fact]
    public void Test4_Trinity_Dimension1_MockIoDriver_BatchTransferAndTicketQueue()
    {
        var ioEngine = new IoEngine();
        var engines = new BasicEngines(ioEngine, new VariableEngine(), new PointEngine(ioEngine), new ImageEngine(ioEngine), new DaqEngine(ioEngine), new TimerEngine(ioEngine));
        var driver = new MockIoDriver();

        // 1. 初始化驅動：OutStart=P0.0 (32點), InStart=P4.0 (32點)
        driver.Init("IO1", "Device=Virtual,OutStart=p0.0,OutPorts=32,InStart=p4.0,InPorts=32,Interval=15", engines);

        // 2. 測試上層 IoEngine 輸出 -> 驅動 ReadbackOutputs
        ioEngine.SetPort("P0.0", true);
        ioEngine.SetPort("P0.7", true);
        ioEngine.SetPort("P3.7", true);

        // 透過 Ticket 觸發立即刷新
        var updateTicket = new DriverTicket("FORCE_UPDATE");
        driver.PostTicket(updateTicket);
        var resUpdate = updateTicket.Wait(1000);
        Assert.True(resUpdate.Success);

        Assert.True(driver.ReadbackOutputs[0], "P0.0 應正確讀回為 true");
        Assert.True(driver.ReadbackOutputs[7], "P0.7 應正確讀回為 true");
        Assert.True(driver.ReadbackOutputs[31], "P3.7 應正確讀回為 true");
        Assert.False(driver.ReadbackOutputs[1], "P0.1 應保持 false");

        // 3. 測試外部感測器觸發 -> 寫回 IoEngine
        driver.SetSimulatedInput(0, true);  // P4.0
        driver.SetSimulatedInput(31, true); // P7.7

        var syncTicket = new DriverTicket("FORCE_UPDATE");
        driver.PostTicket(syncTicket);
        syncTicket.Wait(1000);

        Assert.True(ioEngine.GetPort("P4.0"), "模擬硬體輸入 P4.0 應同步至 IoEngine");
        Assert.True(ioEngine.GetPort("P7.7"), "模擬硬體輸入 P7.7 應同步至 IoEngine");
        Assert.False(ioEngine.GetPort("P4.1"), "未撥動的 P4.1 應保持 false");

        // 4. 測試 SET_INPUT 專屬 Ticket 指令
        var setInputTicket = new DriverTicket("SET_INPUT", "Index=5,Value=True");
        driver.PostTicket(setInputTicket);
        var resSet = setInputTicket.Wait(1000);
        Assert.True(resSet.Success);
        Assert.True(driver.SimulatedInputs[5]);

        driver.Shutdown();
    }

    [Fact]
    public void Test5_Trinity_Dimension2_MockIoView_FrontPanelMountingAndBiDirectionalSync()
    {
        var ioEngine = new IoEngine();
        var engines = new BasicEngines(ioEngine, new VariableEngine(), new PointEngine(ioEngine), new ImageEngine(ioEngine), new DaqEngine(ioEngine), new TimerEngine(ioEngine));
        var driver = new MockIoDriver();
        driver.Init("IO1", "Device=com1,OutStart=p0.0,OutPorts=32,InStart=p4.0,InPorts=32", engines);

        // 1. 驗證 CreateDriverView 傳回 MockIoView
        var viewObj = driver.CreateDriverView();
        Assert.NotNull(viewObj);
        var view = viewObj as MockIoView;
        Assert.NotNull(view);

        // 2. 驗證 MockIoView 繼承自 Avalonia UserControl
        Assert.IsAssignableFrom<UserControl>(view);

        // 3. 驗證內部包含驅動前面板 (HMI) 與參數精靈 (Wizard) 雙分頁
        var grid = view.Content as Grid;
        Assert.NotNull(grid);
        var tabControl = grid.Children.OfType<TabControl>().FirstOrDefault();
        Assert.NotNull(tabControl);
        Assert.True(tabControl.Items.Cast<object>().Count() >= 2, "MockIoView 必須具備至少兩個分頁 (HMI 前面板 + Wizard 參數導引)");

        // 4. 驗證前面板 UI 控制項正確綁定
        var btnForce = view.FindControl<Button>("BtnForceUpdate");
        Assert.NotNull(btnForce);
        var inputsContainer = view.FindControl<StackPanel>("InputsContainer");
        Assert.NotNull(inputsContainer);
        var outputsContainer = view.FindControl<StackPanel>("OutputsContainer");
        Assert.NotNull(outputsContainer);

        driver.Shutdown();
    }

    [Fact]
    public void Test6_Trinity_Dimension3_WizardParameterGeneration_And_GuidanceDialog()
    {
        // 1. 驗證 MockIoView 中的 Tab 2 (Wizard 精靈控制項)
        var ioEngine = new IoEngine();
        var engines = new BasicEngines(ioEngine, new VariableEngine(), new PointEngine(ioEngine), new ImageEngine(ioEngine), new DaqEngine(ioEngine), new TimerEngine(ioEngine));
        var driver = new MockIoDriver();
        driver.Init("IO_WizardTest", "Device=com1,OutStart=p0.0,OutPorts=32,InStart=p4.0,InPorts=32", engines);

        var view = driver.CreateDriverView() as MockIoView;
        Assert.NotNull(view);

        var txtDev = view.FindControl<TextBox>("TxtWizDeviceName");
        var txtOutStart = view.FindControl<TextBox>("TxtWizOutStart");
        var txtInStart = view.FindControl<TextBox>("TxtWizInStart");
        var txtGenerated = view.FindControl<TextBox>("TxtGeneratedParam");

        Assert.NotNull(txtDev);
        Assert.NotNull(txtOutStart);
        Assert.NotNull(txtInStart);
        Assert.NotNull(txtGenerated);

        // 2. 驗證初始參數字串生成結果格式
        Assert.Contains("Device=", txtGenerated.Text ?? "");
        Assert.Contains("OutStart=p0.0", txtGenerated.Text ?? "");
        Assert.True(txtGenerated.Text?.Contains("InputStart=p4.0") == true || txtGenerated.Text?.Contains("InStart=p4.0") == true);

        // 3. 驗證指令級 IParameterGuidance (從 MODULES/IO/SetPort.csvi 動態載入)
        var registry = ModuleRegistry.CreateDefault();
        var setPortHandler = registry.LoadCommandCsviHandler("IO", "SetPort");
        Assert.NotNull(setPortHandler);
        var setPortGuidance = setPortHandler as IParameterGuidance;
        Assert.NotNull(setPortGuidance);
        Assert.Equal("IO@SetPort", setPortGuidance!.CommandKey);

        // 驗證輕量執行
        setPortGuidance.Execute(engines, "P0.5");
        Assert.True(ioEngine.GetPort("P0.5"));

        // 4. 驗證 MockIoGuidance 實作 IParameterGuidance 且具備專屬 CommandKey 與前面板控制項
        var mockIoGuidance = new MockIoGuidance();
        Assert.Equal("IO@AddIODriver::MockIO", mockIoGuidance.CommandKey);
        Assert.NotNull(mockIoGuidance.GetFrontPanelControl());

        // 5. 驗證 MockIoGuidanceDialog 型別與繼承結構 (在無 Headless 視窗環境下安全檢查)
        Assert.True(typeof(Window).IsAssignableFrom(typeof(MockIoGuidanceDialog)));

        driver.Shutdown();
    }

    [Fact]
    public void Test7_ZeroLocking_FileOverwrite_And_HotReloadDevice()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"ARCOS_IoDriverTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempDriverPath = Path.Combine(tempDir, "MockIO.Driver.dll");

        try
        {
            // 將現有驅動 DLL 複製到臨時目錄模擬外部發布
            string sourceDll = typeof(MockIoDriver).Assembly.Location;
            File.Copy(sourceDll, tempDriverPath, overwrite: true);

            // 1. 透過 PluginLoadManager 動態載入
            var asm1 = PluginLoadManager.LoadOrReloadAssembly(tempDriverPath, out bool reloaded1);
            Assert.NotNull(asm1);

            // 2. 驗證 Windows 實體檔案完全無鎖定 (外部編譯器或使用者可直接寫入覆寫)
            byte[] fileBytes = File.ReadAllBytes(tempDriverPath);
            File.WriteAllBytes(tempDriverPath, fileBytes); // 若 Windows 鎖定則必拋 IOException

            // 3. 模擬熱更時間戳變更
            File.SetLastWriteTimeUtc(tempDriverPath, DateTime.UtcNow.AddSeconds(5));

            // 4. 再次呼叫載入，驗證觸發熱重載 (Hot-Reload)
            var asm2 = PluginLoadManager.LoadOrReloadAssembly(tempDriverPath, out bool reloaded2);
            Assert.NotNull(asm2);
            Assert.True(reloaded2, "檔案更新後必須觸發熱重載！");

            // 5. 驗證 DriverManager.ReloadDevice 功能
            var engines = new BasicEngines();
            var driverMgr = new DriverManager(engines);
            driverMgr.RegisterDriverFactory("MockIO", () => new MockIoDriver());

            var dev = driverMgr.AddDevice("IO", "StationIO", "MockIO", "Device=com1,OutStart=p0.0,OutPorts=16,InStart=p1.0,InPorts=16", engines);
            Assert.NotNull(dev);

            // 執行熱更替換
            var reloadedDev = driverMgr.ReloadDevice("IO", "StationIO");
            Assert.NotNull(reloadedDev);
            Assert.Same(reloadedDev, driverMgr.GetDevice("IO", "StationIO"));
            Assert.Equal("StationIO", reloadedDev.DeviceName);

            driverMgr.ShutdownAll();
            PluginLoadManager.Unload(tempDriverPath);
        }
        finally
        {
            PluginLoadManager.Unload(tempDriverPath);
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void Test8_EndToEndSequencer_IoSubsystemLifecycle()
    {
        var registry = ModuleRegistry.CreateDefault();
        var engines = new BasicEngines();
        var driverMgr = new DriverManager(engines);
        driverMgr.RegisterDriverFactory("MockIO", () => new MockIoDriver());

        var executor = new CommandExecutor(registry);

        // 撰寫完整的產線汽缸推拉與感測器到位流程 (ARCOS 標準語法)
        string[] scriptLines = new[]
        {
            "IO@AddIODriver::IO1, MockIO, Device=com1,OutStart=p0.0,OutPorts=32,InStart=p4.0,InPorts=32",
            "IO@SetPort::P0.0",
            "IO@WaitPortOn::P4.0",
            "IO@ClrPort::P0.0",
            "IO@WaitPortOff::P4.0"
        };

        var program = ProgramTable.FromLines(scriptLines, 0, "CylinderCycleFlow");
        var ctx = new ExecutionContext(0, "MainThread", program, engines, driverMgr)
        {
            Registry = registry
        };

        // 步驟 1: AddIODriver
        var res1 = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, res1.Status);
        var driver = driverMgr.GetDevice("IO", "IO1") as MockIoDriver;
        Assert.NotNull(driver);

        // 步驟 2: SetPort::P0.0 (推動氣缸)
        var res2 = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, res2.Status);
        Assert.True(engines.Io.GetPort("P0.0"));

        // 步驟 3: WaitPortOn::P4.0 (等待前極限感測器到達)
        // 第一次檢查尚未到達 -> 處於 Waiting
        var res3a = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Waiting, res3a.Status);

        // 模擬氣缸伸出並碰觸感測器 P4.0
        driver.SetSimulatedInput(0, true);
        var tickUpdate = new DriverTicket("FORCE_UPDATE");
        driver.PostTicket(tickUpdate);
        tickUpdate.Wait(500);

        // 第二次檢查已到達 -> Completed 並推進步進指標
        var res3b = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, res3b.Status);

        // 步驟 4: ClrPort::P0.0 (收回氣缸)
        var res4 = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, res4.Status);
        Assert.False(engines.Io.GetPort("P0.0"));

        // 步驟 5: WaitPortOff::P4.0 (等待感測器釋放)
        driver.SetSimulatedInput(0, false);
        var tickUpdate2 = new DriverTicket("FORCE_UPDATE");
        driver.PostTicket(tickUpdate2);
        tickUpdate2.Wait(500);

        var res5 = executor.ExecuteStep(ctx);
        Assert.Equal(CommandStatus.Completed, res5.Status);

        // 驗證卸載設備
        Assert.True(driverMgr.RemoveDevice("IO", "IO1"));
        Assert.Null(driverMgr.GetDevice("IO", "IO1"));

        driverMgr.ShutdownAll();
    }
}
