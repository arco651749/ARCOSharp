using System.IO;
using System.Reflection;
using ARCOS.Core.Common;
using ARCOS.Core.Csvi;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using ARCOS.Core.Execution;
using ARCOS.Core.Model;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;
using Xunit;

namespace ARCOS.Core.Tests;

/// <summary>
/// 嚴格驗證：
/// 1. 啟動時僅載入 Basic Modules (System, Thread, Variable, User, Call, MultiStation, Redis, AiAgent, PointPath)
/// 2. 非 Basic Module (IO, Motion, Capture, DAQ, Python...) 僅於下達指令時才隨選動態載入
/// 3. Driver 僅於下達指令 (如 IO@AddIODriver) 時才動態載入
/// 4. UserPanel 僅於下達指令 (如 System@UserPanelOpen) 時才動態載入
/// 5. Windows 實體檔案零鎖死、支援動態覆寫與熱更新 (Hot-Reload)
/// </summary>
public class DynamicLoadingAndHotReloadVerificationTests
{
    [Fact]
    public void Startup_OnlyLoadsBasicModules_NeverPreloadsExternalModules()
    {
        var registry = ModuleRegistry.CreateDefault();

        // 1. 驗證 Basic Modules 已一開始載入
        Assert.True(registry.HasModule("System"), "Basic module 'System' must be loaded at startup.");
        Assert.True(registry.HasModule("Thread"), "Basic module 'Thread' must be loaded at startup.");
        Assert.True(registry.HasModule("Variable"), "Basic module 'Variable' must be loaded at startup.");
        Assert.True(registry.HasModule("User"), "Basic module 'User' must be loaded at startup.");
        Assert.True(registry.HasModule("Call"), "Basic module 'Call' must be loaded at startup.");
        Assert.True(registry.HasModule("MultiStation"), "Basic module 'MultiStation' must be loaded at startup.");
        Assert.True(registry.HasModule("Redis"), "Basic module 'Redis' must be loaded at startup.");
        Assert.True(registry.HasModule("AiAgent"), "Basic module 'AiAgent' must be loaded at startup.");
        Assert.True(registry.HasModule("Point&Path"), "Basic module 'Point&Path' must be loaded at startup.");

        // 2. 驗證擴充模組 (IO, Motion, Capture, DAQ, Python 等) 一開始絕對沒有載入！
        Assert.False(registry.HasModule("IO"), "Module 'IO' must NOT be loaded at startup.");
        Assert.False(registry.HasModule("Motion"), "Module 'Motion' must NOT be loaded at startup.");
        Assert.False(registry.HasModule("Capture"), "Module 'Capture' must NOT be loaded at startup.");
        Assert.False(registry.HasModule("DAQ"), "Module 'DAQ' must NOT be loaded at startup.");
        Assert.False(registry.HasModule("Python"), "Module 'Python' must NOT be loaded at startup.");
    }

    [Fact]
    public void Startup_DriverManager_HasZeroDriversLoaded()
    {
        var engines = new BasicEngines();
        var driverMgr = new DriverManager(engines);

        // 驗證啟動時完全沒有任何硬體驅動常駐
        Assert.Empty(driverMgr.GetAllDevices());
    }

    [Fact]
    public void Module_LoadedOnDemand_WhenCommandIsIssued()
    {
        var registry = ModuleRegistry.CreateDefault();
        var executor = new CommandExecutor(registry);
        var engines = new BasicEngines();
        var driverMgr = new DriverManager(engines);

        // 1. 確認一開始 IO 模組尚未載入
        Assert.False(registry.HasModule("IO"));

        // 2. 建立腳本並下達 IO 指令 (標準 ARCOS 語法: <Module>@<Action>::<Param>)
        var program = ProgramTable.FromText("IO@SetPort::1, 1", 0, "TestProgram");
        var ctx = new ExecutionContext(0, "TestThread", program, engines, driverMgr)
        {
            Registry = registry
        };

        // 3. 執行指令
        var res = executor.ExecuteStep(ctx);

        // 4. 驗證下指令後，IO 模組已被隨選動態加載進入 Registry！
        Assert.True(registry.HasModule("IO"), "Module 'IO' should be dynamically loaded on-demand when IO command is issued.");
        Assert.NotNull(registry.GetHandler("IO"));
    }

    [Fact]
    public void Module_LoadedViaIncludeModule_WhenIncludeModuleCommandIsIssued()
    {
        var registry = ModuleRegistry.CreateDefault();
        var executor = new CommandExecutor(registry);
        var engines = new BasicEngines();
        var driverMgr = new DriverManager(engines);

        // 1. 確認一開始 Motion 模組尚未載入
        Assert.False(registry.HasModule("Motion"));

        // 2. 下達 System@IncludeModule::Motion 指令
        var program = ProgramTable.FromText("System@IncludeModule::Motion", 0, "TestProgram");
        var ctx = new ExecutionContext(0, "TestThread", program, engines, driverMgr)
        {
            Registry = registry
        };

        var res = executor.ExecuteStep(ctx);

        // 3. 驗證 Motion 模組已被動態掛載
        Assert.True(registry.HasModule("Motion"), "Module 'Motion' should be loaded after System@IncludeModule Motion.");
    }

