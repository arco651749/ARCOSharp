using System.IO;
using System.Reflection;
using ARCOS.Core.Common;
using ARCOS.Core.Csvi;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using Xunit;

namespace ARCOS.Core.Tests;

public class PluginHotReloadTests
{
    [Fact]
    public void CollectibleContext_LoadsAndUnloads_Correctly()
    {
        // 尋找現有的測試組件 (如 ARCOS.Core.dll)
        string testAssemblyPath = typeof(PluginLoadManager).Assembly.Location;
        Assert.True(File.Exists(testAssemblyPath));

        byte[] rawBytes = File.ReadAllBytes(testAssemblyPath);
        var context = new CollectiblePluginContext("TestCollectibleContext");
        
        Assert.True(context.IsCollectible);

        var loadedAsm = context.LoadAssemblyBytes(rawBytes);
        Assert.NotNull(loadedAsm);
        Assert.Equal(typeof(PluginLoadManager).Assembly.GetName().Name, loadedAsm.GetName().Name);

        // 觸發 Unload
        context.Unload();
    }

    [Fact]
    public void PluginLoadManager_FileIsNotLocked_AndCanBeOverwrittenOnWindows()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"ARCOS_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempDllPath = Path.Combine(tempDir, "TempPlugin.dll");

        try
        {
            // 將現有 DLL 複製到暫存路徑
            string sourceDll = typeof(PluginLoadManager).Assembly.Location;
            File.Copy(sourceDll, tempDllPath, overwrite: true);

            // 1. 使用 PluginLoadManager 載入
            var asm1 = PluginLoadManager.LoadOrReloadAssembly(tempDllPath, out bool reloaded1);
            Assert.NotNull(asm1);

            // 2. 驗證 Windows 磁碟檔案沒有被鎖定，外部編譯器可以自由覆寫或修改！
            bool canWrite = false;
            try
            {
                using (var fs = new FileStream(tempDllPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    canWrite = true;
                }
            }
            catch (IOException)
            {
                canWrite = false;
            }
            Assert.True(canWrite, "Plugin file on Windows must NOT be locked by AssemblyLoadContext!");

            // 3. 再次調用 LoadOrReloadAssembly，若檔案時間未變，應回傳快取組件 (reloaded = false)
            var asm2 = PluginLoadManager.LoadOrReloadAssembly(tempDllPath, out bool reloaded2);
            Assert.Same(asm1, asm2);
            Assert.False(reloaded2);

            // 4. 模擬外部重新編譯 (更新檔案寫入時間)
            File.SetLastWriteTimeUtc(tempDllPath, DateTime.UtcNow.AddSeconds(5));

            // 5. 再次調用，應自動卸載舊上下文並重新載入 (reloaded = true)
            var asm3 = PluginLoadManager.LoadOrReloadAssembly(tempDllPath, out bool reloaded3);
            Assert.NotNull(asm3);
            Assert.True(reloaded3, "When file timestamp changes, PluginLoadManager must hot-reload!");

            // 6. 手動卸載
            bool unloaded = PluginLoadManager.Unload(tempDllPath);
            Assert.True(unloaded);
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

    [Fact]
    public void CsviPackage_UsesCollectibleContext_AndSupportsUnload()
    {
        // 建立微型 CSVI package 位元組 (使用 Core 組件位元組作為容器)
        string testAssemblyPath = typeof(PluginLoadManager).Assembly.Location;
        byte[] bytes = File.ReadAllBytes(testAssemblyPath);

        var package = CsviPackage.LoadFromBytes(bytes, unpackSources: false);
        Assert.NotNull(package.LoadedAssembly);
        Assert.NotNull(package.Context);
        Assert.True(package.Context.IsCollectible);

        // 卸載
        package.Unload();
        Assert.Null(package.LoadedAssembly);
        Assert.Null(package.Context);
    }

    [Fact]
    public void DriverManager_ReloadDevice_ReplacesAndInitializesCorrectly()
    {
        var engines = new BasicEngines();
        var driverMgr = new DriverManager(engines);

        int initCount = 0;
        int shutdownCount = 0;

        // 註冊 Mock Driver Factory
        driverMgr.RegisterDriverFactory("TestHotDriver", () => new TestMockDriver(
            onInit: () => initCount++,
            onShutdown: () => shutdownCount++
        ));

        // 初次加入設備
        var dev1 = driverMgr.AddDevice("IO", "StationIO", "TestHotDriver", "Param1", engines);
        Assert.NotNull(dev1);
        Assert.Equal(1, initCount);
        Assert.Equal(0, shutdownCount);

        // 熱重載設備
        var dev2 = driverMgr.ReloadDevice("IO", "StationIO", "TestHotDriver", "Param2", engines);
        Assert.NotNull(dev2);
        Assert.NotSame(dev1, dev2);
        Assert.Equal(2, initCount);
        Assert.Equal(1, shutdownCount); // 舊設備已被 Shutdown
    }

    private class TestMockDriver : IDriver
    {
        private readonly Action _onInit;
        private readonly Action _onShutdown;

        public string DriverType => "TestHotDriver";
        public string Category => "IO";
        public string DeviceName { get; private set; } = "";
        public bool IsConnected { get; private set; }

        public TestMockDriver(Action onInit, Action onShutdown)
        {
            _onInit = onInit;
            _onShutdown = onShutdown;
        }

        public void Init(string deviceName, string param, BasicEngines engines)
        {
            DeviceName = deviceName;
            IsConnected = true;
            _onInit();
        }

        public void PostTicket(DriverTicket ticket)
        {
            ticket.SetCompleted();
        }

        public void Shutdown()
        {
            IsConnected = false;
            _onShutdown();
        }

        public object? CreateDriverView() => null;
        public void Dispose() { }
    }
}
