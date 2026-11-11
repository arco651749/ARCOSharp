using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ARCOS.Core.Drivers;
using ARCOS.Core.Execution;
using ARCOS.Core.Guidance;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Module.Hello;

public class HelloMasterGuidance : IParameterGuidance, IModuleHandler
{
    public string CommandKey => "Hello";
    public string ModuleName => "Hello";

    public IReadOnlyList<string> GetSupportedCommands() =>
        ModuleRegistry.GetCsviCommandsForModule("Hello") ?? new[] { "HelloWorld" };

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
        => CommandResult.Done();

    public bool Execute(BasicEngines engines, string parameter) => true;

    public CommandResult Execute(ExecutionContext ctx, string action, string parameter)
        => Execute(action, parameter, ctx);

    public async Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
    {
        using var stream = typeof(HelloMasterGuidance).Assembly.GetManifestResourceStream("CSVI.RuntimeXaml.Wizard.axaml");
        if (stream != null && owner is Window ownerWin)
        {
            using var reader = new StreamReader(stream);
            string xaml = reader.ReadToEnd();
            if (AvaloniaRuntimeXamlLoader.Parse<Control>(xaml) is Control wizCtrl)
            {
                var dlg = new Window
                {
                    Title = "Hello Master Instrument - Configuration",
                    Width = 620,
                    Height = 420,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = wizCtrl
                };
                var txtOut = wizCtrl.FindControl<TextBox>("TxtParamOut");
                var btnOk = wizCtrl.FindControl<Button>("BtnOk");
                var btnCancel = wizCtrl.FindControl<Button>("BtnCancel");
                if (btnCancel != null) btnCancel.Click += (_, _) => dlg.Close(null);
                if (btnOk != null) btnOk.Click += (_, _) => dlg.Close(txtOut?.Text?.Trim());
                return await dlg.ShowDialog<string?>(ownerWin);
            }
        }
        return currentParam;
    }

    public object? GetFrontPanelControl()
    {
        using var stream = typeof(HelloMasterGuidance).Assembly.GetManifestResourceStream("CSVI.RuntimeXaml.Panel.axaml");
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            string xaml = reader.ReadToEnd();
            if (AvaloniaRuntimeXamlLoader.Parse<Control>(xaml) is Control panelCtrl)
            {
                panelCtrl.DataContext = new HelloMasterPanelController(panelCtrl);
                return panelCtrl;
            }
        }
        return null;
    }
}

public sealed class HelloMasterPanelController
{
    private readonly Control _root;
    private BasicEngines? _engines;

    public HelloMasterPanelController(Control root)
    {
        _root = root;
        var btnHello = _root.FindControl<Button>("BtnTestHello");
        var txtStatus = _root.FindControl<TextBlock>("TxtHelloStatus");

        if (btnHello != null)
        {
            btnHello.Click += (_, _) =>
            {
                if (_engines != null)
                {
                    _engines.Record.Record("SYSTEMLOG", "[Hello Master Panel] Hello World test triggered.");
                }
                if (txtStatus != null)
                {
                    txtStatus.Text = $"Hello World Triggered @ {DateTime.Now:HH:mm:ss}";
                }
            };
        }
    }

    public void RefreshFromEngines(BasicEngines engines)
    {
        _engines = engines;
        var txtStatus = _root.FindControl<TextBlock>("TxtHelloStatus");
        if (txtStatus != null)
        {
            txtStatus.Text = "Hello Module (ONLINE)";
        }
    }
}
