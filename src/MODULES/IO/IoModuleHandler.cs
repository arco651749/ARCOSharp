using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ARCOS.Core.Common;
using ARCOS.Core.Execution;
using ARCOS.Core.Guidance;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Module.IO;

/// <summary>
/// IO 模組智慧調度跳板 (IO Module Trampoline Dispatcher)
/// 角色職責：
/// 1. 指令動態探索 (Directory-Driven CSVI Discovery)：
///    依據使用者規範「有多少指令就是看 IO 目錄下有多少個 CSVI，DRIVER 目錄下的 CSVI 就是有什麼 DRIVER」，
///    自動掃描 MODULES/IO 與 USERPARAMETEREDITOR 之 *.csvi 檔案！
/// 2. 智慧調度閘道器：執行指令時優先探測 <Action>.csvi / <Action>.Guidance.dll
///    若存在且實作 Execute()，直接跳板轉發給使用者自訂的 CSVI 虛擬儀表！
/// 3. 快取與熱重載抑制 (Caching & Hot-Reload Watcher)：
///    透過 ConcurrentDictionary 抑制高頻迴圈下的磁碟探測與反射開銷，
///    並透過 FileSystemWatcher 與非鎖檔讀取 (File.ReadAllBytes) 支援免重啟現場覆蓋熱替換！
/// 4. 沙盒異常防禦 (Exception Sandbox)：
///    嚴密 try-catch 包覆外部客製化代碼，隔離所有 Crash 與 NullReferenceException，保護主排程器永不當機。
/// 5. 官方保底邏輯：找不到客製化組件時，無縫回退至 Core 內建標準邏輯。
/// </summary>
public sealed class IoModuleHandler : ARCOS.Core.Execution.Handlers.IoModuleHandler, IDisposable
{
    private readonly ConcurrentDictionary<string, IParameterGuidance?> _guidanceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _ioDir;
    private FileSystemWatcher? _ioWatcher;

    public IoModuleHandler()
    {
        string? asmDir = Path.GetDirectoryName(typeof(IoModuleHandler).Assembly.Location);
        _ioDir = !string.IsNullOrEmpty(asmDir) && Directory.Exists(asmDir)
            ? asmDir
            : Path.Combine(AppContext.BaseDirectory, "MODULES", "IO");

        SetupWatchers();
    }

    public IoModuleHandler(string customIoDir)
    {
        _ioDir = customIoDir;
        SetupWatchers();
    }

    private void SetupWatchers()
    {
        TryCreateWatcher(_ioDir, ref _ioWatcher);
    }

