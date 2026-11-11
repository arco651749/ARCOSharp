using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;
using ARCOS.App.Dialogs;
using ARCOS.App.Guidance;
using ARCOS.App.Services;
using ARCOS.App.ViewModels;
using ARCOS.Core.Execution;
using ARCOS.Core.Model;
using ARCOS.Core.Serialization;

namespace ARCOS.App.Controls;

public partial class SequencerEditorControl : UserControl, ISequencerEditorHost
{
    private ArcosThreadVM? _vm;
    private int _currentProgNo = 0;
    private bool _isLocked = false;
    private readonly ObservableCollection<SequencerRowViewModel> _rows = new();
    private readonly DispatcherTimer _autoTimer;
    private readonly DispatcherTimer _pcDrainerTimer;
    private int _latestNoticedPC = -1;
    private int _currentRenderedPC = -1;

    // =========================================================
    // 獨立程式區內部暫存歷程堆疊 (Per-Program-Section Undo/Redo)
    // 依使用者規範：每個 Program Slot (0..99) 獨立記憶修改過程
    // 切換程式區後切回，歷史紀錄完美保持
    // =========================================================
    private readonly Dictionary<int, Stack<List<(string Command, string Parameter)>>> _undoStacks = new();
    private readonly Dictionary<int, Stack<List<(string Command, string Parameter)>>> _redoStacks = new();
    private const int MaxHistoryDepth = 100;

    public ObservableCollection<SequencerRowViewModel> Rows => _rows;
    public IList<SequencerRowViewModel> EditorRows => _rows;
    public int CurrentProgramNumber => _currentProgNo;
    public bool IsLocked => _isLocked;

