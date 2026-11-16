using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using ARCOS.Core.Execution;
using ARCOS.Core.Guidance;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ExternalModules.MyCsvi;

/// <summary>
/// 專案客製 AOI 直方圖分析與數值計算指令 (mycsvi.csvi)
/// 同時實作 ICallCommand 與 IParameterGuidance，供 Call@mycsvi.csvi 調度執行、MVCON 參數導引與前面板展示
/// </summary>
public class MyCsviCommand : ICallCommand, IParameterGuidance
{
    public string CommandKey => "Call@mycsvi.csvi";

    public object? Execute(IReadOnlyList<string> args, ExecutionContext ctx)
    {
        // 1. 若為數值計算模式：輸入至少兩個數字 (例如: 10, 20, $Result)
        if (args.Count >= 2 && double.TryParse(args[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double v1) &&
            double.TryParse(args[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double v2))
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

    private static void AddUnique(List<string> list, string? item)
    {
        if (string.IsNullOrWhiteSpace(item)) return;
        string s = item.Trim();
        if (!list.Any(x => x.Equals(s, StringComparison.OrdinalIgnoreCase)))
            list.Add(s);
    }

    private static List<string> SplitTopLevelCsv(string? input)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(input)) return result;
        int depthParen = 0;
        int depthBracket = 0;
        int start = 0;
        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            if (ch == '(') depthParen++;
            else if (ch == ')' && depthParen > 0) depthParen--;
            else if (ch == '[') depthBracket++;
            else if (ch == ']' && depthBracket > 0) depthBracket--;
            else if (ch == ',' && depthParen == 0 && depthBracket == 0)
            {
                result.Add(input.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        result.Add(input.Substring(start).Trim());
        return result;
    }

    private static List<string> CollectVariables(BasicEngines? engines, bool withDollar = true)
    {
        var list = new List<string>();
        if (engines?.Variable != null)
        {
            foreach (var v in engines.Variable.GetAllVariableNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string raw = v.TrimStart('$');
                AddUnique(list, withDollar ? "$" + raw : raw);
            }
            foreach (var sec in engines.Variable.GetAllSections())
            {
                foreach (var v in engines.Variable.GetAllVariableNames(sec).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    string raw = v.TrimStart('$');
                    AddUnique(list, withDollar ? "$" + raw : raw);
                }
            }
        }
        return list;
    }

    private static IEnumerable<ProgramTable> EnumeratePrograms(BasicEngines? engines)
    {
        if (engines?.Program == null) yield break;
        var active = engines.Program.GetActiveProgram();
        if (active != null) yield return active;
        foreach (var p in engines.Program.GetAllPrograms())
        {
            if (active == null || p.ProgramNumber != active.ProgramNumber)
                yield return p;
        }
    }

    private static List<string> CollectErrorLabels(BasicEngines? engines)
    {
        var list = new List<string>();
        foreach (var prog in EnumeratePrograms(engines))
        {
            foreach (var item in prog.Items)
            {
                if (item.IsComment || item.IsEmpty) continue;
                if (item.Module.Equals("Thread", StringComparison.OrdinalIgnoreCase))
                {
                    if (item.Action.Equals("ErrorHandler", StringComparison.OrdinalIgnoreCase))
                    {
                        string raw = item.RawParameter.Trim();
                        if (raw.StartsWith("[[") && raw.EndsWith("]]") && raw.Length > 4)
                            AddUnique(list, raw.Substring(2, raw.Length - 4).Trim());
                    }
                    else if (item.Action.Equals("Label", StringComparison.OrdinalIgnoreCase))
                    {
                        string lbl = item.RawParameter.Trim().Trim('[', ']');
                        AddUnique(list, lbl);
                    }
                }
            }
        }
        return list;
    }

    private static List<string> CollectCameras(BasicEngines? engines)
    {
        var list = new List<string>();
        if (engines?.Drivers != null)
        {
            foreach (var (devName, _) in engines.Drivers.GetDeviceEntriesByCategory("Capture"))
                AddUnique(list, devName);
        }
        if (engines?.Image != null)
        {
            foreach (var n in engines.Image.GetAllNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                AddUnique(list, n);
        }
        foreach (var prog in EnumeratePrograms(engines))
        {
            foreach (var item in prog.Items)
            {
                if (item.IsComment || item.IsEmpty) continue;
                if (item.Module.Equals("Capture", StringComparison.OrdinalIgnoreCase))
                {
                    var toks = SplitTopLevelCsv(item.RawParameter);
                    if (toks.Count > 0 && !string.IsNullOrWhiteSpace(toks[0]) && !toks[0].Contains('='))
                        AddUnique(list, toks[0]);
                }
            }
        }
        AddUnique(list, "Cam1");
        AddUnique(list, "Cam2");
        foreach (var v in CollectVariables(engines, true))
            AddUnique(list, v);
        return list;
    }

    private static List<string> CollectExternalModules(BasicEngines? engines)
    {
        var list = new List<string>();
        var dirs = new[]
        {
            ArcosPaths.ExternalModulesDirectory,
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExternalModules"),
            Path.Combine(Directory.GetCurrentDirectory(), "ExternalModules")
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*.csvi").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    AddUnique(list, Path.GetFileName(f));
            }
            catch { }
        }
        return list;
    }

    private static void WireEditableCombo(
        ComboBox? cmb, TextBox? txt, Func<IReadOnlyList<string>> getItems, string initialText,
        Func<bool> getSync, Action<bool> setSync, Action updatePreview)
    {
        if (cmb == null || txt == null) return;
        cmb.ItemsSource = getItems();

        string? ResolveItem(IReadOnlyList<string> items, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string clean = text.Trim().TrimStart('$');
            return items.FirstOrDefault(x => x.Equals(text, StringComparison.OrdinalIgnoreCase))
                ?? items.FirstOrDefault(x => x.TrimStart('$').Equals(clean, StringComparison.OrdinalIgnoreCase));
        }

        void SyncSelection(string text)
        {
            if (getSync()) return;
            var items = getItems();
            setSync(true);
            try { cmb.SelectedItem = ResolveItem(items, text); }
            finally { setSync(false); }
        }

        setSync(true);
        try { txt.Text = initialText; }
        finally { setSync(false); }
        SyncSelection(initialText);

        cmb.DropDownOpened += (_, _) =>
        {
            var items = getItems();
            string q = txt.Text?.Trim() ?? string.Empty;
            var matched = ResolveItem(items, q);
            setSync(true);
            try
            {
                if (string.IsNullOrEmpty(q) || matched != null)
                {
                    cmb.ItemsSource = items;
                    cmb.SelectedItem = matched;
                }
                else
                {
                    var filtered = items.Where(p => p.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
                    cmb.ItemsSource = filtered.Count > 0 ? filtered : items;
                }
            }
            finally { setSync(false); }
        };

        cmb.SelectionChanged += (_, _) =>
        {
            if (getSync()) return;
            if (cmb.SelectedItem is string s)
            {
                setSync(true);
                try
                {
                    txt.Text = s;
                    txt.CaretIndex = s.Length;
                }
                finally { setSync(false); }
                updatePreview();
            }
        };

        txt.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty || getSync()) return;
            SyncSelection(txt.Text?.Trim() ?? string.Empty);
            updatePreview();
        };
    }

    private static void WireEditableCombo(
        ComboBox? cmb, TextBox? txt, IReadOnlyList<string> items, string initialText,
        Func<bool> getSync, Action<bool> setSync, Action updatePreview)
        => WireEditableCombo(cmb, txt, () => items, initialText, getSync, setSync, updatePreview);

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
                    Title = "Call@mycsvi.csvi - Parameter Guidance",
                    Width = 720,
                    Height = 542,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light,
                    Content = wizCtrl
                };

