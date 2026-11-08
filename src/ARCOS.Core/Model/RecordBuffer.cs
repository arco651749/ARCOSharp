using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ARCOS.Core.Model;

/// <summary>
/// 單筆打卡/日誌槽位項目 (實作 INotifyPropertyChanged 達成 UI 局部高效率刷屏，零全表重排)
/// </summary>
public class RecordEntry : INotifyPropertyChanged
{
    private string _timestamp = "00:00:00.000";
    private string _message = string.Empty;

    public string Timestamp
    {
        get => _timestamp;
        set
        {
            if (_timestamp != value)
            {
                _timestamp = value;
                OnPropertyChanged();
            }
        }
    }

    public string Message
    {
        get => _message;
        set
        {
            if (_message != value)
            {
                _message = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    public RecordEntry() { }

    public RecordEntry(string timestamp, string message)
    {
        _timestamp = timestamp;
        _message = message;
    }
}

/// <summary>
/// 固定容量環狀通道 (預設 2000 槽位預先分配，O(1) 寫入無動態擴容 GC 負擔)
/// </summary>
public class RecordChannel
{
    private readonly object _lock = new();
    public string Name { get; }
    public int Capacity { get; }
    public int Index { get; private set; } = 0;
    public long StartTicks { get; set; } = 0;

    /// <summary>
    /// 固定預先配置之槽位清單 (長度恆為 Capacity = 2000)
    /// </summary>
    public ObservableCollection<RecordEntry> Entries { get; }

    public event Action? RecordChanged;

    public RecordChannel(string name, int capacity = 2000)
    {
        Name = name;
        Capacity = capacity > 0 ? capacity : 2000;
        Entries = new ObservableCollection<RecordEntry>();
        for (int i = 0; i < Capacity; i++)
        {
            Entries.Add(new RecordEntry("00:00:00.000", string.Empty));
        }
        StartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// 環狀寫入打卡/日誌訊息 ($O(1)$)
    /// </summary>
    public void Add(string message, string? timestamp = null)
    {
        string ts = timestamp ?? DateTime.Now.ToString("HH:mm:ss.fff");
        int slot;
        lock (_lock)
        {
            slot = Index % Capacity;
            Index++;
        }

        // 直接原地賦值，不觸發集合的重新配置與 Layout 重新計算
        var entry = Entries[slot];
        entry.Timestamp = ts;
        entry.Message = message;

        RecordChanged?.Invoke();
    }

    /// <summary>
    /// 清空所有槽位並重設 Index 為 0 (對應 System@Record $Record,CLEAR 或 Clear Log)
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            Index = 0;
            StartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < Capacity; i++)
            {
                Entries[i].Timestamp = "00:00:00.000";
                Entries[i].Message = string.Empty;
            }
        }
        RecordChanged?.Invoke();
    }
}
