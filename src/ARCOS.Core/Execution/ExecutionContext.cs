using System.Diagnostics;
using ARCOS.Core.Drivers;
using ARCOS.Core.Model;

namespace ARCOS.Core.Execution;

public enum StackFrameKind
{
    Call,
    ErrorHandler
}

public readonly record struct ExecutionStackFrame(
    ProgramTable Program,
    int ReturnLine,
    StackFrameKind Kind = StackFrameKind.Call);

/// <summary>
/// 執行緒即時執行上下文與獨立平行單元 (ThreadVM Execution Context)
/// 維護當前 Program、ProgIndex (PC)、微步 CommandState、呼叫堆疊、Local/Global Pause 狀態與獨立高速 CMDEXECUTE 迴圈
/// </summary>
public sealed class ExecutionContext
{
    private int _progIndex = 1;
    private int _reportedProgIndex = 1;
    private ProgramTable _program;
    private bool _pcModifiedInStep;
    private bool _isLocalPaused;
    private CancellationTokenSource? _loopCts;
    private readonly object _pauseLock = new();
    private long _pausedAccumulatedTicks;
    private long _lastPauseStartTicks;

    public int ThreadId { get; set; }
    public string ThreadName { get; set; }

    public ProgramTable Program
    {
        get => _program;
        set
        {
            if (!ReferenceEquals(_program, value))
            {
                _program = value;
                _pcModifiedInStep = true;
            }
        }
    }

    /// <summary>
    /// 當前單步指令執行期間，是否已由指令主動修改過 PC / Program / 堆疊
    /// </summary>
    public bool IsPcModified => _pcModifiedInStep;

    internal void ResetPcModified() => _pcModifiedInStep = false;

