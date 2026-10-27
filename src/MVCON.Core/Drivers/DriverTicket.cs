namespace MVCON.Core.Drivers;

/// <summary>
/// Driver 執行回傳結果
/// </summary>
public class DriverResponse
{
    public bool Success { get; init; } = true;
    public string ErrorCode { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public object? Payload { get; init; }

    public static DriverResponse Ok(object? payload = null) => new() { Success = true, Payload = payload };
    public static DriverResponse Fail(string errorCode, string message) => new() { Success = false, ErrorCode = errorCode, Message = message };
}

/// <summary>
/// 通訊信令票券 (DriverTicket)
/// 呼叫端派發指令至 Driver，內部自帶私有專屬回傳信道 (TaskCompletionSource)，
/// 支援非阻塞輪詢 (IsCompleted) 與異步等待 (await Task)，徹底防止多執行緒串線
/// </summary>
public class DriverTicket
{
    public string Command { get; init; } = string.Empty;
    public string Parameter { get; init; } = string.Empty;
    public string CallerId { get; init; } = string.Empty;
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    private readonly TaskCompletionSource<DriverResponse> _responseSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsCompleted => _responseSource.Task.IsCompleted;
    public Task<DriverResponse> Task => _responseSource.Task;

    public DriverResponse? Result => _responseSource.Task.IsCompleted ? _responseSource.Task.Result : null;

    public DriverTicket() { }

    public DriverTicket(string command, string parameter = "", string callerId = "")
    {
        Command = command;
        Parameter = parameter;
        CallerId = callerId;
    }

    /// <summary>
    /// Driver 執行完畢時，將結果塞回此私有信號
    /// </summary>
    public void SetCompleted(DriverResponse response)
    {
        _responseSource.TrySetResult(response);
    }

    public void SetCompleted()
    {
        _responseSource.TrySetResult(DriverResponse.Ok());
    }

    public void SetFailed(string errorCode, string message)
    {
        _responseSource.TrySetResult(DriverResponse.Fail(errorCode, message));
    }

    /// <summary>
    /// 同步阻塞等待 (支援逾時時間，單位 ms)
    /// </summary>
    public DriverResponse Wait(int timeoutMs = -1)
    {
        if (timeoutMs < 0)
        {
            return _responseSource.Task.GetAwaiter().GetResult();
        }

        bool finished = _responseSource.Task.Wait(timeoutMs);
        if (finished)
        {
            return _responseSource.Task.Result;
        }

        return DriverResponse.Fail("TIMEOUT", $"Driver command '{Command}' timed out after {timeoutMs}ms.");
    }
}
