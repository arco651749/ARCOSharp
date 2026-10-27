using System.Collections.Concurrent;
using MVCON.Core.Common;

namespace MVCON.Core.Execution.Handlers;

/// <summary>
/// Thread 模組直譯處理器 (Thread@)
/// 完整實做 MVCon/references/Thread.md 規範之全部流程與多執行緒指令
/// </summary>
public class ThreadModuleHandler : IModuleHandler
{
    public string ModuleName => "Thread";

    private static readonly ConcurrentDictionary<string, string> ActiveKeyLocks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Commands = new[]
    {
        "Label",
        "Thread Label",
        "Jump",
        "Call",
        "Return",
        "Repeat",
        "Stop",
        "Thread Stop",
        "GoOn",
        "RunNextCMD",
        "SkipNextCMD",
        "If",
        "ElseIf",
        "Else",
        "EndIf",
        "ForeachStart",
        "ForeachLoop",
        "ForeachBreak",
        "ForeachContinue",
        "Run",
        "Wait",
        "Suspend",
        "Resume",
        "SubThreadStart",
        "SubThreadEnd",
        "SubThreadWait",
        "SubThreadSuspend",
        "SubThreadScript",
        "ErrorHandler",
        "ErrorRetry",
        "MutePopupMessage",
        "KeySectionLock",
        "KeySectionUnLock"
    };

