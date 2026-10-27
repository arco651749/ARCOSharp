using System.Collections.Concurrent;
using System.Diagnostics;

namespace MVCON.Core.Engines;

internal class TimerEntry
{
    public string Name { get; set; } = string.Empty;
    public double TargetSeconds { get; set; }
    public long StartTimestampTicks { get; set; }
    public long PausedAccumulatedTicks { get; set; }
    public long LastPauseStartTicks { get; set; }
    public bool IsBypassPause => Name.EndsWith('&');

    public double GetElapsedSeconds(bool isSystemPausedCurrently)
    {
        long currentTicks = Stopwatch.GetTimestamp();
        long totalPaused = PausedAccumulatedTicks;

        // 若當前處於暫停中且非豁免計時器，則把當前這段暫停時間也加上去
        if (isSystemPausedCurrently && !IsBypassPause && LastPauseStartTicks > 0)
        {
            totalPaused += (currentTicks - LastPauseStartTicks);
        }

        long activeTicks = IsBypassPause ? (currentTicks - StartTimestampTicks) : (currentTicks - StartTimestampTicks - totalPaused);
        if (activeTicks < 0) activeTicks = 0;

        return (double)activeTicks / Stopwatch.Frequency;
    }
}

/// <summary>
/// 名稱索引高精度計時器引擎 (TimerEngine)
/// 支援一般計時器 (受全機暫停影響，暫停期間凍結走時) 與
/// &amp; 特權計時器 (如 Watchdog&amp;，完全不受 SystemPause 影響，持續計時)，
/// 並自動連動 IoEngine 的 $(timerName)#Timeout? 虛擬點位
/// </summary>
public class TimerEngine
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, TimerEntry> _timers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (long StartTicks, List<string> Messages)> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly IoEngine? _ioEngine;
    private readonly CounterEngine _counterEngine;
    private bool _isSystemPaused = false;
    private long _systemPauseStartTicks = 0;

    public bool IsSystemPaused => _isSystemPaused;

    public TimerEngine(IoEngine? ioEngine = null)
    {
        _ioEngine = ioEngine;
        _counterEngine = new CounterEngine(ioEngine);
    }

    /// <summary>
    /// 初始化計數器目標值，並設 $(counterName)#Over? = false
    /// </summary>
    public void CounterReset(string counterName, int targetCount) => _counterEngine.CounterReset(counterName, targetCount);

    /// <summary>
    /// 計數器遞減 1；若歸零 (<= 0) 則自動設 $(counterName)#Over? = true 並回傳 true
    /// </summary>
    public bool CounterCheck(string counterName) => _counterEngine.CounterCheck(counterName);

    /// <summary>
    /// 取得計數器當前數值
    /// </summary>
    public int GetCount(string counterName) => _counterEngine.GetCount(counterName);

    /// <summary>
    /// 記錄自定義訊息至檔案或特定紀錄器 (對應 System@Record)
    /// 若 message 為 "Start" 則標記起跑時間點
    /// </summary>
    public void Record(string recordName, string message)
    {
        if (string.IsNullOrWhiteSpace(recordName)) return;
        long nowTicks = Stopwatch.GetTimestamp();

        var entry = _records.GetOrAdd(recordName, _ => (nowTicks, new List<string>()));
        if (string.Equals(message, "Start", StringComparison.OrdinalIgnoreCase))
        {
            _records[recordName] = (nowTicks, new List<string> { $"Start at {DateTime.Now:HH:mm:ss.fff}" });
        }
        else
        {
            lock (entry.Messages)
            {
                entry.Messages.Add($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            }
        }
    }

    /// <summary>
    /// 計算並取得指定 Record 的 Cycle Time 耗時秒數 (對應 System@LogRecordCycleTime)
    /// </summary>
    public double GetRecordElapsedSeconds(string recordName)
    {
        if (string.IsNullOrWhiteSpace(recordName)) return 0.0;
        if (_records.TryGetValue(recordName, out var entry))
        {
            long nowTicks = Stopwatch.GetTimestamp();
            return (double)(nowTicks - entry.StartTicks) / Stopwatch.Frequency;
        }
        return 0.0;
    }

    /// <summary>
    /// 同步全機暫停狀態 (對應 System@SystemPause 與 System@SystemResume)
    /// </summary>
    public void SetSystemPause(bool isPaused)
    {
        lock (_lock)
        {
            if (_isSystemPaused == isPaused) return;

            long nowTicks = Stopwatch.GetTimestamp();
            _isSystemPaused = isPaused;
            _ioEngine?.SetPort("System#Pause?", isPaused);

            if (isPaused)
            {
                _systemPauseStartTicks = nowTicks;
                // 標記所有普通計時器的暫停起點
                foreach (var timer in _timers.Values)
                {
                    if (!timer.IsBypassPause)
                    {
                        timer.LastPauseStartTicks = nowTicks;
                    }
                }
            }
            else
            {
                // 系統恢復：結算所有普通計時器在這段暫停期間的時間差
                foreach (var timer in _timers.Values)
                {
                    if (!timer.IsBypassPause && timer.LastPauseStartTicks > 0)
                    {
                        timer.PausedAccumulatedTicks += (nowTicks - timer.LastPauseStartTicks);
                        timer.LastPauseStartTicks = 0;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 啟動/重置高精度計時器，並設 $(timerName)#Timeout? = false
    /// </summary>
    public void TimerReset(string timerName, double timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(timerName)) return;

        lock (_lock)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            var entry = _timers.GetOrAdd(timerName, name => new TimerEntry { Name = name });
            entry.TargetSeconds = timeoutSeconds;
            entry.StartTimestampTicks = nowTicks;
            entry.PausedAccumulatedTicks = 0;
            entry.LastPauseStartTicks = (_isSystemPaused && !entry.IsBypassPause) ? nowTicks : 0;

            _ioEngine?.SetPort($"{timerName}#Timeout?", false);
        }
    }

    /// <summary>
    /// 檢查是否逾時；若逾時則自動設 $(timerName)#Timeout? = true 並回傳 true
    /// </summary>
    public bool TimerCheck(string timerName)
    {
        if (_timers.TryGetValue(timerName, out var entry))
        {
            double elapsed = entry.GetElapsedSeconds(_isSystemPaused);
            bool isTimeout = elapsed >= entry.TargetSeconds;

            if (isTimeout)
            {
                _ioEngine?.SetPort($"{timerName}#Timeout?", true);
            }
            return isTimeout;
        }
        return false;
    }

    /// <summary>
    /// 取得已經過之有效毫秒數 (ms)，供 StateIndex 非阻塞等待直接讀取
    /// </summary>
    public long GetElapsedMs(string timerName)
    {
        if (_timers.TryGetValue(timerName, out var entry))
        {
            double elapsedSec = entry.GetElapsedSeconds(_isSystemPaused);
            return (long)(elapsedSec * 1000.0);
        }
        return 0;
    }
}

/// <summary>
/// 名稱索引計數器引擎 (CounterEngine)
/// </summary>
public class CounterEngine
{
    private readonly ConcurrentDictionary<string, int> _counters = new(StringComparer.OrdinalIgnoreCase);
    private readonly IoEngine? _ioEngine;

    public CounterEngine(IoEngine? ioEngine = null)
    {
        _ioEngine = ioEngine;
    }

    /// <summary>
    /// 初始化計數器目標值，並設 $(counterName)#Over? = false
    /// </summary>
    public void CounterReset(string counterName, int targetCount)
    {
        if (string.IsNullOrWhiteSpace(counterName)) return;

        _counters[counterName] = targetCount;
        _ioEngine?.SetPort($"{counterName}#Over?", targetCount <= 0);
    }

    /// <summary>
    /// 計數器遞減 1；若歸零 (<= 0) 則自動設 $(counterName)#Over? = true 並回傳 true
    /// </summary>
    public bool CounterCheck(string counterName)
    {
        if (_counters.TryGetValue(counterName, out int cur))
        {
            int next = cur - 1;
            _counters[counterName] = next;
            bool isOver = next <= 0;
            if (isOver)
            {
                _ioEngine?.SetPort($"{counterName}#Over?", true);
            }
            return isOver;
        }
        return true;
    }

    public int GetCount(string counterName)
    {
        return _counters.TryGetValue(counterName, out int count) ? count : 0;
    }
}
