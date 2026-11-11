using System;
using System.Collections.Generic;
using System.Linq;
using ARCOS.App.ViewModels;

namespace ARCOS.App.Services;

public sealed class SequencerClipboardItem
{
    public string Command { get; set; } = "";
    public string Parameter { get; set; } = "";
}

/// <summary>
/// 任何支援共用選取區與快捷操作的指令編輯器介面 (User Tab、Test Thread Editor 及未來擴充之 Editor)。
/// 選取邏輯由 SequencerClipboardManager 集中管理，各 Editor 僅負責實作自身的視覺顯示更新與資料列同步。
/// </summary>
public interface ISequencerEditorHost
{
    IList<SequencerRowViewModel> EditorRows { get; }
    void RefreshSelectionVisuals();
    void PushUndoSnapshot();
    void SyncUIToProgram();
    void UpdateUndoRedoButtonState();
    void JumpToRow(int lineNumber);
}

/// <summary>
/// 獨立的全域選取區管理中心 (MVCON 風格集中式選取與剪貼狀態機)
/// 統一管理所有指令編輯器 (SequencerEditorControl, TestThreadWindow, 其他 Editor) 的選取範圍與跨編輯器操作：
/// 1. 第一次 Shift + Click：單行選取 (Anchor = Start = End)，載入 Buffer，觸發 Jump，等待可能的第二次 Shift + Click。
/// 2. 第二次 Shift + Click (同 Editor)：擴展為多行範圍選取 [Min, Max]，整段載入 Buffer。
/// 3. Ctrl + Click：將選取區 (單行或範圍) 複製並插入至目標 Editor 的指定行或尾端 (空白處)，完成後立即清空選取區。
/// 4. Ctrl + Alt + Click：將選取區 (單行或範圍) 剪下並搬移至目標位置或尾端 (支援同 Editor 與跨 Editor)，完成後立即清空選取區。
/// 5. Alt + Click：若有選取區則刪除選取區並清空；若無選取區則刪除滑鼠點擊的該行。
/// </summary>
public sealed class SequencerClipboardManager
{
    private static readonly Lazy<SequencerClipboardManager> _lazy = new(() => new SequencerClipboardManager());
    public static SequencerClipboardManager Instance => _lazy.Value;

    public object? SourceEditor { get; private set; }
    public int? AnchorLine { get; private set; }
    public int? StartLine { get; private set; }
    public int? EndLine { get; private set; }
    public bool IsWaitingSecondShift { get; private set; }
    public List<SequencerClipboardItem> Buffer { get; } = new();

    public bool HasItems => Buffer.Count > 0;
    public bool HasSelection => SourceEditor != null && StartLine.HasValue && EndLine.HasValue && Buffer.Count > 0;

    /// <summary>
    /// 當選取範圍改變或清空時觸發，通知各個獨立指令編輯器呼叫自身的 RefreshSelectionVisuals() 更新反白顯示。
    /// </summary>
    public event Action<object?>? SelectionChanged;

    /// <summary>
    /// 供各個獨立的指令編輯器查詢某一行是否應顯示選取反白高亮。
    /// </summary>
    public bool IsRowSelected(object editor, int lineNumber)
    {
        if (!HasSelection || !ReferenceEquals(SourceEditor, editor))
            return false;
        return lineNumber >= StartLine!.Value && lineNumber <= EndLine!.Value;
    }

    public static int GetEffectiveRowCount(IList<SequencerRowViewModel> rows)
    {
        int count = rows.Count;
        while (count > 0 && rows[count - 1].IsTrailingPlaceholder)
        {
            count--;
        }
        return count;
    }

    /// <summary>
    /// 集中處理 Shift + Click 選取狀態機 (第一次單行選取 / 第二次範圍選取)。
    /// </summary>
    public void HandleShiftClick(ISequencerEditorHost editor, int clickedLine)
    {
        var rows = editor.EditorRows;
        int effectiveCount = GetEffectiveRowCount(rows);
        if (effectiveCount <= 0 || clickedLine < 0)
            return;

        if (clickedLine >= effectiveCount)
        {
            // 若在等待第二次 Shift+Click 時點到尾端空白列，自動夾限至最後一行真實指令；若是第一次點擊則忽略
            if (ReferenceEquals(SourceEditor, editor) && IsWaitingSecondShift && AnchorLine.HasValue)
            {
                clickedLine = effectiveCount - 1;
            }
            else
            {
                return;
            }
        }

        if (!ReferenceEquals(SourceEditor, editor) || !IsWaitingSecondShift || !AnchorLine.HasValue)
        {
            // 第一次 Shift + Click：單行選取 + Jump 對齊
            SourceEditor = editor;
            AnchorLine = clickedLine;
            StartLine = clickedLine;
            EndLine = clickedLine;
            IsWaitingSecondShift = true;

            RebuildBufferFromRows(rows, clickedLine, clickedLine);
            editor.JumpToRow(clickedLine);
            NotifyVisualsUpdated();
        }
        else
        {
            // 第二次 Shift + Click (同一個 Editor)：由單行擴展為範圍選取 [start, end]
            int start = Math.Min(AnchorLine.Value, clickedLine);
            int end = Math.Max(AnchorLine.Value, clickedLine);

            SourceEditor = editor;
            StartLine = start;
            EndLine = end;
            IsWaitingSecondShift = false;

            RebuildBufferFromRows(rows, start, end);
            NotifyVisualsUpdated();
        }
    }

