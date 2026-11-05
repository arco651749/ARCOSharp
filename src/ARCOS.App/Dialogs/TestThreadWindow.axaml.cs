using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ARCOS.App.Guidance;
using ARCOS.App.Services;
using ARCOS.App.ViewModels;
using ARCOS.Core.Common;
using ARCOS.Core.Execution;
using ARCOS.Core.Model;
using ARCOS.Core.Serialization;
using ArcosExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.App.Dialogs;

public partial class TestThreadWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly ArcosThreadVM? _vm;
    private readonly Window? _ownerWindow;
    private readonly ArcosExecutionContext? _testContext;
    private readonly ObservableCollection<SequencerRowViewModel> _rows = new();
    public ObservableCollection<SequencerRowViewModel> Rows => _rows;

    // 獨立 Undo / Redo 堆疊
    private readonly Dictionary<int, Stack<List<(string Command, string Parameter)>>> _undoStacks = new();
    private readonly Dictionary<int, Stack<List<(string Command, string Parameter)>>> _redoStacks = new();
    private const int MaxUndoDepth = 100;

    private int _currentProgNo = 0;
    private int _currentRenderedPC = -1;
    private int _latestNoticedPC = 0;
    private readonly DispatcherTimer? _pcDrainerTimer;
    private readonly DispatcherTimer? _autoTimer;

    public TestThreadWindow()
    {
        InitializeComponent();
    }

    public TestThreadWindow(ArcosThreadVM vm, Window owner)
    {
        InitializeComponent();
        _vm = vm;
        _ownerWindow = owner;

        // 建立專屬獨立 TestThread& 上下文 (帶 & 特權，Pause 閘門亦可調試)
        var testProg = new ProgramTable { ProgramNumber = 99, Name = "TestProgram" };
        _testContext = new ArcosExecutionContext(99, "TestThread&", testProg, _vm?.Engines!, _vm?.Drivers!)
        {
            VM = _vm,
            CurrentSection = "Test",
            IsRunning = false
        };
        _testContext.OnProgIndexNoticed += (pc) => UpdatePCPointer(pc);

        var grid = this.FindControl<DataGrid>("GridCommands");
        if (grid != null)
        {
            grid.ItemsSource = _rows;
        }

        InitProgSelector();
        InitVariableSectionSelector();
        InitOpacitySlider();
        BindToolbarButtons();
        BindSearchControls();
        SequencerClipboardManager.Instance.SelectionChanged += OnGlobalSelectionChanged;

        this.KeyDown += OnWindowKeyDown;

        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _autoTimer.Tick += AutoTimer_Tick;

        _pcDrainerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _pcDrainerTimer.Tick += (_, _) => DrainPCPointer();
        _pcDrainerTimer.Start();

        if (_vm != null)
        {
            _vm.OnProgramLoaded += (path, isPro) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var txtTitle = this.FindControl<TextBox>("TxtScriptTitle");
                    if (txtTitle != null) txtTitle.Text = System.IO.Path.GetFileName(path);
                    LoadProgramSlotToUI(_currentProgNo);
                });
            };
        }

        LoadProgramSlotToUI(0);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 關鍵需求：按 "X" 不銷毀，而是最小化保存狀態，按 Ctrl+T 隨時喚醒
        e.Cancel = true;
        this.WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// 以 Topmost 模式浮動顯示，但絕不搶奪 MainWindow 焦點
    /// </summary>
    public void ShowWithoutActivation(Window owner)
    {
        if (!this.IsVisible)
        {
            this.Show(owner);
        }
        else if (this.WindowState == WindowState.Minimized)
        {
            this.WindowState = WindowState.Normal;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var handle = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (handle != IntPtr.Zero)
                {
                    ShowWindow(handle, SW_SHOWNOACTIVATE);
                    SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }
            }
            catch { }
        }

        Dispatcher.UIThread.Post(() =>
        {
            owner?.Focus();
        }, DispatcherPriority.Input);
    }

    private void InitProgSelector()
    {
        var progSel = this.FindControl<ComboBox>("ProgSelector");
        if (progSel != null)
        {
            var items = new List<string> { "Main" };
            for (int i = 1; i <= 12; i++) items.Add($"Thread {i}");
            items.Add("Test Slot (99)");
            progSel.ItemsSource = items;
            progSel.SelectedIndex = 0;
            progSel.SelectionChanged += (s, e) =>
            {
                if (progSel.SelectedIndex >= 0)
                {
                    int slot = progSel.SelectedIndex == 13 ? 99 : progSel.SelectedIndex;
                    LoadProgramSlotToUI(slot);
                }
            };
        }

        var btnPrev = this.FindControl<Button>("BtnProgPrev");
        var btnNext = this.FindControl<Button>("BtnProgNext");
        if (btnPrev != null) btnPrev.Click += (_, _) => ChangeProg(-1);
        if (btnNext != null) btnNext.Click += (_, _) => ChangeProg(1);
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

    /// <summary>
    /// [1] 變數 Section 作用域選單初始化 (使用者紅圈 1)
    /// </summary>
    private void InitVariableSectionSelector()
    {
        var cmb = this.FindControl<ComboBox>("CmbVariableSection");
        if (cmb == null) return;

        var sections = new List<string> { "Test", "Global", "ThreadMain" };
        for (int i = 1; i <= 12; i++) sections.Add($"Thread{i}");

        if (_vm?.Engines?.Variable != null)
        {
            foreach (var s in _vm.Engines.Variable.GetAllSections())
            {
                if (!sections.Contains(s, StringComparer.OrdinalIgnoreCase))
                {
                    sections.Add(s);
                }
            }
        }

        cmb.ItemsSource = sections;
        cmb.SelectedItem = "Test";
        cmb.SelectionChanged += (s, e) =>
        {
            if (cmb.SelectedItem is string sec)
            {
                _testContext.CurrentSection = sec;
            }
        };
    }

    /// <summary>
    /// [2] 底部透明度拉桿初始化 (使用者紅圈 2，可透視下方主畫面)
    /// </summary>
    private void InitOpacitySlider()
    {
        var slider = this.FindControl<Slider>("SliderOpacity");
        var txtVal = this.FindControl<TextBlock>("TxtOpacityValue");

        if (slider != null)
        {
            slider.PropertyChanged += (s, e) =>
            {
                if (e.Property == Slider.ValueProperty)
                {
                    this.Opacity = slider.Value;
                    if (txtVal != null)
                    {
                        txtVal.Text = $"{(int)(slider.Value * 100)}%";
                    }
                }
            };
        }
    }

    public void LoadProgramSlot(int progNo) => LoadProgramSlotToUI(progNo);

    private void LoadProgramSlotToUI(int progNo)
    {
        _currentProgNo = progNo;
        _rows.Clear();
        _currentRenderedPC = -1;

        if (_vm != null)
        {
            var prog = _vm.GetProgram(_currentProgNo < 100 ? _currentProgNo : 0);
            for (int i = 0; i < prog.Count; i++)
            {
                var item = prog.Items[i];
                _rows.Add(new SequencerRowViewModel
                {
                    LineNumber = i,
                    Command = item.Command,
                    Parameter = item.RawParameter,
                    IsCurrentPC = false
                });
            }
            if (_testContext != null)
            {
                _testContext.Program = prog;
                _testContext.ProgIndex = 1;
            }
        }

        var txtTitle = this.FindControl<TextBox>("TxtScriptTitle");
        if (txtTitle != null)
        {
            txtTitle.Text = _currentProgNo == 0 ? "Master_V1.pro" : $"Program_{_currentProgNo}.pro";
        }

        UpdateUndoRedoButtonState();
        UpdatePCPointer(1);
        SyncIoDefinitionsFromRows();
    }

    public void SyncUIToProgram()
    {
        var prog = new ProgramTable { ProgramNumber = _currentProgNo, Name = $"Program_{_currentProgNo}" };
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            r.LineNumber = i;
            prog.Add(CommandItem.Parse($"{r.Command}\t{r.Parameter}", i));
        }
        _testContext.Program = prog;
    }

    private void DrainPCPointer()
    {
        int targetPC = Volatile.Read(ref _latestNoticedPC);
        if (targetPC == _currentRenderedPC) return;

        if (_currentRenderedPC >= 0 && _currentRenderedPC < _rows.Count)
        {
            _rows[_currentRenderedPC].IsCurrentPC = false;
        }

        _currentRenderedPC = targetPC;

        if (_currentRenderedPC >= 0 && _currentRenderedPC < _rows.Count)
        {
            _rows[_currentRenderedPC].IsCurrentPC = true;
            var grid = this.FindControl<DataGrid>("GridCommands");
            grid?.ScrollIntoView(_rows[_currentRenderedPC], null);
        }
    }

    public void UpdatePCPointer(int progIndex1Based)
    {
        int uiRow = progIndex1Based >= 1 ? progIndex1Based - 1 : 0;
        Interlocked.Exchange(ref _latestNoticedPC, uiRow);
    }

    private void BindToolbarButtons()
    {
        // 單步 Step
        var btnStep = this.FindControl<Button>("BtnStep");
        if (btnStep != null)
        {
            btnStep.Click += (_, _) =>
            {
                if (_vm == null || _testContext == null) return;
                SyncUIToProgram();
                bool wasRunning = _testContext.IsRunning;
                _testContext.IsRunning = true;
                _vm.Executor.ExecuteStep(_testContext);
                _testContext.IsRunning = wasRunning;
                UpdatePCPointer(_testContext.ProgIndex);
                DrainPCPointer();
            };
        }

        // Auto / Run
        var btnRun = this.FindControl<Button>("BtnRun");
        var btnAuto = this.FindControl<Button>("BtnAuto");
        Action toggleAuto = () =>
        {
            if (_autoTimer.IsEnabled)
            {
                _autoTimer.Stop();
                _testContext.IsRunning = false;
            }
            else
            {
                SyncUIToProgram();
                _testContext.IsRunning = true;
                _autoTimer.Start();
            }
        };
        if (btnRun != null) btnRun.Click += (_, _) => toggleAuto();
        if (btnAuto != null) btnAuto.Click += (_, _) => toggleAuto();

        // Pause
        var btnPause = this.FindControl<Button>("BtnPause");
        if (btnPause != null)
        {
            btnPause.Click += (_, _) =>
            {
                _autoTimer.Stop();
                _testContext.IsRunning = false;
            };
        }

        // Reset
        var btnReset = this.FindControl<Button>("BtnReset");
        if (btnReset != null)
        {
            btnReset.Click += (_, _) =>
            {
                _autoTimer.Stop();
                _testContext.IsRunning = false;
                _testContext.ProgIndex = 1;
                _testContext.CallStack.Clear();
                _testContext.State.Reset();
                UpdatePCPointer(1);
                DrainPCPointer();
            };
        }

        // Add Row
        var btnAdd = this.FindControl<Button>("BtnAdd");
        if (btnAdd != null)
        {
            btnAdd.Click += (_, _) =>
            {
                PushUndoSnapshot();
                var grid = this.FindControl<DataGrid>("GridCommands");
                int cur = grid?.SelectedIndex ?? (_rows.Count - 1);
                int ins = cur >= 0 ? cur + 1 : _rows.Count;
                _rows.Insert(ins, new SequencerRowViewModel { LineNumber = ins, Command = "Timer@Delay", Parameter = "100" });
                for (int i = 0; i < _rows.Count; i++) _rows[i].LineNumber = i;
                UpdateUndoRedoButtonState();
            };
        }

        // Delete Row
        var btnDel = this.FindControl<Button>("BtnDelete");
        var btnMinus = this.FindControl<Button>("BtnMinusLine");
        Action delRow = () =>
        {
            var grid = this.FindControl<DataGrid>("GridCommands");
            if (grid?.SelectedItem is SequencerRowViewModel row)
            {
                PushUndoSnapshot();
                _rows.Remove(row);
                for (int i = 0; i < _rows.Count; i++) _rows[i].LineNumber = i;
                UpdateUndoRedoButtonState();
            }
        };
        if (btnDel != null) btnDel.Click += (_, _) => delRow();
        if (btnMinus != null) btnMinus.Click += (_, _) => delRow();

        // Undo & Redo
        var btnUndo = this.FindControl<Button>("BtnUndo");
        if (btnUndo != null) btnUndo.Click += (_, _) => PerformUndo();

        var btnRedo = this.FindControl<Button>("BtnRedo");
        if (btnRedo != null) btnRedo.Click += (_, _) => PerformRedo();

        // Jump
        var btnJump = this.FindControl<Button>("BtnJump");
        if (btnJump != null)
        {
            btnJump.Click += (_, _) =>
            {
                var grid = this.FindControl<DataGrid>("GridCommands");
                int sel = grid?.SelectedIndex ?? -1;
                if (sel >= 0)
                {
                    _testContext.ProgIndex = sel + 1;
                    UpdatePCPointer(sel + 1);
                    DrainPCPointer();
                }
            };
        }

        // Refresh
        var btnRefresh = this.FindControl<Button>("BtnRefresh");
        if (btnRefresh != null) btnRefresh.Click += (_, _) => LoadProgramSlotToUI(_currentProgNo);

        // Save Line
        var btnSaveLine = this.FindControl<Button>("BtnSaveLine");
        if (btnSaveLine != null)
        {
            btnSaveLine.Click += (_, _) =>
            {
                PushUndoSnapshot();
                SyncUIToProgram();
            };
        }

        // Save Button
        var btnSave = this.FindControl<Button>("BtnSave");
        if (btnSave != null)
        {
            btnSave.Click += async (_, _) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null || _vm == null) return;
                SyncUIToProgram();
                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "儲存程式腳本 (Save Program)",
                    DefaultExtension = "pro"
                });
                if (file != null)
                {
                    string path = file.Path.LocalPath;
                    ScriptSerializer.SaveProFile(path, CollectProgramsToSave());
                }
            };
        }

        // Read .pro / Load
        var btnReadPro = this.FindControl<Button>("BtnReadPro");
        var btnLoad = this.FindControl<Button>("BtnLoad");
        Action readProAction = async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null || _vm == null) return;
            var rootFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(System.IO.Path.GetFullPath(ArcosPaths.RootDirectory)));
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "讀取 .pro 專案檔 (Read .pro Project)",
                SuggestedStartLocation = rootFolder,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("ARCOSharp Project (*.pro)") { Patterns = new[] { "*.pro" } }
                }
            });
            if (files.Count > 0)
            {
                string path = files[0].Path.LocalPath;
                var progs = ScriptSerializer.LoadProFile(path);
                _undoStacks.Clear();
                _redoStacks.Clear();
                foreach (var p in progs)
                {
                    _vm.LoadProgram(p.ProgramNumber, p);
                }
                UpdateUndoRedoButtonState();
                var txtTitle = this.FindControl<TextBox>("TxtScriptTitle");
                if (txtTitle != null) txtTitle.Text = System.IO.Path.GetFileName(path);
                LoadProgramSlotToUI(_currentProgNo);
                _vm.NotifyProgramLoaded(path, true);
            }
        };
        if (btnReadPro != null) btnReadPro.Click += (_, _) => readProAction();
        if (btnLoad != null) btnLoad.Click += (_, _) => readProAction();

        // Save .pro
        var btnSavePro = this.FindControl<Button>("BtnSavePro");
        if (btnSavePro != null)
        {
            btnSavePro.Click += async (_, _) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null || _vm == null) return;
                SyncUIToProgram();
                var rootFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(System.IO.Path.GetFullPath(ArcosPaths.RootDirectory)));
                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "儲存 .pro 專案檔 (Save .pro Project)",
                    SuggestedStartLocation = rootFolder,
                    DefaultExtension = "pro"
                });
                if (file != null)
                {
                    string path = file.Path.LocalPath;
                    ScriptSerializer.SaveProFile(path, CollectProgramsToSave());
                }
            };
        }

        // Read .init
        var btnReadInit = this.FindControl<Button>("BtnReadInit");
        if (btnReadInit != null)
        {
            btnReadInit.Click += async (_, _) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null || _vm == null) return;
                var scriptFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(System.IO.Path.GetFullPath(ArcosPaths.ScriptDirectory)));
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "讀取 .init 程式檔 (Read .init File)",
                    SuggestedStartLocation = scriptFolder,
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("ARCOSharp Program (*.init)") { Patterns = new[] { "*.init" } }
                    }
                });
                if (files.Count > 0)
                {
                    string path = files[0].Path.LocalPath;
                    var prog = ScriptSerializer.LoadInitFile(path, _currentProgNo);
                    _vm.LoadProgram(_currentProgNo, prog);
                    LoadProgramSlotToUI(_currentProgNo);
                }
            };
        }

        // Save .init
        var btnSaveInit = this.FindControl<Button>("BtnSaveInit");
        if (btnSaveInit != null)
        {
            btnSaveInit.Click += async (_, _) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null || _vm == null) return;
                SyncUIToProgram();
                var scriptFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(System.IO.Path.GetFullPath(ArcosPaths.ScriptDirectory)));
                var prog = _vm.GetProgram(_currentProgNo);
                string suggested = (!string.IsNullOrWhiteSpace(prog.Name)) ? $"{prog.Name}.init" : $"Program_{_currentProgNo}.init";
                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "儲存 .init 程式檔 (Save .init File)",
                    SuggestedStartLocation = scriptFolder,
                    SuggestedFileName = suggested,
                    DefaultExtension = "init"
                });
                if (file != null)
                {
                    ScriptSerializer.SaveInitFile(file.Path.LocalPath, prog);
                }
            };
        }

        // Git Diff
        var btnGit = this.FindControl<Button>("BtnGit");
        if (btnGit != null)
        {
            btnGit.Click += async (_, _) =>
            {
                string target = Path.Combine(ArcosPaths.RootDirectory, "Master.pro");
                SyncUIToProgram();
                var dlg = new GitDiffWindow(target, _vm, _currentProgNo);
                await dlg.ShowDialog(this);
                if (dlg.ReloadRequested)
                {
                    LoadProgramSlotToUI(_currentProgNo);
                }
            };
        }
    }

    private List<ProgramTable> CollectProgramsToSave()
    {
        var progs = new List<ProgramTable>();
        if (_vm == null) return progs;
        for (int i = 0; i < 100; i++)
        {
            var p = _vm.GetProgram(i);
            if (p.Count > 0) progs.Add(p);
        }
        return progs;
    }

    private void AutoTimer_Tick(object? sender, EventArgs e)
    {
        if (!_testContext.IsRunning || _rows.Count == 0)
        {
            _autoTimer.Stop();
            return;
        }

        _vm.Executor.ExecuteStep(_testContext);
        UpdatePCPointer(_testContext.ProgIndex);
        DrainPCPointer();

        if (!_testContext.IsRunning || _testContext.ProgIndex > _rows.Count)
        {
            _autoTimer.Stop();
            _testContext.IsRunning = false;
        }
    }

    private void BindSearchControls()
    {
        var btnSearch = this.FindControl<Button>("BtnSearch");
        var searchPanel = this.FindControl<Border>("SearchPanel");
        var txtKeyword = this.FindControl<TextBox>("TxtSearchKeyword");
        var btnFindPrev = this.FindControl<Button>("BtnFindPrev");
        var btnFindNext = this.FindControl<Button>("BtnFindNext");
        var btnClose = this.FindControl<Button>("BtnCloseSearch");

        if (btnSearch != null && searchPanel != null)
        {
            btnSearch.Click += (_, _) =>
            {
                searchPanel.IsVisible = !searchPanel.IsVisible;
                if (searchPanel.IsVisible) txtKeyword?.Focus();
            };
        }

        if (btnClose != null && searchPanel != null)
        {
            btnClose.Click += (_, _) => searchPanel.IsVisible = false;
        }

        Action<bool> doSearch = (forward) =>
        {
            string? kw = txtKeyword?.Text?.Trim();
            if (string.IsNullOrEmpty(kw) || _rows.Count == 0) return;

            var grid = this.FindControl<DataGrid>("GridCommands");
            int cur = grid?.SelectedIndex ?? 0;
            int step = forward ? 1 : -1;

            for (int count = 1; count <= _rows.Count; count++)
            {
                int idx = (cur + count * step) % _rows.Count;
                if (idx < 0) idx += _rows.Count;

                var r = _rows[idx];
                if ((r.Command?.Contains(kw, StringComparison.OrdinalIgnoreCase) == true) ||
                    (r.Parameter?.Contains(kw, StringComparison.OrdinalIgnoreCase) == true))
                {
                    if (grid != null)
                    {
                        grid.SelectedIndex = idx;
                        grid.ScrollIntoView(r, null);
                    }
                    var txtStatus = this.FindControl<TextBlock>("TxtSearchStatus");
                    if (txtStatus != null) txtStatus.Text = $"找到於第 {idx + 1} 行";
                    return;
                }
            }

            var txtStatusNone = this.FindControl<TextBlock>("TxtSearchStatus");
            if (txtStatusNone != null) txtStatusNone.Text = "查無關鍵字";
        };

        if (btnFindNext != null) btnFindNext.Click += (_, _) => doSearch(true);
        if (btnFindPrev != null) btnFindPrev.Click += (_, _) => doSearch(false);
    }

    // =========================================================
    // 快捷鍵指令選取與剪貼管理 (Shift選取 / Ctrl插入 / Ctrl+Alt搬移 / Alt刪除)
    // =========================================================
    private int? _anchorStartLine = null;

    private void OnGlobalSelectionChanged(object? activeEditor)
    {
        if (activeEditor != this)
        {
            _anchorStartLine = null;
            foreach (var r in _rows)
            {
                if (r.IsRangeSelected)
                    r.IsRangeSelected = false;
            }
        }
    }

    private void OnCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var row = (sender as Control)?.DataContext as SequencerRowViewModel;
        HandleShortcutPointerAction(row, e);
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;

        var visual = e.Source as Visual;
        if (visual == null) return;

        // 1. 若點擊在資料行上，由行與單元格事件處理，不觸發空白處動作
        if ((visual as Control)?.DataContext is SequencerRowViewModel) return;

        // 2. 排除點擊在表頭、滾動條、縮放或按鈕控制項上的情況
        if (IsInIgnoredGridArea(visual)) return;

        // 3. Ctrl + 點擊：原有之複製插入貼上
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            ExecuteCopyInsert(null);
            return;
        }

        // 4. 無修飾鍵：在空白處點擊滑鼠左鍵 -> 預設動作為「在尾端新增指令行 (同 + 按鈕)」
        if (e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            PushUndoSnapshot();
            var grid = this.FindControl<DataGrid>("GridCommands");
            int ins = _rows.Count;
            var newRow = new SequencerRowViewModel { LineNumber = ins, Command = "Timer@Delay", Parameter = "100" };
            _rows.Add(newRow);
            for (int i = 0; i < _rows.Count; i++) _rows[i].LineNumber = i;
            UpdateUndoRedoButtonState();
            if (grid != null)
            {
                grid.SelectedIndex = ins;
                grid.ScrollIntoView(newRow, null);
            }
            return;
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

        // 1. Shift + 滑鼠左鍵 (兩次點擊選取範圍，寫入全域選擇區)
        if (km == KeyModifiers.Shift)
        {
            e.Handled = true;
            if (row == null) return true;

            if (_anchorStartLine == null)
            {
                _anchorStartLine = row.LineNumber;
                foreach (var r in _rows)
                {
                    r.IsRangeSelected = (r.LineNumber == _anchorStartLine.Value);
                }
            }
            else
            {
                int start = Math.Min(_anchorStartLine.Value, row.LineNumber);
                int end = Math.Max(_anchorStartLine.Value, row.LineNumber);

                foreach (var r in _rows)
                {
                    r.IsRangeSelected = (r.LineNumber >= start && r.LineNumber <= end);
                }

                var selectedItems = _rows
                    .Where(r => r.IsRangeSelected)
                    .Select(r => (r.Command, r.Parameter))
                    .ToList();

                SequencerClipboardManager.Instance.SetSelection(this, start, end, selectedItems);
                _anchorStartLine = null;
            }
            return true;
        }

        // 2. Ctrl + Alt + 滑鼠左鍵 (本地搬移)
        if ((km & (KeyModifiers.Control | KeyModifiers.Alt)) == (KeyModifiers.Control | KeyModifiers.Alt) && !km.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            ExecuteLocalMove(row);
            return true;
        }

        // 3. Ctrl + 滑鼠左鍵 (複製插入，支援跨視窗)
        if (km == KeyModifiers.Control)
        {
            e.Handled = true;
            ExecuteCopyInsert(row);
            return true;
        }

        // 4. Alt + 滑鼠左鍵 (本地刪除選取區)
        if (km == KeyModifiers.Alt)
        {
            e.Handled = true;
            ExecuteLocalDelete(row);
            return true;
        }

        return false;
    }

    private void ExecuteCopyInsert(SequencerRowViewModel? targetRow)
    {
        if (!SequencerClipboardManager.Instance.HasItems) return;

        PushUndoSnapshot();

        int targetIndex = targetRow != null ? targetRow.LineNumber : _rows.Count;
        if (targetIndex < 0) targetIndex = 0;
        if (targetIndex > _rows.Count) targetIndex = _rows.Count;

        var items = SequencerClipboardManager.Instance.Buffer;
        for (int i = 0; i < items.Count; i++)
        {
            int ins = targetIndex + i;
            _rows.Insert(ins, new SequencerRowViewModel
            {
                Command = items[i].Command,
                Parameter = items[i].Parameter,
                LineNumber = ins
            });
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            _rows[i].LineNumber = i;
        }

        SyncUIToProgram();
        UpdateUndoRedoButtonState();
    }

    private void ExecuteLocalMove(SequencerRowViewModel? targetRow)
    {
        if (SequencerClipboardManager.Instance.SourceEditor != this || !SequencerClipboardManager.Instance.HasSelection)
            return;

        int minLine = SequencerClipboardManager.Instance.StartLine!.Value;
        int maxLine = SequencerClipboardManager.Instance.EndLine!.Value;

        if (minLine < 0 || maxLine >= _rows.Count) return;

        int targetIndex = targetRow != null ? targetRow.LineNumber : _rows.Count;
        if (targetIndex >= minLine && targetIndex <= maxLine)
            return;

        PushUndoSnapshot();

        var movingRows = new List<SequencerRowViewModel>();
        for (int i = minLine; i <= maxLine; i++)
        {
            movingRows.Add(new SequencerRowViewModel
            {
                Command = _rows[i].Command,
                Parameter = _rows[i].Parameter
            });
        }

        for (int i = maxLine; i >= minLine; i--)
        {
            _rows.RemoveAt(i);
        }

        if (targetIndex > maxLine)
        {
            targetIndex -= (maxLine - minLine + 1);
        }
        if (targetIndex < 0) targetIndex = 0;
        if (targetIndex > _rows.Count) targetIndex = _rows.Count;

        for (int i = 0; i < movingRows.Count; i++)
        {
            _rows.Insert(targetIndex + i, movingRows[i]);
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            _rows[i].LineNumber = i;
            _rows[i].IsRangeSelected = false;
        }

        SequencerClipboardManager.Instance.ClearSelection();
        SyncUIToProgram();
        UpdateUndoRedoButtonState();
    }

    private void ExecuteLocalDelete(SequencerRowViewModel? clickedRow)
    {
        if (SequencerClipboardManager.Instance.SourceEditor == this && SequencerClipboardManager.Instance.HasSelection)
        {
            int minLine = SequencerClipboardManager.Instance.StartLine!.Value;
            int maxLine = SequencerClipboardManager.Instance.EndLine!.Value;

            if (minLine >= 0 && minLine < _rows.Count)
            {
                PushUndoSnapshot();
                int end = Math.Min(maxLine, _rows.Count - 1);
                for (int i = end; i >= minLine; i--)
                {
                    _rows.RemoveAt(i);
                }

                for (int i = 0; i < _rows.Count; i++)
                {
                    _rows[i].LineNumber = i;
                    _rows[i].IsRangeSelected = false;
                }

                SequencerClipboardManager.Instance.ClearSelection();
                SyncUIToProgram();
                UpdateUndoRedoButtonState();
                return;
            }
        }

        if (clickedRow != null && _rows.Contains(clickedRow))
        {
            PushUndoSnapshot();
            _rows.Remove(clickedRow);
            for (int i = 0; i < _rows.Count; i++)
            {
                _rows[i].LineNumber = i;
                _rows[i].IsRangeSelected = false;
            }
            SyncUIToProgram();
            UpdateUndoRedoButtonState();
        }
    }

    public void SelectRange(int startLine, int endLine)
    {
        int start = Math.Min(startLine, endLine);
        int end = Math.Max(startLine, endLine);
        foreach (var r in _rows)
        {
            r.IsRangeSelected = (r.LineNumber >= start && r.LineNumber <= end);
        }
        var selectedItems = _rows
            .Where(r => r.IsRangeSelected)
            .Select(r => (r.Command, r.Parameter))
            .ToList();
        SequencerClipboardManager.Instance.SetSelection(this, start, end, selectedItems);
    }

    public void PasteFromClipboard(int targetIndex)
    {
        var targetRow = (targetIndex >= 0 && targetIndex < _rows.Count) ? _rows[targetIndex] : null;
        ExecuteCopyInsert(targetRow);
    }

    public void MoveSelectedRange(int targetIndex)
    {
        var targetRow = (targetIndex >= 0 && targetIndex < _rows.Count) ? _rows[targetIndex] : null;
        ExecuteLocalMove(targetRow);
    }

    public void DeleteSelectedRange()
    {
        ExecuteLocalDelete(null);
    }

    private async void OnCommandCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var row = (sender as Control)?.DataContext as SequencerRowViewModel;
        if (HandleShortcutPointerAction(row, e)) return;

        if (row != null)
        {
            SyncIoDefinitionsFromRows();
            bool wasTopmost = this.Topmost;
            string? selectedCmd = null;
            string? confirmedParam = null;
            try
            {
                this.Topmost = false;
                selectedCmd = await ComSelectWindow.SelectCommandAsync(this, _vm?.Executor.Registry);
                if (string.IsNullOrWhiteSpace(selectedCmd)) return;

                confirmedParam = await ParameterGuidanceManager.Instance.RequestGuidanceAsync(selectedCmd, row.Parameter, _vm?.Engines, this);
            }
            finally
            {
                this.Topmost = wasTopmost;
            }

            if (!string.IsNullOrWhiteSpace(selectedCmd))
            {
                PushUndoSnapshot();
                row.Command = selectedCmd;
                if (confirmedParam != null)
                {
                    row.Parameter = confirmedParam;
                }
                SyncUIToProgram();
                SyncIoDefinitionsFromRows();
                UpdateUndoRedoButtonState();
                e.Handled = true;
            }
        }
    }

    private void OnParameterCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var row = (sender as Control)?.DataContext as SequencerRowViewModel;
        if (HandleShortcutPointerAction(row, e)) return;
    }

    private async void OnParameterCellDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border border && border.DataContext is SequencerRowViewModel row)
        {
            SyncIoDefinitionsFromRows();
            string targetCmd = !string.IsNullOrWhiteSpace(row.Command) ? row.Command : "System@Log Message";
            bool wasTopmost = this.Topmost;
            string? updatedParam = null;
            try
            {
                this.Topmost = false;
                updatedParam = await ParameterGuidanceManager.Instance.RequestGuidanceAsync(targetCmd, row.Parameter, _vm?.Engines, this);
            }
            finally
            {
                this.Topmost = wasTopmost;
            }

            if (updatedParam != null)
            {
                PushUndoSnapshot();
                row.Parameter = updatedParam;
                SyncUIToProgram();
                SyncIoDefinitionsFromRows();
                UpdateUndoRedoButtonState();
            }
            e.Handled = true;
        }
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

    private void PushUndoSnapshot()
    {
        if (!_undoStacks.TryGetValue(_currentProgNo, out var uStack))
        {
            uStack = new Stack<List<(string Command, string Parameter)>>();
            _undoStacks[_currentProgNo] = uStack;
        }

        var snapshot = _rows.Select(r => (r.Command ?? "", r.Parameter ?? "")).ToList();
        uStack.Push(snapshot);
        if (uStack.Count > MaxUndoDepth)
        {
            var temp = uStack.Reverse().Skip(1).Reverse().ToList();
            uStack.Clear();
            foreach (var item in temp) uStack.Push(item);
        }

        if (_redoStacks.TryGetValue(_currentProgNo, out var rStack))
        {
            rStack.Clear();
        }
        UpdateUndoRedoButtonState();
    }

    private void PerformUndo()
    {
        if (!_undoStacks.TryGetValue(_currentProgNo, out var uStack) || uStack.Count == 0) return;

        if (!_redoStacks.TryGetValue(_currentProgNo, out var rStack))
        {
            rStack = new Stack<List<(string Command, string Parameter)>>();
            _redoStacks[_currentProgNo] = rStack;
        }
        rStack.Push(_rows.Select(r => (r.Command ?? "", r.Parameter ?? "")).ToList());

        var prev = uStack.Pop();
        _rows.Clear();
        for (int i = 0; i < prev.Count; i++)
        {
            _rows.Add(new SequencerRowViewModel { LineNumber = i, Command = prev[i].Command, Parameter = prev[i].Parameter });
        }
        SyncUIToProgram();
        UpdateUndoRedoButtonState();
    }

    private void PerformRedo()
    {
        if (!_redoStacks.TryGetValue(_currentProgNo, out var rStack) || rStack.Count == 0) return;

        if (!_undoStacks.TryGetValue(_currentProgNo, out var uStack))
        {
            uStack = new Stack<List<(string Command, string Parameter)>>();
            _undoStacks[_currentProgNo] = uStack;
        }
        uStack.Push(_rows.Select(r => (r.Command ?? "", r.Parameter ?? "")).ToList());

        var next = rStack.Pop();
        _rows.Clear();
        for (int i = 0; i < next.Count; i++)
        {
            _rows.Add(new SequencerRowViewModel { LineNumber = i, Command = next[i].Command, Parameter = next[i].Parameter });
        }
        SyncUIToProgram();
        UpdateUndoRedoButtonState();
    }

    private void UpdateUndoRedoButtonState()
    {
        var btnUndo = this.FindControl<Button>("BtnUndo");
        var btnRedo = this.FindControl<Button>("BtnRedo");
        bool canU = _undoStacks.TryGetValue(_currentProgNo, out var u) && u.Count > 0;
        bool canR = _redoStacks.TryGetValue(_currentProgNo, out var r) && r.Count > 0;

        if (btnUndo != null) { btnUndo.IsEnabled = canU; btnUndo.Opacity = canU ? 1.0 : 0.4; }
        if (btnRedo != null) { btnRedo.IsEnabled = canR; btnRedo.Opacity = canR ? 1.0 : 0.4; }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
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
                var panel = this.FindControl<Border>("SearchPanel");
                if (panel != null)
                {
                    panel.IsVisible = true;
                    var txtKeyword = this.FindControl<TextBox>("TxtSearchKeyword");
                    txtKeyword?.Focus();
                }
                e.Handled = true;
            }
        }
    }
}