    public SequencerEditorControl()
    {
        InitializeComponent();

        var grid = this.FindControl<DataGrid>("GridCommands");
        if (grid != null)
        {
            grid.ItemsSource = _rows;
        }

        RefreshProgSelector(0);

        BindToolbarButtons();
        BindSearchControls();
        this.KeyDown += OnSequencerKeyDown;
        SequencerClipboardManager.Instance.SelectionChanged += OnGlobalSelectionChanged;

        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _autoTimer.Tick += AutoTimer_Tick;

        // 獨立 UI 節流排程器 (33ms / ~30 FPS)：定時非同步抽取最新 PC，絕不阻塞 CMDEXECUTE 微秒級調度
        _pcDrainerTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _pcDrainerTimer.Tick += (_, _) => DrainPCPointer();
        _pcDrainerTimer.Start();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void AttachVM(ArcosThreadVM vm)
    {
        _vm = vm;
        for (int i = 0; i < _vm.MainThreads.Count; i++)
        {
            int threadId = i;
            _vm.MainThreads[i].OnProgIndexNoticed += (pc) =>
            {
                if (_vm.MainThreads[threadId].Program?.ProgramNumber == _currentProgNo)
                {
                    Interlocked.Exchange(ref _latestNoticedPC, pc);
                }
            };
        }
        _vm.OnProgramLoaded += (path, isPro) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var txtTitle = this.FindControl<TextBox>("TxtScriptTitle");
                if (txtTitle != null) txtTitle.Text = System.IO.Path.GetFileName(path);
                RefreshProgSelector(0);
                LoadCurrentProgramToUI();
            });
        };

        RefreshProgSelector(0);
        EnsureDefaultSampleSequence();
        LoadCurrentProgramToUI();
        UpdateMachineStatus("IDLE");
        UpdateUndoRedoButtonState();
    }

    private void EnsureDefaultSampleSequence()
    {
        // 依使用者規範：啟動時不預載任何腳本，維持初始乾淨狀態
        // 使用者透過 UI 的 READ .PRO / READ .INIT 按鍵，或經由 SYSTEM@PROGRAMLOAD 指令載入
    }

    private void UpdateMachineStatus(string status)
    {
        var txtStatus = this.FindControl<TextBlock>("TxtMachineStatus");
        var led = this.FindControl<Ellipse>("LedModernStatus");

        if (txtStatus != null)
        {
            txtStatus.Text = status;
            if (status == "RUNNING")
            {
                txtStatus.Foreground = new SolidColorBrush(Color.Parse("#059669"));
                if (led != null) led.Fill = new SolidColorBrush(Color.Parse("#10B981"));
            }
            else if (status == "PAUSED")
            {
                txtStatus.Foreground = new SolidColorBrush(Color.Parse("#D97706"));
                if (led != null) led.Fill = new SolidColorBrush(Color.Parse("#F59E0B"));
            }
            else
            {
                txtStatus.Foreground = new SolidColorBrush(Color.Parse("#334155"));
                if (led != null) led.Fill = new SolidColorBrush(Color.Parse("#94A3B8"));
            }
        }
    }

    private void OnTabOpClick(object? sender, RoutedEventArgs e)
    {
        var pnlOp = this.FindControl<Border>("PnlOperator");
        var pnlSup = this.FindControl<Border>("PnlSupervisor");
        var btnOp = this.FindControl<Button>("BtnTabOp");
        var btnSup = this.FindControl<Button>("BtnTabSup");

        if (pnlOp != null) pnlOp.IsVisible = true;
        if (pnlSup != null) pnlSup.IsVisible = false;

        if (btnOp != null)
        {
            btnOp.Background = Brushes.White;
            btnOp.BorderThickness = new Avalonia.Thickness(1);
        }
        if (btnSup != null)
        {
            btnSup.Background = Brushes.Transparent;
            btnSup.BorderThickness = new Avalonia.Thickness(0);
        }

        _isLocked = true;
        var grid = this.FindControl<DataGrid>("GridCommands");
        if (grid != null) grid.IsReadOnly = true;
    }

    private void OnTabSupClick(object? sender, RoutedEventArgs e)
    {
        var pnlOp = this.FindControl<Border>("PnlOperator");
        var pnlSup = this.FindControl<Border>("PnlSupervisor");
        var btnOp = this.FindControl<Button>("BtnTabOp");
        var btnSup = this.FindControl<Button>("BtnTabSup");

        if (pnlOp != null) pnlOp.IsVisible = false;
        if (pnlSup != null) pnlSup.IsVisible = true;

        if (btnSup != null)
        {
            btnSup.Background = Brushes.White;
            btnSup.BorderThickness = new Avalonia.Thickness(1);
        }
        if (btnOp != null)
        {
            btnOp.Background = Brushes.Transparent;
            btnOp.BorderThickness = new Avalonia.Thickness(0);
        }

        _isLocked = false;
        var grid = this.FindControl<DataGrid>("GridCommands");
        if (grid != null) grid.IsReadOnly = false;
    }

    private bool _isOpeningCommandSelect = false;

    public async Task<bool> EditCommandForRowAsync(SequencerRowViewModel row, Window topLevel)
    {
        if (_isOpeningCommandSelect) return false;
        try
        {
            _isOpeningCommandSelect = true;
            string? selectedCmd = await ComSelectWindow.SelectCommandAsync(topLevel, _vm?.Executor.Registry);
            if (string.IsNullOrWhiteSpace(selectedCmd)) return false;

            SyncIoDefinitionsFromRows();
            string initialParam = row.Command.Equals(selectedCmd, StringComparison.OrdinalIgnoreCase) ? row.Parameter : "";
            string? confirmedParam = await ParameterGuidanceManager.Instance.RequestGuidanceAsync(selectedCmd, initialParam, _vm?.Engines, topLevel, newCmd => { selectedCmd = newCmd; });
            if (confirmedParam != null)
            {
                PushUndoSnapshot();
                row.Command = selectedCmd;
                row.Parameter = confirmedParam;
                SyncUIToProgram();
                SyncIoDefinitionsFromRows();
                return true;
            }
            return false;
        }
        finally
        {
            _isOpeningCommandSelect = false;
        }
    }

    public async Task<SequencerRowViewModel?> PromptInsertRowAsync(int insertIndex, Window topLevel)
    {
        if (_isLocked || _isOpeningCommandSelect) return null;
        if (insertIndex < 0 || insertIndex > _rows.Count) insertIndex = _rows.Count;

        try
        {
            _isOpeningCommandSelect = true;
            string? selectedCmd = await ComSelectWindow.SelectCommandAsync(topLevel, _vm?.Executor.Registry);
            if (string.IsNullOrWhiteSpace(selectedCmd)) return null;

            SyncIoDefinitionsFromRows();
            string? confirmedParam = await ParameterGuidanceManager.Instance.RequestGuidanceAsync(selectedCmd, "", _vm?.Engines, topLevel, newCmd => { selectedCmd = newCmd; });
            if (confirmedParam == null) return null;

            PushUndoSnapshot();
            var newRow = new SequencerRowViewModel
            {
                LineNumber = insertIndex,
                Command = selectedCmd,
                Parameter = confirmedParam
            };
            _rows.Insert(insertIndex, newRow);
            RenumberRows();
            SyncUIToProgram();
            SyncIoDefinitionsFromRows();
            UpdateUndoRedoButtonState();
            NavigateToRow(insertIndex);
            return newRow;
        }
        finally
        {
            _isOpeningCommandSelect = false;
        }
    }

    private async void OnCommandCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isLocked || _isOpeningCommandSelect) return;
        var topLevel = TopLevel.GetTopLevel(this) as Window;
        if (topLevel == null) return;

        var row = (sender as Control)?.DataContext as SequencerRowViewModel;

        // 1. 優先交由全域快捷鍵狀態機處理 (Shift 單行/範圍選取、Ctrl 複製插入、Alt 刪除、Ctrl+Alt 搬移)
        if (HandleShortcutPointerAction(row, e)) return;
        if (row == null) return;

        var props = e.GetCurrentPoint(this).Properties;

        // 2. 若無選取項目時按 Ctrl+Click：在該行位置插入新指令並自動帶出指令選擇視窗
        if (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            e.Handled = true;
            ClearRangeSelection();
            int cur = row.LineNumber;
            int ins = (cur >= 0 && cur < _rows.Count) ? cur : _rows.Count;
            await PromptInsertRowAsync(ins, topLevel);
            return;
        }

        // 3. 無修飾鍵單擊指令欄：開啟該行指令編輯視窗
        if (props.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None)
        {
            await EditCommandForRowAsync(row, topLevel);
        }
    }

    private DateTime _lastShiftDoubleStepTime = DateTime.MinValue;

    private async void OnParameterCellDoubleTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || (DateTime.UtcNow - _lastShiftDoubleStepTime).TotalMilliseconds < 500)
        {
            var row = (sender as Control)?.DataContext as SequencerRowViewModel;
            if (row != null && (DateTime.UtcNow - _lastShiftDoubleStepTime).TotalMilliseconds >= 500)
            {
                _lastShiftDoubleStepTime = DateTime.UtcNow;
                ClearRangeSelection();
                JumpToRow(row.LineNumber);
                StepOnce();
            }
            return;
        }

        await OpenParameterGuidance(sender);
    }

    private void OnParameterCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var row = (sender as Control)?.DataContext as SequencerRowViewModel;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.ClickCount >= 2 && row != null)
        {
            e.Handled = true;
            _lastShiftDoubleStepTime = DateTime.UtcNow;
            ClearRangeSelection();
            JumpToRow(row.LineNumber);
            StepOnce();
            return;
        }

        if (HandleShortcutPointerAction(row, e)) return;
        // 注意：雙擊開啟導引視窗已由 OnParameterCellDoubleTapped 專責處理，此處切勿重複調用，避免同時開啟兩個視窗重疊
    }

    private void SyncIoDefinitionsFromRows()
    {
        if (_vm?.Engines?.Io == null || _rows == null) return;
        foreach (var r in _rows)
        {
            if (string.IsNullOrWhiteSpace(r.Command) || string.IsNullOrWhiteSpace(r.Parameter)) continue;
            _vm.Engines.Io.ScanAndRegister(r.Command, r.Parameter);
        }
    }

    private bool _isOpeningGuidance = false;

    private async Task OpenParameterGuidance(object? sender)
    {
        if (_isLocked || _isOpeningGuidance) return;
        var topLevel = TopLevel.GetTopLevel(this) as Window;
        if (topLevel == null) return;

        var row = (sender as Control)?.DataContext as SequencerRowViewModel;
        if (row == null) return;

        try
        {
            _isOpeningGuidance = true;
            string targetCmd = !string.IsNullOrWhiteSpace(row.Command) ? row.Command : "System@Log Message";
            SyncIoDefinitionsFromRows();
            string? updatedParam = await ParameterGuidanceManager.Instance.RequestGuidanceAsync(targetCmd, row.Parameter, _vm?.Engines, topLevel, newCmd => { row.Command = newCmd; });
            if (updatedParam != null)
            {
                PushUndoSnapshot();
                row.Parameter = updatedParam;
                SyncUIToProgram();
                SyncIoDefinitionsFromRows();
            }
        }
        finally
        {
            _isOpeningGuidance = false;
        }
    }

    // =========================================================
    // 快捷鍵指令選取與剪貼管理 (委派給獨立的全域 SequencerClipboardManager，本 Editor 僅負責顯示)
    // =========================================================
    public void RefreshSelectionVisuals()
    {
        var mgr = SequencerClipboardManager.Instance;
        foreach (var r in _rows)
        {
            bool shouldSelect = mgr.IsRowSelected(this, r.LineNumber);
            if (r.IsRangeSelected != shouldSelect)
            {
                r.IsRangeSelected = shouldSelect;
            }
        }
    }

    public void ClearRangeSelection()
    {
        SequencerClipboardManager.Instance.ClearAll();
        RefreshSelectionVisuals();
    }

    public void JumpToRow(int lineNumber)
    {
        if (lineNumber < 0 || lineNumber >= _rows.Count) return;
        if (_vm == null) return;

        var ctx = GetActiveContext();
        ctx.ProgIndex = lineNumber + 1;
        ctx.State.Reset();
        UpdatePCPointer(ctx.ProgIndex);
        DrainPCPointer();
        ctx.Log($"[PC JUMP] PC pointer jumped to Line {lineNumber}: {(_rows.Count > lineNumber ? _rows[lineNumber].Command : "")}");

        var grid = this.FindControl<DataGrid>("GridCommands");
        if (grid != null && lineNumber < _rows.Count)
        {
            grid.SelectedIndex = lineNumber;
        }
    }

    public void StepOnce()
    {
        if (_vm == null) return;
        SyncUIToProgram();

        var ctx = GetActiveContext();
        if (!ctx.IsRunning)
        {
            int startIdx = (ctx.ProgIndex >= 1 && ctx.ProgIndex <= _rows.Count) ? ctx.ProgIndex : 1;
            _vm.StartThread(ctx.ThreadId, _currentProgNo, startIdx);
        }

        UpdateMachineStatus("RUNNING");
        _vm.Executor.ExecuteStep(ctx);
        UpdatePCPointer(ctx.ProgIndex);
        DrainPCPointer();

        if (!ctx.IsRunning || ctx.ProgIndex > _rows.Count)
        {
            ctx.IsRunning = false;
            UpdateMachineStatus("IDLE");
        }
    }

    private void OnGlobalSelectionChanged(object? activeEditor)
    {
        // 純視覺更新：根據全域選取管理器決定本 Editor 是否顯示反白，絕不反向清空全域管理器
        RefreshSelectionVisuals();
    }

    private void OnCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var row = (sender as Control)?.DataContext as SequencerRowViewModel;
        HandleShortcutPointerAction(row, e);
    }

    private async void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;

        var visual = e.Source as Visual;
        if (visual == null) return;

        // 1. 若點擊在資料行上，交由行或單元格快捷鍵狀態機處理
        if ((visual as Control)?.DataContext is SequencerRowViewModel rowVm)
        {
            if (HandleShortcutPointerAction(rowVm, e))
            {
                e.Handled = true;
            }
            return;
        }

        // 2. 排除點擊在表頭、滾動條、縮放或按鈕控制項上的情況
        if (IsInIgnoredGridArea(visual)) return;

        var km = e.KeyModifiers;
        bool isCtrl = km.HasFlag(KeyModifiers.Control);
        bool isAlt = km.HasFlag(KeyModifiers.Alt);
        bool isShift = km.HasFlag(KeyModifiers.Shift);

        // 3. 點擊程式空白處：指操作在程式最後面 (targetLine = null -> _rows.Count)
        // 3.1 Ctrl + Alt + 滑鼠左鍵：將選取的單行或範圍搬移至程式尾端
        if (isCtrl && isAlt && !isShift)
        {
            e.Handled = true;
            if (SequencerClipboardManager.Instance.HasSelection)
            {
                ExecuteLocalMove(null);
            }
            return;
        }

        // 3.2 Ctrl + 滑鼠左鍵：將剪貼簿內容複製插入到程式尾端；若剪貼簿為空，則在尾端新增並開啟指令選擇視窗
        if (isCtrl && !isAlt && !isShift)
        {
            e.Handled = true;
            if (SequencerClipboardManager.Instance.HasItems)
            {
                ExecuteCopyInsert(null);
            }
            else
            {
                if (_isLocked) return;
                var topLevel = TopLevel.GetTopLevel(this) as Window;
                if (topLevel != null)
                {
                    await PromptInsertRowAsync(_rows.Count, topLevel);
                }
            }
            return;
        }

        // 3.3 Alt + 滑鼠左鍵：刪除選取區
        if (isAlt && !isCtrl && !isShift)
        {
            e.Handled = true;
            if (SequencerClipboardManager.Instance.HasSelection)
            {
                ExecuteLocalDelete(null);
            }
            return;
        }

        // 3.4 無修飾鍵：
        if (!isCtrl && !isAlt && !isShift)
        {
            // 若有選取範圍，點擊空白處時取消選取
            if (_rows.Any(r => r.IsRangeSelected) || SequencerClipboardManager.Instance.HasSelection)
            {
                ClearRangeSelection();
                e.Handled = true;
                return;
            }

            // 無選取狀態下單擊空白處：在尾端新增指令行並跳出指令選擇視窗
            if (_isLocked) return;
            e.Handled = true;
            var topLevel = TopLevel.GetTopLevel(this) as Window;
            if (topLevel != null)
            {
                await PromptInsertRowAsync(_rows.Count, topLevel);
            }
            return;
        }

        // 其它情況取消選取
        if (_rows.Any(r => r.IsRangeSelected) || SequencerClipboardManager.Instance.HasSelection)
        {
            ClearRangeSelection();
        }
    }

    private static bool IsInIgnoredGridArea(Visual? visual)
    {
        if (visual == null) return true;

        if (visual.FindAncestorOfType<DataGridRow>(includeSelf: true) != null ||
            visual.FindAncestorOfType<DataGridCell>(includeSelf: true) != null ||
            visual.FindAncestorOfType<DataGridRowHeader>(includeSelf: true) != null ||
            visual.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) != null ||
            visual.FindAncestorOfType<DataGridColumnHeadersPresenter>(includeSelf: true) != null ||
            visual.FindAncestorOfType<ScrollBar>(includeSelf: true) != null ||
            visual.FindAncestorOfType<Thumb>(includeSelf: true) != null ||
            visual.FindAncestorOfType<Button>(includeSelf: true) != null ||
            visual.FindAncestorOfType<TextBox>(includeSelf: true) != null)
        {
            return true;
        }

        return false;
    }

    private bool HandleShortcutPointerAction(SequencerRowViewModel? row, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return false;

        var km = e.KeyModifiers;
        bool isCtrl = km.HasFlag(KeyModifiers.Control);
        bool isAlt = km.HasFlag(KeyModifiers.Alt);
        bool isShift = km.HasFlag(KeyModifiers.Shift);

        // 1. Shift + 滑鼠左鍵 (集中由 SequencerClipboardManager.HandleShiftClick 管理單行與範圍選取)
        if (isShift && !isCtrl && !isAlt)
        {
            if (e.ClickCount >= 2) return false;

            e.Handled = true;
            if (row == null) return true;

            SequencerClipboardManager.Instance.HandleShiftClick(this, row.LineNumber);
            return true;
        }

        // 2. Ctrl + Alt + 滑鼠左鍵 (搬移選取區，支援同視窗與跨視窗)
        if (isCtrl && isAlt && !isShift)
        {
            e.Handled = true;
            ExecuteLocalMove(row);
            return true;
        }

        // 3. Ctrl + 滑鼠左鍵 (複製插入選取區，支援同視窗與跨視窗)
        if (isCtrl && !isAlt && !isShift)
        {
            if (SequencerClipboardManager.Instance.HasItems)
            {
                e.Handled = true;
                ExecuteCopyInsert(row);
                return true;
            }
            // 若剪貼簿為空，不在此攔截，交由 Command 欄位之 PromptInsertRowAsync 處理
            return false;
        }

        // 4. Alt + 滑鼠左鍵 (刪除選取區或點擊行)
        if (isAlt && !isCtrl && !isShift)
        {
            e.Handled = true;
            ExecuteLocalDelete(row);
            return true;
        }

        // 5. 若不是以上任何快捷鍵操作 (例如：一般無修飾鍵點擊行/單元格)，清空先前的選取區
        if (_rows.Any(r => r.IsRangeSelected) || SequencerClipboardManager.Instance.HasSelection)
        {
            ClearRangeSelection();
        }

        return false;
    }

    private void ExecuteCopyInsert(SequencerRowViewModel? targetRow)
    {
        int? targetLine = targetRow != null ? targetRow.LineNumber : null;
        SequencerClipboardManager.Instance.ExecuteCopyInsert(this, targetLine);
    }

    private void ExecuteLocalMove(SequencerRowViewModel? targetRow)
    {
        int? targetLine = targetRow != null ? targetRow.LineNumber : null;
        SequencerClipboardManager.Instance.ExecuteMove(this, targetLine);
    }

    private void ExecuteLocalDelete(SequencerRowViewModel? clickedRow)
    {
        SequencerClipboardManager.Instance.ExecuteDelete(this, clickedRow);
    }

    public void SelectRange(int startLine, int endLine)
    {
        int start = Math.Min(startLine, endLine);
        int end = Math.Max(startLine, endLine);
        var selectedItems = _rows
            .Where(r => r.LineNumber >= start && r.LineNumber <= end)
            .Select(r => (r.Command ?? "", r.Parameter ?? ""))
            .ToList();
        SequencerClipboardManager.Instance.SetSelection(this, start, end, selectedItems);
    }

    public void PasteFromClipboard(int targetIndex)
    {
        int? targetLine = (targetIndex >= 0 && targetIndex < _rows.Count) ? targetIndex : null;
        SequencerClipboardManager.Instance.ExecuteCopyInsert(this, targetLine);
    }

    public void MoveSelectedRange(int targetIndex)
    {
        int? targetLine = (targetIndex >= 0 && targetIndex < _rows.Count) ? targetIndex : null;
        SequencerClipboardManager.Instance.ExecuteMove(this, targetLine);
    }

    public void DeleteSelectedRange()
    {
        SequencerClipboardManager.Instance.ExecuteDelete(this, null);
    }

    // =========================================================
    // MCP 與自動化控制專用公開方法
    // =========================================================

    public void SetSupervisorMode(bool supervisor)
    {
        if (supervisor) OnTabSupClick(null, new RoutedEventArgs());
        else OnTabOpClick(null, new RoutedEventArgs());
    }

    public async Task<string?> OpenParameterGuidanceForRowAsync(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return null;
        if (_isLocked) SetSupervisorMode(true);
        NavigateToRow(rowIndex);

        var topLevel = TopLevel.GetTopLevel(this) as Window;
        if (topLevel == null) return null;

        var row = _rows[rowIndex];
        string targetCmd = !string.IsNullOrWhiteSpace(row.Command) ? row.Command : "System@Log Message";
        SyncIoDefinitionsFromRows();
        string? updatedParam = await ParameterGuidanceManager.Instance.RequestGuidanceAsync(targetCmd, row.Parameter, _vm?.Engines, topLevel);
        if (updatedParam != null)
        {
            row.Parameter = updatedParam;
            SyncUIToProgram();
        }
        return updatedParam;
    }

    public void SelectRow(int rowIndex) => NavigateToRow(rowIndex);
    public void TriggerStep() => BtnStep_Click(null, new RoutedEventArgs());
    public void TriggerAutoRun() => BtnAuto_Click(null, new RoutedEventArgs());
    public void TriggerPause() => BtnPause_Click(null, new RoutedEventArgs());
    public void TriggerReset() => BtnReset_Click(null, new RoutedEventArgs());

    public void TriggerJump(int rowIndex)
    {
        NavigateToRow(rowIndex);
        BtnJump_Click(null, new RoutedEventArgs());
    }

    public SequencerRowViewModel TriggerAddRow(int? atIndex = null, string command = "System@Delay", string parameter = "0.1")
    {
        PushUndoSnapshot();
        var grid = this.FindControl<DataGrid>("GridCommands");
        int insertIndex = atIndex ?? (grid?.SelectedIndex >= 0 && grid.SelectedIndex < _rows.Count ? grid.SelectedIndex : _rows.Count);
        if (insertIndex < 0 || insertIndex > _rows.Count) insertIndex = _rows.Count;

        var newRow = new SequencerRowViewModel
        {
            LineNumber = insertIndex,
            Command = command,
            Parameter = parameter
        };
        _rows.Insert(insertIndex, newRow);

        RenumberRows();
        SyncUIToProgram();
        NavigateToRow(insertIndex);
        return newRow;
    }

    public void TriggerDeleteRow(int? atIndex = null)
    {
        var grid = this.FindControl<DataGrid>("GridCommands");
        if (atIndex.HasValue && atIndex.Value >= 0 && atIndex.Value < _rows.Count && grid != null)
        {
            grid.SelectedIndex = atIndex.Value;
        }
        BtnDelete_Click(null, new RoutedEventArgs());
    }

    public void TriggerClearSection()
    {
        BtnClearSection_Click(null, new RoutedEventArgs());
    }

    public void SimulateKeyDown(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        OnSequencerKeyDown(this, new KeyEventArgs
        {
            RoutedEvent = KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers
        });
    }

    private void BindToolbarButtons()
    {
        // 核心流程控制 (Run, Step, Jump, Pause, Reset)
        var btnBigRunOp = this.FindControl<Button>("BtnBigRunOp");
        if (btnBigRunOp != null) btnBigRunOp.Click += BtnAuto_Click;

        var btnRunSup = this.FindControl<Button>("BtnRunSup");
        if (btnRunSup != null) btnRunSup.Click += BtnAuto_Click;

        var btnAuto = this.FindControl<Button>("BtnAuto");
        if (btnAuto != null) btnAuto.Click += BtnAuto_Click;

        var btnStep = this.FindControl<Button>("BtnStep");
        if (btnStep != null) btnStep.Click += BtnStep_Click;

        var btnPause = this.FindControl<Button>("BtnPause");
        if (btnPause != null) btnPause.Click += BtnPause_Click;

        var btnReset = this.FindControl<Button>("BtnReset");
        if (btnReset != null) btnReset.Click += BtnReset_Click;

        var btnJump = this.FindControl<Button>("BtnJump");
        if (btnJump != null) btnJump.Click += BtnJump_Click;

        var btnCrossSup = this.FindControl<Button>("BtnCrossSup");
        if (btnCrossSup != null) btnCrossSup.Click += BtnReset_Click;

        // 行編輯與檔案
        var btnAdd = this.FindControl<Button>("BtnAdd");
        if (btnAdd != null) btnAdd.Click += BtnAdd_Click;

        var btnDelete = this.FindControl<Button>("BtnDelete");
        if (btnDelete != null) btnDelete.Click += BtnClearSection_Click;

        var btnMinus = this.FindControl<Button>("BtnMinusLine");
        if (btnMinus != null) btnMinus.Click += BtnDelete_Click;

        var btnSave = this.FindControl<Button>("BtnSave");
        if (btnSave != null) btnSave.Click += BtnSave_Click;

        var btnSaveLine = this.FindControl<Button>("BtnSaveLine");
        if (btnSaveLine != null) btnSaveLine.Click += (_, _) =>
        {
            PushUndoSnapshot();
            SyncUIToProgram();
        };

        var btnLoad = this.FindControl<Button>("BtnLoad");
        if (btnLoad != null) btnLoad.Click += BtnLoad_Click;

        var btnReadPro = this.FindControl<Button>("BtnReadPro");
        if (btnReadPro != null) btnReadPro.Click += BtnReadPro_Click;

        var btnSavePro = this.FindControl<Button>("BtnSavePro");
        if (btnSavePro != null) btnSavePro.Click += BtnSavePro_Click;

        var btnReadInit = this.FindControl<Button>("BtnReadInit");
        if (btnReadInit != null) btnReadInit.Click += BtnReadInit_Click;

        var btnSaveInit = this.FindControl<Button>("BtnSaveInit");
        if (btnSaveInit != null) btnSaveInit.Click += BtnSaveInit_Click;

        // 編輯歷程 (Undo / Redo) 與權限
        var btnUndo = this.FindControl<Button>("BtnUndo");
        if (btnUndo != null) btnUndo.Click += (_, _) => PerformUndo();

        var btnRedo = this.FindControl<Button>("BtnRedo");
        if (btnRedo != null) btnRedo.Click += (_, _) => PerformRedo();

        var btnRefresh = this.FindControl<Button>("BtnRefresh");
        if (btnRefresh != null) btnRefresh.Click += (_, _) => LoadCurrentProgramToUI();

        var btnLockOp = this.FindControl<Button>("BtnLockOp");
        if (btnLockOp != null) btnLockOp.Click += BtnLock_Click;

        var btnLockSup = this.FindControl<Button>("BtnLockSup");
        if (btnLockSup != null) btnLockSup.Click += BtnLock_Click;

        // Program 翻頁
        var btnPrev = this.FindControl<Button>("BtnProgPrev");
        if (btnPrev != null) btnPrev.Click += (_, _) => ChangeProg(-1);

        var btnNext = this.FindControl<Button>("BtnProgNext");
        if (btnNext != null) btnNext.Click += (_, _) => ChangeProg(1);
    }

    private void BindSearchControls()
    {
        var btnSearch = this.FindControl<Button>("BtnSearch");
        var searchPanel = this.FindControl<Border>("SearchPanel");
        var btnCloseSearch = this.FindControl<Button>("BtnCloseSearch");
        var txtSearch = this.FindControl<TextBox>("TxtSearchKeyword");
        var btnFindNext = this.FindControl<Button>("BtnFindNext");
        var btnFindPrev = this.FindControl<Button>("BtnFindPrev");

        if (btnSearch != null && searchPanel != null)
        {
            btnSearch.Click += (_, _) =>
            {
                if (!searchPanel.IsVisible)
                {
                    searchPanel.IsVisible = true;
                    txtSearch?.Focus();
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(txtSearch?.Text))
                    {
                        PerformSearch(txtSearch.Text, forward: true);
                    }
                    else
                    {
                        searchPanel.IsVisible = false;
                    }
                }
            };
        }

        if (btnCloseSearch != null && searchPanel != null)
        {
            btnCloseSearch.Click += (_, _) => searchPanel.IsVisible = false;
        }

        if (btnFindNext != null && txtSearch != null)
        {
            btnFindNext.Click += (_, _) => PerformSearch(txtSearch.Text, forward: true);
        }

        if (btnFindPrev != null && txtSearch != null)
        {
            btnFindPrev.Click += (_, _) => PerformSearch(txtSearch.Text, forward: false);
        }

        if (txtSearch != null)
        {
            txtSearch.KeyUp += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    bool forward = !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    PerformSearch(txtSearch.Text, forward);
                }
                else if (e.Key == Key.Escape)
                {
                    if (searchPanel != null) searchPanel.IsVisible = false;
                }
            };
        }
    }

    public void PerformSearch(string? keyword, bool forward = true)
    {
        var txtStatus = this.FindControl<TextBlock>("TxtSearchStatus");
        if (string.IsNullOrWhiteSpace(keyword))
        {
            if (txtStatus != null) txtStatus.Text = "";
            return;
        }

        if (_rows.Count == 0)
        {
            if (txtStatus != null)
            {
                txtStatus.Text = "目前無指令行";
                txtStatus.Foreground = new SolidColorBrush(Color.Parse("#94A3B8"));
            }
            return;
        }

        var grid = this.FindControl<DataGrid>("GridCommands");
        int current = grid?.SelectedIndex ?? 0;
        int step = forward ? 1 : -1;

        for (int count = 1; count <= _rows.Count; count++)
        {
            int idx = (current + count * step) % _rows.Count;
            if (idx < 0) idx += _rows.Count;

            var row = _rows[idx];
            bool matchCmd = !string.IsNullOrEmpty(row.Command) && row.Command.Contains(keyword, StringComparison.OrdinalIgnoreCase);
            bool matchParam = !string.IsNullOrEmpty(row.Parameter) && row.Parameter.Contains(keyword, StringComparison.OrdinalIgnoreCase);

            if (matchCmd || matchParam)
            {
                NavigateToRow(idx);
                if (txtStatus != null)
                {
                    txtStatus.Text = $"找到於第 {idx + 1} 行";
                    txtStatus.Foreground = new SolidColorBrush(Color.Parse("#0284C7"));
                }
                return;
            }
        }

        if (txtStatus != null)
        {
            txtStatus.Text = "查無關鍵字";
            txtStatus.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
        }
    }

    private void NavigateToRow(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        var grid = this.FindControl<DataGrid>("GridCommands");
        if (grid != null)
        {
            grid.SelectedIndex = index;
            grid.ScrollIntoView(_rows[index], null);
        }
    }

    private void BtnLock_Click(object? sender, RoutedEventArgs e)
    {
        _isLocked = !_isLocked;
        var grid = this.FindControl<DataGrid>("GridCommands");
        if (grid != null) grid.IsReadOnly = _isLocked;
    }

    public void RefreshProgSelector(int? selectedSlot = null)
    {
        var progSel = this.FindControl<ComboBox>("ProgSelector");
        if (progSel == null) return;

        int targetSlot = selectedSlot ?? _currentProgNo;
        var slots = ProgramSlotItem.BuildStandardSlotList(_vm);

        progSel.SelectionChanged -= ProgSelector_SelectionChanged;
        progSel.ItemsSource = slots;

        int matchIdx = slots.FindIndex(s => s.SlotNumber == targetSlot);
        progSel.SelectedIndex = matchIdx >= 0 ? matchIdx : 0;

        if (progSel.SelectedItem is ProgramSlotItem selected)
        {
            _currentProgNo = selected.SlotNumber;
        }
        progSel.SelectionChanged += ProgSelector_SelectionChanged;
    }

    private void ChangeProg(int delta)
    {
        var progSel = this.FindControl<ComboBox>("ProgSelector");
        if (progSel != null && progSel.ItemsSource is System.Collections.IList list)
        {
            int next = Math.Clamp(progSel.SelectedIndex + delta, 0, list.Count - 1);
            progSel.SelectedIndex = next;
        }
    }

    private void ProgSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var progSel = this.FindControl<ComboBox>("ProgSelector");
        if (progSel?.SelectedItem is ProgramSlotItem slotItem)
        {
            SyncUIToProgram();
            _currentProgNo = slotItem.SlotNumber;
            LoadCurrentProgramToUI();
            UpdateUndoRedoButtonState();
        }
    }

    private void LoadCurrentProgramToUI()
    {
        _rows.Clear();
        _currentRenderedPC = -1;
        if (_vm == null) return;

        var prog = _vm.GetProgram(_currentProgNo);
        ExecutionContext? runningCtx = null;
        for (int t = 0; t < 13; t++)
        {
            if (_vm.MainThreads[t].Program?.ProgramNumber == _currentProgNo && _vm.MainThreads[t].IsRunning)
            {
                runningCtx = _vm.MainThreads[t];
                break;
            }
        }

        int activePC = runningCtx?.ProgIndex ?? 1;
        bool isRunning = runningCtx?.IsRunning ?? false;

        for (int i = 0; i < prog.Count; i++)
        {
            var item = prog.Items[i];
            _rows.Add(new SequencerRowViewModel
            {
                LineNumber = i,
                Command = item.Command,
                Parameter = item.RawParameter,
                IsCurrentPC = (i == activePC && isRunning)
            });
        }
        UpdatePCPointer(activePC);
        DrainPCPointer();
    }

    public void SyncUIToProgram()
    {
        if (_vm == null) return;
        var existingProg = _vm.GetProgram(_currentProgNo);
        string name = (!string.IsNullOrWhiteSpace(existingProg?.Name) && !existingProg.Name.Equals($"Program_{_currentProgNo}", StringComparison.OrdinalIgnoreCase))
            ? existingProg.Name
            : ProgramSlotItem.GetDefaultName(_currentProgNo);

        // 支援 System@ProgramSection 指令動態重命名程式區
        foreach (var r in _rows)
        {
            if (r.Command.Equals("System@ProgramSection", StringComparison.OrdinalIgnoreCase))
            {
                var parts = r.Parameter.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    name = parts[1];
                }
                else if (parts.Length == 1 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    name = parts[0];
                }
                break;
            }
        }

        bool nameChanged = existingProg != null && existingProg.Name != name;
        var prog = new ProgramTable { ProgramNumber = _currentProgNo, Name = name };
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            row.LineNumber = i;
            prog.Add(CommandItem.Parse($"{row.Command}\t{row.Parameter}", i));
        }
        _vm.LoadProgram(_currentProgNo, prog);
        var ctx = GetActiveContext();
        if (ctx.Program?.ProgramNumber == _currentProgNo)
        {
            ctx.Program = prog;
        }
        SyncIoDefinitionsFromRows();

        if (nameChanged)
        {
            RefreshProgSelector(_currentProgNo);
        }
    }

    public void UpdatePCPointer(int progIndex1Based)
    {
        // 將 1-based 的 VM ProgIndex 轉為 0-based 的 UI DataGrid 列索引
        int uiRow = progIndex1Based >= 1 ? progIndex1Based - 1 : 0;
        Interlocked.Exchange(ref _latestNoticedPC, uiRow);
    }

    /// <summary>
    /// UI 節流定時抽取器 (30 FPS)：以 O(1) 複雜度更新最新 PC 行號的粉色反白顯示
    /// </summary>
    private void DrainPCPointer()
    {
        int targetPC = Volatile.Read(ref _latestNoticedPC);
        if (targetPC == _currentRenderedPC) return;

        // O(1) 關閉前一個行號反白
        if (_currentRenderedPC >= 0 && _currentRenderedPC < _rows.Count)
        {
            _rows[_currentRenderedPC].IsCurrentPC = false;
        }

        _currentRenderedPC = targetPC;

        // O(1) 開啟目前 PC 行號的粉色反白
        if (_currentRenderedPC >= 0 && _currentRenderedPC < _rows.Count)
        {
            _rows[_currentRenderedPC].IsCurrentPC = true;
            var grid = this.FindControl<DataGrid>("GridCommands");
            grid?.ScrollIntoView(_rows[_currentRenderedPC], null);
        }
    }

    private ExecutionContext GetActiveContext()
    {
        if (_vm != null)
        {
            for (int t = 0; t < 13; t++)
            {
                if (_vm.MainThreads[t].Program?.ProgramNumber == _currentProgNo && _vm.MainThreads[t].IsRunning)
                {
                    return _vm.MainThreads[t];
                }
            }
        }
        int tIdx = _currentProgNo < 13 ? _currentProgNo : 0;
        return _vm!.MainThreads[tIdx];
    }

    private void BtnStep_Click(object? sender, RoutedEventArgs e)
    {
        ClearRangeSelection();
        StepOnce();
    }

    private void BtnAuto_Click(object? sender, RoutedEventArgs e)
    {
        ClearRangeSelection();
        if (_vm == null) return;
        SyncUIToProgram();

        var ctx = GetActiveContext();
        if (!ctx.IsRunning)
        {
            int startIdx = (ctx.ProgIndex >= 1 && ctx.ProgIndex <= _rows.Count) ? ctx.ProgIndex : 1;
            _vm.StartThread(ctx.ThreadId, _currentProgNo, startIdx);
        }

        UpdateMachineStatus("RUNNING");
        _autoTimer.Start();
    }

    private void AutoTimer_Tick(object? sender, EventArgs e)
    {
        if (_vm == null) return;
        var ctx = GetActiveContext();

        if (!ctx.IsRunning || ctx.ProgIndex > _rows.Count)
        {
            ctx.IsRunning = false;
            _autoTimer.Stop();
            UpdateMachineStatus("IDLE");
            return;
        }

        _vm.Executor.ExecuteStep(ctx);
        UpdatePCPointer(ctx.ProgIndex);
    }

    private void BtnPause_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        bool isPaused = _vm.Engines.Timer.IsSystemPaused;
        _vm.Engines.Timer.SetSystemPause(!isPaused);
        UpdateMachineStatus(!isPaused ? "PAUSED" : "RUNNING");
    }

    private void BtnReset_Click(object? sender, RoutedEventArgs e)
    {
        ClearRangeSelection();
        _autoTimer.Stop();
        if (_vm != null)
        {
            var ctx = GetActiveContext();
            ctx.IsRunning = false;
            ctx.ProgIndex = 1;
            ctx.State.Reset();
            UpdatePCPointer(1);
            DrainPCPointer();
            UpdateMachineStatus("IDLE");
        }
    }

    private void BtnJump_Click(object? sender, RoutedEventArgs e)
    {
        ClearRangeSelection();
        var grid = this.FindControl<DataGrid>("GridCommands");
        if (grid?.SelectedItem is SequencerRowViewModel row)
        {
            JumpToRow(row.LineNumber);
        }
    }

    private async void BtnAdd_Click(object? sender, RoutedEventArgs e)
    {
        if (_isLocked) return;
        var topLevel = TopLevel.GetTopLevel(this) as Window;
        if (topLevel == null) return;

        ClearRangeSelection();
        var grid = this.FindControl<DataGrid>("GridCommands");
        int ins = (grid?.SelectedIndex >= 0 && grid.SelectedIndex < _rows.Count) ? grid.SelectedIndex : _rows.Count;
        await PromptInsertRowAsync(ins, topLevel);
    }

    private void BtnClearSection_Click(object? sender, RoutedEventArgs e)
    {
        if (_isLocked) return;
        if (_rows.Count == 0) return;

        PushUndoSnapshot();
        ClearRangeSelection();
        _rows.Clear();
        SyncUIToProgram();
        UpdateUndoRedoButtonState();
        UpdatePCPointer(1);
        DrainPCPointer();

        if (_vm != null)
        {
            var ctx = GetActiveContext();
            ctx.Log($"[PROG] Program Section {_currentProgNo} cleared");
        }
    }

    private void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        ClearRangeSelection();
        if (_rows.Count == 0) return;
        var grid = this.FindControl<DataGrid>("GridCommands");
        if (grid?.SelectedItem is SequencerRowViewModel row)
        {
            PushUndoSnapshot();
            int cur = grid.SelectedIndex;
            _rows.Remove(row);
            RenumberRows();
            SyncUIToProgram();
            if (_rows.Count > 0)
            {
                NavigateToRow(Math.Clamp(cur, 0, _rows.Count - 1));
            }
        }
    }

    private void RenumberRows()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            _rows[i].LineNumber = i;
        }
    }

    private async void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null || _vm == null) return;

        SyncUIToProgram();

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Program Table",
            DefaultExtension = "prog",
            SuggestedFileName = "Master_V1.prog"
        });

        if (file != null)
        {
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            for (int p = 0; p < 100; p++)
            {
                var prog = _vm.GetProgram(p);
                if (prog.Count > 0)
                {
                    writer.WriteLine($"[Program_{p}]");
                    for (int i = 0; i < prog.Count; i++)
                    {
                        var item = prog.Items[i];
                        writer.WriteLine($"{item.Command}::{item.RawParameter};");
                    }
                }
            }
        }
    }

    private async void BtnReadPro_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null || _vm == null) return;

        var rootFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(System.IO.Path.GetFullPath(ARCOS.Core.Common.ArcosPaths.RootDirectory)));
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "讀取 .prog 專案檔 (Read .prog Project)",
            SuggestedStartLocation = rootFolder,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ARCOSharp Project (*.prog, *.pro)") { Patterns = new[] { "*.prog", "*.pro" } }
            }
        });

        if (files.Count > 0)
        {
            string path = files[0].Path.LocalPath;
            var progs = ScriptSerializer.LoadProgFile(path);
            _undoStacks.Clear();
            _redoStacks.Clear();
            foreach (var p in progs)
            {
                _vm.LoadProgram(p.ProgramNumber, p);
            }
            UpdateUndoRedoButtonState();
            var txtTitle = this.FindControl<TextBox>("TxtScriptTitle");
            if (txtTitle != null) txtTitle.Text = System.IO.Path.GetFileName(path);
            RefreshProgSelector(0);
            LoadCurrentProgramToUI();
            _vm.MainThreads[0].Log($"[PROG] Loaded project '{path}' with {progs.Count} programs");
        }
    }

    private async void BtnSavePro_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null || _vm == null) return;

        SyncUIToProgram();
        var rootFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(System.IO.Path.GetFullPath(ARCOS.Core.Common.ArcosPaths.RootDirectory)));
        var txtTitle = this.FindControl<TextBox>("TxtScriptTitle");
        string suggested = (!string.IsNullOrWhiteSpace(txtTitle?.Text) && (txtTitle.Text.EndsWith(".prog", StringComparison.OrdinalIgnoreCase) || txtTitle.Text.EndsWith(".pro", StringComparison.OrdinalIgnoreCase)))
            ? txtTitle.Text
            : "Master.prog";

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "儲存 .prog 專案檔 (Save .prog Project)",
            SuggestedStartLocation = rootFolder,
            DefaultExtension = "prog",
            SuggestedFileName = suggested,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("ARCOSharp Project (*.prog)") { Patterns = new[] { "*.prog" } }
            }
        });

        if (file != null)
        {
            string path = file.Path.LocalPath;
            var progsToSave = new List<ProgramTable>();
            for (int i = 0; i < 100; i++)
            {
                var prog = _vm.GetProgram(i);
                if (prog.Count > 0) progsToSave.Add(prog);
            }
            ScriptSerializer.SaveProgFile(path, progsToSave);
            if (txtTitle != null) txtTitle.Text = System.IO.Path.GetFileName(path);
            _vm.MainThreads[0].Log($"[PROG] Saved {progsToSave.Count} programs to '{path}'");
        }
    }

    private async void BtnReadInit_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null || _vm == null) return;

        var scriptFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(System.IO.Path.GetFullPath(ARCOS.Core.Common.ArcosPaths.ScriptDirectory)));
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "讀取 .init 腳本 (Read .init Script)",
            SuggestedStartLocation = scriptFolder,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ARCOSharp Script (*.init)") { Patterns = new[] { "*.init" } }
            }
        });

        if (files.Count > 0)
        {
            string path = files[0].Path.LocalPath;
            var prog = ScriptSerializer.LoadInitFile(path, _currentProgNo, System.IO.Path.GetFileNameWithoutExtension(path));
            _vm.LoadProgram(_currentProgNo, prog);
            var txtTitle = this.FindControl<TextBox>("TxtScriptTitle");
            if (txtTitle != null) txtTitle.Text = System.IO.Path.GetFileName(path);
            RefreshProgSelector(_currentProgNo);
            LoadCurrentProgramToUI();
            _vm.MainThreads[0].Log($"[INIT] Loaded script '{path}' into Program slot {_currentProgNo}");
        }
    }

    private async void BtnSaveInit_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null || _vm == null) return;

        SyncUIToProgram();
        var scriptFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(System.IO.Path.GetFullPath(ARCOS.Core.Common.ArcosPaths.ScriptDirectory)));
        var prog = _vm.GetProgram(_currentProgNo);
        string suggested = (!string.IsNullOrWhiteSpace(prog.Name)) ? $"{prog.Name}.init" : $"Program_{_currentProgNo}.init";

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "儲存 .init 腳本 (Save .init Script)",
            SuggestedStartLocation = scriptFolder,
            DefaultExtension = "init",
            SuggestedFileName = suggested,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("ARCOSharp Script (*.init)") { Patterns = new[] { "*.init" } }
            }
        });

        if (file != null)
        {
            string path = file.Path.LocalPath;
            ScriptSerializer.SaveInitFile(path, prog);
            var txtTitle = this.FindControl<TextBox>("TxtScriptTitle");
            if (txtTitle != null) txtTitle.Text = System.IO.Path.GetFileName(path);
            _vm.MainThreads[0].Log($"[INIT] Saved Program {_currentProgNo} to '{path}'");
        }
    }

    private async void BtnLoad_Click(object? sender, RoutedEventArgs e)
    {
        BtnReadPro_Click(sender, e);
    }

    // =========================================================
    // 獨立程式區 Undo / Redo 管理核心 (以程式區隔離記錄)
    // =========================================================

    private void OnSequencerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearRangeSelection();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Z)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) PerformRedo();
                else PerformUndo();
                e.Handled = true;
            }
            else if (e.Key == Key.Y)
            {
                PerformRedo();
                e.Handled = true;
            }
            else if (e.Key == Key.F)
            {
                var searchPanel = this.FindControl<Border>("SearchPanel");
                var txtSearch = this.FindControl<TextBox>("TxtSearchKeyword");
                if (searchPanel != null)
                {
                    searchPanel.IsVisible = true;
                    txtSearch?.Focus();
                }
                e.Handled = true;
            }
        }
    }

    private List<(string Command, string Parameter)> CaptureCurrentRows()
    {
        var list = new List<(string Command, string Parameter)>(_rows.Count);
        for (int i = 0; i < _rows.Count; i++)
        {
            list.Add((_rows[i].Command ?? string.Empty, _rows[i].Parameter ?? string.Empty));
        }
        return list;
    }

    private static bool AreSnapshotsEqual(List<(string Command, string Parameter)> a, List<(string Command, string Parameter)> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Command != b[i].Command || a[i].Parameter != b[i].Parameter)
                return false;
        }
        return true;
    }

    public void PushUndoSnapshot()
    {
        if (!_undoStacks.TryGetValue(_currentProgNo, out var undoStack))
        {
            undoStack = new Stack<List<(string Command, string Parameter)>>();
            _undoStacks[_currentProgNo] = undoStack;
        }

        var current = CaptureCurrentRows();
        // 避免連續推入完全相同的快照
        if (undoStack.Count > 0 && AreSnapshotsEqual(undoStack.Peek(), current))
        {
            return;
        }

        undoStack.Push(current);

        // 限制歷程深度
        if (undoStack.Count > MaxHistoryDepth)
        {
            var arr = undoStack.ToArray();
            undoStack.Clear();
            for (int i = MaxHistoryDepth - 1; i >= 0; i--)
            {
                undoStack.Push(arr[i]);
            }
        }

        // 新增修改時，清空當前程式區的 Redo 堆疊
        if (_redoStacks.TryGetValue(_currentProgNo, out var redoStack))
        {
            redoStack.Clear();
        }

        UpdateUndoRedoButtonState();
    }

    public void PerformUndo()
    {
        if (!_undoStacks.TryGetValue(_currentProgNo, out var undoStack) || undoStack.Count == 0)
            return;

        if (!_redoStacks.TryGetValue(_currentProgNo, out var redoStack))
        {
            redoStack = new Stack<List<(string Command, string Parameter)>>();
            _redoStacks[_currentProgNo] = redoStack;
        }

        // 將目前狀態推入 Redo 堆疊
        var currentState = CaptureCurrentRows();
        redoStack.Push(currentState);

        // 取出上一步狀態並應用
        var previousState = undoStack.Pop();
        ApplySnapshot(previousState);
        UpdateUndoRedoButtonState();
    }

    public void PerformRedo()
    {
        if (!_redoStacks.TryGetValue(_currentProgNo, out var redoStack) || redoStack.Count == 0)
            return;

        if (!_undoStacks.TryGetValue(_currentProgNo, out var undoStack))
        {
            undoStack = new Stack<List<(string Command, string Parameter)>>();
            _undoStacks[_currentProgNo] = undoStack;
        }

        // 將目前狀態推入 Undo 堆疊
        var currentState = CaptureCurrentRows();
        undoStack.Push(currentState);

        // 取出重做狀態並應用
        var nextState = redoStack.Pop();
        ApplySnapshot(nextState);
        UpdateUndoRedoButtonState();
    }

    private void ApplySnapshot(List<(string Command, string Parameter)> snapshot)
    {
        var grid = this.FindControl<DataGrid>("GridCommands");
        int prevSelected = grid?.SelectedIndex ?? 0;

        _rows.Clear();
        for (int i = 0; i < snapshot.Count; i++)
        {
            _rows.Add(new SequencerRowViewModel
            {
                LineNumber = i,
                Command = snapshot[i].Command,
                Parameter = snapshot[i].Parameter,
                IsCurrentPC = false
            });
        }

        SyncUIToProgram();

        if (_rows.Count > 0)
        {
            int selectIdx = Math.Clamp(prevSelected, 0, _rows.Count - 1);
            NavigateToRow(selectIdx);
        }
    }

    public void UpdateUndoRedoButtonState()
    {
        var btnUndo = this.FindControl<Button>("BtnUndo");
        var btnRedo = this.FindControl<Button>("BtnRedo");

        bool canUndo = _undoStacks.TryGetValue(_currentProgNo, out var u) && u.Count > 0;
        bool canRedo = _redoStacks.TryGetValue(_currentProgNo, out var r) && r.Count > 0;

        if (btnUndo != null)
        {
            btnUndo.IsEnabled = canUndo;
            btnUndo.Opacity = canUndo ? 1.0 : 0.4;
            ToolTip.SetTip(btnUndo, canUndo ? $"復原 (Undo) - 尚有 {u!.Count} 步" : "復原 (Undo)");
        }

        if (btnRedo != null)
        {
            btnRedo.IsEnabled = canRedo;
            btnRedo.Opacity = canRedo ? 1.0 : 0.4;
            ToolTip.SetTip(btnRedo, canRedo ? $"重做 (Redo) - 尚有 {r!.Count} 步" : "重做 (Redo)");
        }
    }

    public int GetUndoCount(int? progNo = null)
    {
        int p = progNo ?? _currentProgNo;
        return _undoStacks.TryGetValue(p, out var s) ? s.Count : 0;
    }

    public int GetRedoCount(int? progNo = null)
    {
        int p = progNo ?? _currentProgNo;
        return _redoStacks.TryGetValue(p, out var s) ? s.Count : 0;
    }

}
