using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using MVCON.Core.Drivers;
using MVCON.Core.Engines;

namespace MVCON.Driver.MockDevice;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppBuilder.Configure<StandaloneApp>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }
}

public class StandaloneApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var io = new IoEngine();
            var engines = new BasicEngines(io, new VariableEngine(), new PointEngine(io), new ImageEngine(io), new DaqEngine(io), new TimerEngine(io));

            var driver = new MockDeviceDriver();
            driver.Init("MockDevice_Standalone", "COM1,9600", engines);

            var win = new Window
            {
                Title = "🛠️ MockDevice Driver 獨立除錯面板 (Standalone Test)",
                Width = 460,
                Height = 280,
                Content = driver.CreateDriverView()
            };

            win.Closed += (_, _) => driver.Shutdown();
            desktop.MainWindow = win;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
