using System;
using System.Collections.Generic;

namespace ARCOS.App.Services;

public sealed class SequencerClipboardItem
{
    public string Command { get; set; } = "";
    public string Parameter { get; set; } = "";
}

/// <summary>
/// 全域指令剪貼簿與選取狀態管理器 (跨 User Tab 與 Test Thread 共享)
/// 支援：
/// 1. Shift + 滑鼠左鍵兩次選取指令範圍 -> 存入全域選擇區 Buffer
/// 2. Ctrl + 滑鼠左鍵 -> 複製插入到目標行 (跨視窗/同視窗通用)
/// 3. Ctrl + Alt + 滑鼠左鍵 -> 本地剪下並搬移 (同視窗操作)
/// 4. Alt + 滑鼠左鍵 -> 本地刪除選取區 (同視窗操作)
/// </summary>
public sealed class SequencerClipboardManager
{
    private static readonly Lazy<SequencerClipboardManager> _lazy = new(() => new SequencerClipboardManager());
    public static SequencerClipboardManager Instance => _lazy.Value;

    public object? SourceEditor { get; private set; }
    public int? StartLine { get; private set; }
    public int? EndLine { get; private set; }
    public List<SequencerClipboardItem> Buffer { get; } = new();

    public bool HasItems => Buffer.Count > 0;
    public bool HasSelection => SourceEditor != null && StartLine.HasValue && EndLine.HasValue;

    public event Action<object?>? SelectionChanged;

    public void SetSelection(object sourceEditor, int startLine, int endLine, IEnumerable<(string Command, string Parameter)> items)
    {
        SourceEditor = sourceEditor;
        StartLine = Math.Min(startLine, endLine);
        EndLine = Math.Max(startLine, endLine);

        Buffer.Clear();
        foreach (var (cmd, param) in items)
        {
            Buffer.Add(new SequencerClipboardItem
            {
                Command = cmd ?? "",
                Parameter = param ?? ""
            });
        }

        SelectionChanged?.Invoke(sourceEditor);
    }

    public void ClearSelection()
    {
        SourceEditor = null;
        StartLine = null;
        EndLine = null;
        SelectionChanged?.Invoke(null);
    }

    public void ClearAll()
    {
        SourceEditor = null;
        StartLine = null;
        EndLine = null;
        Buffer.Clear();
        SelectionChanged?.Invoke(null);
    }
}