    /// <summary>
    /// 程式化直接設定選取區 (相容測試與自動化 API)。
    /// </summary>
    public void SetSelection(object sourceEditor, int startLine, int endLine, IEnumerable<(string Command, string Parameter)> items)
    {
        int start = Math.Min(startLine, endLine);
        int end = Math.Max(startLine, endLine);

        SourceEditor = sourceEditor;
        AnchorLine = start;
        StartLine = start;
        EndLine = end;
        IsWaitingSecondShift = (start == end);

        Buffer.Clear();
        foreach (var (cmd, param) in items)
        {
            Buffer.Add(new SequencerClipboardItem
            {
                Command = cmd ?? "",
                Parameter = param ?? ""
            });
        }

        NotifyVisualsUpdated();
    }

    /// <summary>
    /// 執行 Ctrl + Click 複製並插入 (支援同 Editor 與跨 Editor，targetLine 為 null 時代表點在空白處即插入在程式最尾端)。
    /// 操作完畢後立即清空選取區。
    /// </summary>
    public bool ExecuteCopyInsert(ISequencerEditorHost targetEditor, int? targetLine)
    {
        if (!HasItems)
            return false;

        var rows = targetEditor.EditorRows;
        int effectiveCount = GetEffectiveRowCount(rows);
        targetEditor.PushUndoSnapshot();

        int targetIndex = targetLine ?? effectiveCount;
        if (targetIndex < 0) targetIndex = 0;
        if (targetIndex > effectiveCount) targetIndex = effectiveCount;

        var itemsCopy = Buffer.ToList();
        for (int i = 0; i < itemsCopy.Count; i++)
        {
            int ins = targetIndex + i;
            rows.Insert(ins, new SequencerRowViewModel
            {
                Command = itemsCopy[i].Command,
                Parameter = itemsCopy[i].Parameter,
                LineNumber = ins
            });
        }

        RenumberRows(rows);
        ClearAll();

        targetEditor.SyncUIToProgram();
        targetEditor.UpdateUndoRedoButtonState();
        return true;
    }

