using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using ARCOS.App.ViewModels;
using ArcosExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.App.Controls;

public partial class ThreadMonitorControl : UserControl
{
    private static readonly IBrush BrushGreen = new SolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush BrushAmber = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush BrushGray = new SolidColorBrush(Color.Parse("#94A3B8"));

    private readonly ObservableCollection<SequencerRowViewModel> _rows = new();
    private ArcosExecutionContext? _context;
    private ARCOS.Core.Execution.ProgramTable? _lastProgramRef;
    private int _lastProgramCount = -1;
    private string _threadTitle = "Thread";
    private int _currentRenderedPC = -1;
    private readonly DispatcherTimer _drainTimer;

    public static readonly StyledProperty<bool> IsFullViewProperty =
        AvaloniaProperty.Register<ThreadMonitorControl, bool>(nameof(IsFullView), defaultValue: false);

    public bool IsFullView
    {
        get => GetValue(IsFullViewProperty);
        set
        {
            SetValue(IsFullViewProperty, value);
            UpdateHeaderVisibility();
        }
    }

    public ArcosExecutionContext? Context => _context;
    public ObservableCollection<SequencerRowViewModel> Rows => _rows;

    public ThreadMonitorControl()
    {
        InitializeComponent();

        var grid = this.FindControl<DataGrid>("GridCommands");
        if (grid != null)
        {
            grid.ItemsSource = _rows;
        }

        UpdateHeaderVisibility();

        // 獨立 40ms (~25 FPS) 監控輪詢器，抽取 PC 與執行狀態更新
        _drainTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _drainTimer.Tick += (_, _) => DrainPCPointer();
        _drainTimer.Start();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsFullViewProperty)
        {
            UpdateHeaderVisibility();
        }
    }

    private void UpdateHeaderVisibility()
    {
        var fullHeader = this.FindControl<Border>("PnlFullHeader");
        var compactHeader = this.FindControl<Border>("PnlCompactHeader");

        if (fullHeader != null) fullHeader.IsVisible = IsFullView;
        if (compactHeader != null) compactHeader.IsVisible = !IsFullView;
    }

    public void BindContext(ArcosExecutionContext? ctx, string? threadTitle = null)
    {
        _context = ctx;
        if (threadTitle != null)
        {
            _threadTitle = threadTitle;
        }
        else if (ctx != null)
        {
            _threadTitle = ctx.ThreadName;
        }

        var txtCompactTitle = this.FindControl<TextBlock>("TxtCompactTitle");
        if (txtCompactTitle != null) txtCompactTitle.Text = _threadTitle;

        ReloadProgram();
    }

    public void ReloadProgram()
    {
        _rows.Clear();
        _currentRenderedPC = -1;

        if (_context == null) return;

        // 僅主執行緒 (Thread 0~12) 需與 VM 全域插槽 (0~99) 同步最新 ProgramTable；SubThread 持有獨立 ProgramTable 不可被覆蓋
        var prog = _context.Program;
        bool isMainThread = _context.VM != null && _context.VM.MainThreads.Any(t => ReferenceEquals(t, _context));
        if (prog != null && isMainThread)
        {
            var latestProg = _context.VM!.GetProgram(prog.ProgramNumber);
            if (latestProg != null && !ReferenceEquals(prog, latestProg))
            {
                _context.Program = latestProg;
                prog = latestProg;
            }
        }
        if (prog == null)
        {
            _lastProgramRef = null;
            _lastProgramCount = 0;
            _rows.Add(new SequencerRowViewModel
            {
                LineNumber = 0,
                Command = string.Empty,
                Parameter = string.Empty,
                IsCurrentPC = false,
                IsTrailingPlaceholder = true
            });
            DrainPCPointer();
            return;
        }

        _lastProgramRef = prog;
        _lastProgramCount = prog.Count;

        for (int i = 0; i < prog.Count; i++)
        {
            var item = prog.Items[i];
            _rows.Add(new SequencerRowViewModel
            {
                LineNumber = i,
                Command = item.Command,
                Parameter = item.RawParameter,
                IsCurrentPC = false,
                IsTrailingPlaceholder = false
            });
        }

        // 所有 Thread (Main / Worker 1~12 / SubThread) 皆固定保留最下方空白佔位列，方便執行完畢後 PC 停駐顯示
        _rows.Add(new SequencerRowViewModel
        {
            LineNumber = prog.Count,
            Command = string.Empty,
            Parameter = string.Empty,
            IsCurrentPC = false,
            IsTrailingPlaceholder = true
        });

        var txtProgNo = this.FindControl<TextBlock>("TxtProgramNo");
        if (txtProgNo != null) txtProgNo.Text = prog.ProgramNumber.ToString();

        var txtCompactProg = this.FindControl<TextBlock>("TxtCompactProg");
        if (txtCompactProg != null) txtCompactProg.Text = $"Prog {prog.ProgramNumber}";

        var txtScriptTitle = this.FindControl<TextBox>("TxtScriptTitle");
        if (txtScriptTitle != null)
        {
            txtScriptTitle.Text = string.IsNullOrWhiteSpace(prog.Name) ? _threadTitle : prog.Name;
        }

        DrainPCPointer();
    }

    private void DrainPCPointer()
    {
        if (_context == null) return;

        // 檢查該 Thread 綁定的 Program 是否因編輯器修改或 Thread@Run 切換而改變（僅限 MainThread，避免 SubThread 被 VM 空插槽覆寫）
        var currentProg = _context.Program;
        bool isMainThread = _context.VM != null && _context.VM.MainThreads.Any(t => ReferenceEquals(t, _context));
        if (currentProg != null && isMainThread)
        {
            var vmProg = _context.VM!.GetProgram(currentProg.ProgramNumber);
            if (vmProg != null && !ReferenceEquals(currentProg, vmProg))
            {
                _context.Program = vmProg;
                currentProg = vmProg;
            }
        }

        if (!ReferenceEquals(currentProg, _lastProgramRef) || (currentProg != null && currentProg.Count != _lastProgramCount))
        {
            ReloadProgram();
            return;
        }

        bool isRunning = _context.IsRunning;
        bool isPaused = _context.IsEffectivelyPaused;
        int pc1Based = _context.ReportedProgIndex;
        int targetRow = _rows.Count > 0 ? Math.Clamp(pc1Based >= 1 ? pc1Based - 1 : 0, 0, _rows.Count - 1) : -1;

        // 1. 更新數值標籤
        var txtProgIdx = this.FindControl<TextBlock>("TxtProgIndex");
        if (txtProgIdx != null) txtProgIdx.Text = pc1Based.ToString();

        var txtCompactPC = this.FindControl<TextBlock>("TxtCompactPC");
        if (txtCompactPC != null) txtCompactPC.Text = $"PC: {pc1Based}";

        var txtStateIdx = this.FindControl<TextBlock>("TxtStateIndex");
        if (txtStateIdx != null) txtStateIdx.Text = _context.State.Value.ToString();

        // 2. 更新狀態字串與 LED
        string stateStr = isPaused ? "Paused" : (isRunning ? "Running" : "Idle");
        IBrush ledBrush = isPaused ? BrushAmber : (isRunning ? BrushGreen : BrushGray);

        var txtState = this.FindControl<TextBlock>("TxtState");
        if (txtState != null) txtState.Text = stateStr;

        var txtCompactState = this.FindControl<TextBlock>("TxtCompactState");
        if (txtCompactState != null) txtCompactState.Text = isPaused ? "PAUSE" : (isRunning ? "RUN" : "IDLE");

        var ledFull = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("LedFullStatus");
        if (ledFull != null) ledFull.Fill = ledBrush;

        var ledCompact = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("LedCompactStatus");
        if (ledCompact != null) ledCompact.Fill = ledBrush;

        // 3. 更新目前指令文字（停在尾端空白列時清空顯示）
        var txtStatusMsg = this.FindControl<TextBox>("TxtStatusMessage");
        if (txtStatusMsg != null)
        {
            if (_context.Program != null && pc1Based >= 1 && pc1Based <= _context.Program.Count)
            {
                var item = _context.Program[pc1Based];
                txtStatusMsg.Text = $"{item.Command}  {item.RawParameter}";
            }
            else
            {
                txtStatusMsg.Text = string.Empty;
            }
        }

        // 4. 更新 DataGrid 當前 PC 行號粉色反白指示 (與編輯頁相同，隨時顯示目前執行到的行號)
        if (targetRow != _currentRenderedPC)
        {
            if (_currentRenderedPC >= 0 && _currentRenderedPC < _rows.Count)
            {
                _rows[_currentRenderedPC].IsCurrentPC = false;
            }

            _currentRenderedPC = targetRow;

            if (_currentRenderedPC >= 0 && _currentRenderedPC < _rows.Count)
            {
                _rows[_currentRenderedPC].IsCurrentPC = true;
                var grid = this.FindControl<DataGrid>("GridCommands");
                grid?.ScrollIntoView(_rows[_currentRenderedPC], null);
            }
        }
    }
}
