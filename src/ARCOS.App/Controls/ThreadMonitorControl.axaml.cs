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

        if (_context == null || _context.Program == null) return;

        var prog = _context.Program;
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

        bool isRunning = _context.IsRunning;
        bool isPaused = _context.IsEffectivelyPaused;
        int pc1Based = _context.ReportedProgIndex;
        int targetRow = pc1Based >= 1 ? pc1Based - 1 : 0;

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

        // 3. 更新目前指令文字
        var txtStatusMsg = this.FindControl<TextBox>("TxtStatusMessage");
        if (txtStatusMsg != null && _context.Program != null && pc1Based >= 1 && pc1Based <= _context.Program.Count)
        {
            var item = _context.Program[pc1Based];
            txtStatusMsg.Text = $"{item.Command}  {item.RawParameter}";
        }

        // 4. 更新 DataGrid 當前 PC 行號粉色反白指示
        if (targetRow != _currentRenderedPC || !isRunning)
        {
            if (_currentRenderedPC >= 0 && _currentRenderedPC < _rows.Count)
            {
                _rows[_currentRenderedPC].IsCurrentPC = false;
            }

            _currentRenderedPC = isRunning ? targetRow : -1;

            if (isRunning && _currentRenderedPC >= 0 && _currentRenderedPC < _rows.Count)
            {
                _rows[_currentRenderedPC].IsCurrentPC = true;
                var grid = this.FindControl<DataGrid>("GridCommands");
                grid?.ScrollIntoView(_rows[_currentRenderedPC], null);
            }
        }
    }
}
