using Avalonia.Media;
using ArcosExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.App.ViewModels;

public sealed class SubThreadItemViewModel
{
    private static readonly IBrush RunningBg = new SolidColorBrush(Color.Parse("#DCFCE7"));
    private static readonly IBrush RunningFg = new SolidColorBrush(Color.Parse("#166534"));
    private static readonly IBrush IdleBg = new SolidColorBrush(Color.Parse("#F1F5F9"));
    private static readonly IBrush IdleFg = new SolidColorBrush(Color.Parse("#475569"));

    public string Name { get; }
    public ArcosExecutionContext Context { get; }

    public string ProgSummary => $"Prog {Context.Program?.ProgramNumber ?? 0} | PC: {Context.ProgIndex}";
    public string StatusText => Context.IsRunning ? "RUNNING" : "IDLE";
    public IBrush BadgeBackground => Context.IsRunning ? RunningBg : IdleBg;
    public IBrush BadgeForeground => Context.IsRunning ? RunningFg : IdleFg;

    public SubThreadItemViewModel(string name, ArcosExecutionContext context)
    {
        Name = name;
        Context = context;
    }
}
