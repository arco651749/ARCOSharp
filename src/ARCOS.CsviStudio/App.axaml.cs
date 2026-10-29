using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ARCOS.CsviStudio.Views;

namespace ARCOS.CsviStudio;

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
            string? file = _initialFilePath;
            if (string.IsNullOrEmpty(file) && desktop.Args != null)
            {
                file = desktop.Args
                    .Select(a => a.Trim('"').Trim())
                    .FirstOrDefault(a => !a.StartsWith("-") && a.EndsWith(".csvi", StringComparison.OrdinalIgnoreCase));
            }
            desktop.MainWindow = new MainWindow(file);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
