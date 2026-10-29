using ARCOS.Core.Drivers;
using ARCOS.Core.Model;

namespace ARCOS.Core.Execution;

/// <summary>
/// 執行緒即時執行上下文 (Execution Context)
/// 維護當前 Program、ProgIndex (PC)、微步 CommandState、呼叫堆疊與硬體引擎參照
/// </summary>
public sealed class ExecutionContext
{
    private int _progIndex = 1;
    private int _reportedProgIndex = 1;

    public int ThreadId { get; set; }
    public string ThreadName { get; set; }
    public ProgramTable Program { get; set; }

    /// <summary>
    /// 目前指令行號 (PC)
    /// 當 ProgIndex 改變時，自動發送非阻塞原子通知，零等待 CMDEXECUTE 執行緒
    /// </summary>
    public int ProgIndex
    {
        get => _progIndex;
        set
        {
            if (_progIndex != value)
            {
                _progIndex = value;
                NotifyProgIndex(value);
            }
        }
    }

    /// <summary>
    /// 供 UI 異步節流讀取的最新原子 PC 指針 (Thread-safe)
    /// </summary>
    public int ReportedProgIndex => _reportedProgIndex;

    /// <summary>
    /// 非阻塞 PC 異步通知事件 (訂閱者絕不可在此事件中執行同步阻塞)
    /// </summary>
    public event Action<int>? OnProgIndexNoticed;

    public void NotifyProgIndex(int pc)
    {
        Interlocked.Exchange(ref _reportedProgIndex, pc);
        OnProgIndexNoticed?.Invoke(pc);
    }
    public int FailedPC { get; set; } = 1;
    public CommandState State { get; } = new();
    public Stack<(ProgramTable Program, int ReturnLine)> CallStack { get; } = new();
    public string CurrentSection { get; set; } = "Global";
    public BasicEngines Engines { get; }
    public DriverManager Drivers { get; }
    public ArcosThreadVM? VM { get; set; }
    public bool IsMuted { get; set; }
    public bool IsRunning { get; set; } = true;
    public bool IsBypassPause => ThreadName.EndsWith('&');
    public List<string> SysLogEntries { get; } = new();

    public event Action<string, bool>? OnLog;

    public ExecutionContext(int threadId, string threadName, ProgramTable program, BasicEngines engines, DriverManager drivers)
    {
        ThreadId = threadId;
        ThreadName = threadName;
        Program = program;
        Engines = engines;
        Drivers = drivers;
    }

    public void Log(string message, bool isError = false)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        string entry = $"[{timestamp}] [Thread {ThreadId}:{ThreadName}] {(isError ? "[ERROR] " : "")}{message}";
        SysLogEntries.Add(entry);
        OnLog?.Invoke(entry, isError);

        // 同步寫入系統日誌旗標與 TimerEngine 日誌
        if (isError)
        {
            Engines.Io.SetPort("System#Error?", true);
        }
    }
}