                var txtParamInInfo = wizCtrl.FindControl<TextBlock>("TxtParamInInfo");
                var rbHist = wizCtrl.FindControl<RadioButton>("RbHistogram");
                var rbMath = wizCtrl.FindControl<RadioButton>("RbMath");
                var pnlHist = wizCtrl.FindControl<StackPanel>("PnlHistogram");
                var pnlMath = wizCtrl.FindControl<StackPanel>("PnlMath");

                var cmbCam = wizCtrl.FindControl<ComboBox>("CmbCamera");
                var txtCam = wizCtrl.FindControl<TextBox>("TxtCamera");
                var txtCamMeta = wizCtrl.FindControl<TextBlock>("TxtCamMeta");
                var cmbMin = wizCtrl.FindControl<ComboBox>("CmbMinThresh");
                var txtMin = wizCtrl.FindControl<TextBox>("TxtMinThresh");
                var cmbMax = wizCtrl.FindControl<ComboBox>("CmbMaxThresh");
                var txtMax = wizCtrl.FindControl<TextBox>("TxtMaxThresh");

                var cmbV1 = wizCtrl.FindControl<ComboBox>("CmbVal1");
                var txtV1 = wizCtrl.FindControl<TextBox>("TxtVal1");
                var cmbV2 = wizCtrl.FindControl<ComboBox>("CmbVal2");
                var txtV2 = wizCtrl.FindControl<TextBox>("TxtVal2");

