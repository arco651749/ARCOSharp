using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using MVCON.Core.Drivers;
using MVCON.Core.Engines;

namespace MVCON.Driver.MockMotion;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppBuilder.Configure<StandaloneApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .StartWithClassicDesktopLifetime(args);
    }
}

public class StandaloneApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var io = new IoEngine();
            var engines = new BasicEngines(io, new(), new(io), new(io), new(io), new(io));

            var driver = new MockMotionDriver();
            driver.Init("Standalone_Motion1", "", engines);

            var window = new Window
            {
                Title = "MockMotion - Standalone Hardware Commissioning Tool (.NET 8)",
                Width = 720,
                Height = 560,
                Content = driver.CreateDriverView(),
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            window.Closed += (_, _) =>
            {
                driver.Shutdown();
                driver.Dispose();
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
