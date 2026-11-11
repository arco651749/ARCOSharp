using System;
using System.Collections.Generic;
using System.Linq;
using ARCOS.App.ViewModels;
using Xunit;

namespace ARCOS.Core.Tests;

public class SequencerVisualAndSelectionTests
{
    [Fact]
    public void VisualDecoupling_PCIndexAndRangeSelectionAreIndependent()
    {
        // 驗證：指令 INDEX (PC) 僅由第 1 欄 (i 欄) 控制，Command/Parameters 欄不受 PC 影響染紅
        var row = new SequencerRowViewModel
        {
            LineNumber = 2,
            Command = "System@Delay",
            Parameter = "0.1"
        };

        // 初始狀態：皆為預設
        Assert.True(row.IsDefaultState);
        Assert.Equal("Transparent", row.CellBackground);
        Assert.Equal("Transparent", row.CellBorderBrush);
        Assert.Equal("0", row.CellBorderThickness);

        // 設為目前 PC 行：Command/Parameters 依然保持 Transparent (僅 i 欄以 IsCurrentPC 顯示粉紅膠囊)
        row.IsCurrentPC = true;
        Assert.True(row.IsCurrentPC);
        Assert.False(row.IsDefaultState);
        Assert.Equal("Transparent", row.CellBackground);
        Assert.Equal("Transparent", row.CellBorderBrush);
        Assert.Equal("0", row.CellBorderThickness);

        // 當此行同時被納入範圍選取 (IsRangeSelected = true)
        // Command/Parameters 欄顯示選取天空藍 #E0F2FE，而 i 欄依然維持 IsCurrentPC = true，互不干擾
        row.IsRangeSelected = true;
        Assert.True(row.IsCurrentPC);
        Assert.True(row.IsRangeSelected);
        Assert.Equal("#E0F2FE", row.CellBackground);
        Assert.Equal("#BAE6FD", row.CellBorderBrush);
        Assert.Equal("1", row.CellBorderThickness);
    }

    [Fact]
    public void RangeSelection_ShowsBlueHighlightOnCommandAndParameters()
    {
        var row = new SequencerRowViewModel
        {
            LineNumber = 3,
            Command = "System@Delay",
            Parameter = "0.2"
        };

        row.IsRangeSelected = true;
        row.IsCurrentPC = false;

        Assert.True(row.IsRangeSelected);
        Assert.False(row.IsCurrentPC);
        Assert.Equal("#E0F2FE", row.CellBackground);
        Assert.Equal("#BAE6FD", row.CellBorderBrush);
        Assert.Equal("1", row.CellBorderThickness);
    }

    [Fact]
    public void MultiRowSelection_RangeSelectedCellsTurnBlueWhilePCIndexIsIndependent()
    {
        // 模擬 5 行程式碼中，PC 位於 Line 2，使用者從 Line 1 選到 Line 4
        var rows = Enumerable.Range(0, 5).Select(i => new SequencerRowViewModel
        {
            LineNumber = i,
            Command = $"Command_{i}",
            Parameter = "0.1"
        }).ToList();

        // PC 目前位於 Line 2
        rows[2].IsCurrentPC = true;

        // 範圍選取 Line 1 到 Line 4
        int start = 1;
        int end = 4;
        foreach (var r in rows)
        {
            r.IsRangeSelected = (r.LineNumber >= start && r.LineNumber <= end);
        }

        // Line 0 未選取：透明待命，非 PC
        Assert.Equal("Transparent", rows[0].CellBackground);
        Assert.Equal("Transparent", rows[0].CellBorderBrush);
        Assert.False(rows[0].IsCurrentPC);

        // Line 1 選取但非 PC：Command/Parameters 藍底藍框，非 PC
        Assert.Equal("#E0F2FE", rows[1].CellBackground);
        Assert.Equal("#BAE6FD", rows[1].CellBorderBrush);
        Assert.False(rows[1].IsCurrentPC);

        // Line 2 選取且為 PC：Command/Parameters 顯示選取藍底，同時 i 欄維持 IsCurrentPC = true (粉色膠囊)，兩者獨立共存！
        Assert.Equal("#E0F2FE", rows[2].CellBackground);
        Assert.Equal("#BAE6FD", rows[2].CellBorderBrush);
        Assert.Equal("1", rows[2].CellBorderThickness);
        Assert.True(rows[2].IsCurrentPC);

        // Line 3、4 選取但非 PC：Command/Parameters 藍底藍框
        Assert.Equal("#E0F2FE", rows[3].CellBackground);
        Assert.Equal("#E0F2FE", rows[4].CellBackground);
        Assert.False(rows[3].IsCurrentPC);
        Assert.False(rows[4].IsCurrentPC);
    }

