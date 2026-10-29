using System.Collections.Concurrent;
using System.Reflection;

namespace ARCOS.Core.Drivers;

/// <summary>
/// 驅動外掛動態載入器 (DriverLoader)
/// 依據類別 (IO/Motion/Capture/DAQ) 掃描 Drivers/<Category>/ 目錄，
/// 動態加載目標 Driver DLL 並透過反射實例化 IDriver
/// </summary>
public class DriverLoader
{
    public string DriversRootPath { get; set; }

    public DriverLoader(string? driversRootPath = null)
    {
        DriversRootPath = driversRootPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers");
    }

    /// <summary>
    /// 動態加載指定的 Driver DLL 並產生實例
    /// </summary>
    public IDriver? LoadDriver(string category, string driverType)
    {
        // 搜尋可能的路徑 (依優先權)：
        // 1. 標準發行拓撲：MODULES/<Category>/DRIVER/<DriverType>/<DriverType>.csvi 與 .Driver.dll
        // 2. 開發期除錯：專案編譯輸出 bin/Debug/net8.0/
        // 3. 向下相容備選
        var candidatePaths = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MODULES", category, "DRIVER", driverType, $"{driverType}.csvi"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MODULES", category, "DRIVER", $"{driverType}.csvi"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MODULES", category, "DRIVER", driverType, $"{driverType}.Driver.dll"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MODULES", category, "DRIVER", $"{driverType}.Driver.dll"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MODULES", category, "DRIVER", driverType, $"{driverType}.dll"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MODULES", category, "DRIVER", $"{driverType}.dll"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "MODULES", category, "DRIVER", driverType, "bin", "Debug", "net8.0", $"{driverType}.Driver.dll"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "src", "MODULES", category, "DRIVER", driverType, "bin", "Debug", "net8.0", $"{driverType}.Driver.dll"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "MODULES", category, "DRIVER", driverType, "bin", "Debug", "net8.0", $"{driverType}.dll"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "src", "MODULES", category, "DRIVER", driverType, "bin", "Debug", "net8.0", $"{driverType}.dll"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "dist", "ARCOS_Runtime", "MODULES", category, "DRIVER", driverType, $"{driverType}.csvi"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "dist", "ARCOS_Runtime", "MODULES", category, "DRIVER", $"{driverType}.csvi"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "dist", "ARCOS_Runtime", "MODULES", category, "DRIVER", driverType, $"{driverType}.Driver.dll"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "dist", "ARCOS_Runtime", "MODULES", category, "DRIVER", driverType, $"{driverType}.Driver.dll"),
            Path.Combine(DriversRootPath, category, driverType, $"{driverType}.csvi"),
            Path.Combine(DriversRootPath, category, $"{driverType}.csvi"),
            Path.Combine(DriversRootPath, category, driverType, $"{driverType}.Driver.dll"),
            Path.Combine(DriversRootPath, category, driverType, $"{driverType}.dll"),
            Path.Combine(DriversRootPath, category, $"{driverType}.dll"),
            Path.Combine(DriversRootPath, $"{driverType}.dll"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{driverType}.csvi"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{driverType}.Driver.dll"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{driverType}.dll")
        };

        string? foundPath = candidatePaths.FirstOrDefault(File.Exists);
        if (foundPath == null)
        {
            return null;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(foundPath);
            var assembly = Assembly.Load(bytes);
            var driverTypeInfo = assembly.GetExportedTypes()
                .FirstOrDefault(t => typeof(IDriver).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

            if (driverTypeInfo != null)
            {
                return Activator.CreateInstance(driverTypeInfo) as IDriver;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load driver '{driverType}' from '{foundPath}': {ex.Message}", ex);
        }

        return null;
    }

    /// <summary>
    /// 列舉指定類別下所有已安裝之硬體驅動型號清單 (掃描 MODULES/<Category>/DRIVER/ 下所有 *.csvi 與子目錄)
    /// </summary>
    public IReadOnlyList<string> GetAvailableDrivers(string category)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var searchRoots = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MODULES", category, "DRIVER"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "dist", "ARCOS_Runtime", "MODULES", category, "DRIVER"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "dist", "ARCOS_Runtime", "MODULES", category, "DRIVER"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "MODULES", category, "DRIVER"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "src", "MODULES", category, "DRIVER"),
            DriversRootPath
        };

        foreach (var driverRoot in searchRoots)
        {
            if (!Directory.Exists(driverRoot)) continue;

            // 1. 直屬 *.csvi (例如 DRIVER/MockIO.csvi)
            foreach (var file in Directory.GetFiles(driverRoot, "*.csvi"))
            {
                result.Add(Path.GetFileNameWithoutExtension(file));
            }

            // 2. 子目錄 (例如 DRIVER/MockIO/MockIO.csvi 或 MockIO.Driver.dll)
            foreach (var dir in Directory.GetDirectories(driverRoot))
            {
                var driverType = Path.GetFileName(dir);
                if (driverType.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    driverType.Equals("obj", StringComparison.OrdinalIgnoreCase))
                    continue;

                string csviEntry = Path.Combine(dir, $"{driverType}.csvi");
                string entry = Path.Combine(dir, $"{driverType}.Driver.dll");
                string entryFallback = Path.Combine(dir, $"{driverType}.dll");
                string devEntry = Path.Combine(dir, "bin", "Debug", "net8.0", $"{driverType}.Driver.dll");

                if (File.Exists(csviEntry) || File.Exists(entry) || File.Exists(entryFallback) || File.Exists(devEntry) ||
                    Directory.GetFiles(dir, "*.csvi").Length > 0)
                {
                    result.Add(driverType);
                }
            }
        }
        return result.OrderBy(x => x).ToList();
    }
}