    /// <summary>
    /// 執行 Ctrl + Alt + Click 剪下並搬移 (支援同 Editor 搬移與跨 Editor 搬移，targetLine 為 null 時代表搬移至程式最尾端)。
    /// 操作完畢後立即清空選取區。
    /// </summary>
    public bool ExecuteMove(ISequencerEditorHost targetEditor, int? targetLine)
    {
        if (!HasSelection)
            return false;

        int minLine = StartLine!.Value;
        int maxLine = EndLine!.Value;

        // 情況 A：在同一個 Editor 內搬移
        if (ReferenceEquals(SourceEditor, targetEditor))
        {
            var rows = targetEditor.EditorRows;
            int effectiveCount = GetEffectiveRowCount(rows);
            if (minLine < 0 || maxLine >= effectiveCount)
            {
                ClearAll();
                return false;
            }

            int targetIndex = targetLine ?? effectiveCount;
            if (targetIndex > effectiveCount) targetIndex = effectiveCount;
            // 若目標位置就在選取範圍內，無須搬移，但必須清空選取區防呆
            if (targetIndex >= minLine && targetIndex <= maxLine)
            {
                ClearAll();
                return true;
            }

            targetEditor.PushUndoSnapshot();

            var movingRows = new List<SequencerRowViewModel>();
            for (int i = minLine; i <= maxLine; i++)
            {
                movingRows.Add(new SequencerRowViewModel
                {
                    Command = rows[i].Command,
                    Parameter = rows[i].Parameter
                });
            }

            for (int i = maxLine; i >= minLine; i--)
            {
                rows.RemoveAt(i);
            }

            int newEffectiveCount = GetEffectiveRowCount(rows);
            if (targetIndex > maxLine)
            {
                targetIndex -= (maxLine - minLine + 1);
            }
            if (targetIndex < 0) targetIndex = 0;
            if (targetIndex > newEffectiveCount) targetIndex = newEffectiveCount;

            for (int i = 0; i < movingRows.Count; i++)
            {
                rows.Insert(targetIndex + i, movingRows[i]);
            }

            RenumberRows(rows);
            ClearAll();

            targetEditor.SyncUIToProgram();
            targetEditor.UpdateUndoRedoButtonState();
            return true;
        }

        // 情況 B：跨 Editor 剪下並搬移 (例如從 User Tab 剪下搬移到 Test Thread Editor，或反向)
        if (SourceEditor is ISequencerEditorHost sourceHost)
        {
            var srcRows = sourceHost.EditorRows;
            var dstRows = targetEditor.EditorRows;
            int srcEffectiveCount = GetEffectiveRowCount(srcRows);

            if (minLine < 0 || maxLine >= srcEffectiveCount)
            {
                ClearAll();
                return false;
            }

            sourceHost.PushUndoSnapshot();
            targetEditor.PushUndoSnapshot();

            var itemsCopy = Buffer.ToList();

            // 1. 從來源 Editor 移除選取區
            for (int i = maxLine; i >= minLine; i--)
            {
                srcRows.RemoveAt(i);
            }
            RenumberRows(srcRows);
            sourceHost.SyncUIToProgram();
            sourceHost.UpdateUndoRedoButtonState();

            // 2. 插入到目標 Editor 指定行或尾端
            int dstEffectiveCount = GetEffectiveRowCount(dstRows);
            int targetIndex = targetLine ?? dstEffectiveCount;
            if (targetIndex < 0) targetIndex = 0;
            if (targetIndex > dstEffectiveCount) targetIndex = dstEffectiveCount;

            for (int i = 0; i < itemsCopy.Count; i++)
            {
                int ins = targetIndex + i;
                dstRows.Insert(ins, new SequencerRowViewModel
                {
                    Command = itemsCopy[i].Command,
                    Parameter = itemsCopy[i].Parameter,
                    LineNumber = ins
                });
            }
            RenumberRows(dstRows);
            ClearAll();

            targetEditor.SyncUIToProgram();
            targetEditor.UpdateUndoRedoButtonState();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 執行 Alt + Click 刪除：
    /// 1. 若目前有選取區 (單行或範圍)，刪除該選取區並立即清空選取狀態。
    /// 2. 若目前無選取區且點在某一行上，直接刪除該點擊行。
    /// </summary>
    public bool ExecuteDelete(ISequencerEditorHost targetEditor, SequencerRowViewModel? clickedRow)
    {
        if (HasSelection && SourceEditor is ISequencerEditorHost sourceHost)
        {
            var srcRows = sourceHost.EditorRows;
            int srcEffectiveCount = GetEffectiveRowCount(srcRows);
            int minLine = StartLine!.Value;
            int maxLine = EndLine!.Value;

            if (minLine >= 0 && minLine < srcEffectiveCount)
            {
                sourceHost.PushUndoSnapshot();
                int end = Math.Min(maxLine, srcEffectiveCount - 1);
                for (int i = end; i >= minLine; i--)
                {
                    srcRows.RemoveAt(i);
                }

                RenumberRows(srcRows);
                ClearAll();

                sourceHost.SyncUIToProgram();
                sourceHost.UpdateUndoRedoButtonState();
                return true;
            }
        }

        var rows = targetEditor.EditorRows;
        if (clickedRow != null && !clickedRow.IsTrailingPlaceholder && rows.Contains(clickedRow))
        {
            targetEditor.PushUndoSnapshot();
            rows.Remove(clickedRow);
            RenumberRows(rows);
            ClearAll();

            targetEditor.SyncUIToProgram();
            targetEditor.UpdateUndoRedoButtonState();
            return true;
        }

        return false;
    }

    public void ClearSelection()
    {
        ClearAll();
    }

    public void ClearAll()
    {
        bool hadAny = SourceEditor != null || AnchorLine != null || StartLine != null || EndLine != null || Buffer.Count > 0;
        SourceEditor = null;
        AnchorLine = null;
        StartLine = null;
        EndLine = null;
        IsWaitingSecondShift = false;
        Buffer.Clear();

        if (hadAny)
        {
            NotifyVisualsUpdated();
        }
        else
        {
            SelectionChanged?.Invoke(null);
        }
    }

    private void RebuildBufferFromRows(IList<SequencerRowViewModel> rows, int start, int end)
    {
        Buffer.Clear();
        for (int i = start; i <= end && i < rows.Count; i++)
        {
            if (i < 0) continue;
            Buffer.Add(new SequencerClipboardItem
            {
                Command = rows[i].Command ?? "",
                Parameter = rows[i].Parameter ?? ""
            });
        }
    }

    private static void RenumberRows(IList<SequencerRowViewModel> rows)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].LineNumber = i;
        }
    }

    private void NotifyVisualsUpdated()
    {
        SelectionChanged?.Invoke(SourceEditor);
    }
}