    public IReadOnlyList<string> GetSupportedCommands() => Commands;

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        // 1. Label 定位點 (純標記無動作)
        if (action.Equals("Label", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("Thread Label", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("ErrorHandler", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Done();
        }

        // 2. Jump 條件跳躍
        if (action.Equals("Jump", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            string targetLabel = parts[0];

            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                bool cond = LogicEvaluator.EvaluateLogic(parts[1], ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                if (!cond) return CommandResult.Done();
            }

            return CommandResult.Jump(targetLabel);
        }

        // 3. Call 內部副程式呼叫
        if (action.Equals("Call", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            string targetProg = parts[0];

            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                bool cond = LogicEvaluator.EvaluateLogic(parts[1], ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                if (!cond) return CommandResult.Done();
            }

            return CommandResult.Call(targetProg);
        }

        // 4. Return 副程式返回
        if (action.Equals("Return", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(filteredParam))
            {
                bool cond = LogicEvaluator.EvaluateLogic(filteredParam, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                if (!cond) return CommandResult.Done();
            }

            return CommandResult.Return();
        }

        // 5. Repeat 重頭執行
        if (action.Equals("Repeat", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(filteredParam))
            {
                bool cond = LogicEvaluator.EvaluateLogic(filteredParam, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                if (!cond) return CommandResult.Done();
            }

            return CommandResult.Jump(1);
        }

        // 6. Stop 終止程式
        if (action.Equals("Stop", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("Thread Stop", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                bool cond = LogicEvaluator.EvaluateLogic(parts[0], ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                if (!cond) return CommandResult.Done();
            }

            string? msg = parts.Length > 1 ? parts[1] : null;
            if (!string.IsNullOrEmpty(msg))
            {
                ctx.Log($"Thread stopped: {msg}", isError: false);
            }
            return CommandResult.Stop(msg);
        }

        // 7. GoOn / RunNextCMD / SkipNextCMD
        if (action.Equals("GoOn", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(filteredParam))
            {
                bool cond = LogicEvaluator.EvaluateLogic(filteredParam, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                if (!cond)
                {
                    ctx.Engines.Timer.SetSystemPause(true);
                    return CommandResult.Wait();
                }
            }
            return CommandResult.Done();
        }

        if (action.Equals("RunNextCMD", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(filteredParam))
            {
                bool cond = LogicEvaluator.EvaluateLogic(filteredParam, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                if (!cond)
                {
                    // 條件不成立，跳過下一行指令
                    return CommandResult.Jump(ctx.ProgIndex + 2);
                }
            }
            return CommandResult.Done();
        }

        if (action.Equals("SkipNextCMD", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(filteredParam))
            {
                return CommandResult.Jump(ctx.ProgIndex + 2);
            }

            bool cond = LogicEvaluator.EvaluateLogic(filteredParam, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
            if (cond)
            {
                return CommandResult.Jump(ctx.ProgIndex + 2);
            }
            return CommandResult.Done();
        }

        // 8. If / ElseIf / Else / EndIf 分支邏輯
        if (action.Equals("If", StringComparison.OrdinalIgnoreCase))
        {
            bool cond = LogicEvaluator.EvaluateLogic(filteredParam, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
            if (cond)
            {
                ctx.Engines.Variable.Set("__IfTaken", "1", ctx.CurrentSection);
                return CommandResult.Done();
            }

            ctx.Engines.Variable.Set("__IfTaken", "0", ctx.CurrentSection);
            int skipTo = FindMatchingBranch(ctx.Program, ctx.ProgIndex);
            if (skipTo > 0)
            {
                return CommandResult.Jump(skipTo);
            }
            return CommandResult.Done();
        }

        if (action.Equals("ElseIf", StringComparison.OrdinalIgnoreCase))
        {
            bool alreadyTaken = false;
            try { alreadyTaken = ctx.Engines.Variable.Get("__IfTaken", ctx.CurrentSection) == "1"; } catch { }

            if (alreadyTaken)
            {
                int endIf = FindMatchingEndIf(ctx.Program, ctx.ProgIndex);
                return endIf > 0 ? CommandResult.Jump(endIf) : CommandResult.Done();
            }

            bool cond = LogicEvaluator.EvaluateLogic(filteredParam, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
            if (cond)
            {
                ctx.Engines.Variable.Set("__IfTaken", "1", ctx.CurrentSection);
                return CommandResult.Done();
            }

            int skipTo = FindMatchingBranch(ctx.Program, ctx.ProgIndex);
            return skipTo > 0 ? CommandResult.Jump(skipTo) : CommandResult.Done();
        }

        if (action.Equals("Else", StringComparison.OrdinalIgnoreCase))
        {
            bool alreadyTaken = false;
            try { alreadyTaken = ctx.Engines.Variable.Get("__IfTaken", ctx.CurrentSection) == "1"; } catch { }

            if (alreadyTaken)
            {
                int endIf = FindMatchingEndIf(ctx.Program, ctx.ProgIndex);
                return endIf > 0 ? CommandResult.Jump(endIf) : CommandResult.Done();
            }

            return CommandResult.Done();
        }

        if (action.Equals("EndIf", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Engines.Variable.Set("__IfTaken", "0", ctx.CurrentSection);
            return CommandResult.Done();
        }

        // 9. ForeachStart / ForeachLoop / ForeachBreak / ForeachContinue
        if (action.Equals("ForeachStart", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Done();
        }

        if (action.Equals("ForeachLoop", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            string label = parts[0];
            string arrayVar = parts.Length > 1 ? parts[1] : "";

            if (!string.IsNullOrEmpty(arrayVar))
            {
                bool hasMore = ctx.Engines.Variable.LMoveNext(arrayVar, ctx.CurrentSection);
                if (hasMore)
                {
                    return CommandResult.Jump(label);
                }
                ctx.Engines.Variable.LResetIndex(arrayVar, 0, ctx.CurrentSection);
            }

            return CommandResult.Done();
        }

        if (action.Equals("ForeachBreak", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(filteredParam))
            {
                bool cond = LogicEvaluator.EvaluateLogic(filteredParam, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                if (!cond) return CommandResult.Done();
            }

            for (int i = ctx.ProgIndex + 1; i <= ctx.Program.Count; i++)
            {
                var item = ctx.Program[i];
                if (item.Module.Equals("Thread", StringComparison.OrdinalIgnoreCase) &&
                    item.Action.Equals("ForeachLoop", StringComparison.OrdinalIgnoreCase))
                {
                    return CommandResult.Jump(i + 1);
                }
            }
            return CommandResult.Done();
        }

        if (action.Equals("ForeachContinue", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(filteredParam))
            {
                bool cond = LogicEvaluator.EvaluateLogic(filteredParam, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                if (!cond) return CommandResult.Done();
            }

            for (int i = ctx.ProgIndex + 1; i <= ctx.Program.Count; i++)
            {
                var item = ctx.Program[i];
                if (item.Module.Equals("Thread", StringComparison.OrdinalIgnoreCase) &&
                    item.Action.Equals("ForeachLoop", StringComparison.OrdinalIgnoreCase))
                {
                    return CommandResult.Jump(i);
                }
            }
            return CommandResult.Done();
        }

        // 10. ErrorRetry 原處自癒重試
        if (action.Equals("ErrorRetry", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(filteredParam))
            {
                bool cond = LogicEvaluator.EvaluateLogic(filteredParam, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                if (!cond) return CommandResult.Done();
            }

            ctx.Log($"Executing ErrorRetry: jumping back to failed line {ctx.FailedPC}");
            return CommandResult.Retry();
        }

        // 11. MutePopupMessage
        if (action.Equals("MutePopupMessage", StringComparison.OrdinalIgnoreCase))
        {
            ctx.IsMuted = filteredParam.Trim() == "1";
            return CommandResult.Done();
        }

        // 12. Run 啟動指定主執行緒 (0~12)
        if (action.Equals("Run", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out int progNo) && int.TryParse(parts[1], out int threadNo))
            {
                int startIndex = parts.Length > 2 && int.TryParse(parts[2], out int idx) ? idx : 1;
                if (parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]))
                {
                    bool cond = LogicEvaluator.EvaluateLogic(parts[3], ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                    if (!cond) return CommandResult.Done();
                }

                ctx.VM?.StartThread(threadNo, progNo, startIndex);
            }
            return CommandResult.Done();
        }

        // 13. Suspend / Resume
        if (action.Equals("Suspend", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length > 0 && int.TryParse(parts[0], out int tNo))
            {
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    bool cond = LogicEvaluator.EvaluateLogic(parts[1], ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                    if (!cond) return CommandResult.Done();
                }
                ctx.VM?.SuspendThread(tNo);
            }
            return CommandResult.Done();
        }

        if (action.Equals("Resume", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length > 0 && int.TryParse(parts[0], out int tNo))
            {
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    bool cond = LogicEvaluator.EvaluateLogic(parts[1], ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                    if (!cond) return CommandResult.Done();
                }
                ctx.VM?.ResumeThread(tNo);
            }
            return CommandResult.Done();
        }

        // 14. Wait (等待主執行緒結束)
        if (action.Equals("Wait", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            foreach (var p in parts)
            {
                string threadName = int.TryParse(p, out int tNum) ? (tNum == 0 ? "ThreadMain" : $"Thread{tNum}") : p;
                if (ctx.Engines.Io.GetPort($"{threadName}#Run?"))
                {
                    return CommandResult.Wait();
                }
            }
            return CommandResult.Done();
        }

        // 15. SubThreadStart / SubThreadEnd / SubThreadSuspend / SubThreadWait / SubThreadScript
        if (action.Equals("SubThreadStart", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            string subName = parts[0];

            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                bool cond = LogicEvaluator.EvaluateLogic(parts[1], ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                if (!cond) return CommandResult.Done();
            }

            int endLine = -1;
            for (int i = ctx.ProgIndex + 1; i <= ctx.Program.Count; i++)
            {
                var itm = ctx.Program[i];
                if (itm.Module.Equals("Thread", StringComparison.OrdinalIgnoreCase) &&
                    itm.Action.Equals("SubThreadEnd", StringComparison.OrdinalIgnoreCase) &&
                    itm.RawParameter.Trim().Equals(subName, StringComparison.OrdinalIgnoreCase))
                {
                    endLine = i;
                    break;
                }
            }

            if (endLine > ctx.ProgIndex)
            {
                var subProg = new ProgramTable { Name = subName };
                for (int i = ctx.ProgIndex + 1; i < endLine; i++)
                {
                    subProg.Add(ctx.Program[i]);
                }
                ctx.VM?.StartSubThread(subName, subProg, 1);
                return CommandResult.Jump(endLine + 1);
            }

            return CommandResult.Done();
        }

        if (action.Equals("SubThreadEnd", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Done();
        }

        if (action.Equals("SubThreadSuspend", StringComparison.OrdinalIgnoreCase))
        {
            ctx.VM?.SuspendSubThread(filteredParam.Trim());
            return CommandResult.Done();
        }

        if (action.Equals("SubThreadWait", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            foreach (var sub in parts)
            {
                if (sub.StartsWith("SKIPNOTFIND", StringComparison.OrdinalIgnoreCase)) continue;
                string ioKey = $"{sub}#Run?";
                if (ctx.Engines.Io.GetPort(ioKey))
                {
                    return CommandResult.Wait();
                }
            }
            return CommandResult.Done();
        }

        if (action.Equals("SubThreadScript", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                string subName = parts[0];
                string scriptFile = parts[1];
                if (File.Exists(scriptFile))
                {
                    var subProg = ProgramTable.FromIniFile(scriptFile, 0, subName);
                    ctx.VM?.StartSubThread(subName, subProg, 1);
                }
            }
            return CommandResult.Done();
        }

        // 16. KeySectionLock / KeySectionUnLock
        if (action.Equals("KeySectionLock", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 3, StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0])) return CommandResult.Done();

            string lockName = parts[0];
            double timeoutSec = parts.Length > 1 && double.TryParse(parts[1], out double t) ? t : 5.0;
            string timeoutMsg = parts.Length > 2 ? parts[2] : $"Lock '{lockName}' timeout after {timeoutSec}s";
            long targetTimeoutMs = (long)(timeoutSec * 1000);

            // 若目前此鎖為當前執行緒持有，直接通行 (可重入)
            if (ActiveKeyLocks.TryGetValue(lockName, out var owner) && owner == ctx.ThreadName)
            {
                return CommandResult.Done();
            }

            // 若無人持有，嘗試奪取鎖
            if (ActiveKeyLocks.TryAdd(lockName, ctx.ThreadName))
            {
                ctx.State.Reset();
                return CommandResult.Done();
            }

            // 被其他執行緒鎖定，進入微步輪詢等待
            if (ctx.State.Value == 0 && !ctx.State.HasData)
            {
                ctx.State.Value = 1;
                ctx.State["timeoutMs"] = targetTimeoutMs;
                ctx.State["startTicks"] = Environment.TickCount64;
                return CommandResult.Wait();
            }

            long startTicks = ctx.State["startTicks"]?.GetValue<long>() ?? Environment.TickCount64;
            long elapsed = Environment.TickCount64 - startTicks;

            if (elapsed >= targetTimeoutMs)
            {
                ctx.State.Reset();
                return CommandResult.Fail(timeoutMsg);
            }

            return CommandResult.Wait();
        }

        if (action.Equals("KeySectionUnLock", StringComparison.OrdinalIgnoreCase))
        {
            string lockName = filteredParam.Trim();
            if (!string.IsNullOrEmpty(lockName))
            {
                if (ActiveKeyLocks.TryGetValue(lockName, out var owner) && owner == ctx.ThreadName)
                {
                    ActiveKeyLocks.TryRemove(lockName, out _);
                }
            }
            return CommandResult.Done();
        }

        return CommandResult.Done();
    }

    private static int FindMatchingBranch(ProgramTable program, int ifLine)
    {
        int depth = 0;
        for (int i = ifLine + 1; i <= program.Count; i++)
        {
            var item = program[i];
            if (!item.Module.Equals("Thread", StringComparison.OrdinalIgnoreCase)) continue;

            if (item.Action.Equals("If", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
            }
            else if (depth == 0 && (item.Action.Equals("ElseIf", StringComparison.OrdinalIgnoreCase) ||
                                    item.Action.Equals("Else", StringComparison.OrdinalIgnoreCase) ||
                                    item.Action.Equals("EndIf", StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
            else if (item.Action.Equals("EndIf", StringComparison.OrdinalIgnoreCase))
            {
                depth--;
            }
        }
        return -1;
    }

    private static int FindMatchingEndIf(ProgramTable program, int currentLine)
    {
        int depth = 0;
        for (int i = currentLine + 1; i <= program.Count; i++)
        {
            var item = program[i];
            if (!item.Module.Equals("Thread", StringComparison.OrdinalIgnoreCase)) continue;

            if (item.Action.Equals("If", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
            }
            else if (item.Action.Equals("EndIf", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0) return i;
                depth--;
            }
        }
        return -1;
    }
}
