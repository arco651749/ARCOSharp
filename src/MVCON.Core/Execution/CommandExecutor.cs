using MVCON.Core.Common;

namespace MVCON.Core.Execution;

/// <summary>
/// MVCON CMDEXECUTE 指令直譯與自癒核心引擎
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

        // 5. 模組分發查詢
        var handler = Registry.GetHandler(item.Module);
        if (handler == null)
        {
            string err = $"Module '{item.Module}' not found in registry at line {ctx.ProgIndex} ({item.Command})";
            return HandleError(ctx, item, CommandResult.Fail(err), filteredParam);
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
                // 將下一行推進點壓入呼叫堆疊
                ctx.CallStack.Push((ctx.Program, ctx.ProgIndex + 1));
                ctx.State.Reset();
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
                // ErrorRetry: 還原為 FailedPC 重新執行
                ctx.ProgIndex = ctx.FailedPC;
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

        // 無對應自癒標籤，停止該執行緒
        ctx.IsRunning = false;
        return errorResult;
    }
}
