using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using MVCON.Core.Drivers;
using MVCON.Core.Engines;

namespace MVCON.Driver.MockIO;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        File.WriteAllText(@"D:\AntiGravity\MVCON2CSharp\mockio_run.txt", "Starting Main...\n");
        try
        {
            AppBuilder.Configure<StandaloneApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .StartWithClassicDesktopLifetime(args);
            File.AppendAllText(@"D:\AntiGravity\MVCON2CSharp\mockio_run.txt", "Finished cleanly.");
        }
        catch (Exception ex)
        {
            File.WriteAllText(@"D:\AntiGravity\MVCON2CSharp\mockio_run.txt", ex.ToString());
        }
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

            var driver = new MockIoDriver();
            driver.Init("Standalone_StationIO", "StartPort=P0.0,BitCount=32,Interval=25", engines);

            var window = new Window
            {
                Title = "MockIO - Standalone Hardware Commissioning Tool (.NET 8)",
                Width = 560,
                Height = 420,
                Content = driver.CreateDriverView(),
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            window.Closed += (_, _) => driver.Shutdown();
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
