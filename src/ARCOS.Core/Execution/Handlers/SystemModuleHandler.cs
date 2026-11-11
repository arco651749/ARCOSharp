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
        "Note",
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
        "Pause",
        "Resume",
        "SystemPause",
        "SystemResume",
        "ProgramSection",
        "ProgramLoad",
        "UserPanelOpen",
        "IncludeModule"
    };

    public IReadOnlyList<string> GetSupportedCommands() => Commands;

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        // 0. 流程註記指令 (Note / Legacy #Comment)
        if (action.Equals("Note", StringComparison.OrdinalIgnoreCase) || action.StartsWith("#Comment", StringComparison.OrdinalIgnoreCase))
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
            // HandleError 內部會呼叫 ctx.Log(..., isError: true) 並觸發自癒跳轉或 99: EMG Stop
            return CommandResult.Fail(filteredParam);
        }

        if (action.Equals("Clear Message", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Engines.Record.Record("SYSTEMLOG", "CLEAR");
            return CommandResult.Done();
        }

        if (action.Equals("Clear Error", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Engines.Io.SetPort("System#Error?", false);
            ctx.Engines.Record.ClearSystemErrors();
            return CommandResult.Done();
        }

        if (action.Equals("MessageBox", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.VM?.OnMessageBoxRequested == null)
            {
                ctx.Log($"[MessageBox] {filteredParam}");
                return CommandResult.Done();
            }

            if (ctx.State.Value == 0)
            {
                ctx.Log($"[MessageBox] {filteredParam}");
                ctx.State.Value = 1;
                ctx.State.TransientObject = ctx.VM.OnMessageBoxRequested(ctx.ThreadName, filteredParam);
                return CommandResult.Wait();
            }

            if (ctx.State.TransientObject is Task dialogTask && !dialogTask.IsCompleted)
            {
                return CommandResult.Wait();
            }

            return CommandResult.Done();
        }

        if (action.Equals("WarningBox", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.State.Value == 0)
            {
                var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
                bool cond;
                string msg;
                if (parts.Length > 1)
                {
                    cond = LogicEvaluator.EvaluateLogic(parts[0], ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                    msg = parts[1];
                }
                else
                {
                    cond = true;
                    msg = filteredParam.Trim();
                }

                if (!cond)
                {
                    return CommandResult.Done();
                }

                ctx.Log($"[WarningBox] {msg}");
                if (ctx.VM?.OnWarningBoxRequested == null)
                {
                    return CommandResult.Done();
                }

                ctx.State.Value = 1;
                ctx.State.TransientObject = ctx.VM.OnWarningBoxRequested(ctx.ThreadName, msg);
                return CommandResult.Wait();
            }

            if (ctx.State.TransientObject is Task<bool> warnTask)
            {
                if (!warnTask.IsCompleted)
                {
                    return CommandResult.Wait();
                }

                bool continueExecution = warnTask.Status == TaskStatus.RanToCompletion && warnTask.Result;
                if (!continueExecution)
                {
                    ctx.Log($"[WarningBox] Operator selected STOP on thread '{ctx.ThreadName}'.");
                    return CommandResult.Stop("Operator chose to stop execution in WarningBox.");
                }
            }

            return CommandResult.Done();
        }

        if (action.Equals("Record", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            string recName = parts[0];
            string msg = parts.Length > 1 ? parts[1] : "";
            ctx.Engines.Record.Record(recName, msg);
            return CommandResult.Done();
        }

        if (action.Equals("LogRecordCycleTime", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            string recName = parts[0];
            string prefix = parts.Length > 1 ? parts[1] : $"CycleTime [{recName}]:";
            double sec = ctx.Engines.Record.GetRecordElapsedSeconds(recName);
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
            long nowActiveMs = ctx.GetActiveClockMs();

            // 利用 CommandState 實現零阻塞延遲，並自動扣除 Local/Global Pause 暫停期間的時鐘
            if (ctx.State.Value == 0 && !ctx.State.HasData)
            {
                ctx.State.Value = 1;
                ctx.State["targetMs"] = targetMs;
                ctx.State["startActiveMs"] = nowActiveMs;
                return CommandResult.Wait();
            }

            long startActiveMs = ctx.State["startActiveMs"]?.GetValue<long>() ?? nowActiveMs;
            long elapsed = nowActiveMs - startActiveMs;

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

        if (action.Equals("Pause", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("SystemPause", StringComparison.OrdinalIgnoreCase))
        {
            ExecutePauseOrResume(filteredParam, ctx, isPause: true);
            return CommandResult.Done();
        }

        if (action.Equals("Resume", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("SystemResume", StringComparison.OrdinalIgnoreCase))
        {
            ExecutePauseOrResume(filteredParam, ctx, isPause: false);
            return CommandResult.Done();
        }

        // 3. 資源載入與參數儲存 (Parameters & External Loading)
        if (action.Equals("ProgramSection", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length > 1)
            {
                ctx.CurrentSection = parts[0];
                string sectionName = parts[1];
                if (int.TryParse(parts[0], out int targetSlot) && ctx.VM != null)
                {
                    var targetProg = ctx.VM.GetProgram(targetSlot);
                    if (targetProg != null) targetProg.Name = sectionName;
                }
                else
                {
                    ctx.Program.Name = sectionName;
                }
            }
            else if (parts.Length == 1)
            {
                ctx.CurrentSection = parts[0];
                ctx.Program.Name = parts[0];
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
                    : ARCOS.Core.Common.ArcosPaths.ResolveProgPath(inputPath);
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
                    ctx.Log($"Loaded .prog project '{proPath}' with {progs.Count} programs");
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

        // 4. 擴充模組動態載入 (Dynamic Modules)
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

            // 3. 探測 MODULES/<modName>/Init.csvi (或 init.csvi) 並執行初始化
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

        return CommandResult.Fail($"Command CSVI not found: MODULES/System/{action}.csvi");
    }

    private static void ExecutePauseOrResume(string filteredParam, ExecutionContext ctx, bool isPause)
    {
        var kvArgs = ArgParaHelper.ParseKeyValueArgs(filteredParam, out var posArgs);

        // 1. 檢查 CONDITION=$IOLOGIC (預設為 TRUE)
        string? condExpr = null;
        if (kvArgs.TryGetValue("CONDITION", out var c1))
        {
            condExpr = c1;
        }
        else if (kvArgs.TryGetValue("COND", out var c2))
        {
            condExpr = c2;
        }
        else if (kvArgs.TryGetValue("LOGIC", out var c3))
        {
            condExpr = c3;
        }

        if (!string.IsNullOrWhiteSpace(condExpr))
        {
            bool condOk = LogicEvaluator.EvaluateLogic(condExpr, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
            if (!condOk)
            {
                return;
            }
        }

        // 亦支援 THREAD=$THREAD 具名寫法
        if (kvArgs.TryGetValue("THREAD", out var namedThread) && !string.IsNullOrWhiteSpace(namedThread))
        {
            posArgs.Add(namedThread);
        }

        // 2. 若有指定目標 $THREAD -> 設定該 Thread 的 Local Pause
        if (posArgs.Count > 0)
        {
            if (ctx.VM != null)
            {
                ctx.VM.SetThreadLocalPause(posArgs, isPause);
            }
            else if (posArgs.Any(t => t.Equals(ctx.ThreadName, StringComparison.OrdinalIgnoreCase) ||
                                      t.Equals(ctx.ThreadId.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                ctx.IsLocalPaused = isPause;
            }
            return;
        }

        // 3. 未指定 $THREAD -> 設定 Global Pause (除帶 & 特權外的全部 Thread 暫停/恢復)
        if (ctx.VM != null)
        {
            ctx.VM.SetGlobalPause(isPause, triggerHook: false);
        }
        else
        {
            ctx.Engines.Timer.SetSystemPause(isPause);
        }
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
