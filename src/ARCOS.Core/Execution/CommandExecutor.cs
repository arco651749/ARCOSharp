using ARCOS.Core.Common;

namespace ARCOS.Core.Execution;

/// <summary>
/// ARCOS CMDEXECUTE 指令直譯與自癒核心引擎
/// 遵循三層調度規範，負責前置變數過濾 (Param Filter)、模組分派、雙模態 CommandState 狀態微步、
/// [[Label]] 異常攔截與 ErrorRetry 原處自癒。
/// </summary>
public sealed class CommandExecutor
{
    public ModuleRegistry Registry { get; }

    public CommandExecutor(ModuleRegistry? registry = null)
    {
        Registry = registry ?? new ModuleRegistry();
    }

    /// <summary>
    /// 執行當前 PC 所在之單步指令 (Single Step Execution)
    /// </summary>
    public CommandResult ExecuteStep(ExecutionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Registry = Registry;

        if (!ctx.IsRunning)
        {
            return CommandResult.Stop();
        }

        // 1. 邊界檢查
        if (ctx.ProgIndex < 1 || ctx.ProgIndex > ctx.Program.Count)
        {
            // 若呼叫堆疊內有待返回的副程式，自動返回
            if (ctx.CallStack.Count > 0)
            {
                var (callerProg, returnLine) = ctx.CallStack.Pop();
                ctx.Program = callerProg;
                ctx.ProgIndex = returnLine;
                ctx.State.Reset();
                return CommandResult.Done();
            }

            ctx.IsRunning = false;
            return CommandResult.Done();
        }

        // 2. 獲取指令項目
        CommandItem item = ctx.Program[ctx.ProgIndex];

        // 3. 空行或註解直接推進
        if (item.IsEmpty || item.IsComment)
        {
            ctx.ProgIndex++;
            ctx.State.Reset();
            return CommandResult.Done();
        }

        // 4. 前置變數過濾 (Param Filter)
        string filteredParam = ctx.Engines.Variable.FilterParameters(item.RawParameter, ctx.CurrentSection);

        // 5. 模組分發查詢與 MODULES/<Module>/<Action>.csvi 強制對應檢查
        var handler = Registry.GetHandler(item.Module);
        if (handler == null)
        {
            string err = $"Module '{item.Module}' not found in registry at line {ctx.ProgIndex} ({item.Command})";
            return HandleError(ctx, item, CommandResult.Fail(err), filteredParam);
        }

        // 對已完成單一指令 CSVI 化的模組，強制要求對應的 MODULES/<Module>/<Action>.csvi 必須存在！
        // 若 <Action>.csvi 不存在，直接跳錯誤，絕不退回舊保底！
        if (item.Module.Equals("System", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("Variable", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("Thread", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("IO", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("Point&Path", StringComparison.OrdinalIgnoreCase))
        {
            string? csviPath = ModuleRegistry.ResolveCommandCsviPath(item.Module, item.Action);
            if (csviPath == null)
            {
                string err = $"Command CSVI file not found: MODULES/{item.Module}/{item.Action}.csvi at line {ctx.ProgIndex} ({item.Command})";
                return HandleError(ctx, item, CommandResult.Fail(err), filteredParam);
            }

            var csviHandler = Registry.LoadCommandCsviHandler(csviPath);
            if (csviHandler == null)
            {
                string err = $"Failed to load command CSVI handler from: {csviPath} at line {ctx.ProgIndex} ({item.Command})";
                return HandleError(ctx, item, CommandResult.Fail(err), filteredParam);
            }
            handler = csviHandler;
        }

        // 6. 執行指令
        CommandResult result;
        try
        {
            result = handler.Execute(item.Action, filteredParam, ctx);
        }
        catch (Exception ex)
        {
            result = CommandResult.Fail($"Exception in '{item.Command}': {ex.Message}");
        }

        // 7. 狀態處理與 PC 流動
        switch (result.Status)
        {
            case CommandStatus.Completed:
                ctx.ProgIndex++;
                ctx.State.Reset();
                break;

            case CommandStatus.Waiting:
                // 保持當前 ProgIndex，保留 CommandState 供下一輪繼續非阻塞輪詢
                break;

            case CommandStatus.Jump:
                int targetLine = result.TargetLine;
                if (targetLine <= 0 && !string.IsNullOrEmpty(result.TargetLabel))
                {
                    targetLine = ctx.Program.FindLabel(result.TargetLabel);
                    if (targetLine <= 0)
                    {
                        string err = $"Jump target label '{result.TargetLabel}' not found in program {ctx.Program.ProgramNumber}";
                        return HandleError(ctx, item, CommandResult.Fail(err), filteredParam);
                    }
                }

                ctx.ProgIndex = targetLine;
                ctx.State.Reset();
                break;

            case CommandStatus.Call:
                // 將下一行推進點壓入呼叫堆疊，並切換至目標副程式區段或同區段 Label
                var targetSubProg = ctx.VM?.FindProgram(result.TargetProgram);
                if (targetSubProg != null && !ReferenceEquals(targetSubProg, ctx.Program))
                {
                    ctx.CallStack.Push((ctx.Program, ctx.ProgIndex + 1));
                    ctx.Program = targetSubProg;
                    ctx.ProgIndex = 1;
                    ctx.State.Reset();
                }
                else if (!string.IsNullOrEmpty(result.TargetProgram))
                {
                    int labelLine = ctx.Program.FindLabel(result.TargetProgram);
                    if (labelLine > 0)
                    {
                        ctx.CallStack.Push((ctx.Program, ctx.ProgIndex + 1));
                        ctx.ProgIndex = labelLine;
                        ctx.State.Reset();
                    }
                    else if (targetSubProg != null)
                    {
                        ctx.CallStack.Push((ctx.Program, ctx.ProgIndex + 1));
                        ctx.Program = targetSubProg;
                        ctx.ProgIndex = 1;
                        ctx.State.Reset();
                    }
                    else
                    {
                        string err = $"Call target program or label '{result.TargetProgram}' not found";
                        return HandleError(ctx, item, CommandResult.Fail(err), filteredParam);
                    }
                }
                else
                {
                    ctx.State.Reset();
                }
                break;

            case CommandStatus.Return:
                if (ctx.CallStack.Count > 0)
                {
                    var (callerProg, returnLine) = ctx.CallStack.Pop();
                    ctx.Program = callerProg;
                    ctx.ProgIndex = returnLine;
                }
                else
                {
                    ctx.IsRunning = false;
                }
                ctx.State.Reset();
                break;

            case CommandStatus.Error:
                return HandleError(ctx, item, result, filteredParam);

            case CommandStatus.Retry:
                // ErrorRetry: 還原為 FailedPC (+ offset) 重新執行
                ctx.ProgIndex = Math.Max(1, ctx.FailedPC + result.TargetLine);
                ctx.State.Reset();
                break;

            case CommandStatus.Stop:
                ctx.IsRunning = false;
                ctx.State.Reset();
                break;
        }

        return result;
    }

    /// <summary>
    /// 異常攔截與 [[Label]] 自癒跳轉處置
    /// </summary>
    private CommandResult HandleError(ExecutionContext ctx, CommandItem item, CommandResult errorResult, string filteredParam)
    {
        ctx.FailedPC = ctx.ProgIndex;
        string errorMsg = errorResult.ErrorMessage ?? "Unknown command error";
        ctx.Log($"Error at line {ctx.ProgIndex} ({item.Command}): {errorMsg}", isError: true);

        // 檢查是否有指定自癒標籤
        string? errorLabel = errorResult.ErrorLabel;
        if (string.IsNullOrWhiteSpace(errorLabel))
        {
            var parsed = ArgParaHelper.ParseKeyValueArgs(filteredParam, out _);
            if (parsed.TryGetValue("_ErrorLabel", out var tag))
            {
                errorLabel = tag;
            }
        }

        // 搜尋目前 ProgramTable 中的 [[Label]] 或通用 ErrorHandler
        int healingLine = ctx.Program.FindErrorHandler(errorLabel);
        if (healingLine > 0)
        {
            ctx.Log($"Self-healing jump: redirecting from line {ctx.ProgIndex} to ErrorHandler at line {healingLine} [[{errorLabel}]]");
            ctx.ProgIndex = healingLine;
            ctx.State.Reset();
            // 回傳 Jump 讓外部調度迴圈繼續 Auto Running
            return CommandResult.Jump(healingLine);
        }

        // 無對應自癒標籤，停止該執行緒，並觸發 99: EMG Stop 系統錯誤處理程序
        ctx.IsRunning = false;
        if (ctx.Program.ProgramNumber != 99 && !ctx.ThreadName.Equals("EMGStop&", StringComparison.OrdinalIgnoreCase))
        {
            ctx.VM?.TriggerSystemHook(99);
        }
        return errorResult;
    }

    /// <summary>
    /// 直接執行單行腳本指令（供即時指令輸入、MCP 遠端呼叫或診斷使用）
    /// </summary>
    public CommandResult ExecuteCommandLine(ExecutionContext ctx, string commandLine)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Registry = Registry;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return CommandResult.Done();
        }

        var item = CommandItem.Parse(commandLine);
        if (item.IsEmpty || item.IsComment)
        {
            return CommandResult.Done();
        }

        string filteredParam = ctx.Engines.Variable.FilterParameters(item.RawParameter, ctx.CurrentSection);

        var handler = Registry.GetHandler(item.Module);
        if (handler == null)
        {
            string err = $"Module '{item.Module}' not found in registry ({item.Command})";
            return CommandResult.Fail(err);
        }

        if (item.Module.Equals("System", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("Variable", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("Thread", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("IO", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("Point&Path", StringComparison.OrdinalIgnoreCase))
        {
            string? csviPath = ModuleRegistry.ResolveCommandCsviPath(item.Module, item.Action);
            if (csviPath == null)
            {
                return CommandResult.Fail($"Command CSVI file not found: MODULES/{item.Module}/{item.Action}.csvi ({item.Command})");
            }

            var csviHandler = Registry.LoadCommandCsviHandler(csviPath);
            if (csviHandler == null)
            {
                return CommandResult.Fail($"Failed to load command CSVI handler from: {csviPath} ({item.Command})");
            }
            handler = csviHandler;
        }
        else
        {
            string? csviPath = ModuleRegistry.ResolveCommandCsviPath(item.Module, item.Action);
            if (csviPath != null)
            {
                var csviHandler = Registry.LoadCommandCsviHandler(csviPath);
                if (csviHandler != null)
                {
                    handler = csviHandler;
                }
            }
        }

        try
        {
            return handler.Execute(item.Action, filteredParam, ctx);
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Exception in '{item.Command}': {ex.Message}");
        }
    }
}
