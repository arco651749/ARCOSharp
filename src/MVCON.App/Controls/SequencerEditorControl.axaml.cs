using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ExecutionContext = MVCON.Core.Execution.ExecutionContext;
using MVCON.App.Dialogs;
using MVCON.App.Guidance;
using MVCON.App.ViewModels;
using MVCON.Core.Execution;
using MVCON.Core.Model;
using MVCON.Core.Serialization;

namespace MVCON.App.Controls;

public partial class SequencerEditorControl : UserControl
{
    private MvconThreadVM? _vm;
    private int _currentProgNo = 0;
    private bool _isLocked = false;
    private readonly ObservableCollection<SequencerRowViewModel> _rows = new();
    private readonly DispatcherTimer _autoTimer;
    private readonly DispatcherTimer _pcDrainerTimer;
    private int _latestNoticedPC = -1;
    private int _currentRenderedPC = -1;

    public SequencerEditorControl()
    {
        InitializeComponent();

        var grid = this.FindControl<DataGrid>("GridCommands");
        if (grid != null)
        {
            grid.ItemsSource = _rows;
        }

        var progSel = this.FindControl<ComboBox>("ProgSelector");
        if (progSel != null)
        {
            var items = new List<string> { "Main" };
            for (int i = 1; i <= 12; i++) items.Add($"Thread {i}");
            progSel.ItemsSource = items;
            progSel.SelectedIndex = 0;
            progSel.SelectionChanged += ProgSelector_SelectionChanged;
        }

        BindToolbarButtons();
        BindSearchControls();

        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _autoTimer.Tick += AutoTimer_Tick;

        // 獨立 UI 節流排程器 (33ms / ~30 FPS)：定時非同步抽取最新 PC，絕不阻塞 CMDEXECUTE 微秒級調度
        _pcDrainerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _pcDrainerTimer.Tick += (_, _) => DrainPCPointer();
        _pcDrainerTimer.Start();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void AttachVM(MvconThreadVM vm)
    {
        _vm = vm;
        for (int i = 0; i < _vm.MainThreads.Count; i++)
        {
            int threadId = i;
            _vm.MainThreads[i].OnProgIndexNoticed += (pc) =>
            {
                if (_currentProgNo == threadId)
                {
                    Interlocked.Exchange(ref _latestNoticedPC, pc);
                }
            };
        }
        EnsureDefaultSampleSequence();
        LoadCurrentProgramToUI();
        UpdateMachineStatus("IDLE");
    }

    private void EnsureDefaultSampleSequence()
    {
        if (_vm == null) return;
        var prog0 = _vm.GetProgram(0);
        if (prog0.Count == 0)
        {
            prog0.Add(new CommandItem(0, "System@#Comment----->>\t生產自動檢測主流程 (標準 MVCON 腳本)", "System@#Comment----->>", "System", "#Comment----->>", "生產自動檢測主流程 (標準 MVCON 腳本)", true, false));
            prog0.Add(new CommandItem(1, "System@Log Message\tSystem Initialized, Start Inspection Routine", "System@Log Message", "System", "Log Message", "System Initialized, Start Inspection Routine", false, false));
            prog0.Add(new CommandItem(2, "System@Record\tCycleTimer,InspectionStart", "System@Record", "System", "Record", "CycleTimer,InspectionStart", false, false));
            prog0.Add(new CommandItem(3, "IO@SetPort\tP0.0,0.2", "IO@SetPort", "IO", "SetPort", "P0.0,0.2", false, false));
            prog0.Add(new CommandItem(4, "Variable@Set\tCycleCount,0", "Variable@Set", "Variable", "Set", "CycleCount,0", false, false));
            prog0.Add(new CommandItem(5, "Point&Path@Calculate\tP_Inspect=(150.0, 50.0, 20.0)", "Point&Path@Calculate", "Point&Path", "Calculate", "P_Inspect=(150.0, 50.0, 20.0)", false, false));
            prog0.Add(new CommandItem(6, "Thread@Label\tInspectionLoop", "Thread@Label", "Thread", "Label", "InspectionLoop", false, false));
            prog0.Add(new CommandItem(7, "Motion@GoPoint\tMotor1,80,P_Inspect", "Motion@GoPoint", "Motion", "GoPoint", "Motor1,80,P_Inspect", false, false));
            prog0.Add(new CommandItem(8, "Motion@WaitMotor\tMotor1,TimeOut=5", "Motion@WaitMotor", "Motion", "WaitMotor", "Motor1,TimeOut=5", false, false));
            prog0.Add(new CommandItem(9, "Capture@Snap\tCam1", "Capture@Snap", "Capture", "Snap", "Cam1", false, false));
            prog0.Add(new CommandItem(10, "Capture@PatternMatchOne\tCam1,ROI_Align,Template.png,0.85", "Capture@PatternMatchOne", "Capture", "PatternMatchOne", "Cam1,ROI_Align,Template.png,0.85", false, false));
            prog0.Add(new CommandItem(11, "IO@ClrPort\tP0.0", "IO@ClrPort", "IO", "ClrPort", "P0.0", false, false));
            prog0.Add(new CommandItem(12, "Variable@Inc\tCycleCount", "Variable@Inc", "Variable", "Inc", "CycleCount", false, false));
            prog0.Add(new CommandItem(13, "System@Delay\t0.1", "System@Delay", "System", "Delay", "0.1", false, false));
            prog0.Add(new CommandItem(14, "Thread@Jump\tInspectionLoop,$CycleCount<3", "Thread@Jump", "Thread", "Jump", "InspectionLoop,$CycleCount<3", false, false));
            prog0.Add(new CommandItem(15, "System@LogRecordCycleTime\tCycleTimer,s", "System@LogRecordCycleTime", "System", "LogRecordCycleTime", "CycleTimer,s", false, false));
            prog0.Add(new CommandItem(16, "System@Log Message\t[PASS] Inspection Routine Completed Successfully", "System@Log Message", "System", "Log Message", "[PASS] Inspection Routine Completed Successfully", false, false));
        }
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

    private async void OnCommandCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isLocked) return;
        var topLevel = TopLevel.GetTopLevel(this) as Window;
        if (topLevel == null) return;

        var row = (sender as Control)?.DataContext as SequencerRowViewModel;
        if (row == null) return;

        string? selectedCmd = await ComSelectWindow.SelectCommandAsync(topLevel, _vm?.Executor.Registry);
        if (string.IsNullOrWhiteSpace(selectedCmd)) return;

        string? confirmedParam = await ParameterGuidanceWindow.ShowGuidanceAsync(topLevel, selectedCmd, row.Parameter, _vm?.Engines);
        if (confirmedParam != null)
        {
            row.Command = selectedCmd;
            row.Parameter = confirmedParam;
            SyncUIToProgram();
        }
    }