    /// <summary>
    /// 目前指令行號 (PC)
    /// 當 ProgIndex 改變時，自動發送非阻塞原子通知，零等待 CMDEXECUTE 執行緒
    /// </summary>
    public int ProgIndex
    {
        get => _progIndex;
        set
        {
            _pcModifiedInStep = true;
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

    /// <summary>
    /// 當此平行單元執行完畢或停止時觸發
    /// </summary>
    public event Action<ExecutionContext>? OnExecutionStopped;

    public void NotifyProgIndex(int pc)
    {
        Interlocked.Exchange(ref _reportedProgIndex, pc);
        OnProgIndexNoticed?.Invoke(pc);
    }

    public int FailedPC { get; set; } = 1;
    public CommandState State { get; } = new();
    public Stack<ExecutionStackFrame> CallStack { get; } = new();
    public string CurrentSection { get; set; } = "Global";
    public BasicEngines Engines { get; }
    public DriverManager Drivers { get; }
    public ModuleRegistry? Registry { get; set; }
    public ArcosThreadVM? VM { get; set; }
    public bool IsMuted { get; set; }
    public bool IsRunning { get; set; } = true;
    public bool IsBypassPause => ThreadName.EndsWith('&');

    /// <summary>
    /// 單一 Thread 的局部暫停狀態 (Local Pause，由 System@Pause::$Thread 設定)
    /// </summary>
    public bool IsLocalPaused
    {
        get => _isLocalPaused;
        set
        {
            lock (_pauseLock)
            {
                if (_isLocalPaused == value) return;
                bool wasEffective = IsEffectivelyPaused;
                _isLocalPaused = value;
                Engines.Io.SetPort($"{ThreadName}#Pause?", value);
                UpdatePauseTimingInternal(wasEffective, IsEffectivelyPaused);
            }
        }
    }

    /// <summary>
    /// 綜合判定此 Thread 目前是否處於暫停凍結狀態 (Local Pause 或 Global SystemPause)
    /// </summary>
    public bool IsEffectivelyPaused => _isLocalPaused || (!IsBypassPause && Engines.Timer.IsSystemPaused);

    /// <summary>
    /// 當全域 SystemPause 改變時，由 VM 通知此 Thread 同步結算暫停時間差
    /// </summary>
    internal void OnGlobalPauseChanged(bool isGlobalPaused)
    {
        if (IsBypassPause) return;
        lock (_pauseLock)
        {
            bool wasEffective = _isLocalPaused || !isGlobalPaused;
            bool nowEffective = _isLocalPaused || isGlobalPaused;
            UpdatePauseTimingInternal(wasEffective, nowEffective);
        }
    }

    private void UpdatePauseTimingInternal(bool wasPaused, bool nowPaused)
    {
        long now = Stopwatch.GetTimestamp();
        if (!wasPaused && nowPaused)
        {
            _lastPauseStartTicks = now;
        }
        else if (wasPaused && !nowPaused && _lastPauseStartTicks > 0)
        {
            _pausedAccumulatedTicks += (now - _lastPauseStartTicks);
            _lastPauseStartTicks = 0;
        }
    }

    /// <summary>
    /// 取得扣除 Local Pause 與 Global Pause 凍結時間後的有效執行時鐘毫秒數 (供 System@Delay 等使用)
    /// </summary>
    public long GetActiveClockMs()
    {
        lock (_pauseLock)
        {
            long now = Stopwatch.GetTimestamp();
            long totalPaused = _pausedAccumulatedTicks;
            if (IsEffectivelyPaused && _lastPauseStartTicks > 0)
            {
                totalPaused += (now - _lastPauseStartTicks);
            }
            long activeTicks = now - totalPaused;
            return (long)((double)activeTicks * 1000.0 / Stopwatch.Frequency);
        }
    }

    public ExecutionContext(int threadId, string threadName, ProgramTable program, BasicEngines engines, DriverManager drivers)
    {
        ThreadId = threadId;
        ThreadName = threadName;
        _program = program;
        Engines = engines;
        Drivers = drivers;
    }

    /// <summary>
    /// 直接跳躍至指定 1-based 行號 (配合 CommandResult.Done() 回傳即不額外 +1)
    /// </summary>
    public void Jump(int line)
    {
        ProgIndex = line;
    }

    /// <summary>
    /// 依標籤名稱跳躍至當前程式區內的目標行 (找不到標籤時回傳 false)
    /// </summary>
    public bool Jump(string label)
    {
        int targetLine = Program.FindLabel(label);
        if (targetLine <= 0) return false;
        ProgIndex = targetLine;
        return true;
    }

    /// <summary>
    /// 呼叫指定編號程式區作為副程式，將返回點壓入共用 CallStack
    /// </summary>
    public bool Call(int progNo) => Call(progNo.ToString());

    /// <summary>
    /// 呼叫指定程式區或同區段標籤作為副程式，將返回點壓入共用 CallStack (找不到目標回傳 false)
    /// </summary>
    public bool Call(string? targetProgOrLabel)
    {
        if (string.IsNullOrWhiteSpace(targetProgOrLabel))
        {
            return true;
        }

        var targetSubProg = VM?.FindProgram(targetProgOrLabel);
        if (targetSubProg != null && !ReferenceEquals(targetSubProg, Program))
        {
            CallStack.Push(new ExecutionStackFrame(Program, ProgIndex + 1, StackFrameKind.Call));
            Program = targetSubProg;
            ProgIndex = 1;
            return true;
        }

        int labelLine = Program.FindLabel(targetProgOrLabel);
        if (labelLine > 0)
        {
            CallStack.Push(new ExecutionStackFrame(Program, ProgIndex + 1, StackFrameKind.Call));
            ProgIndex = labelLine;
            return true;
        }

        if (targetSubProg != null)
        {
            CallStack.Push(new ExecutionStackFrame(Program, ProgIndex + 1, StackFrameKind.Call));
            Program = targetSubProg;
            ProgIndex = 1;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 自副程式返回：自動清理未重試的 ErrorHandler 幀並彈出 Call 幀；若堆疊已空則停止本執行緒
    /// </summary>
    public void Return()
    {
        while (CallStack.Count > 0 && CallStack.Peek().Kind == StackFrameKind.ErrorHandler)
        {
            CallStack.Pop();
        }

        if (CallStack.Count > 0)
        {
            var frame = CallStack.Pop();
            Program = frame.Program;
            ProgIndex = frame.ReturnLine;
        }
        else
        {
            _pcModifiedInStep = true;
            IsRunning = false;
            Engines.Io.SetPort($"{ThreadName}#Run?", false);
        }
    }

    /// <summary>
    /// ErrorRetry：從共用 CallStack 彈出最近的 ErrorHandler 來源幀並還原至失敗行 + offset 重試
    /// </summary>
    public void Retry(int offset = 0)
    {
        while (CallStack.Count > 0 && CallStack.Peek().Kind != StackFrameKind.ErrorHandler)
        {
            CallStack.Pop();
        }

        if (CallStack.Count > 0)
        {
            var errFrame = CallStack.Pop();
            Program = errFrame.Program;
            ProgIndex = Math.Max(1, errFrame.ReturnLine + offset);
        }
        else
        {
            ProgIndex = Math.Max(1, FailedPC + offset);
        }
    }

    /// <summary>
    /// 啟動此平行單元專屬的獨立背景高速 CMDEXECUTE 迴圈
    /// </summary>
    public void StartBackgroundLoop(CommandExecutor executor)
    {
        StopBackgroundLoop(resetRunningFlag: false);

        IsRunning = true;
        Engines.Io.SetPort($"{ThreadName}#Run?", true);

        var cts = new CancellationTokenSource();
        _loopCts = cts;
        var token = cts.Token;

        Task.Run(async () =>
        {
            int burstCount = 0;
            try
            {
                while (!token.IsCancellationRequested && IsRunning)
                {
                    if (ProgIndex < 1 || (Program != null && ProgIndex > Program.Count && CallStack.Count == 0))
                    {
                        IsRunning = false;
                        break;
                    }

                    // 檢查 Local Pause 或 Global Pause
                    if (IsEffectivelyPaused)
                    {
                        burstCount = 0;
                        await Task.Delay(2, token).ConfigureAwait(false);
                        continue;
                    }

                    int prevPC = ProgIndex;
                    executor.ExecuteStep(this);

                    if (!IsRunning)
                    {
                        break;
                    }

                    // 若處於非阻塞等待狀態 (如 System@Delay, IO@WaitPort 等 PC 尚未推進)，讓出 1ms
                    if (ProgIndex == prevPC)
                    {
                        burstCount = 0;
                        await Task.Delay(1, token).ConfigureAwait(false);
                    }
                    else
                    {
                        // 瞬時指令零延遲全速衝刺
                        if (++burstCount >= 128)
                        {
                            burstCount = 0;
                            Thread.Yield();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (!IsRunning || (Program != null && ProgIndex > Program.Count && CallStack.Count == 0))
                {
                    IsRunning = false;
                    Engines.Io.SetPort($"{ThreadName}#Run?", false);
                    OnExecutionStopped?.Invoke(this);
                }
            }
        }, token);
    }

    /// <summary>
    /// 停止此平行單元的背景高速迴圈
    /// </summary>
    public void StopBackgroundLoop(bool resetRunningFlag = true)
    {
        var oldCts = Interlocked.Exchange(ref _loopCts, null);
        if (oldCts != null)
        {
            try { oldCts.Cancel(); } catch { }
            oldCts.Dispose();
        }

        if (resetRunningFlag)
        {
            IsRunning = false;
            Engines.Io.SetPort($"{ThreadName}#Run?", false);
        }
    }

    public void Log(string message, bool isError = false)
    {
        // 寫入 RecordEngine SYSTEMLOG 通道 (執行緒層錯誤旗標 <ThreadName>#Error? 由 CommandExecutor 統一控管)
        Engines.Record.Record("SYSTEMLOG", $"[Thread {ThreadId}:{ThreadName}] {(isError ? "[ERROR] " : "")}{message}");
    }
}
