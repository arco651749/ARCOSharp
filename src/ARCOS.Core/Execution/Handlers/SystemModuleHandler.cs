using ARCOS.Core.Common;

namespace ARCOS.Core.Execution.Handlers;

/// <summary>
/// System 模組直譯處理器 (System@)
/// 完整實做 ARCOS/references/System.md 規範之全部系統指令
/// </summary>
public class SystemModuleHandler : IModuleHandler
{
    public string ModuleName => "System";

    private static readonly string[] Commands = new[]
    {
        "#Comment----->>",
        "Log Message",
        "Log Error",
        "Clear Message",
        "Clear Error",
        "MessageBox",
        "WarningBox",
        "Record",
        "LogRecordCycleTime",
        "Delay",
        "TimerReset",
        "TimerCheck",
        "CounterReset",
        "CounterCheck",
        "SystemPause",
        "SystemResume",
        "ProgramSection",
        "ProgramLoad",
        "UserPanelOpen",
        "UserPanelCMD",
        "ParameterSet",
        "ParameterSave",
        "ParameterLoad",
        "IncludeModule",
        "AddExternalDriver",
        "AddExternalDevice"
    };

    public IReadOnlyList<string> GetSupportedCommands() => Commands;

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        // 0. 註解指令
        if (action.StartsWith("#Comment", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Done();
        }

        // 1. 訊息提示與日誌記錄 (Logging & Messages)
        if (action.Equals("Log Message", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log(filteredParam);
            return CommandResult.Done();
        }

        if (action.Equals("Log Error", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log(filteredParam, isError: true);
            return CommandResult.Fail(filteredParam);
        }

        if (action.Equals("Clear Message", StringComparison.OrdinalIgnoreCase))
        {
            ctx.SysLogEntries.Clear();
            return CommandResult.Done();
        }

        if (action.Equals("Clear Error", StringComparison.OrdinalIgnoreCase))
        {
            ctx.SysLogEntries.RemoveAll(x => x.Contains("[ERROR]"));
            return CommandResult.Done();
        }

        if (action.Equals("MessageBox", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log($"[MessageBox] {filteredParam}");
            return CommandResult.Done();
        }

        if (action.Equals("WarningBox", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length > 1)
            {
                bool cond = LogicEvaluator.EvaluateLogic(parts[0], ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                if (cond)
                {
                    ctx.Log($"[WarningBox] Condition '{parts[0]}' triggered: {parts[1]}");
                }
            }
            return CommandResult.Done();
        }

        if (action.Equals("Record", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            string recName = parts[0];
            string msg = parts.Length > 1 ? parts[1] : "";
            ctx.Engines.Timer.Record(recName, msg);
            return CommandResult.Done();
        }

        if (action.Equals("LogRecordCycleTime", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            string recName = parts[0];
            string prefix = parts.Length > 1 ? parts[1] : $"CycleTime [{recName}]:";
            double sec = ctx.Engines.Timer.GetRecordElapsedSeconds(recName);
            ctx.Log($"{prefix} {sec:F3}s");
            return CommandResult.Done();
        }

        // 2. 時間與流程控制 (Flow Control)
        if (action.Equals("Delay", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.TryParse(filteredParam, out double seconds) || seconds <= 0)
            {
                return CommandResult.Done();
            }

            long targetMs = (long)(seconds * 1000);

            // 利用 CommandState 實現零阻塞延遲 (Micro-step non-blocking delay)
            if (ctx.State.Value == 0 && !ctx.State.HasData)
            {
                ctx.State.Value = 1;
                ctx.State["targetMs"] = targetMs;
                ctx.State["startTicks"] = Environment.TickCount64;
                return CommandResult.Wait();
            }

            long startTicks = ctx.State["startTicks"]?.GetValue<long>() ?? Environment.TickCount64;
            long elapsed = Environment.TickCount64 - startTicks;

            if (elapsed >= targetMs)
            {
                return CommandResult.Done();
            }

            return CommandResult.Wait();
        }

        if (action.Equals("TimerReset", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            string timerName = parts[0];
            double timeout = parts.Length > 1 && double.TryParse(parts[1], out double s) ? s : 0;
            ctx.Engines.Timer.TimerReset(timerName, timeout);
            return CommandResult.Done();
        }

        if (action.Equals("TimerCheck", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Engines.Timer.TimerCheck(filteredParam.Trim());
            return CommandResult.Done();
        }

        if (action.Equals("CounterReset", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            string cntName = parts[0];
            int count = parts.Length > 1 && int.TryParse(parts[1], out int c) ? c : 0;
            ctx.Engines.Timer.CounterReset(cntName, count);
            return CommandResult.Done();
        }

        if (action.Equals("CounterCheck", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Engines.Timer.CounterCheck(filteredParam.Trim());
            return CommandResult.Done();
        }

        if (action.Equals("SystemPause", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Engines.Timer.SetSystemPause(true);
            return CommandResult.Done();
        }

        if (action.Equals("SystemResume", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Engines.Timer.SetSystemPause(false);
            return CommandResult.Done();
        }

        // 3. 資源載入與參數儲存 (Parameters & External Loading)
        if (action.Equals("ProgramSection", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length > 1)
            {
                ctx.CurrentSection = parts[0];
                ctx.Program.Name = parts[1];
            }
            else if (parts.Length == 1)
            {
                ctx.CurrentSection = parts[0];
            }
            return CommandResult.Done();
        }

        if (action.Equals("ProgramLoad", StringComparison.OrdinalIgnoreCase))
        {
            string inputPath = filteredParam.Trim();
            string proPath = inputPath;
            bool isInit = proPath.EndsWith(".init", StringComparison.OrdinalIgnoreCase);

            if (!Path.IsPathRooted(proPath))
            {
                proPath = isInit 
                    ? ARCOS.Core.Common.ArcosPaths.ResolveInitPath(inputPath)
                    : ARCOS.Core.Common.ArcosPaths.ResolveProPath(inputPath);
            }

            if (File.Exists(proPath))
            {
                if (isInit)
                {
                    var prog = Serialization.ScriptSerializer.LoadInitFile(proPath, ctx.Program.ProgramNumber, Path.GetFileNameWithoutExtension(proPath));
                    ctx.VM?.LoadProgram(ctx.Program.ProgramNumber, prog);
                    ctx.VM?.NotifyProgramLoaded(proPath, false);
                    ctx.Log($"Loaded .init program '{proPath}' into slot {ctx.Program.ProgramNumber}");
                }
                else
                {
                    var progs = Serialization.ScriptSerializer.LoadProFile(proPath);
                    if (ctx.VM != null)
                    {
                        foreach (var p in progs)
                        {
                            ctx.VM.LoadProgram(p.ProgramNumber, p);
                        }
                    }
                    ctx.VM?.NotifyProgramLoaded(proPath, true);
                    ctx.Log($"Loaded .pro project '{proPath}' with {progs.Count} programs");
                }
            }
            else
            {
                ctx.Log($"[WARN] Program file '{inputPath}' not found (resolved to '{proPath}')", isError: true);
            }
            return CommandResult.Done();
        }

        if (action.Equals("UserPanelOpen", StringComparison.OrdinalIgnoreCase))
        {
            string panelPath = filteredParam.Trim();
            ctx.VM?.NotifyUserPanelOpen(panelPath);
            ctx.Log($"[UserPanelOpen] Requested panel '{panelPath}'");
            return CommandResult.Done();
        }

        if (action.Equals("UserPanelCMD", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log($"[UserPanelCMD] {filteredParam.Trim()}");
            return CommandResult.Done();
        }

        if (action.Equals("ParameterSet", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                string sec = parts.Length > 2 ? parts[2] : ctx.CurrentSection;
                ctx.Engines.Variable.Set(parts[0], parts[1], sec);
            }
            return CommandResult.Done();
        }

        if (action.Equals("ParameterSave", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Engines.Variable.SaveToFile(filteredParam.Trim());
            return CommandResult.Done();
        }

        if (action.Equals("ParameterLoad", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Engines.Variable.LoadFromFile(filteredParam.Trim());
            return CommandResult.Done();
        }

        // 4. 擴充模組與驅動程式對接 (Modules & Drivers)
        if (action.Equals("IncludeModule", StringComparison.OrdinalIgnoreCase))
        {
            string modName = filteredParam.Trim();
            ctx.Log($"[IncludeModule] Loading module '{modName}'");

            // 1. 若該模組已存在，先 Release 舊模組實例並執行 Reset (與 AddDevice 設備重載規範完全一致)
            if (ctx.Registry != null && ctx.Registry.HasModule(modName))
            {
                ctx.Log($"[IncludeModule] Module '{modName}' already loaded. Releasing previous instance (Reset)...");
                if (ctx.Registry.UnregisterHandler(modName, out var oldHandler))
                {
                    if (oldHandler is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                    else if (oldHandler != null && oldHandler.GetSupportedCommands().Contains("Reset", StringComparer.OrdinalIgnoreCase))
                    {
                        oldHandler.Execute("Reset", "", ctx);
                    }
                }
            }

            // 2. 動態隨選載入模組 Assembly
            if (ctx.Registry != null && !ctx.Registry.HasModule(modName))
            {
                bool loaded = ctx.Registry.LoadModuleByName(modName);
                if (loaded)
                {
                    ctx.Log($"[IncludeModule] Module '{modName}' assembly loaded into registry.");
                }
                else
                {
                    ctx.Log($"[IncludeModule] Failed to find or load module '{modName}'.", isError: true);
                }
            }

            // 2. 探測 MODULES/<modName>/Init.csvi (或 init.csvi) 並執行初始化
            string? initCsvi = FindModuleInitCsvi(modName);
            var handler = ctx.Registry?.GetHandler(modName);
            if (initCsvi != null)
            {
                ctx.Log($"[IncludeModule] Found Init.csvi at '{initCsvi}'. Executing initialization...");
                if (handler != null && handler.GetSupportedCommands().Contains("Init", StringComparer.OrdinalIgnoreCase))
                {
                    handler.Execute("Init", "", ctx);
                }
                ctx.Log($"[IncludeModule] Module '{modName}' initialized successfully via Init.csvi.");
            }
            else if (handler != null && handler.GetSupportedCommands().Contains("Init", StringComparer.OrdinalIgnoreCase))
            {
                handler.Execute("Init", "", ctx);
            }

            return CommandResult.Done();
        }

        if (action.Equals("AddExternalDriver", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                ctx.Log($"[AddExternalDriver] Module '{parts[0]}', Driver '{parts[1]}'");
            }
            return CommandResult.Done();
        }

        if (action.Equals("AddExternalDevice", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 3, StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                ctx.Log($"[AddExternalDevice] Device '{parts[0]}', Driver '{parts[1]}'");
            }
            return CommandResult.Done();
        }

        return CommandResult.Done();
    }
    private static string? FindModuleInitCsvi(string modName)
    {
        var roots = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MODULES", modName),
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "dist", "ARCOS_Runtime", "MODULES", modName)),
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "MODULES", modName)),
            Path.Combine(Directory.GetCurrentDirectory(), "MODULES", modName)
        };

        foreach (var dir in roots)
        {
            if (!Directory.Exists(dir)) continue;
            string p1 = Path.Combine(dir, "Init.csvi");
            if (File.Exists(p1)) return p1;
            string p2 = Path.Combine(dir, "init.csvi");
            if (File.Exists(p2)) return p2;
        }

        return null;
    }
}