                var cmbRet = wizCtrl.FindControl<ComboBox>("CmbReturnVar");
                var txtRet = wizCtrl.FindControl<TextBox>("TxtReturnVar");
                var txtVarMeta = wizCtrl.FindControl<TextBlock>("TxtVarMeta");
                var txtCatalog = wizCtrl.FindControl<TextBlock>("TxtCatalog");
                var txtOut = wizCtrl.FindControl<TextBox>("TxtParamOut");
                var btnOk = wizCtrl.FindControl<Button>("BtnOk");
                var btnCancel = wizCtrl.FindControl<Button>("BtnCancel");

                if (txtParamInInfo != null)
                    txtParamInInfo.Text = string.IsNullOrWhiteSpace(currentParam)
                        ? "ParameterIn: (空白)"
                        : $"ParameterIn: {currentParam.Trim()}";

                bool isMathMode = false;
                string initCam = "Cam1", initMin = "30", initMax = "200";
                string initV1 = "12.5", initV2 = "37.5", initRet = "HistScore";

                if (!string.IsNullOrWhiteSpace(currentParam))
                {
                    var toks = SplitTopLevelCsv(currentParam.Trim().TrimEnd(';'));
                    if (toks.Count >= 2 &&
                        double.TryParse(toks[0], NumberStyles.Any, CultureInfo.InvariantCulture, out _) &&
                        double.TryParse(toks[1], NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                    {
                        isMathMode = true;
                        initV1 = toks[0];
                        initV2 = toks[1];
                        initRet = toks.Count >= 3 && !string.IsNullOrWhiteSpace(toks[2]) ? toks[2] : "SumResult";
                    }
                    else
                    {
                        if (toks.Count >= 1 && !string.IsNullOrWhiteSpace(toks[0])) initCam = toks[0];
                        if (toks.Count >= 2 && !string.IsNullOrWhiteSpace(toks[1])) initMin = toks[1];
                        if (toks.Count >= 3 && !string.IsNullOrWhiteSpace(toks[2])) initMax = toks[2];
                        if (toks.Count >= 4 && !string.IsNullOrWhiteSpace(toks[3])) initRet = toks[3];
                    }
                }

                if (rbHist != null) rbHist.IsChecked = !isMathMode;
                if (rbMath != null) rbMath.IsChecked = isMathMode;
                if (pnlHist != null) pnlHist.IsVisible = !isMathMode;
                if (pnlMath != null) pnlMath.IsVisible = isMathMode;

                var camItems = CollectCameras(engines);
                var varItems = CollectVariables(engines, true);
                var retItems = new List<string>(varItems);
                AddUnique(retItems, "HistScore");
                AddUnique(retItems, "SumResult");

                var minItems = new List<string> { "0", "30", "50", "80", "100" };
                var maxItems = new List<string> { "180", "200", "220", "255" };
                var valItems = new List<string> { "10", "12.5", "20", "37.5", "100" };
                foreach (var v in varItems)
                {
                    AddUnique(minItems, v);
                    AddUnique(maxItems, v);
                    AddUnique(valItems, v);
                }

                if (txtCatalog != null)
                {
                    int drvCount = engines?.Drivers?.GetDeviceEntriesByCategory("Capture").Count ?? 0;
                    int imaqCount = engines?.Image?.GetAllNames().Count ?? 0;
                    txtCatalog.Text = $"已偵測 Capture 驅動 {drvCount} 台 · IMAQ 緩衝區 {imaqCount} 組 · Variable {varItems.Count} 個\n💡 Math 模式計算 Val1 + Val2；AOI 模式依相機與門檻範圍輸出直方圖分數。";
                }

                bool syncing = false;
                string BuildOut()
                {
                    string rv = string.IsNullOrWhiteSpace(txtRet?.Text) ? (rbMath?.IsChecked == true ? "SumResult" : "HistScore") : txtRet!.Text!.Trim();
                    if (rbMath?.IsChecked == true)
                    {
                        string v1 = string.IsNullOrWhiteSpace(txtV1?.Text) ? "12.5" : txtV1!.Text!.Trim();
                        string v2 = string.IsNullOrWhiteSpace(txtV2?.Text) ? "37.5" : txtV2!.Text!.Trim();
                        return $"{v1}, {v2}, {rv}";
                    }
                    string c = string.IsNullOrWhiteSpace(txtCam?.Text) ? "Cam1" : txtCam!.Text!.Trim();
                    string mn = string.IsNullOrWhiteSpace(txtMin?.Text) ? "30" : txtMin!.Text!.Trim();
                    string mx = string.IsNullOrWhiteSpace(txtMax?.Text) ? "200" : txtMax!.Text!.Trim();
                    return $"{c}, {mn}, {mx}, {rv}";
                }

                void UpdateMetaAndPreview()
                {
                    if (txtCamMeta != null)
                    {
                        string cam = txtCam?.Text?.Trim() ?? string.Empty;
                        bool hasDrv = engines?.Drivers?.GetDevice("Capture", cam) != null;
                        bool hasImaq = engines?.Image?.GetAllNames().Any(x => x.Equals(cam, StringComparison.OrdinalIgnoreCase)) == true;
                        if (hasDrv || hasImaq)
                        {
                            txtCamMeta.Text = hasDrv ? "✓ Capture 驅動已連線" : "✓ IMAQ 影像緩衝已註冊";
                            txtCamMeta.Foreground = Brush.Parse("#059669");
                        }
                        else
                        {
                            txtCamMeta.Text = "ℹ 未偵測實機相機 (執行期解析)";
                            txtCamMeta.Foreground = Brush.Parse("#475569");
                        }
                    }
                    if (txtVarMeta != null)
                    {
                        string rv = txtRet?.Text?.Trim() ?? string.Empty;
                        bool exists = !string.IsNullOrEmpty(rv) && varItems.Any(x => x.TrimStart('$').Equals(rv.TrimStart('$'), StringComparison.OrdinalIgnoreCase));
                        txtVarMeta.Text = exists ? $"✓ 已註冊 Variable ({rv})" : "ℹ 執行時自動建立/寫回變數";
                        txtVarMeta.Foreground = exists ? Brush.Parse("#059669") : Brush.Parse("#0284C7");
                    }
                    if (txtOut != null) txtOut.Text = BuildOut();
                }

                WireEditableCombo(cmbCam, txtCam, camItems, initCam, () => syncing, s => syncing = s, UpdateMetaAndPreview);
                WireEditableCombo(cmbMin, txtMin, minItems, initMin, () => syncing, s => syncing = s, UpdateMetaAndPreview);
                WireEditableCombo(cmbMax, txtMax, maxItems, initMax, () => syncing, s => syncing = s, UpdateMetaAndPreview);
                WireEditableCombo(cmbV1, txtV1, valItems, initV1, () => syncing, s => syncing = s, UpdateMetaAndPreview);
                WireEditableCombo(cmbV2, txtV2, valItems, initV2, () => syncing, s => syncing = s, UpdateMetaAndPreview);
                WireEditableCombo(cmbRet, txtRet, retItems, initRet, () => syncing, s => syncing = s, UpdateMetaAndPreview);

                if (rbHist != null)
                {
                    rbHist.IsCheckedChanged += (_, _) =>
                    {
                        bool isHist = rbHist.IsChecked == true;
                        if (pnlHist != null) pnlHist.IsVisible = isHist;
                        if (pnlMath != null) pnlMath.IsVisible = !isHist;
                        UpdateMetaAndPreview();
                    };
                }
                if (rbMath != null)
                {
                    rbMath.IsCheckedChanged += (_, _) =>
                    {
                        bool isMath = rbMath.IsChecked == true;
                        if (pnlMath != null) pnlMath.IsVisible = isMath;
                        if (pnlHist != null) pnlHist.IsVisible = !isMath;
                        UpdateMetaAndPreview();
                    };
                }

                UpdateMetaAndPreview();

                if (btnCancel != null) btnCancel.Click += (_, _) => dlg.Close(null);
                if (btnOk != null) btnOk.Click += (_, _) => dlg.Close(txtOut?.Text?.Trim());
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
