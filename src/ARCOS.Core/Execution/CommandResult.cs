namespace ARCOS.Core.Execution;

/// <summary>
/// 指令執行狀態列舉 (流程跳躍與副程式切換由指令直接操作 ExecutionContext 控制，此處僅表達單步結果狀態)
/// </summary>
public enum CommandStatus
{
    Completed,  // 執行完成（若指令未主動修改 PC 則 PC + 1，並重設 CommandState）
    Waiting,    // 異步等待/微步進行中，保持 PC，保留 CommandState
    Error,      // 異常失敗（攜帶 ErrorMessage 與自癒 [[Label]]）
    Stop        // 終止執行緒
}

/// <summary>
/// 單步指令執行結果回傳物件
/// </summary>
public sealed class CommandResult
{
    public CommandStatus Status { get; }
    public string? ErrorMessage { get; }
    public string? ErrorLabel { get; }

    private CommandResult(CommandStatus status, string? errorMsg = null, string? errorLabel = null)
    {
        Status = status;
        ErrorMessage = errorMsg;
        ErrorLabel = errorLabel;
    }

    public static CommandResult Done() => new(CommandStatus.Completed);

    public static CommandResult Wait() => new(CommandStatus.Waiting);

    public static CommandResult Fail(string errorMsg, string? errorLabel = null) =>
        new(CommandStatus.Error, errorMsg: errorMsg, errorLabel: errorLabel);

    public static CommandResult Stop(string? msg = null) => new(CommandStatus.Stop, errorMsg: msg);
}
