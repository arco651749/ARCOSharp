using System.Collections.Concurrent;
using System.Reflection;

namespace ARCOS.Core.Execution;

/// <summary>
/// 全域模組指令註冊表 (Module Registry)
/// 負責收集各模組小程式分派器、執行 MODULES/&lt;Module&gt;/&lt;Action&gt;.csvi 單一指令組件並支援動態查詢與 *@getCMD 探索
/// </summary>
public sealed class ModuleRegistry
{
    private readonly Dictionary<string, IModuleHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, IModuleHandler Handler)> _csviActionCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 當有新模組被註冊或動態加載時觸發 (提供模組名稱供 UI 即時更新)
    /// </summary>
    public event Action<string>? ModuleRegistered;

    /// <summary>
    /// 取得候選 MODULES 根目錄清單
    /// </summary>
    public static IReadOnlyList<string> GetCandidateModulesRoots(string? customRoot = null)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(customRoot) && Directory.Exists(customRoot))
        {
            roots.Add(customRoot);
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        roots.Add(Path.Combine(baseDir, "MODULES"));
        roots.Add(Path.Combine(Directory.GetCurrentDirectory(), "MODULES"));
        try
        {
            var cur = new DirectoryInfo(baseDir);
            for (int depth = 0; depth < 7 && cur != null; depth++, cur = cur.Parent)
            {
                string srcMod = Path.Combine(cur.FullName, "src", "MODULES");
                if (Directory.Exists(srcMod)) roots.Add(srcMod);

                string distMod = Path.Combine(cur.FullName, "dist", "ARCOS_Runtime", "MODULES");
                if (Directory.Exists(distMod)) roots.Add(distMod);

                string directMod = Path.Combine(cur.FullName, "MODULES");
                if (Directory.Exists(directMod)) roots.Add(directMod);
            }
        }
        catch { }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 尋找指定模組的所有候選目錄路徑 (MODULES/&lt;Module&gt;)
    /// </summary>
    public static IReadOnlyList<string> FindModuleDirectories(string moduleName, string? customRoot = null)
    {
        var dirs = new List<string>();
        if (string.IsNullOrWhiteSpace(moduleName)) return dirs;
        string mod = moduleName.Trim();
        foreach (var root in GetCandidateModulesRoots(customRoot))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (string.Equals(Path.GetFileName(dir), mod, StringComparison.OrdinalIgnoreCase))
                {
                    dirs.Add(dir);
                }
            }
        }
        return dirs;
    }

    /// <summary>
    /// 尋找指定模組目錄路徑 (MODULES/&lt;Module&gt;，優先回傳含有 .csvi 檔案的目錄)
    /// </summary>
    public static string? FindModuleDirectory(string moduleName, string? customRoot = null)
    {
        var dirs = FindModuleDirectories(moduleName, customRoot);
        return dirs.FirstOrDefault(d => Directory.GetFiles(d, "*.csvi").Length > 1) ?? dirs.FirstOrDefault();
    }

    /// <summary>
    /// 尋找單一指令對應的 MODULES/&lt;Module&gt;/&lt;Action&gt;.csvi 檔案路徑 (1:1 同名對應)
    /// </summary>
    public static string? ResolveCommandCsviPath(string moduleName, string actionName, string? customRoot = null)
    {
        var modDirs = FindModuleDirectories(moduleName, customRoot);
        if (modDirs.Count == 0) return null;

        string targetFileName = $"{actionName.Trim()}.csvi";

        foreach (var modDir in modDirs)
        {
            if (!Directory.Exists(modDir)) continue;
            foreach (var file in Directory.GetFiles(modDir, "*.csvi"))
            {
                string fileName = Path.GetFileName(file);
                // 排除與模組同名的模組前面板 <Module>.csvi 及 UserPanel.csvi
                if (string.Equals(Path.GetFileNameWithoutExtension(fileName), moduleName.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, "UserPanel.csvi", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(fileName, targetFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 掃描 MODULES/&lt;Module&gt;/*.csvi 取得該模組目前實際存在的指令清單 (排除同名 &lt;Module&gt;.csvi 前面板與 UserPanel.csvi)
    /// </summary>
    public static IReadOnlyList<string>? GetCsviCommandsForModule(string moduleName, string? customRoot = null)
    {
        string? modDir = FindModuleDirectory(moduleName, customRoot);
        if (modDir == null || !Directory.Exists(modDir)) return null;

        var cmds = new List<string>();
        foreach (var file in Directory.GetFiles(modDir, "*.csvi").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string nameNoExt = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(nameNoExt, moduleName.Trim(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(nameNoExt, "UserPanel", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cmds.Add(nameNoExt);
        }

        return cmds;
    }

    /// <summary>
    /// 依模組與指令名稱尋找並載入 MODULES/&lt;Module&gt;/&lt;Action&gt;.csvi 的 IModuleHandler 實例
    /// </summary>
    public IModuleHandler? LoadCommandCsviHandler(string moduleName, string actionName)
    {
        var path = ResolveCommandCsviPath(moduleName, actionName);
        return path != null ? LoadCommandCsviHandler(path) : null;
    }

    /// <summary>
    /// 從指定 &lt;Action&gt;.csvi 檔案載入 (或熱重載) IModuleHandler 實例
    /// </summary>
    public IModuleHandler? LoadCommandCsviHandler(string csviPath)
    {
        string fullPath = Path.GetFullPath(csviPath);
        if (!File.Exists(fullPath)) return null;

        DateTime lastWrite = File.GetLastWriteTimeUtc(fullPath);
        if (_csviActionCache.TryGetValue(fullPath, out var cached) && cached.LastWriteUtc == lastWrite)
        {
            return cached.Handler;
        }

        var asm = ARCOS.Core.Common.PluginLoadManager.LoadOrReloadAssembly(fullPath, out _);
        if (asm == null) return null;

        foreach (var t in asm.GetTypes())
        {
            if (typeof(IModuleHandler).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            {
                if (Activator.CreateInstance(t) is IModuleHandler handler)
                {
                    _csviActionCache[fullPath] = (lastWrite, handler);
                    return handler;
                }
            }
        }

        return null;
    }

    public void RegisterHandler(IModuleHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[handler.ModuleName] = handler;
        ModuleRegistered?.Invoke(handler.ModuleName);
    }

    /// <summary>
    /// 取得目前所有已載入之模組名稱清單
    /// </summary>
    public IReadOnlyList<string> GetLoadedModules() => _handlers.Keys.ToList();


    /// <summary>
    /// 動態掃描 MODULES 與 EXTRACMD 目錄，加載所有外部指令模組 (如 MODULES/IO/IO.dll, EXTRACMD/LaserMarking/LaserMarking.dll 等)
    /// 遵循雙模架構 (Dual-Mode)：ARCOS 主程式載入 DLL 同進程執行，工程師平時可直接雙擊同名 EXE 單獨測試。
    /// </summary>
    public void LoadExternalModules(string? modulesRootPath = null)
    {
        var candidateRoots = new List<string>();
        if (!string.IsNullOrWhiteSpace(modulesRootPath) && Directory.Exists(modulesRootPath))
        {
            candidateRoots.Add(modulesRootPath);
        }

        candidateRoots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MODULES"));
        candidateRoots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EXTRACMD"));
        candidateRoots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "dist", "ARCOS_Runtime", "MODULES"));
        candidateRoots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "dist", "ARCOS_Runtime", "EXTRACMD"));
        candidateRoots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "MODULES"));
        candidateRoots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "EXTRACMD"));

        foreach (var root in candidateRoots)
        {
            if (!Directory.Exists(root)) continue;

            // 1. 掃描子目錄結構 (MODULES/<Category>/... 或 EXTRACMD/<CustomCmd>/...)
            foreach (var moduleDir in Directory.GetDirectories(root))
            {
                var dirName = Path.GetFileName(moduleDir);
                if (dirName.Equals("DRIVER", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals("PYTHON", StringComparison.OrdinalIgnoreCase)) // 外部隨選擴充模組，開機不預載
                    continue;

                // 方案 A 嚴格檔名契約 (Convention over Configuration)：
                // 1. 標準發行拓撲：<Category>/<Category>.Module.dll 或 <CustomCmd>/<CustomCmd>.dll
                // 2. 開發期除錯：bin/Debug/net8.0/
                var candidateDllPaths = new[]
                {
                    Path.Combine(moduleDir, $"{dirName}.Module.dll"),
                    Path.Combine(moduleDir, $"{dirName}.Command.dll"),
                    Path.Combine(moduleDir, $"{dirName}.dll"),
                    Path.Combine(moduleDir, "bin", "Debug", "net8.0", $"{dirName}.Module.dll"),
                    Path.Combine(moduleDir, "bin", "Debug", "net8.0", $"{dirName}.dll"),
                    Path.Combine(moduleDir, $"{dirName}Module.dll")
                };

                string? foundDllPath = candidateDllPaths.FirstOrDefault(File.Exists);
                if (foundDllPath != null)
                {
                    TryLoadHandlersFromAssembly(foundDllPath);
                }
                else if (!HasModule(dirName) &&
                         !dirName.Equals("CAPTURE", StringComparison.OrdinalIgnoreCase) &&
                         !dirName.Equals("MOTION", StringComparison.OrdinalIgnoreCase) &&
                         !dirName.Equals("DAQ", StringComparison.OrdinalIgnoreCase) &&
                         !dirName.Equals("PYTHON", StringComparison.OrdinalIgnoreCase) &&
                         Directory.GetFiles(moduleDir, "*.csvi").Length > 0)
                {
                    RegisterHandler(new CsviDynamicModuleHandler(dirName));
                }
            }

            // 2. 亦支援直接散落在 EXTRACMD/ 根目錄下的獨立指令 DLL
            foreach (var flatDll in Directory.GetFiles(root, "*.dll"))
            {
                var fName = Path.GetFileName(flatDll);
                if (fName.EndsWith(".Guidance.dll", StringComparison.OrdinalIgnoreCase) ||
                    fName.EndsWith(".Driver.dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                TryLoadHandlersFromAssembly(flatDll);
            }
        }
    }

    private void TryLoadHandlersFromAssembly(string dllPath)
    {
        try
        {
            var asm = ARCOS.Core.Common.PluginLoadManager.LoadOrReloadAssembly(dllPath, out _);
            if (asm == null) return;

            var handlerTypes = asm.GetExportedTypes()
                .Where(t => typeof(IModuleHandler).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

            foreach (var ht in handlerTypes)
            {
                if (Activator.CreateInstance(ht) is IModuleHandler handler)
                {
                    RegisterHandler(handler);
                }
            }
        }
        catch
        {
            // 忽略非目標或依賴不齊全的 assembly 載入異常
        }
    }

    /// <summary>
    /// 建立包含所有標準核心模組處理器的預設註冊表
    /// </summary>
    public static ModuleRegistry CreateDefault(string? modulesRootPath = null)
    {
        var registry = new ModuleRegistry();

        // 僅註冊核心保留系統與內建流程處理器 (Basic Modules)
        registry.RegisterHandler(new Handlers.SystemModuleHandler());
        registry.RegisterHandler(new Handlers.ThreadModuleHandler());
        registry.RegisterHandler(new Handlers.VariableModuleHandler());
        registry.RegisterHandler(new Handlers.UserModuleHandler());
        registry.RegisterHandler(new Handlers.CallModuleHandler());
        registry.RegisterHandler(new Handlers.MultiStationModuleHandler());
        registry.RegisterHandler(new Handlers.RedisModuleHandler());
        registry.RegisterHandler(new Handlers.AiAgentModuleHandler());
        registry.RegisterHandler(new Handlers.PointPathModuleHandler());

        // 嚴格規範：其餘擴充模組 (IO, Motion, Capture, DAQ, Python 等)
        // 絕對不在啟動時預載入，必須待下達指令 (例如 System@IncludeModule 或首次調用該模組指令) 時才動態加載！
        return registry;
    }

    /// <summary>
    /// 依據模組名稱隨選動態載入外部模組 (例如由 System@IncludeModule::Python 觸發)
    /// </summary>
    public bool LoadModuleByName(string moduleName, string? modulesRootPath = null)
    {
        if (string.IsNullOrWhiteSpace(moduleName)) return false;
        string mod = moduleName.Trim();
        if (HasModule(mod)) return true;

        var candidateRoots = GetCandidateModulesRoots(modulesRootPath).ToList();
        candidateRoots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EXTRACMD"));

        foreach (var root in candidateRoots)
        {
            if (!Directory.Exists(root)) continue;

            string moduleDir = Path.Combine(root, mod);
            if (Directory.Exists(moduleDir))
            {
                var candidateDllPaths = new[]
                {
                    Path.Combine(moduleDir, $"{mod}.Module.dll"),
                    Path.Combine(moduleDir, $"{mod}.Command.dll"),
                    Path.Combine(moduleDir, $"{mod}.dll"),
                    Path.Combine(moduleDir, "bin", "Debug", "net8.0", $"{mod}.Module.dll"),
                    Path.Combine(moduleDir, "bin", "Release", "net8.0", $"{mod}.Module.dll"),
                    Path.Combine(moduleDir, $"{mod}Module.dll")
                };

                string? foundDll = candidateDllPaths.FirstOrDefault(File.Exists);
                if (foundDll != null)
                {
                    TryLoadHandlersFromAssembly(foundDll);
                    if (HasModule(mod)) return true;
                }
            }
        }

        // 若未找到外部獨立 DLL，檢查是否有內建模組實作保底 (如純測試或未獨立發布環境)
        if (!HasModule(mod))
        {
            switch (mod.ToUpperInvariant())
            {
                case "IO":
                    RegisterHandler(new Handlers.IoModuleHandler());
                    return true;
                case "MOTION":
                    RegisterHandler(new Handlers.MotionModuleHandler());
                    return true;
                case "CAPTURE":
                    RegisterHandler(new Handlers.CaptureModuleHandler());
                    return true;
                case "DAQ":
                    RegisterHandler(new Handlers.DaqModuleHandler());
                    return true;
                case "PYTHON":
                    RegisterHandler(new Handlers.PythonModuleHandler());
                    return true;
            }
        }

        // 若為自訂純 CSVI 模組 (無獨立 .Module.dll，但 MODULES/<mod>/ 下有 .csvi 指令檔)，自動註冊動態 CSVI 轉發處理器
        if (!HasModule(mod))
        {
            var cmds = GetCsviCommandsForModule(mod, modulesRootPath);
            if (cmds != null && cmds.Count > 0)
            {
                RegisterHandler(new CsviDynamicModuleHandler(mod));
                return true;
            }
        }

        return HasModule(mod);
    }

    public IModuleHandler? GetHandler(string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName)) return null;
        string mod = moduleName.Trim();
        if (!_handlers.TryGetValue(mod, out var handler))
        {
            // 下指令時才載入該模組 (隨選隨載 On-Demand Lazy Loading)
            if (LoadModuleByName(mod))
            {
                _handlers.TryGetValue(mod, out handler);
            }
        }
        return handler;
    }

    public bool HasModule(string moduleName) =>
        !string.IsNullOrWhiteSpace(moduleName) && _handlers.ContainsKey(moduleName.Trim());

    /// <summary>
    /// 解除註冊特定模組處理器 (供 IncludeModule 重新載入時 Release 舊實例)
    /// </summary>
    public bool UnregisterHandler(string moduleName, out IModuleHandler? oldHandler)
    {
        oldHandler = null;
        if (string.IsNullOrWhiteSpace(moduleName)) return false;
        return _handlers.Remove(moduleName.Trim(), out oldHandler);
    }

    /// <summary>
    /// 取得指令清單 (對應 *@getCMD)
    /// 若 moduleFilter 為 null、空字串或 "*" 則回傳全模組所有指令 (<Module>@<Action>)
    /// </summary>
    public IReadOnlyList<string> GetAllCommands(string? moduleFilter = null)
    {
        var result = new List<string>();

        if (string.IsNullOrWhiteSpace(moduleFilter) || moduleFilter == "*")
        {
            // 若以 *@getCMD 查詢全指令，隨選載入可用模組
            foreach (var mod in new[] { "IO", "Motion", "Capture", "DAQ", "Python" })
            {
                GetHandler(mod);
            }

            foreach (var kvp in _handlers.OrderBy(k => k.Key))
            {
                var csviCmds = GetCsviCommandsForModule(kvp.Key);
                var cmdList = (csviCmds != null && csviCmds.Count > 0) ? csviCmds : kvp.Value.GetSupportedCommands();
                foreach (var cmd in cmdList)
                {
                    result.Add($"{kvp.Key}@{cmd}");
                }
            }
        }
        else
        {
            string modName = moduleFilter.Trim();
            var handler = GetHandler(modName);
            if (handler != null)
            {
                var csviCmds = GetCsviCommandsForModule(handler.ModuleName);
                var cmdList = (csviCmds != null && csviCmds.Count > 0) ? csviCmds : handler.GetSupportedCommands();
                foreach (var cmd in cmdList)
                {
                    result.Add($"{handler.ModuleName}@{cmd}");
                }
            }
        }

        return result;
    }

    private sealed class CsviDynamicModuleHandler : IModuleHandler
    {
        public string ModuleName { get; }

        public CsviDynamicModuleHandler(string moduleName)
        {
            ModuleName = moduleName;
        }

        public IReadOnlyList<string> GetSupportedCommands() =>
            GetCsviCommandsForModule(ModuleName) ?? Array.Empty<string>();

        public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
        {
            string? csviPath = ResolveCommandCsviPath(ModuleName, action);
            if (csviPath == null)
            {
                return CommandResult.Fail($"Command CSVI file not found: MODULES/{ModuleName}/{action}.csvi");
            }

            var handler = (ctx.Registry ?? new ModuleRegistry()).LoadCommandCsviHandler(csviPath);
            if (handler == null)
            {
                return CommandResult.Fail($"Failed to load command CSVI handler from: {csviPath}");
            }

            return handler.Execute(action, filteredParam, ctx);
        }
    }
}