    private void TryCreateWatcher(string path, ref FileSystemWatcher? watcher)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            watcher.Changed += OnGuidanceChanged;
            watcher.Created += OnGuidanceChanged;
            watcher.Deleted += OnGuidanceChanged;
            watcher.Renamed += OnGuidanceRenamed;
        }
        catch
        {
            // 在權限受限或虛擬環境下靜默回退
        }
    }

    private void OnGuidanceChanged(object sender, FileSystemEventArgs e)
    {
        _guidanceCache.Clear();
    }

    private void OnGuidanceRenamed(object sender, RenamedEventArgs e)
    {
        _guidanceCache.Clear();
    }

    /// <summary>
    /// 動態探索目前 IO 目錄支援的所有指令清單 (依據 MODULES/IO 目錄下的 *.csvi)
    /// </summary>
    public override IReadOnlyList<string> GetSupportedCommands()
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var searchDirs = new[] { _ioDir, Path.Combine(AppContext.BaseDirectory, "MODULES", "IO") };

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;

            // 1. 搜尋所有 *.csvi
            foreach (var file in Directory.GetFiles(dir, "*.csvi"))
            {
                ExtractCommandsFromFileNameOrAssembly(file, discovered);
            }

            // 2. 舊式相容備援
            string legacyDir = Path.Combine(dir, "USERPARAMETEREDITOR");
            if (Directory.Exists(legacyDir))
            {
                foreach (var file in Directory.GetFiles(legacyDir, "*.csvi"))
                {
                    ExtractCommandsFromFileNameOrAssembly(file, discovered);
                }
            }
        }

        if (discovered.Count > 0)
        {
            return discovered.OrderBy(x => x).ToList();
        }

        // 若磁碟尚無 CSVI 檔案，安全回退至官方內建 14 指令
        return base.GetSupportedCommands();
    }

    private static void ExtractCommandsFromFileNameOrAssembly(string filePath, HashSet<string> targetSet)
    {
        string fileName = Path.GetFileName(filePath);
        string nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);

        // 若是多合一集合庫 (如 IO.csvi / IO.Guidance.dll)
        if (nameWithoutExt.Equals("IO", StringComparison.OrdinalIgnoreCase) ||
            nameWithoutExt.Equals("IO.Guidance", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                var asm = Assembly.Load(bytes);
                foreach (var t in asm.GetExportedTypes())
                {
                    if (typeof(IParameterGuidance).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                    {
                        if (Activator.CreateInstance(t) is IParameterGuidance g && !string.IsNullOrEmpty(g.CommandKey))
                        {
                            string cmd = g.CommandKey;
                            if (cmd.StartsWith("IO@", StringComparison.OrdinalIgnoreCase))
                            {
                                cmd = cmd.Substring(3);
                            }
                            targetSet.Add(cmd);
                        }
                    }
                }
            }
            catch { }
            return;
        }

        // 單指令檔案命名慣例：SetPort.csvi, io@SetPort.csvi, IO@SetPort.csvi, SetPort.Guidance.dll
        string candidate = nameWithoutExt;
        if (candidate.EndsWith(".Guidance", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate.Substring(0, candidate.Length - ".Guidance".Length);
        }
        if (candidate.StartsWith("io@", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate.Substring(3);
        }
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            targetSet.Add(candidate);
        }
    }

    /// <summary>
    /// 動態探索目前 IO DRIVER 目錄下所有已安裝之驅動清單 (依據 DRIVER/ 下的 *.csvi 與子目錄)
    /// </summary>
    public IReadOnlyList<string> GetSupportedDrivers()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var driverDirs = new[]
        {
            Path.Combine(_ioDir, "DRIVER"),
            Path.Combine(AppContext.BaseDirectory, "MODULES", "IO", "DRIVER")
        };

        foreach (var driverDir in driverDirs)
        {
            if (!Directory.Exists(driverDir)) continue;

            // 1. 直屬 *.csvi (如 DRIVER/MockIO.csvi)
            foreach (var file in Directory.GetFiles(driverDir, "*.csvi"))
            {
                string driverName = Path.GetFileNameWithoutExtension(file);
                result.Add(driverName);
            }

            // 2. 子目錄 (如 DRIVER/MockIO/MockIO.csvi 或 MockIO.Driver.dll)
            foreach (var subDir in Directory.GetDirectories(driverDir))
            {
                string dirName = Path.GetFileName(subDir);
                if (dirName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;

                if (File.Exists(Path.Combine(subDir, $"{dirName}.csvi")) ||
                    File.Exists(Path.Combine(subDir, $"{dirName}.Driver.dll")) ||
                    Directory.GetFiles(subDir, "*.csvi").Length > 0)
                {
                    result.Add(dirName);
                }
            }
        }

        return result.OrderBy(x => x).ToList();
    }

    public override CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // 1. 響應指令探索契約 (@getCMD)
        if (action.Equals("getCMD", StringComparison.OrdinalIgnoreCase))
        {
            return base.Execute(action, filteredParam, ctx);
        }

        // 2. ⭐ 智慧跳板轉發 (Smart Trampoline Dispatcher)
        var guidance = ResolveGuidance(action);
        if (guidance != null)
        {
            // 🛡️ 客製化邏輯沙盒防禦 (Exception Sandbox)
            try
            {
                return guidance.Execute(ctx, action, filteredParam);
            }
            catch (Exception ex)
            {
                ctx.Log($"[IO Trampoline Exception in {action}] {ex.Message}", isError: true);
                return CommandResult.Fail($"[IO Trampoline Sandbox Fault in {action}] {ex.Message}");
            }
        }

        // 3. 🛡️ 官方保底邏輯 (Default Fallback Implementation)
        return base.Execute(action, filteredParam, ctx);
    }

    /// <summary>
    /// 解析或從快取取得指定指令的 CSVI 虛擬儀表
    /// </summary>
    public IParameterGuidance? ResolveGuidance(string action)
    {
        if (_guidanceCache.TryGetValue(action, out var cached))
        {
            return cached;
        }

        IParameterGuidance? resolved = LoadGuidanceFromDisk(action);
        _guidanceCache[action] = resolved;
        return resolved;
    }

    /// <summary>
    /// 從磁碟載入 CSVI 組件 (採用非鎖檔 File.ReadAllBytes，支援熱覆蓋抽換)
    /// </summary>
    private IParameterGuidance? LoadGuidanceFromDisk(string action)
    {
        var candidatePaths = new List<string>
        {
            // 1. MODULES/IO/<action>.csvi (最高優先)
            Path.Combine(_ioDir, $"{action}.csvi"),
            Path.Combine(_ioDir, $"io@{action}.csvi"),
            Path.Combine(_ioDir, $"IO@{action}.csvi"),
            Path.Combine(AppContext.BaseDirectory, "MODULES", "IO", $"{action}.csvi"),
            Path.Combine(AppContext.BaseDirectory, "MODULES", "IO", $"io@{action}.csvi"),

            // 2. 模組複合包 IO.csvi
            Path.Combine(_ioDir, "IO.csvi"),
            Path.Combine(AppContext.BaseDirectory, "MODULES", "IO", "IO.csvi"),

            // 3. 歷史相容備援
            Path.Combine(_ioDir, "USERPARAMETEREDITOR", $"{action}.csvi"),
            Path.Combine(AppContext.BaseDirectory, "MODULES", "IO", "USERPARAMETEREDITOR", $"{action}.csvi")
        };

        foreach (var p in candidatePaths)
        {
            if (File.Exists(p))
            {
                var g = ExtractGuidanceFromAssembly(p, action);
                if (g != null) return g;
            }
        }

        return null;
    }

    private static IParameterGuidance? ExtractGuidanceFromAssembly(string dllPath, string action)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(dllPath);
            var asm = Assembly.Load(bytes);
            string expectedKey = $"IO@{action}";

            foreach (var type in asm.GetExportedTypes())
            {
                if (typeof(IParameterGuidance).IsAssignableFrom(type) && !type.IsAbstract)
                {
                    if (Activator.CreateInstance(type) is IParameterGuidance instance)
                    {
                        if (instance.CommandKey.Equals(expectedKey, StringComparison.OrdinalIgnoreCase) ||
                            instance.CommandKey.Equals(action, StringComparison.OrdinalIgnoreCase))
                        {
                            return instance;
                        }
                    }
                }
            }
        }
        catch
        {
            // 載入失敗走保底
        }
        return null;
    }

    public void Dispose()
    {
        if (_ioWatcher != null)
        {
            _ioWatcher.EnableRaisingEvents = false;
            _ioWatcher.Dispose();
            _ioWatcher = null;
        }
        _guidanceCache.Clear();
    }
}
