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

namespace ExternalModules.Hi;

public class HiInstrument : ICallCommand, IParameterGuidance, IModuleHandler
{
    public string CommandKey => "Call@hi.csvi";
    public string ModuleName => "Call";

    public IReadOnlyList<string> GetSupportedCommands() => new[] { "hi.csvi" };

    public object? Execute(IReadOnlyList<string> args, ExecutionContext ctx)
    {
        string msg = (args.Count > 0 && !string.IsNullOrWhiteSpace(args[0])) ? args[0].Trim() : "hi";
        ctx.Log(msg);
        ctx.Engines.Record.Record("SYSTEMLOG", msg);
        return msg;
    }

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        string msg = string.IsNullOrWhiteSpace(filteredParam) ? "hi" : filteredParam.Trim();
        ctx.Log(msg);
        ctx.Engines.Record.Record("SYSTEMLOG", msg);
        return CommandResult.Done();
    }

    public bool Execute(BasicEngines engines, string parameter)
    {
        string msg = string.IsNullOrWhiteSpace(parameter) ? "hi" : parameter.Trim();
        engines.Record.Record("SYSTEMLOG", msg);
        return true;
    }

    public CommandResult Execute(ExecutionContext ctx, string action, string parameter)
        => Execute(action, parameter, ctx);

    public async Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
    {
        using var stream = typeof(HiInstrument).Assembly.GetManifestResourceStream("CSVI.RuntimeXaml.Wizard.axaml");
        if (stream != null && owner is Window ownerWin)
        {
            using var reader = new StreamReader(stream);
            string xaml = reader.ReadToEnd();
            if (AvaloniaRuntimeXamlLoader.Parse<Control>(xaml) is Control wizCtrl)
            {
                var dlg = new Window
                {
                    Title = "Call@hi.csvi - Parameter Guidance",
                    Width = 600,
                    Height = 400,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = wizCtrl
                };
                var txtInput = wizCtrl.FindControl<TextBox>("TxtCallInput");
                var txtOut = wizCtrl.FindControl<TextBox>("TxtParamOut");
                var btnOk = wizCtrl.FindControl<Button>("BtnOk");
                var btnCancel = wizCtrl.FindControl<Button>("BtnCancel");

                string initVal = string.IsNullOrWhiteSpace(currentParam) ? "hi" : currentParam;
                if (txtInput != null)
                {
                    txtInput.Text = initVal;
                    txtInput.KeyUp += (_, _) => { if (txtOut != null) txtOut.Text = txtInput.Text ?? ""; };
                }
                if (txtOut != null) txtOut.Text = initVal;

                if (btnCancel != null) btnCancel.Click += (_, _) => dlg.Close(null);
                if (btnOk != null) btnOk.Click += (_, _) => dlg.Close(txtOut?.Text?.Trim());
                return await dlg.ShowDialog<string?>(ownerWin);
            }
        }
        return string.IsNullOrWhiteSpace(currentParam) ? "hi" : currentParam;
    }

    public object? GetFrontPanelControl()
    {
        using var stream = typeof(HiInstrument).Assembly.GetManifestResourceStream("CSVI.RuntimeXaml.Panel.axaml");
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            string xaml = reader.ReadToEnd();
            if (AvaloniaRuntimeXamlLoader.Parse<Control>(xaml) is Control panelCtrl)
            {
                return panelCtrl;
            }
        }
        return null;
    }
}
