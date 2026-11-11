using System.Collections.Concurrent;
using ARCOS.Core.Common;

namespace ARCOS.Core.Execution.Handlers;

/// <summary>
/// Thread 模組直譯處理器 (Thread@)
/// 完整實做 ARCOS/references/Thread.md 規範之全部流程與多執行緒指令
/// </summary>
public class ThreadModuleHandler : IModuleHandler
{
    public string ModuleName => "Thread";

    private static readonly string[] Commands = new[]
    {
        "Label",
        "Jump",
        "Call",
        "Return",
        "Repeat",
        "Stop",
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
        if (action.Equals("Stop", StringComparison.OrdinalIgnoreCase))
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
                ctx.Log($"Thread stopped with error: {msg}", isError: true);
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

        // 8. If / ElseIf / Else / EndIf 分支邏輯 (支援巢狀 If)
        if (action.Equals("If", StringComparison.OrdinalIgnoreCase))
        {
            int depth = GetIfNestingDepth(ctx.Program, ctx.ProgIndex);
            string takenKey = $"__IfTaken_{depth}";
            bool cond = LogicEvaluator.EvaluateLogic(filteredParam, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
            if (cond)
            {
                ctx.Engines.Variable.Set(takenKey, "1", ctx.CurrentSection);
                return CommandResult.Done();
            }

            ctx.Engines.Variable.Set(takenKey, "0", ctx.CurrentSection);
            int skipTo = FindMatchingBranch(ctx.Program, ctx.ProgIndex);
            if (skipTo > 0)
            {
                return CommandResult.Jump(skipTo);
            }
            return CommandResult.Done();
        }

        if (action.Equals("ElseIf", StringComparison.OrdinalIgnoreCase))
        {
            int depth = GetIfNestingDepth(ctx.Program, ctx.ProgIndex);
            string takenKey = $"__IfTaken_{depth}";
            bool alreadyTaken = false;
            try { alreadyTaken = ctx.Engines.Variable.Get(takenKey, ctx.CurrentSection) == "1"; } catch { }

            if (alreadyTaken)
            {
                int endIf = FindMatchingEndIf(ctx.Program, ctx.ProgIndex);
                return endIf > 0 ? CommandResult.Jump(endIf) : CommandResult.Done();
            }

            bool cond = LogicEvaluator.EvaluateLogic(filteredParam, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
            if (cond)
            {
                ctx.Engines.Variable.Set(takenKey, "1", ctx.CurrentSection);
                return CommandResult.Done();
            }

            int skipTo = FindMatchingBranch(ctx.Program, ctx.ProgIndex);
            return skipTo > 0 ? CommandResult.Jump(skipTo) : CommandResult.Done();
        }

        if (action.Equals("Else", StringComparison.OrdinalIgnoreCase))
        {
            int depth = GetIfNestingDepth(ctx.Program, ctx.ProgIndex);
            string takenKey = $"__IfTaken_{depth}";
            bool alreadyTaken = false;
            try { alreadyTaken = ctx.Engines.Variable.Get(takenKey, ctx.CurrentSection) == "1"; } catch { }

            if (alreadyTaken)
            {
                int endIf = FindMatchingEndIf(ctx.Program, ctx.ProgIndex);
                return endIf > 0 ? CommandResult.Jump(endIf) : CommandResult.Done();
            }

            return CommandResult.Done();
        }

        if (action.Equals("EndIf", StringComparison.OrdinalIgnoreCase))
        {
            int depth = GetIfNestingDepth(ctx.Program, ctx.ProgIndex);
            string takenKey = $"__IfTaken_{depth}";
            ctx.Engines.Variable.Set(takenKey, "0", ctx.CurrentSection);
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

            bool anyHasMore = false;
            for (int i = 1; i < parts.Length; i++)
            {
                string arrayVar = parts[i].Trim().TrimStart('$');
                if (!string.IsNullOrEmpty(arrayVar))
                {
                    if (ctx.Engines.Variable.LMoveNext(arrayVar, ctx.CurrentSection))
                    {
                        anyHasMore = true;
                    }
                }
            }

            if (anyHasMore)
            {
                return CommandResult.Jump(label);
            }

            for (int i = 1; i < parts.Length; i++)
            {
                string arrayVar = parts[i].Trim().TrimStart('$');
                if (!string.IsNullOrEmpty(arrayVar))
                {
                    ctx.Engines.Variable.LResetIndex(arrayVar, 0, ctx.CurrentSection);
                }
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

            int depth = 0;
            for (int i = ctx.ProgIndex + 1; i <= ctx.Program.Count; i++)
            {
                var item = ctx.Program[i];
                if (!item.Module.Equals("Thread", StringComparison.OrdinalIgnoreCase)) continue;
                if (item.Action.Equals("ForeachStart", StringComparison.OrdinalIgnoreCase)) depth++;
                else if (item.Action.Equals("ForeachLoop", StringComparison.OrdinalIgnoreCase))
                {
                    if (depth == 0)
                    {
                        var loopParts = item.RawParameter.Split(',', StringSplitOptions.TrimEntries);
                        for (int k = 1; k < loopParts.Length; k++)
                        {
                            string arrayVar = loopParts[k].Trim().TrimStart('$');
                            if (!string.IsNullOrEmpty(arrayVar))
                                ctx.Engines.Variable.LResetIndex(arrayVar, 0, ctx.CurrentSection);
                        }
                        return CommandResult.Jump(i + 1);
                    }
                    depth--;
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

            int depth = 0;
            for (int i = ctx.ProgIndex + 1; i <= ctx.Program.Count; i++)
            {
                var item = ctx.Program[i];
                if (!item.Module.Equals("Thread", StringComparison.OrdinalIgnoreCase)) continue;
                if (item.Action.Equals("ForeachStart", StringComparison.OrdinalIgnoreCase)) depth++;
                else if (item.Action.Equals("ForeachLoop", StringComparison.OrdinalIgnoreCase))
                {
                    if (depth == 0) return CommandResult.Jump(i);
                    depth--;
                }
            }
            return CommandResult.Done();
        }

        // 10. ErrorRetry 原處自癒重試 ($logic, $offset)
        if (action.Equals("ErrorRetry", StringComparison.OrdinalIgnoreCase))
        {
            int offset = 0;
            if (!string.IsNullOrWhiteSpace(filteredParam))
            {
                var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
                if (!string.IsNullOrWhiteSpace(parts[0]))
                {
                    bool cond = LogicEvaluator.EvaluateLogic(parts[0], ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                    if (!cond) return CommandResult.Done();
                }
                if (parts.Length > 1 && int.TryParse(parts[1], out int parsedOffset))
                {
                    offset = parsedOffset;
                }
            }

            ctx.Log($"Executing ErrorRetry: jumping back to line {Math.Max(1, ctx.FailedPC + offset)}");
            return CommandResult.Retry(offset);
        }

        // 11. MutePopupMessage
        if (action.Equals("MutePopupMessage", StringComparison.OrdinalIgnoreCase))
        {
            ctx.IsMuted = filteredParam.Trim() == "1";
            return CommandResult.Done();
        }

        // 12. Run 啟動指定主執行緒 (0~12)，支援 $index 為行號或 Label 名稱
        if (action.Equals("Run", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out int progNo) && int.TryParse(parts[1], out int threadNo))
            {
                if (parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]))
                {
                    bool cond = LogicEvaluator.EvaluateLogic(parts[3], ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                    if (!cond) return CommandResult.Done();
                }

                int startIndex = 1;
                if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]))
                {
                    if (int.TryParse(parts[2], out int idx))
                    {
                        startIndex = idx;
                    }
                    else if (ctx.VM != null)
                    {
                        var targetProg = ctx.VM.GetProgram(progNo);
                        int labelLine = targetProg.FindLabel(parts[2]);
                        if (labelLine > 0) startIndex = labelLine;
                    }
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

            bool shouldStart = true;
            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                shouldStart = LogicEvaluator.EvaluateLogic(parts[1], ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
            }

            if (endLine > ctx.ProgIndex)
            {
                if (shouldStart)
                {
                    var subProg = new ProgramTable { Name = subName };
                    for (int i = ctx.ProgIndex + 1; i < endLine; i++)
                    {
                        subProg.Add(ctx.Program[i]);
                    }
                    ctx.VM?.StartSubThread(subName, subProg, 1, ctx.CurrentSection);
                }
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
            bool skipNotFind = parts.Any(p => p.Replace(" ", "").Equals("SKIPNOTFIND=1", StringComparison.OrdinalIgnoreCase));

            foreach (var sub in parts)
            {
                if (string.IsNullOrWhiteSpace(sub) || sub.StartsWith("SKIPNOTFIND", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ctx.VM != null)
                {
                    if (!ctx.VM.HasSubThread(sub) && !ctx.Engines.Io.GetAllPortNames().Contains($"{sub}#Run?", StringComparer.OrdinalIgnoreCase))
                    {
                        if (skipNotFind) continue;
                        return CommandResult.Fail($"SubThread '{sub}' not found");
                    }

                    if (ctx.VM.IsSubThreadRunning(sub))
                    {
                        return CommandResult.Wait();
                    }
                }
                else
                {
                    string ioKey = $"{sub}#Run?";
                    if (ctx.Engines.Io.GetPort(ioKey))
                    {
                        return CommandResult.Wait();
                    }
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
                string scriptFile = ArcosPaths.ResolveInitPath(parts[1]);
                if (File.Exists(scriptFile))
                {
                    var subProg = ProgramTable.FromIniFile(scriptFile, 0, subName);
                    ctx.VM?.StartSubThread(subName, subProg, 1, ctx.CurrentSection);
                }
                else
                {
                    return CommandResult.Fail($"SubThreadScript file not found: {scriptFile}");
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
            if (ArcosThreadVM.ActiveKeyLocks.TryGetValue(lockName, out var owner) && owner == ctx.ThreadName)
            {
                return CommandResult.Done();
            }

            // 若無人持有，嘗試奪取鎖
            if (ArcosThreadVM.ActiveKeyLocks.TryAdd(lockName, ctx.ThreadName))
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
                if (ArcosThreadVM.ActiveKeyLocks.TryGetValue(lockName, out var owner) && owner == ctx.ThreadName)
                {
                    ArcosThreadVM.ActiveKeyLocks.TryRemove(lockName, out _);
                }
            }
            return CommandResult.Done();
        }

        return CommandResult.Done();
    }

    public static int GetIfNestingDepth(ProgramTable program, int line)
    {
        int depth = 0;
        for (int i = 1; i < line && i <= program.Count; i++)
        {
            var item = program[i];
            if (!item.Module.Equals("Thread", StringComparison.OrdinalIgnoreCase)) continue;
            if (item.Action.Equals("If", StringComparison.OrdinalIgnoreCase)) depth++;
            else if (item.Action.Equals("EndIf", StringComparison.OrdinalIgnoreCase) && depth > 0) depth--;
        }
        if (line >= 1 && line <= program.Count)
        {
            var cur = program[line];
            if (cur.Module.Equals("Thread", StringComparison.OrdinalIgnoreCase) &&
                cur.Action.Equals("If", StringComparison.OrdinalIgnoreCase))
            {
                return depth + 1;
            }
        }
        return depth;
    }

    public static int FindMatchingBranch(ProgramTable program, int ifLine)
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

    public static int FindMatchingEndIf(ProgramTable program, int currentLine)
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
