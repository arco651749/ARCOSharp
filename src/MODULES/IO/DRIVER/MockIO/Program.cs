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

            // 步驟 1: AddIODriver 初始化硬體驅動與點位區間 (IO1, COM1, Out: P0.0 32點, In: P4.0 32點)
            var driver = new MockIoDriver();
            driver.Init("IO1", "Device=com1,OutStart=p0.0,OutPorts=32,InputStart=p4.0,InPorts=32,Interval=25", engines);

            // 步驟 2: SetPortName 設定語意別名 (PORTNAME)
            io.SetPortName("P0.0", "汽缸下");
            io.SetPortName("P0.3", "吸嘴開");
            io.SetPortName("P1.2", "綠燈");
            io.SetPortName("P2.5", "吹氣閥");
            io.SetPortName("P4.0", "啟動鈕?");
            io.SetPortName("P4.2", "下極限?");
            io.SetPortName("P5.1", "真空OK?");

            // 步驟 3: InitIOPort 收網刷新名稱並啟動訊號交換
            driver.PostTicket(new DriverTicket("InitIOPort", "25"));

            // 預設點亮幾盞燈與輸入狀態以利現場視覺化展示
            io.SetPort("汽缸下", true);
            io.SetPort("吸嘴開", true);
            io.SetPort("綠燈", true);
            io.SetPort("吹氣閥", true);
            driver.SetSimulatedInput(0, true); // P4.0 (啟動鈕?)
            driver.SetSimulatedInput(2, true); // P4.2 (下極限?)
            driver.SetSimulatedInput(9, true); // P5.1 (真空OK?)

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
