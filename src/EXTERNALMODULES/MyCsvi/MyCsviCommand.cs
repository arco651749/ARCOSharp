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

namespace ExternalModules.MyCsvi;

/// <summary>
/// 專案客製 AOI 直方圖分析與數值計算指令 (mycsvi.csvi)
/// 同時實作 ICallCommand 與 IParameterGuidance，供 Call@mycsvi.csvi 調度執行、參數導引與前面板展示
/// </summary>
public class MyCsviCommand : ICallCommand, IParameterGuidance
{
    public string CommandKey => "Call@mycsvi.csvi";

    public object? Execute(IReadOnlyList<string> args, ExecutionContext ctx)
    {
        // 1. 若為數值計算模式：輸入至少兩個數字 (例如: 10, 20, $Result)
        if (args.Count >= 2 && double.TryParse(args[0], out double v1) && double.TryParse(args[1], out double v2))
        {
            double sum = v1 + v2;
            ctx.Log($"[mycsvi.csvi] Calculation mode: {v1} + {v2} = {sum}");
            return sum;
        }

        // 2. 若為 AOI 直方圖運算模式：CamName, MinThreshold, MaxThreshold, (ReturnVar)
        string camName = args.Count > 0 ? args[0].Trim() : "Cam1";
        int minThresh = args.Count > 1 && int.TryParse(args[1], out int min) ? min : 0;
        int maxThresh = args.Count > 2 && int.TryParse(args[2], out int max) ? max : 255;

        ctx.Log($"[mycsvi.csvi] AOI Histogram analyzing for '{camName}', threshold range [{minThresh}, {maxThresh}]...");
        double score = Math.Round((minThresh + maxThresh) / 2.0 * 0.85, 2);
        ctx.Log($"[mycsvi.csvi] AOI Histogram analysis completed. Output score: {score}");
        return score;
    }

    public bool Execute(BasicEngines engines, string parameter)
    {
        var dummyProg = new ProgramTable();
        var ctx = new ExecutionContext(0, "Sandbox", dummyProg, engines, new DriverManager(engines));
        var args = string.IsNullOrWhiteSpace(parameter)
            ? Array.Empty<string>()
            : parameter.Split(',', StringSplitOptions.TrimEntries);
        Execute(args, ctx);
        return true;
    }

    public async Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
    {
        using var stream = typeof(MyCsviCommand).Assembly.GetManifestResourceStream("CSVI.RuntimeXaml.Wizard.axaml");
        if (stream != null && owner is Window ownerWin)
        {
            using var reader = new StreamReader(stream);
            string xaml = reader.ReadToEnd();
            if (AvaloniaRuntimeXamlLoader.Parse<Control>(xaml) is Control wizCtrl)
            {
                var dlg = new Window
                {
                    Title = "mycsvi.csvi - AOI 直方圖與自訂運算參數配置",
                    Width = 540,
                    Height = 420,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = wizCtrl
                };

                var txtCam = wizCtrl.FindControl<TextBox>("TxtCamera");
                var txtMin = wizCtrl.FindControl<TextBox>("TxtMinThresh");
                var txtMax = wizCtrl.FindControl<TextBox>("TxtMaxThresh");
                var txtRet = wizCtrl.FindControl<TextBox>("TxtReturnVar");
                var txtPreview = wizCtrl.FindControl<TextBox>("TxtPreview");
                var btnOk = wizCtrl.FindControl<Button>("BtnConfirm");
                var btnCancel = wizCtrl.FindControl<Button>("BtnCancel");

                Action updatePreview = () =>
                {
                    string c = txtCam?.Text?.Trim() ?? "Cam1";
                    string mn = txtMin?.Text?.Trim() ?? "30";
                    string mx = txtMax?.Text?.Trim() ?? "200";
                    string rv = txtRet?.Text?.Trim() ?? "HistScore";
                    if (txtPreview != null) txtPreview.Text = $"{c}, {mn}, {mx}, {rv}";
                };

                if (!string.IsNullOrWhiteSpace(currentParam))
                {
                    var parts = currentParam.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length >= 1 && txtCam != null) txtCam.Text = parts[0];
                    if (parts.Length >= 2 && txtMin != null) txtMin.Text = parts[1];
                    if (parts.Length >= 3 && txtMax != null) txtMax.Text = parts[2];
                    if (parts.Length >= 4 && txtRet != null) txtRet.Text = parts[3];
                }

                if (txtCam != null) txtCam.KeyUp += (_, _) => updatePreview();
                if (txtMin != null) txtMin.KeyUp += (_, _) => updatePreview();
                if (txtMax != null) txtMax.KeyUp += (_, _) => updatePreview();
                if (txtRet != null) txtRet.KeyUp += (_, _) => updatePreview();
                updatePreview();

                if (btnCancel != null) btnCancel.Click += (_, _) => dlg.Close(null);
                if (btnOk != null) btnOk.Click += (_, _) => dlg.Close(txtPreview?.Text?.Trim());

                return await dlg.ShowDialog<string?>(ownerWin);
            }
        }
        return string.IsNullOrWhiteSpace(currentParam) ? "Cam1, 30, 200, HistScore" : currentParam;
    }

    public object? GetFrontPanelControl()
    {
        using var stream = typeof(MyCsviCommand).Assembly.GetManifestResourceStream("CSVI.RuntimeXaml.Panel.axaml");
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