/// <summary>
/// 設備驅動總管 (DriverManager)
/// 支援 (Category, DeviceName) 複合鍵管理，確保不同類別同名設備互不衝突；
/// 支援指令動態 AddDevice、生命週期管理與工廠/動態 DLL 載入
/// </summary>
public class DriverManager : IDisposable
{
    private readonly ConcurrentDictionary<(string Category, string DeviceName), IDriver> _devices =
        new(new CategoryDeviceEqualityComparer());

    private readonly ConcurrentDictionary<string, Func<IDriver>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    public DriverLoader Loader { get; }

    public DriverManager(string? driversRootPath = null)
    {
        Loader = new DriverLoader(driversRootPath);
    }

    public DriverManager(BasicEngines engines, string? driversRootPath = null)
        : this(driversRootPath)
    {
    }

    /// <summary>
    /// 列舉指定類別下所有已安裝之硬體驅動型號清單
    /// </summary>
    public IReadOnlyList<string> GetAvailableDrivers(string category) => Loader.GetAvailableDrivers(category);

    /// <summary>
    /// 手動註冊驅動實例 (供內建 Mock 或測試使用)
    /// </summary>
    public void RegisterDriver(string category, string deviceName, IDriver driver)
    {
        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentNullException(nameof(category));
        if (string.IsNullOrWhiteSpace(deviceName)) throw new ArgumentNullException(nameof(deviceName));
        if (driver == null) throw new ArgumentNullException(nameof(driver));

        var key = (category.Trim(), deviceName.Trim());
        _devices[key] = driver;
    }

    /// <summary>
    /// 手動註冊驅動工廠 (供內建 Mock 或測試使用)
    /// </summary>
    public void RegisterDriverFactory(string driverType, Func<IDriver> factory)
    {
        _factories[driverType] = factory;
    }

    /// <summary>
    /// 動態加入設備 (對應 IO@AddIODriver, Motion@AddMotor, Capture@AddCam, DAQ@AddDAQ)
    /// </summary>
    public IDriver AddDevice(string category, string deviceName, string driverType, string param, BasicEngines engines)
    {
        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentNullException(nameof(category));
        if (string.IsNullOrWhiteSpace(deviceName)) throw new ArgumentNullException(nameof(deviceName));
        if (string.IsNullOrWhiteSpace(driverType)) throw new ArgumentNullException(nameof(driverType));

        var key = (category.Trim(), deviceName.Trim());

        // 若已存在則先釋放舊設備
        if (_devices.TryRemove(key, out var oldDriver))
        {
            oldDriver.Shutdown();
            oldDriver.Dispose();
        }

        IDriver? driver = null;

        // 1. 先查手動登記的工廠 (Mock / 預編譯驅動)
        if (_factories.TryGetValue(driverType, out var factory))
        {
            driver = factory();
        }
        else
        {
            // 2. 前往 Drivers/<Category>/ 目錄動態加載 DLL
            driver = Loader.LoadDriver(category, driverType);
        }

        if (driver == null)
        {
            throw new DllNotFoundException($"Driver '{driverType}' in category '{category}' was not found in registered factories or Drivers folder.");
        }

        driver.Init(key.Item2, param, engines);
        _devices[key] = driver;
        return driver;
    }

    /// <summary>
    /// 依 (Category, DeviceName) 取得特定設備
    /// </summary>
    public IDriver? GetDevice(string category, string deviceName)
    {
        var key = (category.Trim(), deviceName.Trim());
        _devices.TryGetValue(key, out var driver);
        return driver;
    }

    /// <summary>
    /// 依 (Category, DeviceName) 取得特定設備 (別名方法供模組處理器調用)
    /// </summary>
    public IDriver? GetDriver(string category, string deviceName) => GetDevice(category, deviceName);

    /// <summary>
    /// 移除設備
    /// </summary>
    public bool RemoveDevice(string category, string deviceName)
    {
        var key = (category.Trim(), deviceName.Trim());
        if (_devices.TryRemove(key, out var driver))
        {
            driver.Shutdown();
            driver.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>
    /// 取得指定類別的所有設備
    /// </summary>
    public IReadOnlyList<IDriver> GetDevicesByCategory(string category)
    {
        return _devices
            .Where(kvp => string.Equals(kvp.Key.Category, category, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Value)
            .ToList();
    }

    public IReadOnlyList<IDriver> GetAllDevices()
    {
        return _devices.Values.ToList();
    }

    public void ShutdownAll()
    {
        foreach (var driver in _devices.Values)
        {
            try
            {
                driver.Shutdown();
                driver.Dispose();
            }
            catch { }
        }
        _devices.Clear();
    }

    public void Dispose()
    {
        ShutdownAll();
    }

    private class CategoryDeviceEqualityComparer : IEqualityComparer<(string Category, string DeviceName)>
    {
        public bool Equals((string Category, string DeviceName) x, (string Category, string DeviceName) y)
        {
            return string.Equals(x.Category, y.Category, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.DeviceName, y.DeviceName, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string Category, string DeviceName) obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Category),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.DeviceName));
        }
    }
}
