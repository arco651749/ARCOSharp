using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MVCON.CsviStudio.Views;

namespace MVCON.CsviStudio;

public partial class App : Application
{
    private readonly string? _initialFilePath;

    public App() { }

    public App(string? initialFilePath)
    {
        _initialFilePath = initialFilePath;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(_initialFilePath);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
