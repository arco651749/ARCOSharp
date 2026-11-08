using System;
using System.Collections.Generic;

namespace ARCOS.Core.Model;

/// <summary>
/// 單筆打卡/日誌槽位項目 (純 POCO 結構，零 PropertyChanged 事件開銷，保障高速執行吞吐量)
/// </summary>
public class RecordEntry
{
    public string Timestamp { get; set; } = "00:00:00.000";
    public string Message { get; set; } = string.Empty;

    public RecordEntry() { }

    public RecordEntry(string timestamp, string message)
    {
        Timestamp = timestamp;
        Message = message;
    }
}

/// <summary>
/// 固定容量環狀通道 (預設 2000 槽位預先分配，O(1) 純記憶體寫入，絕不主動通知 UI)
/// 遵循 ARCOS 最高架構準則：
/// 自動化只保證內部資料正確，UI 僅在使用者主動點擊 [Get RecordS] 或選取清單時才進行按需拉取 (On-Demand Pull)。
/// </summary>
public class RecordChannel
{
    private readonly object _lock = new();
    private readonly RecordEntry[] _entries;

    public string Name { get; }
    public int Capacity { get; }
    public int Index { get; private set; } = 0;
    public long StartTicks { get; set; } = 0;

    /// <summary>
    /// 固定預先配置之槽位清單 (長度恆為 Capacity = 2000，實作 IReadOnlyList 支援索引與 Count)
    /// </summary>
    public IReadOnlyList<RecordEntry> Entries => _entries;

    public RecordChannel(string name, int capacity = 2000)
    {
        Name = name;
        Capacity = capacity > 0 ? capacity : 2000;
        _entries = new RecordEntry[Capacity];
        for (int i = 0; i < Capacity; i++)
        {
            _entries[i] = new RecordEntry("00:00:00.000", string.Empty);
        }
        StartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// 環狀寫入打卡/日誌訊息 ($O(1)$ 純記憶體原地賦值，零 UI 事件通知)
    /// </summary>
    public void Add(string message, string? timestamp = null)
    {
        string ts = timestamp ?? DateTime.Now.ToString("HH:mm:ss.fff");
        lock (_lock)
        {
            int slot = Index % Capacity;
            Index++;
            _entries[slot].Timestamp = ts;
            _entries[slot].Message = message;
        }
    }

    /// <summary>
    /// 清空所有槽位並重設 Index 為 0 (對應 System@Record $Record,CLEAR 或 Clear 按鈕)
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            Index = 0;
            StartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < Capacity; i++)
            {
                _entries[i].Timestamp = "00:00:00.000";
                _entries[i].Message = string.Empty;
            }
        }
    }
}
