using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;

namespace ARCOS.Driver.DirectShowCamera;

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

public sealed class StandaloneApp : Application
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

            var driver = new DirectShowCameraDriver();
            driver.Init("Cam0", "Index=0,Width=1280,Height=720,FourCC=MJPG", engines);

            var window = new Window
            {
                Title = "DirectShowCamera - Windows Webcam Commissioning Tool (.NET 8)",
                Width = 860,
                Height = 680,
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
