using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ARCOS.App.Dialogs;
using ARCOS.Core.Guidance;

namespace ARCOS.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 註冊跨模組共用的 $logic 視覺編輯器入口，供所有 CSVI Wizard 動態調用
        LogicEditorService.ShowAsync = (expr, engines, owner) => LogicEditorWindow.ShowAsync(expr, engines, owner);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
