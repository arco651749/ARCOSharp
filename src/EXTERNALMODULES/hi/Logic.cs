using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
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

    private static void AddUnique(List<string> list, string? item)
    {
        if (string.IsNullOrWhiteSpace(item)) return;
        string s = item.Trim();
        if (!list.Any(x => x.Equals(s, StringComparison.OrdinalIgnoreCase)))
            list.Add(s);
    }

    private static List<string> CollectMessagePresets(BasicEngines? engines)
    {
        var list = new List<string>
        {
            "hi",
            "Hello World",
            "PING",
            "Station Ready",
            "Inspection Pass"
        };
        if (engines?.Variable != null)
        {
            foreach (var v in engines.Variable.GetAllVariableNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                AddUnique(list, "$" + v.TrimStart('$'));
            foreach (var sec in engines.Variable.GetAllSections())
            {
                foreach (var v in engines.Variable.GetAllVariableNames(sec).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    AddUnique(list, "$" + v.TrimStart('$'));
            }
        }
        return list;
    }

    private static string DescribeMessage(BasicEngines? engines, string? rawMsg)
    {
        if (string.IsNullOrWhiteSpace(rawMsg))
            return "ℹ 留空時執行期將輸出預設訊息: \"hi\" 至 SYSTEMLOG 並回傳 \"hi\"";
        string m = rawMsg.Trim();
        if (m.StartsWith("$") && engines?.Variable != null)
        {
            string clean = m.TrimStart('$');
            var allVars = new HashSet<string>(engines.Variable.GetAllVariableNames().Select(x => x.TrimStart('$')), StringComparer.OrdinalIgnoreCase);
            foreach (var sec in engines.Variable.GetAllSections())
            {
                foreach (var v in engines.Variable.GetAllVariableNames(sec))
                    allVars.Add(v.TrimStart('$'));
            }
            if (allVars.Contains(clean))
            {
                try
                {
                    string cur = engines.Variable.GetVariable(clean);
                    return $"✓ 參照變數 {m} (目前值: \"{cur}\") -> 寫入 SYSTEMLOG 並回傳";
                }
                catch { }
            }
            return $"ℹ 動態變數參照 {m} -> 寫入 SYSTEMLOG 並回傳";
        }
        return $"✓ 執行時將輸出 \"{m}\" 至日誌與 SYSTEMLOG 並作為 Call 回傳值";
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
                    SizeToContent = SizeToContent.WidthAndHeight,
                    MinWidth = 680,
                    CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = wizCtrl
                };

                var txtParamInInfo = wizCtrl.FindControl<TextBlock>("TxtParamInInfo");
                var txtVarMeta = wizCtrl.FindControl<TextBlock>("TxtVarMeta");
                var txtCatalog = wizCtrl.FindControl<TextBlock>("TxtCatalog");
                var cmbInput = wizCtrl.FindControl<ComboBox>("CmbCallInput");
                var txtInput = wizCtrl.FindControl<TextBox>("TxtCallInput");
                var txtOut = wizCtrl.FindControl<TextBox>("TxtParamOut");
                var btnOk = wizCtrl.FindControl<Button>("BtnOk");
                var btnCancel = wizCtrl.FindControl<Button>("BtnCancel");

                string initVal = string.IsNullOrWhiteSpace(currentParam) ? "hi" : currentParam.Trim();
                if (txtParamInInfo != null)
                    txtParamInInfo.Text = string.IsNullOrWhiteSpace(currentParam)
                        ? "ParameterIn: (空白，預設輸出 \"hi\")"
                        : $"ParameterIn: {currentParam.Trim()}";

                var presets = CollectMessagePresets(engines);
                int varCount = engines?.Variable?.GetAllVariableNames().Count ?? 0;

                bool syncing = false;
                void UpdateAll()
                {
                    string val = txtInput?.Text?.Trim() ?? "";
                    if (txtVarMeta != null)
                        txtVarMeta.Text = $"預設常用字詞 + Variable 清單 ({varCount} 筆)";
                    if (txtCatalog != null)
                        txtCatalog.Text = DescribeMessage(engines, val);
                    if (txtOut != null)
                        txtOut.Text = val;
                }

                WireEditableCombo(cmbInput, txtInput, () => presets, initVal, () => syncing, v => syncing = v, UpdateAll);
                UpdateAll();

                void AcceptAndClose()
                {
                    string outVal = txtOut?.Text?.Trim() ?? "";
                    dlg.Close(outVal);
                }

                if (btnCancel != null) btnCancel.Click += (_, _) => dlg.Close(null);
                if (btnOk != null) btnOk.Click += (_, _) => AcceptAndClose();
                dlg.KeyDown += (_, e) =>
                {
                    if (cmbInput?.IsDropDownOpen == true) return;
                    if (e.Key == Key.Enter) { AcceptAndClose(); e.Handled = true; }
                    else if (e.Key == Key.Escape) { dlg.Close(null); e.Handled = true; }
                };

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
