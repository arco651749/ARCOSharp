using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using ARCOS.Core.Model;

namespace ARCOS.Core.Engines;

/// <summary>
/// 獨立的生產記錄與日誌引擎 (RecordEngine)
/// 職責：
/// 1. 管理所有 2000 槽位環狀通道 (預設包含 SYSTEMLOG 與使用者自訂之 Record 如 cycletime、TIME)
/// 2. 提供 O(1) 極速打卡寫入、清空 (CLEAR) 與週期耗時計算 (GetRecordElapsedSeconds)
/// 3. 與 TimerEngine 徹底解耦，專注於時間戳 + 訊息之履歷管理
/// </summary>
public class RecordEngine
{
    private readonly ConcurrentDictionary<string, RecordChannel> _channels = new(StringComparer.OrdinalIgnoreCase);

    public const int DefaultCapacity = 2000;

    public RecordEngine()
    {
        // 開機預設常駐 SYSTEMLOG 通道
        _channels["SYSTEMLOG"] = new RecordChannel("SYSTEMLOG", DefaultCapacity);
    }

    /// <summary>
    /// 寫入打卡或日誌訊息 (對應 System@Record 或 System@Log Message)
    /// </summary>
    public void Record(string recordName, string message)
    {
        if (string.IsNullOrWhiteSpace(recordName)) return;

        var channel = _channels.GetOrAdd(recordName, name => new RecordChannel(name, DefaultCapacity));

        if (string.Equals(message, "CLEAR", StringComparison.OrdinalIgnoreCase))
        {
            channel.Clear();
            return;
        }

        if (string.Equals(message, "Start", StringComparison.OrdinalIgnoreCase))
        {
            channel.StartTicks = Stopwatch.GetTimestamp();
        }

        string ts = DateTime.Now.ToString("HH:mm:ss.fff");
        channel.Add(message, ts);
        Recorded?.Invoke(recordName, ts, message);
    }

    /// <summary>
    /// 可選觀察者事件 (recordName, timestamp, message)。無訂閱者時零成本；
    /// 僅供 USER TAB 即時 LOG 等明確需要即時顯示的 UI 訂閱，Module Info 仍採 On-Demand Pull。
    /// </summary>
    public event Action<string, string, string>? Recorded;

    /// <summary>
    /// 計算並取得指定 Record 的 Cycle Time 耗時秒數 (對應 System@LogRecordCycleTime)
    /// </summary>
    public double GetRecordElapsedSeconds(string recordName)
    {
        if (string.IsNullOrWhiteSpace(recordName)) return 0.0;
        if (_channels.TryGetValue(recordName, out var channel))
        {
            long nowTicks = Stopwatch.GetTimestamp();
            return (double)(nowTicks - channel.StartTicks) / Stopwatch.Frequency;
        }
        return 0.0;
    }

    /// <summary>
    /// 取得或建立指定的 Record 通道
    /// </summary>
    public RecordChannel GetOrCreateChannel(string recordName, int capacity = DefaultCapacity)
        => _channels.GetOrAdd(recordName, name => new RecordChannel(name, capacity));

    /// <summary>
    /// 取得指定 Record 通道
    /// </summary>
    public RecordChannel? GetChannel(string recordName)
        => _channels.TryGetValue(recordName, out var ch) ? ch : null;

    /// <summary>
    /// 取得所有已註冊的 Record 通道名稱 (SYSTEMLOG 恆為首項)
    /// </summary>
    public IReadOnlyList<string> GetAllRecordNames()
    {
        var list = new List<string> { "SYSTEMLOG" };
        foreach (var key in _channels.Keys)
        {
            if (!string.Equals(key, "SYSTEMLOG", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(key);
            }
        }
        return list;
    }
}
