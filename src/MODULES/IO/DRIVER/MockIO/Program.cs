using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;

namespace ARCOS.Driver.MockIO;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            AppBuilder.Configure<StandaloneApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
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

            // 初始化使用者的典型設定：IO1, COM1, Out: P0.0 (32點), In: P4.0 (32點)
            var driver = new MockIoDriver();
            driver.Init("IO1", "Device=com1,OutStart=p0.0,OutPorts=32,InputStart=p4.0,InPorts=32,Interval=25", engines);

            // 預設點亮幾盞燈與輸入狀態以利現場視覺化展示
            io.SetPort("P0.0", true);
            io.SetPort("P0.3", true);
            io.SetPort("P1.2", true);
            io.SetPort("P2.5", true);
            driver.SetSimulatedInput(0, true); // P4.0
            driver.SetSimulatedInput(2, true); // P4.2
            driver.SetSimulatedInput(9, true); // P5.1

            var window = new Window
            {
                Title = "MockIO Driver HMI & Parameter Wizard (CSVI 3.0)",
                Width = 720,
                Height = 560,
                Content = driver.CreateDriverView(),
                Position = new PixelPoint(1950, -650),
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Closed += (_, _) => driver.Shutdown();
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