    [Fact]
    public void Driver_LoadedOnDemand_WhenAddDriverCommandIsIssued()
    {
        var engines = new BasicEngines();
        var driverMgr = new DriverManager(engines);

        // 1. 啟動時設備列表為空
        Assert.Null(driverMgr.GetDevice("IO", "StationIO"));

        // 2. 下達指令加入驅動 (模擬 MockIO 驅動或註冊工廠)
        driverMgr.RegisterDriverFactory("MockIO", () => new MockIoTestDriver());

        var dev = driverMgr.AddDevice("IO", "StationIO", "MockIO", "StartPort=P0.0,BitCount=16", engines);

        // 3. 驗證下指令後設備才被動態建立
        Assert.NotNull(dev);
        Assert.NotNull(driverMgr.GetDevice("IO", "StationIO"));
        Assert.Equal("StationIO", dev.DeviceName);
    }

    [Fact]
    public void UserPanel_LoadedOnDemand_NeverPreloaded_AndSupportsNonLockingHotReload()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"ARCOS_UserPanel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempCsviPath = Path.Combine(tempDir, "UserPanel.csvi");

        try
        {
            // 使用 Core 實體作為微型虛擬 CSVI 容器
            string sourceDll = typeof(PluginLoadManager).Assembly.Location;
            File.Copy(sourceDll, tempCsviPath, overwrite: true);

            // 1. 下指令載入 UserPanel (如 System@UserPanelOpen)
            var pkg1 = CsviPackage.LoadFromFile(tempCsviPath, unpackSources: false);
            Assert.NotNull(pkg1.LoadedAssembly);
            Assert.NotNull(pkg1.Context);
            Assert.True(pkg1.Context.IsCollectible);

            // 2. 驗證 Windows 磁碟上的 CSVI 檔案沒有被鎖定，開發者可在機台運行時自由覆寫更新！
            byte[] bytes = File.ReadAllBytes(tempCsviPath);
            File.WriteAllBytes(tempCsviPath, bytes); // 若鎖檔必報錯

            // 3. 卸載前一個 UserPanel
            pkg1.Unload();
            Assert.Null(pkg1.LoadedAssembly);
            Assert.Null(pkg1.Context);

            // 4. 熱更新重載新版本
            File.SetLastWriteTimeUtc(tempCsviPath, DateTime.UtcNow.AddSeconds(5));
            var pkg2 = CsviPackage.LoadFromFile(tempCsviPath, unpackSources: false);
            Assert.NotNull(pkg2.LoadedAssembly);
            Assert.NotNull(pkg2.Context);

            pkg2.Unload();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void HotReload_OverwritesDllOnWindows_AndReloadsWithoutProcessRestart()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"ARCOS_HotReload_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempDllPath = Path.Combine(tempDir, "DynamicPlugin.dll");

        try
        {
            // 將現有 DLL 複製到暫存外掛目錄
            string sourceDll = typeof(PluginLoadManager).Assembly.Location;
            File.Copy(sourceDll, tempDllPath, overwrite: true);

            // 1. 初次載入 (Hot Load)
            var asm1 = PluginLoadManager.LoadOrReloadAssembly(tempDllPath, out bool reloaded1);
            Assert.NotNull(asm1);

            // 2. 驗證 Windows 磁碟完全無鎖定 (外部編譯器可立即覆寫檔案內容)
            byte[] dummyContent = File.ReadAllBytes(tempDllPath);
            File.WriteAllBytes(tempDllPath, dummyContent); // 若有檔案鎖，在 Windows 上此處必拋 IOException

            // 3. 模擬二次開發者重新編譯 (時間戳推進)
            File.SetLastWriteTimeUtc(tempDllPath, DateTime.UtcNow.AddSeconds(10));

            // 4. 熱更探測：PluginLoadManager 檢測到新時間戳，自動 Unload 舊上下文並載入新組件！
            var asm2 = PluginLoadManager.LoadOrReloadAssembly(tempDllPath, out bool reloaded2);
            Assert.NotNull(asm2);
            Assert.True(reloaded2, "Hot reload must be triggered when plugin file is modified!");

            // 5. 卸載清理
            PluginLoadManager.Unload(tempDllPath);
            Assert.False(PluginLoadManager.IsLoaded(tempDllPath));
        }
        finally
        {
            PluginLoadManager.Unload(tempDllPath);
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private class MockIoTestDriver : IDriver
    {
        public string DeviceName { get; private set; } = "";
        public string Category => "IO";
        public string DriverType => "MockIO";

        public void Init(string deviceName, string param, BasicEngines engines)
        {
            DeviceName = deviceName;
        }

        public void Shutdown()
        {
        }

        public void PostTicket(DriverTicket ticket)
        {
            ticket.SetCompleted();
        }

        public object? CreateDriverView() => null;
        public void Dispose() { }
    }
}
