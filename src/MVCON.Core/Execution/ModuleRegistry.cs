using System.Reflection;

namespace MVCON.Core.Execution;

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
    /// 遵循雙模架構 (Dual-Mode)：MVCON 主程式載入 DLL 同進程執行，工程師平時可直接雙擊同名 EXE 單獨測試。
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
        candidateRoots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "dist", "MVCON_Runtime", "MODULES"));
        candidateRoots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "dist", "MVCON_Runtime", "EXTRACMD"));
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
                    dirName.Equals("USERPARAMETEREDITOR", StringComparison.OrdinalIgnoreCase))
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
            var asm = Assembly.LoadFrom(dllPath);
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

        // 1. 核心保留系統與內建流程處理器
        registry.RegisterHandler(new Handlers.UserModuleHandler());
        registry.RegisterHandler(new Handlers.CallModuleHandler());
        registry.RegisterHandler(new Handlers.MultiStationModuleHandler());
        registry.RegisterHandler(new Handlers.RedisModuleHandler());
        registry.RegisterHandler(new Handlers.PythonModuleHandler());
        registry.RegisterHandler(new Handlers.AiAgentModuleHandler());
        registry.RegisterHandler(new Handlers.PointPathModuleHandler());

        // 2. 動態掃描並加載外部解耦模組 (IO, MOTION, CAPTURE, DAQ, SYSTEM, THREAD, VARIABLE)
        registry.LoadExternalModules(modulesRootPath);

        // 3. 內建回退安全防護 (若未經發行獨立運行單元測試，確保相容)
        if (!registry.HasModule("IO")) registry.RegisterHandler(new Handlers.IoModuleHandler());
        if (!registry.HasModule("Motion")) registry.RegisterHandler(new Handlers.MotionModuleHandler());
        if (!registry.HasModule("Capture")) registry.RegisterHandler(new Handlers.CaptureModuleHandler());
        if (!registry.HasModule("DAQ")) registry.RegisterHandler(new Handlers.DaqModuleHandler());
        if (!registry.HasModule("System")) registry.RegisterHandler(new Handlers.SystemModuleHandler());
        if (!registry.HasModule("Thread")) registry.RegisterHandler(new Handlers.ThreadModuleHandler());
        if (!registry.HasModule("Variable")) registry.RegisterHandler(new Handlers.VariableModuleHandler());

        return registry;
    }

    public IModuleHandler? GetHandler(string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName)) return null;
        _handlers.TryGetValue(moduleName.Trim(), out var handler);
        return handler;
    }

    public bool HasModule(string moduleName) =>
        !string.IsNullOrWhiteSpace(moduleName) && _handlers.ContainsKey(moduleName.Trim());

    /// <summary>
    /// 取得指令清單 (對應 *@getCMD)
    /// 若 moduleFilter 為 null、空字串或 "*" 則回傳全模組所有指令 (<Module>@<Action>)
    /// </summary>
    public IReadOnlyList<string> GetAllCommands(string? moduleFilter = null)
    {
        var result = new List<string>();

        if (string.IsNullOrWhiteSpace(moduleFilter) || moduleFilter == "*")
        {
            foreach (var kvp in _handlers.OrderBy(k => k.Key))
            {
                foreach (var cmd in kvp.Value.GetSupportedCommands())
                {
                    result.Add($"{kvp.Key}@{cmd}");
                }
            }
        }
        else if (_handlers.TryGetValue(moduleFilter.Trim(), out var handler))
        {
            foreach (var cmd in handler.GetSupportedCommands())
            {
                result.Add($"{handler.ModuleName}@{cmd}");
            }
        }

        return result;
    }
}
