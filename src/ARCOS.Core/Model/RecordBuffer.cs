using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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
/// 若配置了 AutoFlushFilePath (如 SYSTEMLOG 綁定當次 Session Log 檔)，滿 2000 筆或 Clear 時自動批次寫入檔案。
/// </summary>
public class RecordChannel
{
    private readonly object _lock = new();
    private readonly RecordEntry[] _entries;
    private int _flushedUpToIndex = 0;

    public string Name { get; }
    public int Capacity { get; }
    public int Index { get; private set; } = 0;
    public long StartTicks { get; set; } = 0;
    public Func<string>? AutoFlushFilePathProvider { get; set; }

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
    /// 環狀寫入打卡/日誌訊息 ($O(1)$ 純記憶體原地賦值，零 UI 事件通知；若配置 AutoFlushFilePathProvider 且滿 2000 筆時自動批次寫入檔案)
    /// </summary>
    public void Add(string message, string? timestamp = null)
    {
        string ts = timestamp ?? DateTime.Now.ToString("HH:mm:ss.fff");
        lock (_lock)
        {
            // 若已存滿 Capacity (2000 筆) 且綁定有 Session Log 檔，先將這 2000 筆批次寫入檔案後再寫入新槽位
            if (AutoFlushFilePathProvider != null && (Index - _flushedUpToIndex) >= Capacity)
            {
                FlushUnwrittenLocked();
            }

            int slot = Index % Capacity;
            Index++;
            _entries[slot].Timestamp = ts;
            _entries[slot].Message = message;
        }
    }

    /// <summary>
    /// 將目前緩衝區內尚未寫入檔案的紀錄批次附加寫入至綁定的 Log 檔案
    /// </summary>
    public void FlushToFile()
    {
        lock (_lock)
        {
            FlushUnwrittenLocked();
        }
    }

    private void FlushUnwrittenLocked()
    {
        if (AutoFlushFilePathProvider == null) return;
        int unwrittenCount = Index - _flushedUpToIndex;
        if (unwrittenCount <= 0) return;

        int count = Math.Min(unwrittenCount, Capacity);
        int startIdx = Index - count;

        try
        {
            string filePath = AutoFlushFilePathProvider();
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var sb = new StringBuilder(count * 64);
                for (int i = 0; i < count; i++)
                {
                    var e = _entries[(startIdx + i) % Capacity];
                    if (!string.IsNullOrEmpty(e.Message))
                    {
                        sb.Append('[').Append(e.Timestamp).Append("] ").AppendLine(e.Message);
                    }
                }
                if (sb.Length > 0)
                {
                    File.AppendAllText(filePath, sb.ToString(), Encoding.UTF8);
                }
            }
        }
        catch
        {
        }

        _flushedUpToIndex = Index;
    }

    /// <summary>
    /// 清除符合條件的槽位 (例如 System@Clear Error 清除 [ERROR] 訊息)
    /// </summary>
    public void ClearMatching(Func<string, bool> predicate)
    {
        lock (_lock)
        {
            if (AutoFlushFilePathProvider != null)
            {
                FlushUnwrittenLocked();
            }

            int count = Math.Min(Index, Capacity);
            int start = Index > Capacity ? Index % Capacity : 0;
            var keep = new List<RecordEntry>(count);
            for (int i = 0; i < count; i++)
            {
                var e = _entries[(start + i) % Capacity];
                if (!string.IsNullOrEmpty(e.Message) && !predicate(e.Message))
                {
                    keep.Add(new RecordEntry(e.Timestamp, e.Message));
                }
            }
            Index = 0;
            for (int i = 0; i < Capacity; i++)
            {
                _entries[i].Timestamp = "00:00:00.000";
                _entries[i].Message = string.Empty;
            }
            foreach (var e in keep)
            {
                int slot = Index % Capacity;
                Index++;
                _entries[slot].Timestamp = e.Timestamp;
                _entries[slot].Message = e.Message;
            }
            _flushedUpToIndex = Index;
        }
    }

    /// <summary>
    /// 清空所有槽位並重設 Index 為 0；若綁定有 Session Log 檔，清空前會先把尚未落盤的訊息寫入該檔
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (AutoFlushFilePathProvider != null)
            {
                FlushUnwrittenLocked();
            }

            Index = 0;
            _flushedUpToIndex = 0;
            StartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < Capacity; i++)
            {
                _entries[i].Timestamp = "00:00:00.000";
                _entries[i].Message = string.Empty;
            }
        }
    }
}
