using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;
using ARCOS.Core.Execution;
using ARCOS.Core.Guidance;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Module.Hello;

public class HelloWorldGuidance : IParameterGuidance, IModuleHandler
{
    public string CommandKey => "Hello@HelloWorld";
    public string ModuleName => "Hello";

    public IReadOnlyList<string> GetSupportedCommands() => new[] { "HelloWorld" };

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        string msg = string.IsNullOrWhiteSpace(filteredParam) ? "hello world" : filteredParam.Trim();
        ctx.Log(msg);
        ctx.Engines.Record.Record("SYSTEMLOG", msg);
        return CommandResult.Done();
    }

    public bool Execute(BasicEngines engines, string parameter)
    {
        string msg = string.IsNullOrWhiteSpace(parameter) ? "hello world" : parameter.Trim();
        engines.Record.Record("SYSTEMLOG", msg);
        return true;
    }

    public CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        return Execute(action, parameter, ctx);
    }

    public async Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
    {
        using var stream = typeof(HelloWorldGuidance).Assembly.GetManifestResourceStream("CSVI.RuntimeXaml.Wizard.axaml");
        if (stream != null && owner is Window ownerWin)
        {
            using var reader = new StreamReader(stream);
            string xaml = reader.ReadToEnd();
            if (AvaloniaRuntimeXamlLoader.Parse<Control>(xaml) is Control wizCtrl)
            {
                var dlg = new Window
                {
                    Title = "Hello@HelloWorld - Parameter Guidance",
                    Width = 620,
                    Height = 420,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = wizCtrl
                };

                var txtInput = wizCtrl.FindControl<TextBox>("TxtMessageInput");
                var txtOut = wizCtrl.FindControl<TextBox>("TxtParamOut");
                var btnOk = wizCtrl.FindControl<Button>("BtnOk");
                var btnCancel = wizCtrl.FindControl<Button>("BtnCancel");

                string initVal = string.IsNullOrWhiteSpace(currentParam) ? "hello world" : currentParam;
                if (txtInput != null)
                {
                    txtInput.Text = initVal;
                    txtInput.KeyUp += (_, _) =>
                    {
                        if (txtOut != null) txtOut.Text = txtInput.Text ?? "";
                    };
                }
                if (txtOut != null) txtOut.Text = initVal;

                if (btnCancel != null) btnCancel.Click += (_, _) => dlg.Close(null);
                if (btnOk != null) btnOk.Click += (_, _) =>
                {
                    string res = txtOut?.Text?.Trim() ?? "";
                    if (string.IsNullOrEmpty(res) && txtInput != null) res = txtInput.Text?.Trim() ?? "";
                    dlg.Close(res);
                };

                return await dlg.ShowDialog<string?>(ownerWin);
            }
        }
        return string.IsNullOrWhiteSpace(currentParam) ? "hello world" : currentParam;
    }

    public object? GetFrontPanelControl()
    {
        using var stream = typeof(HelloWorldGuidance).Assembly.GetManifestResourceStream("CSVI.RuntimeXaml.Panel.axaml");
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            string xaml = reader.ReadToEnd();
            if (AvaloniaRuntimeXamlLoader.Parse<Control>(xaml) is Control panelCtrl)
            {
                panelCtrl.DataContext = new HelloWorldPanelController(panelCtrl);
                return panelCtrl;
            }
        }
        return null;
    }
}

public sealed class HelloWorldPanelController
{
    public HelloWorldPanelController(Control root)
    {
        var btn = root.FindControl<Button>("BtnTestCmd");
        var txt = root.FindControl<TextBlock>("TxtCmdStatus");
        if (btn != null)
        {
            btn.Click += (_, _) =>
            {
                if (txt != null) txt.Text = $"Executed Hello@HelloWorld @ {DateTime.Now:HH:mm:ss}";
            };
        }
    }
}