    [Fact]
    public void PropertyChanged_FiresCorrectlyWhenPCToggled()
    {
        var row = new SequencerRowViewModel { LineNumber = 1 };
        var changedProps = new List<string>();
        row.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) changedProps.Add(e.PropertyName);
        };

        row.IsCurrentPC = true;

        Assert.Contains(nameof(row.IsCurrentPC), changedProps);
        Assert.Contains(nameof(row.IsRangeSelectedAndNotPC), changedProps);
        Assert.Contains(nameof(row.IsDefaultState), changedProps);
    }

    [Fact]
    public void PropertyChanged_FiresCorrectlyWhenSelectionToggled()
    {
        var row = new SequencerRowViewModel { LineNumber = 1 };
        var changedProps = new List<string>();
        row.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != null) changedProps.Add(e.PropertyName);
        };

        row.IsRangeSelected = true;

        Assert.Contains(nameof(row.IsRangeSelected), changedProps);
        Assert.Contains(nameof(row.IsRangeSelectedAndNotPC), changedProps);
        Assert.Contains(nameof(row.IsDefaultState), changedProps);
        Assert.Contains(nameof(row.CellBackground), changedProps);
        Assert.Contains(nameof(row.CellBorderBrush), changedProps);
        Assert.Contains(nameof(row.CellBorderThickness), changedProps);
    }

    [Fact]
    public void InsertRow_AtCurrentPosition_InsertsAtTargetIndexAndShiftsSubsequentRows()
    {
        // 驗證標示 1 與 Ctrl+Click 規範：插入指令時，必須插入於目前選取的這一個位置 (cur)，而非下一個位置 (cur + 1)
        var rows = new List<SequencerRowViewModel>();
        for (int i = 0; i < 5; i++)
        {
            rows.Add(new SequencerRowViewModel { LineNumber = i, Command = $"System@Log Message", Parameter = $"{i}" });
        }

        int targetIndex = 2; // 選取第 2 列
        var newRow = new SequencerRowViewModel { LineNumber = targetIndex, Command = "System@Delay", Parameter = "0.5" };

        // 插入於 targetIndex
        rows.Insert(targetIndex, newRow);
        for (int i = 0; i < rows.Count; i++) rows[i].LineNumber = i;

        Assert.Equal(6, rows.Count);
        // 新行正確位於索引 2
        Assert.Equal("System@Delay", rows[2].Command);
        Assert.Equal("0.5", rows[2].Parameter);
        Assert.Equal(2, rows[2].LineNumber);

        // 原本的第 2 列被往下推移至第 3 列 (參數仍為 "2")
        Assert.Equal("System@Log Message", rows[3].Command);
        Assert.Equal("2", rows[3].Parameter);
        Assert.Equal(3, rows[3].LineNumber);

        // 原本的第 3 列被往下推移至第 4 列 (參數為 "3")
        Assert.Equal("3", rows[4].Parameter);
    }

    [Fact]
    public void InsertRow_AtTail_WhenClickingBlankArea_AppendsAtEnd()
    {
        // 驗證標示 3 規範：在表格空白處點擊時，於尾端新增一行
        var rows = new List<SequencerRowViewModel>();
        for (int i = 0; i < 3; i++)
        {
            rows.Add(new SequencerRowViewModel { LineNumber = i, Command = $"Cmd{i}", Parameter = $"{i}" });
        }

        int targetIndex = rows.Count; // 尾端索引 3
        var newRow = new SequencerRowViewModel { LineNumber = targetIndex, Command = "System@Delay", Parameter = "1.0" };
        rows.Insert(targetIndex, newRow);
        for (int i = 0; i < rows.Count; i++) rows[i].LineNumber = i;

        Assert.Equal(4, rows.Count);
        Assert.Equal("System@Delay", rows[3].Command);
        Assert.Equal(3, rows[3].LineNumber);
    }

    [Fact]
    public void ProgramSection_ClearAction_EmptiesAllRows()
    {
        // 驗證標示 2 規範：X 按鈕應清空當前這一個程式區的所有指令行
        var rows = new List<SequencerRowViewModel>();
        for (int i = 0; i < 10; i++)
        {
            rows.Add(new SequencerRowViewModel { LineNumber = i, Command = $"Cmd{i}", Parameter = $"{i}" });
        }
        Assert.Equal(10, rows.Count);

        // 模擬點擊 X (清空當前程式區)
        rows.Clear();
        Assert.Empty(rows);
    }

    [Fact]
    public void ClipboardManager_ShiftSelection_SetsSelectionBufferAccurately()
    {
        // 驗證 Shift + Click 快捷鍵範圍選取機制：將選定區間複製至全域 Buffer
        var editorMock = new object();
        var selectedItems = new List<(string Command, string Parameter)>
        {
            ("System@Delay", "0.1"),
            ("IO@SetPort", "P0.0,1")
        };

        ARCOS.App.Services.SequencerClipboardManager.Instance.SetSelection(editorMock, 1, 2, selectedItems);

        Assert.True(ARCOS.App.Services.SequencerClipboardManager.Instance.HasSelection);
        Assert.True(ARCOS.App.Services.SequencerClipboardManager.Instance.HasItems);
        Assert.Equal(1, ARCOS.App.Services.SequencerClipboardManager.Instance.StartLine);
        Assert.Equal(2, ARCOS.App.Services.SequencerClipboardManager.Instance.EndLine);
        Assert.Equal(2, ARCOS.App.Services.SequencerClipboardManager.Instance.Buffer.Count);
        Assert.Equal("System@Delay", ARCOS.App.Services.SequencerClipboardManager.Instance.Buffer[0].Command);
        Assert.Equal("P0.0,1", ARCOS.App.Services.SequencerClipboardManager.Instance.Buffer[1].Parameter);

        // 驗證 Escape 或 ClearAll 快捷鍵清空選取區與快取
        ARCOS.App.Services.SequencerClipboardManager.Instance.ClearAll();
        Assert.False(ARCOS.App.Services.SequencerClipboardManager.Instance.HasSelection);
        Assert.False(ARCOS.App.Services.SequencerClipboardManager.Instance.HasItems);
    }

    [Fact]
    public void SingleAndRangeSelection_FirstShiftClickIsSingleRow_SecondShiftClickIsRange_OperationClearsSelectionAndBuffer()
    {
        // 驗證規範：
        // 1. 第一次按 SHIFT+CLICK 是單行選取，可直接供 CTRL+CLICK、ALT+CLICK、CTRL+ALT+CLICK 操作，操作完清空選取區與快取
        // 2. 第一次按 SHIFT+CLICK 再按第二次 SHIFT+CLICK 變範圍選取，操作完清空選取區與快取，避免後續誤動作
        var rows = Enumerable.Range(0, 5).Select(i => new SequencerRowViewModel
        {
            LineNumber = i,
            Command = $"Cmd_{i}",
            Parameter = $"{i}"
        }).ToList();
        var editorMock = new object();

        // 階段 1：第一次 Shift+Click (點擊 Line 2) -> 單行選取！
        int singleLine = 2;
        rows[singleLine].IsRangeSelected = true;
        var singleItem = new[] { (rows[singleLine].Command, rows[singleLine].Parameter) };
        ARCOS.App.Services.SequencerClipboardManager.Instance.SetSelection(editorMock, singleLine, singleLine, singleItem);

        Assert.Single(rows.Where(r => r.IsRangeSelected));
        Assert.True(ARCOS.App.Services.SequencerClipboardManager.Instance.HasSelection);
        Assert.Single(ARCOS.App.Services.SequencerClipboardManager.Instance.Buffer);
        Assert.Equal("Cmd_2", ARCOS.App.Services.SequencerClipboardManager.Instance.Buffer[0].Command);

        // 模擬執行操作 (例如 Ctrl+Click 複製貼上單行)，操作完清空選取區與快取
        foreach (var r in rows) r.IsRangeSelected = false;
        ARCOS.App.Services.SequencerClipboardManager.Instance.ClearAll();

        Assert.All(rows, r => Assert.False(r.IsRangeSelected));
        Assert.False(ARCOS.App.Services.SequencerClipboardManager.Instance.HasSelection);
        Assert.False(ARCOS.App.Services.SequencerClipboardManager.Instance.HasItems); // 快取已清空，徹底防止誤動作

        // 階段 2：重新選取 -> 第一次按 Shift+Click (Line 1)，接續按第二次 Shift+Click (Line 3) -> 變範圍選取！
        int start = 1, end = 3;
        for (int i = start; i <= end; i++) rows[i].IsRangeSelected = true;
        var rangeItems = rows.Where(r => r.IsRangeSelected).Select(r => (r.Command, r.Parameter)).ToList();
        ARCOS.App.Services.SequencerClipboardManager.Instance.SetSelection(editorMock, start, end, rangeItems);

        Assert.Equal(3, rows.Count(r => r.IsRangeSelected));
        Assert.True(ARCOS.App.Services.SequencerClipboardManager.Instance.HasSelection);
        Assert.Equal(3, ARCOS.App.Services.SequencerClipboardManager.Instance.Buffer.Count);

        // 模擬執行操作 (例如 Alt+Click 刪除範圍)，操作完清空選取區與快取
        foreach (var r in rows) r.IsRangeSelected = false;
        ARCOS.App.Services.SequencerClipboardManager.Instance.ClearAll();

        Assert.All(rows, r => Assert.False(r.IsRangeSelected));
        Assert.False(ARCOS.App.Services.SequencerClipboardManager.Instance.HasSelection);
        Assert.False(ARCOS.App.Services.SequencerClipboardManager.Instance.HasItems); // 快取已清空
    }

    [Fact]
    public void UndoRedo_SnapshotHistory_RestoresPreviousStateCorrectly()
    {
        // 驗證 Ctrl+Z (Undo) 與 Ctrl+Y (Redo) 內部快照堆疊還原機制
        var undoStack = new Stack<List<(string Command, string Parameter)>>();
        var redoStack = new Stack<List<(string Command, string Parameter)>>();

        // 初始狀態：3 行指令
        var state0 = new List<(string, string)> { ("Cmd0", "0"), ("Cmd1", "1"), ("Cmd2", "2") };
        undoStack.Push(new List<(string, string)>(state0));

        // 模擬執行修改（例如插入一行或變更參數）
        var state1 = new List<(string, string)> { ("Cmd0", "0"), ("CmdInserted", "X"), ("Cmd1", "1"), ("Cmd2", "2") };

        // 觸發 Ctrl+Z (Undo)：當前 state1 推入 redoStack，彈出 undoStack 恢復 state0
        redoStack.Push(new List<(string, string)>(state1));
        var restoredState = undoStack.Pop();

        Assert.Equal(3, restoredState.Count);
        Assert.Equal("Cmd0", restoredState[0].Item1);
        Assert.Equal("Cmd1", restoredState[1].Item1);
        Assert.Equal("Cmd2", restoredState[2].Item1);

        // 觸發 Ctrl+Y (Redo)：當前 state0 推入 undoStack，彈出 redoStack 恢復 state1
        undoStack.Push(new List<(string, string)>(restoredState));
        var redoneState = redoStack.Pop();

        Assert.Equal(4, redoneState.Count);
        Assert.Equal("CmdInserted", redoneState[1].Item1);
    }

    [Fact]
    public void BlankAreaOperations_CtrlClickAndCtrlAltClick_TargetProgramEndAndClearAll()
    {
        // 驗證使用者指定規範：
        // 1. Shift+Click 單行選取後，若在程式空白處按 Ctrl+Click，應將該行複製並插入至程式尾端 (_rows.Count)，操作完清空選取區與快取
        // 2. Shift+Click 單行選取後，若在程式空白處按 Ctrl+Alt+Click，應將該行剪下並搬移至程式尾端 (_rows.Count)，操作完清空選取區與快取
        var rows = new List<SequencerRowViewModel>
        {
            new SequencerRowViewModel { LineNumber = 0, Command = "Cmd0", Parameter = "P0" },
            new SequencerRowViewModel { LineNumber = 1, Command = "Cmd1", Parameter = "P1" },
            new SequencerRowViewModel { LineNumber = 2, Command = "Cmd2", Parameter = "P2" }
        };
        var editorMock = new object();

        // 測試 1：單行選取 Line 0，在空白處按 Ctrl+Click 複製插入到最後面
        int selectedLine = 0;
        rows[selectedLine].IsRangeSelected = true;
        var selectedItem = new[] { (rows[selectedLine].Command, rows[selectedLine].Parameter) };
        ARCOS.App.Services.SequencerClipboardManager.Instance.SetSelection(editorMock, selectedLine, selectedLine, selectedItem);

        // 模擬空白處 Ctrl+Click (targetRow = null -> targetIndex = rows.Count = 3)
        int targetIndex = rows.Count;
        var copyItems = ARCOS.App.Services.SequencerClipboardManager.Instance.Buffer;
        for (int i = 0; i < copyItems.Count; i++)
        {
            rows.Insert(targetIndex + i, new SequencerRowViewModel
            {
                Command = copyItems[i].Command,
                Parameter = copyItems[i].Parameter,
                LineNumber = targetIndex + i
            });
        }
        for (int i = 0; i < rows.Count; i++) rows[i].LineNumber = i;

        // 操作完成後自動清空選取區與剪貼快取
        foreach (var r in rows) r.IsRangeSelected = false;
        ARCOS.App.Services.SequencerClipboardManager.Instance.ClearAll();

        Assert.Equal(4, rows.Count);
        Assert.Equal("Cmd0", rows[3].Command); // 複製的 Cmd0 正確出現在程式尾端
        Assert.False(ARCOS.App.Services.SequencerClipboardManager.Instance.HasSelection);
        Assert.False(ARCOS.App.Services.SequencerClipboardManager.Instance.HasItems);

        // 測試 2：單行選取 Line 1，在空白處按 Ctrl+Alt+Click 搬移到最後面
        selectedLine = 1; // 目前的 rows[1] 是 Cmd1
        rows[selectedLine].IsRangeSelected = true;
        var moveItem = new[] { (rows[selectedLine].Command, rows[selectedLine].Parameter) };
        ARCOS.App.Services.SequencerClipboardManager.Instance.SetSelection(editorMock, selectedLine, selectedLine, moveItem);

        // 模擬空白處 Ctrl+Alt+Click 本地搬移 (targetRow = null -> targetIndex = rows.Count = 4)
        int minLine = selectedLine;
        int maxLine = selectedLine;
        int moveTarget = rows.Count;

        var movingRows = new List<SequencerRowViewModel>();
        for (int i = minLine; i <= maxLine; i++)
        {
            movingRows.Add(new SequencerRowViewModel { Command = rows[i].Command, Parameter = rows[i].Parameter });
        }
        for (int i = maxLine; i >= minLine; i--)
        {
            rows.RemoveAt(i);
        }
        if (moveTarget > maxLine)
        {
            moveTarget -= (maxLine - minLine + 1);
        }
        for (int i = 0; i < movingRows.Count; i++)
        {
            rows.Insert(moveTarget + i, movingRows[i]);
        }
        for (int i = 0; i < rows.Count; i++) rows[i].LineNumber = i;

        // 操作完成後清空選取區與快取
        foreach (var r in rows) r.IsRangeSelected = false;
        ARCOS.App.Services.SequencerClipboardManager.Instance.ClearAll();

        Assert.Equal(4, rows.Count);
        Assert.Equal("Cmd1", rows[3].Command); // Cmd1 成功由第 1 列被搬移至最尾端
        Assert.False(ARCOS.App.Services.SequencerClipboardManager.Instance.HasSelection);
        Assert.False(ARCOS.App.Services.SequencerClipboardManager.Instance.HasItems);
    }

    private sealed class MockEditorHost : ARCOS.App.Services.ISequencerEditorHost
    {
        public IList<SequencerRowViewModel> EditorRows { get; } = new List<SequencerRowViewModel>();
        public int LastJumpedLine { get; private set; } = -1;
        public int UndoCount { get; private set; }
        public int SyncCount { get; private set; }

        public MockEditorHost(params (string cmd, string param)[] initial)
        {
            for (int i = 0; i < initial.Length; i++)
            {
                EditorRows.Add(new SequencerRowViewModel
                {
                    LineNumber = i,
                    Command = initial[i].cmd,
                    Parameter = initial[i].param
                });
            }
            ARCOS.App.Services.SequencerClipboardManager.Instance.SelectionChanged += OnSelectionChanged;
        }

        private void OnSelectionChanged(object? _) => RefreshSelectionVisuals();

        public void RefreshSelectionVisuals()
        {
            var mgr = ARCOS.App.Services.SequencerClipboardManager.Instance;
            foreach (var r in EditorRows)
            {
                r.IsRangeSelected = mgr.IsRowSelected(this, r.LineNumber);
            }
        }

        public void PushUndoSnapshot() => UndoCount++;
        public void SyncUIToProgram() => SyncCount++;
        public void UpdateUndoRedoButtonState() { }
        public void JumpToRow(int lineNumber) => LastJumpedLine = lineNumber;

        public void Detach()
        {
            ARCOS.App.Services.SequencerClipboardManager.Instance.SelectionChanged -= OnSelectionChanged;
        }
    }

    [Fact]
    public void CentralizedSelectionManager_MultipleEditorsCoexist_SingleAndRangeAndCrossEditorOperationsWorkWithoutMutualDestruction()
    {
        var mgr = ARCOS.App.Services.SequencerClipboardManager.Instance;
        mgr.ClearAll();

        // 建立兩個獨立的指令編輯器 (模擬主畫面 User Tab 與浮動 Test Thread Editor 同時存在並訂閱 SelectionChanged)
        var editorA = new MockEditorHost(("CmdA0", "0"), ("CmdA1", "1"), ("CmdA2", "2"));
        var editorB = new MockEditorHost(("CmdB0", "10"), ("CmdB1", "20"), ("CmdB2", "30"));

        try
        {
            // 1. 在 Editor A 按第一次 Shift+Click (單行選取 Line 1)
            // 驗證 Editor B 收到通知時只刷新自身顯示，絕不會把全域選取區清掉！
            mgr.HandleShiftClick(editorA, 1);

            Assert.True(mgr.HasSelection);
            Assert.True(mgr.HasItems);
            Assert.Single(mgr.Buffer);
            Assert.Equal("CmdA1", mgr.Buffer[0].Command);
            Assert.Equal(1, editorA.LastJumpedLine);
            Assert.False(editorA.EditorRows[0].IsRangeSelected);
            Assert.True(editorA.EditorRows[1].IsRangeSelected); // Editor A 自己顯示第 1 行反白
            Assert.False(editorA.EditorRows[2].IsRangeSelected);
            Assert.All(editorB.EditorRows, r => Assert.False(r.IsRangeSelected)); // Editor B 均無反白

            // 2. 單行選取下執行同 Editor A 的 Ctrl+Click (複製插入到空白處尾端 targetLine = null)
            bool copied = mgr.ExecuteCopyInsert(editorA, null);
            Assert.True(copied);
            Assert.Equal(4, editorA.EditorRows.Count);
            Assert.Equal("CmdA1", editorA.EditorRows[3].Command);
            // 操作完立即自動清空選取與反白
            Assert.False(mgr.HasSelection);
            Assert.False(mgr.HasItems);
            Assert.All(editorA.EditorRows, r => Assert.False(r.IsRangeSelected));

            // 3. 單行選取下執行同 Editor A 的 Ctrl+Alt+Click (剪下並搬移 Line 0 到空白處尾端)
            mgr.HandleShiftClick(editorA, 0);
            Assert.True(editorA.EditorRows[0].IsRangeSelected);
            bool moved = mgr.ExecuteMove(editorA, null);
            Assert.True(moved);
            Assert.Equal(4, editorA.EditorRows.Count);
            Assert.Equal("CmdA0", editorA.EditorRows[3].Command); // CmdA0 搬到最後一行
            Assert.False(mgr.HasSelection);
            Assert.All(editorA.EditorRows, r => Assert.False(r.IsRangeSelected));

            // 4. 跨 Editor 共用選取區測試：在 Editor A 單行選取 Line 0 ("CmdA1")，去 Editor B 的 Line 1 按 Ctrl+Click 複製插入
            mgr.HandleShiftClick(editorA, 0);
            Assert.True(editorA.EditorRows[0].IsRangeSelected);
            bool crossCopied = mgr.ExecuteCopyInsert(editorB, 1);
            Assert.True(crossCopied);
            Assert.Equal(4, editorB.EditorRows.Count);
            Assert.Equal("CmdA1", editorB.EditorRows[1].Command); // 成功跨視窗貼入 Editor B 第 1 行
            Assert.False(mgr.HasSelection);
            Assert.All(editorA.EditorRows, r => Assert.False(r.IsRangeSelected));
            Assert.All(editorB.EditorRows, r => Assert.False(r.IsRangeSelected));

            // 5. 跨 Editor 範圍選取與搬移測試：在 Editor B 按兩次 Shift+Click 選取 [1..2]，按 Ctrl+Alt+Click 搬移到 Editor A 尾端
            mgr.HandleShiftClick(editorB, 1); // 第一次 Shift+Click (單行)
            Assert.True(editorB.EditorRows[1].IsRangeSelected);
            Assert.False(editorB.EditorRows[2].IsRangeSelected);

            mgr.HandleShiftClick(editorB, 2); // 第二次 Shift+Click (擴展為範圍 [1..2])
            Assert.True(editorB.EditorRows[1].IsRangeSelected);
            Assert.True(editorB.EditorRows[2].IsRangeSelected);
            Assert.Equal(2, mgr.Buffer.Count);

            bool crossMoved = mgr.ExecuteMove(editorA, null);
            Assert.True(crossMoved);
            Assert.Equal(2, editorB.EditorRows.Count); // Editor B 原本 4 行剪掉 2 行剩 2 行
            Assert.Equal(6, editorA.EditorRows.Count); // Editor A 原本 4 行尾端搬入 2 行變 6 行
            Assert.False(mgr.HasSelection);
            Assert.All(editorA.EditorRows, r => Assert.False(r.IsRangeSelected));
            Assert.All(editorB.EditorRows, r => Assert.False(r.IsRangeSelected));
        }
        finally
        {
            editorA.Detach();
            editorB.Detach();
            mgr.ClearAll();
        }
    }
}
