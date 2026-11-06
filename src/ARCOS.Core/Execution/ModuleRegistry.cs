using System.Reflection;

namespace ARCOS.Core.Execution;

/// <summary>
/// 全域模組指令註冊表 (Module Registry)
/// 負責收集各模組小程式分派器並支援動態查詢與 *@getCMD 探索
/// </summary>
public sealed class ModuleRegistry
{
    private readonly Dictionary<string, IModuleHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterHandler(IModuleHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[handler.ModuleName] = handler;
    }

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
                    dirName.Equals("USERPARAMETEREDITOR", StringComparison.OrdinalIgnoreCase) ||
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
                if (foundDllPath == null) continue;

                TryLoadHandlersFromAssembly(foundDllPath);
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

        var candidateRoots = new List<string>();
        if (!string.IsNullOrEmpty(modulesRootPath) && Directory.Exists(modulesRootPath))
        {
            candidateRoots.Add(modulesRootPath);
        }

        candidateRoots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MODULES"));
        candidateRoots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EXTRACMD"));
        candidateRoots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "dist", "ARCOS_Runtime", "MODULES"));
        candidateRoots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "MODULES"));

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
                foreach (var cmd in kvp.Value.GetSupportedCommands())
                {
                    result.Add($"{kvp.Key}@{cmd}");
                }
            }
        }
        else
        {
            var handler = GetHandler(moduleFilter.Trim());
            if (handler != null)
            {
                foreach (var cmd in handler.GetSupportedCommands())
                {
                    result.Add($"{handler.ModuleName}@{cmd}");
                }
            }
        }

        return result;
    }
}