    private async void OnParameterCellDoubleTapped(object? sender, TappedEventArgs e)
    {
        await OpenParameterGuidance(sender);
    }

    private async void OnParameterCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            await OpenParameterGuidance(sender);
        }
    }

    private async Task OpenParameterGuidance(object? sender)
    {
        if (_isLocked) return;
        var topLevel = TopLevel.GetTopLevel(this) as Window;
        if (topLevel == null) return;

        var row = (sender as Control)?.DataContext as SequencerRowViewModel;
        if (row == null) return;

        string targetCmd = !string.IsNullOrWhiteSpace(row.Command) ? row.Command : "System@Log Message";
        string? updatedParam = await ParameterGuidanceManager.Instance.RequestGuidanceAsync(targetCmd, row.Parameter, _vm?.Engines, topLevel);
        if (updatedParam != null)
        {
            row.Parameter = updatedParam;
            SyncUIToProgram();
        }
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
        if (btnDelete != null) btnDelete.Click += BtnDelete_Click;

        var btnMinus = this.FindControl<Button>("BtnMinusLine");
        if (btnMinus != null) btnMinus.Click += BtnDelete_Click;

        var btnSave = this.FindControl<Button>("BtnSave");
        if (btnSave != null) btnSave.Click += BtnSave_Click;

        var btnSaveLine = this.FindControl<Button>("BtnSaveLine");
        if (btnSaveLine != null) btnSaveLine.Click += (_, _) => SyncUIToProgram();

        var btnLoad = this.FindControl<Button>("BtnLoad");
        if (btnLoad != null) btnLoad.Click += BtnLoad_Click;

        // 導航與權限
        var btnRewind = this.FindControl<Button>("BtnRewind");
        if (btnRewind != null) btnRewind.Click += (_, _) => NavigateToRow(0);

        var btnForward = this.FindControl<Button>("BtnForward");
        if (btnForward != null) btnForward.Click += (_, _) => NavigateToRow(_rows.Count - 1);

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

        if (btnSearch != null && searchPanel != null)
        {
            btnSearch.Click += (_, _) =>
            {
                searchPanel.IsVisible = !searchPanel.IsVisible;
                if (searchPanel.IsVisible) txtSearch?.Focus();
            };
        }

        if (btnCloseSearch != null && searchPanel != null)
        {
            btnCloseSearch.Click += (_, _) => searchPanel.IsVisible = false;
        }

        if (txtSearch != null)
        {
            txtSearch.KeyUp += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    PerformSearch(txtSearch.Text);
                }
            };
        }
    }

    private void PerformSearch(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return;
        var grid = this.FindControl<DataGrid>("GridCommands");
        int startIdx = (grid?.SelectedIndex ?? -1) + 1;
        if (startIdx >= _rows.Count) startIdx = 0;

        for (int i = 0; i < _rows.Count; i++)
        {
            int idx = (startIdx + i) % _rows.Count;
            var row = _rows[idx];
            if (row.Command.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.Parameter.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                NavigateToRow(idx);
                return;
            }
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

    private void ChangeProg(int delta)
    {
        var progSel = this.FindControl<ComboBox>("ProgSelector");
        if (progSel != null)
        {
            int maxIdx = (progSel.ItemsSource as System.Collections.IList)?.Count ?? 13;
            int next = Math.Clamp(progSel.SelectedIndex + delta, 0, maxIdx - 1);
            progSel.SelectedIndex = next;
        }
    }

    private void ProgSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var progSel = this.FindControl<ComboBox>("ProgSelector");
        if (progSel != null && progSel.SelectedIndex >= 0)
        {
            SyncUIToProgram();
            _currentProgNo = progSel.SelectedIndex;
            LoadCurrentProgramToUI();

            var txtTitle = this.FindControl<TextBox>("TxtScriptTitle");
            if (txtTitle != null)
            {
                txtTitle.Text = _currentProgNo == 0 ? "Master_V1.pro" : $"Thread_{_currentProgNo}.pro";
            }
        }
    }

    private void LoadCurrentProgramToUI()
    {
        _rows.Clear();
        _currentRenderedPC = -1;
        if (_vm == null) return;

        var prog = _vm.GetProgram(_currentProgNo);
        var ctx = _vm.MainThreads[_currentProgNo < 13 ? _currentProgNo : 0];

        for (int i = 0; i < prog.Count; i++)
        {
            var item = prog.Items[i];
            _rows.Add(new SequencerRowViewModel
            {
                LineNumber = i,
                Command = item.Command,
                Parameter = item.RawParameter,
                IsCurrentPC = (i == ctx.ProgIndex && ctx.IsRunning)
            });
        }
        UpdatePCPointer(ctx.ProgIndex);
        DrainPCPointer();
    }

    public void SyncUIToProgram()
    {
        if (_vm == null) return;
        var prog = new ProgramTable { ProgramNumber = _currentProgNo, Name = $"Program_{_currentProgNo}" };
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            row.LineNumber = i;
            prog.Add(CommandItem.Parse($"{row.Command}\t{row.Parameter}", i));
        }
        _vm.LoadProgram(_currentProgNo, prog);
        var ctx = _vm.MainThreads[_currentProgNo < 13 ? _currentProgNo : 0];
        ctx.Program = prog;
    }

    public void UpdatePCPointer(int pc)
    {
        // 零等待原子寫入：直接通知最新位置，絕不阻塞 CMDEXECUTE 調度
        Interlocked.Exchange(ref _latestNoticedPC, pc);
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
        int tIdx = _currentProgNo < 13 ? _currentProgNo : 0;
        return _vm!.MainThreads[tIdx];
    }

    private void BtnStep_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        SyncUIToProgram();

        var ctx = GetActiveContext();
        if (!ctx.IsRunning)
        {
            _vm.StartThread(ctx.ThreadId, _currentProgNo, ctx.ProgIndex >= 0 ? ctx.ProgIndex : 0);
        }

        UpdateMachineStatus("RUNNING");
        _vm.Executor.ExecuteStep(ctx);
        UpdatePCPointer(ctx.ProgIndex);

        if (!ctx.IsRunning || ctx.ProgIndex >= _rows.Count)
        {
            UpdateMachineStatus("IDLE");
        }
    }

    private void BtnAuto_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        SyncUIToProgram();

        var ctx = GetActiveContext();
        if (!ctx.IsRunning)
        {
            _vm.StartThread(ctx.ThreadId, _currentProgNo, 0);
        }

        UpdateMachineStatus("RUNNING");
        _autoTimer.Start();
    }

    private void AutoTimer_Tick(object? sender, EventArgs e)
    {
        if (_vm == null) return;
        var ctx = GetActiveContext();

        if (!ctx.IsRunning || ctx.ProgIndex >= _rows.Count)
        {
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
        _autoTimer.Stop();
        if (_vm != null)
        {
            var ctx = GetActiveContext();
            ctx.IsRunning = false;
            ctx.ProgIndex = 0;
            ctx.State.Reset();
            UpdatePCPointer(0);
            UpdateMachineStatus("IDLE");
        }
    }

    private void BtnJump_Click(object? sender, RoutedEventArgs e)
    {
        var grid = this.FindControl<DataGrid>("GridCommands");
        if (grid?.SelectedItem is SequencerRowViewModel row && _vm != null)
        {
            var ctx = GetActiveContext();
            ctx.ProgIndex = row.LineNumber;
            UpdatePCPointer(row.LineNumber);
            ctx.Log($"[PC JUMP] PC pointer jumped to Line {row.LineNumber}: {row.Command}");
        }
    }

    private void BtnAdd_Click(object? sender, RoutedEventArgs e)
    {
        var grid = this.FindControl<DataGrid>("GridCommands");
        int insertIndex = (grid?.SelectedIndex ?? -1) + 1;
        if (insertIndex < 0 || insertIndex > _rows.Count) insertIndex = _rows.Count;

        _rows.Insert(insertIndex, new SequencerRowViewModel
        {
            LineNumber = insertIndex,
            Command = "System@Delay",
            Parameter = "0.1"
        });

        RenumberRows();
        SyncUIToProgram();
        NavigateToRow(insertIndex);
    }

    private void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        var grid = this.FindControl<DataGrid>("GridCommands");
        if (grid?.SelectedItem is SequencerRowViewModel row)
        {
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
            DefaultExtension = "pro",
            SuggestedFileName = "Master_V1.pro"
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

    private async void BtnLoad_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null || _vm == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Program Table",
            AllowMultiple = false
        });

        if (files.Count > 0)
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            string content = await reader.ReadToEndAsync();

            var loadedProgs = ScriptSerializer.DeserializePro(content);
            foreach (var kvp in loadedProgs)
            {
                _vm.LoadProgram(kvp.Key, kvp.Value);
            }
            LoadCurrentProgramToUI();
        }
    }
}