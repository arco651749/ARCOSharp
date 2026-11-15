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
            // 丟棄未呼叫 ErrorRetry 即自然走到底部的 ErrorHandler 幀，避免誤當副程式返回
            while (ctx.CallStack.Count > 0 && ctx.CallStack.Peek().Kind == StackFrameKind.ErrorHandler)
            {
                ctx.CallStack.Pop();
            }

            // 若呼叫堆疊內有待返回的副程式，自動返回
            if (ctx.CallStack.Count > 0)
            {
                var frame = ctx.CallStack.Pop();
                ctx.Program = frame.Program;
                ctx.ProgIndex = frame.ReturnLine;
                ctx.State.Reset();
                return CommandResult.Done();
            }

            ctx.IsRunning = false;
            ctx.Engines.Io.SetPort($"{ctx.ThreadName}#Run?", false);
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
            item.Module.Equals("Motion", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("Capture", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("DAQ", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("Python", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("Point&Path", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("User", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("MultiStation", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("Redis", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("AIAgent", StringComparison.OrdinalIgnoreCase))
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

        // 6. 執行指令 (先重設 PC 異動旗標，允許指令直接修改 ctx.ProgIndex / CallStack 並回傳 Done)
        ctx.ResetPcModified();
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
                if (!ctx.IsPcModified)
                {
                    ctx.ProgIndex++;
                }
                ctx.State.Reset();
                break;

            case CommandStatus.Waiting:
                // 保持當前 ProgIndex，保留 CommandState 供下一輪繼續非阻塞輪詢
                break;

            case CommandStatus.Error:
                return HandleError(ctx, item, result, filteredParam);

            case CommandStatus.Stop:
                ctx.IsRunning = false;
                ctx.Engines.Io.SetPort($"{ctx.ThreadName}#Run?", false);
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

        // 自動從參數尾端解析 [[Label]]Message（即使指令 CSVI 未自行解析 errorLabel / errorMsg 也能自動生效）
        string? errorLabel = errorResult.ErrorLabel;
        string errorMsg = errorResult.ErrorMessage ?? "Unknown command error";

        var parsed = ArgParaHelper.ParseKeyValueArgs(filteredParam, out _);
        if (string.IsNullOrWhiteSpace(errorLabel) && parsed.TryGetValue("_ErrorLabel", out var tag))
        {
            errorLabel = tag;
        }
        if (parsed.TryGetValue("_ErrorMsg", out var customMsg) && !string.IsNullOrWhiteSpace(customMsg))
        {
            errorMsg = customMsg;
        }

        // 僅搜尋當前獨立 ProgramTable 中的 [[Label]] 或通用 ErrorHandler (不跨區段查看其他程式)
        int healingLine = ctx.Program.FindErrorHandler(errorLabel);
        if (healingLine > 0)
        {
            ctx.CallStack.Push(new ExecutionStackFrame(ctx.Program, ctx.ProgIndex, StackFrameKind.ErrorHandler));
            ctx.Log($"Error at line {ctx.ProgIndex} ({item.Command}): {errorMsg}", isError: true);
            ctx.Log($"Self-healing jump: redirecting from line {ctx.ProgIndex} to ErrorHandler at line {healingLine} [[{errorLabel}]]");
            ctx.Jump(healingLine);
            ctx.State.Reset();
            return CommandResult.Done();
        }

        // 若為 System@Log Error 指令，等效於觸發面板 EMG STOP (設定全機 EMG 鎖定並啟動 99: EMG Stop)
        bool isExplicitLogError = item.Module.Equals("System", StringComparison.OrdinalIgnoreCase) &&
                                  item.Action.Equals("Log Error", StringComparison.OrdinalIgnoreCase);
        if (isExplicitLogError)
        {
            ctx.Log($"[EMG] System@Log Error at line {ctx.ProgIndex} ({item.Command}): {errorMsg}", isError: true);
            ctx.IsRunning = false;
            ctx.Engines.Io.SetPort($"{ctx.ThreadName}#Run?", false);
            if (ctx.Program.ProgramNumber != 99 && !ctx.ThreadName.Equals("EMGStop&", StringComparison.OrdinalIgnoreCase))
            {
                ctx.VM?.TriggerEmgStop(errorMsg);
            }
            return errorResult;
        }

        // 一般 Worker 指令錯誤且找不到對應的 [[$label]] (或無 ErrorHandler)：
        // 僅設定該 Thread 專屬 <ThreadName>#Error?、記 Log 與彈出 MessageBox 後停下該 Thread，不觸發 99: EMG Stop (交由 Manager 監控處置)
        string msgBoxText = string.IsNullOrWhiteSpace(errorLabel)
            ? $"Line {ctx.ProgIndex} ({item.Command}): {errorMsg}"
            : $"Line {ctx.ProgIndex} ({item.Command}): {errorMsg} (找不到對應的 ErrorHandler 標籤: [[{errorLabel}]])";

        ctx.Engines.Io.SetPort($"{ctx.ThreadName}#Error?", true);
        ctx.Log($"[MessageBox] {msgBoxText}", isError: true);
        if (!ctx.IsMuted && ctx.VM?.OnMessageBoxRequested != null)
        {
            _ = ctx.VM.OnMessageBoxRequested(ctx.ThreadName, msgBoxText);
        }

        ctx.IsRunning = false;
        ctx.Engines.Io.SetPort($"{ctx.ThreadName}#Run?", false);
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
            item.Module.Equals("Motion", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("Capture", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("DAQ", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("Python", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("Point&Path", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("User", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("MultiStation", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("Redis", StringComparison.OrdinalIgnoreCase) ||
            item.Module.Equals("AIAgent", StringComparison.OrdinalIgnoreCase))
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
