namespace ARCOS.Core.Execution;

/// <summary>
/// 指令執行狀態列舉
/// </summary>
public enum CommandStatus
{
    Completed,  // 執行完成，PC + 1 並重設 CommandState
    Waiting,    // 異步等待/微步進行中，保持 PC，保留 CommandState
    Jump,       // 跳躍至指定行號或標籤
    Call,       // 呼叫內部副程式
    Return,     // 副程式返回
    Error,      // 異常失敗（攜帶 ErrorMessage 與自癒 [[Label]]）
    Retry,      // 原處重試（跳回 FailedPC）
    Stop        // 終止執行緒
}

/// <summary>
/// 單步指令執行結果回傳物件
/// </summary>
public sealed class CommandResult
{
    public CommandStatus Status { get; }
    public int TargetLine { get; }
    public string? TargetLabel { get; }
    public string? TargetProgram { get; }
    public string? ErrorMessage { get; }
    public string? ErrorLabel { get; }

    private CommandResult(CommandStatus status, int targetLine = 0, string? targetLabel = null, string? targetProgram = null, string? errorMsg = null, string? errorLabel = null)
    {
        Status = status;
        TargetLine = targetLine;
        TargetLabel = targetLabel;
        TargetProgram = targetProgram;
        ErrorMessage = errorMsg;
        ErrorLabel = errorLabel;
    }

    public static CommandResult Done() => new(CommandStatus.Completed);

    public static CommandResult Wait() => new(CommandStatus.Waiting);

    public static CommandResult Jump(int line) => new(CommandStatus.Jump, targetLine: line);

    public static CommandResult Jump(string label) => new(CommandStatus.Jump, targetLabel: label);

    public static CommandResult Call(int progNo) => new(CommandStatus.Call, targetProgram: progNo.ToString());

    public static CommandResult Call(string progName) => new(CommandStatus.Call, targetProgram: progName);

    public static CommandResult Return() => new(CommandStatus.Return);

    public static CommandResult Fail(string errorMsg, string? errorLabel = null) =>
        new(CommandStatus.Error, errorMsg: errorMsg, errorLabel: errorLabel);

    public static CommandResult Retry() => new(CommandStatus.Retry);

    public static CommandResult Stop(string? msg = null) => new(CommandStatus.Stop, errorMsg: msg);
}
