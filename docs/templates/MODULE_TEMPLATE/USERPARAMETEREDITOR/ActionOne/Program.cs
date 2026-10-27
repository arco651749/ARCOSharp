using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace MVCON.Guidance.ActionOne;

/// <summary>
/// 雙模 Standalone EXE 進入點：
/// 工程師在檔案總管雙擊 ActionOne.Guidance.exe，注入 Mock 假資料秒開預覽 UI！
/// </summary>
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
            var mockTargets = new[] { "MockPin_1", "MockPin_2", "Cylinder_Extend", "Vacuum_Valve" };
            desktop.MainWindow = new ActionOneDialog("Target=MockPin_1,Delay=0.5", mockTargets);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
