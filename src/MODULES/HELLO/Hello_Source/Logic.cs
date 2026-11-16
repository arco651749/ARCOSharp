using System;
using System.Collections.Generic;
using System.IO;
using ARCOS.Core.Engines;
using Avalonia.Input;
using System.Linq;
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
    {
        if (string.IsNullOrWhiteSpace(action) || action.Equals("getCMD", StringComparison.OrdinalIgnoreCase))
            return CommandResult.Done();

        string? csviPath = ModuleRegistry.ResolveCommandCsviPath(ModuleName, action);
        if (csviPath == null)
            return CommandResult.Fail($"Command CSVI file not found: MODULES/{ModuleName}/{action}.csvi");

        var handler = (ctx.Registry ?? new ModuleRegistry()).LoadCommandCsviHandler(csviPath);
        if (handler == null)
            return CommandResult.Fail($"Failed to load command CSVI handler from: {csviPath}");

        return handler.Execute(action, filteredParam, ctx);
    }

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
                    Title = "Hello Master Instrument - Parameter Guidance",
                    Width = 660,
                    Height = 440,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = wizCtrl
                };

                var txtIn = wizCtrl.FindControl<TextBlock>("TxtParamInInfo");
                var txtMeta = wizCtrl.FindControl<TextBlock>("TxtMasterMeta");
                var txtOut = wizCtrl.FindControl<TextBox>("TxtParamOut");
                var btnOk = wizCtrl.FindControl<Button>("BtnOk");
                var btnCancel = wizCtrl.FindControl<Button>("BtnCancel");

                if (txtIn != null) txtIn.Text = $"ParameterIn: {(string.IsNullOrWhiteSpace(currentParam) ? "(空白)" : currentParam.Trim())}";
                if (txtMeta != null)
                {
                    var cmds = GetSupportedCommands();
                    txtMeta.Text = $"目前狀態: {GetHelloStatusSummary(engines)} | 支援指令: {string.Join(", ", cmds)}";
                }
                if (txtOut != null) txtOut.Text = currentParam?.Trim() ?? "";

                if (btnCancel != null) btnCancel.Click += (_, _) => dlg.Close(null);
                if (btnOk != null) btnOk.Click += (_, _) => dlg.Close(txtOut?.Text?.Trim() ?? "");
                dlg.KeyDown += (_, e) => { if (e.Key == Key.Enter) dlg.Close(txtOut?.Text?.Trim() ?? ""); else if (e.Key == Key.Escape) dlg.Close(null); };

                return await dlg.ShowDialog<string?>(ownerWin);
            }
        }
        return currentParam;
    }

    private static void AddUnique(List<string> list, string? item)
    {
        if (string.IsNullOrWhiteSpace(item)) return;
        string s = item.Trim();
        if (!list.Any(x => x.Equals(s, StringComparison.OrdinalIgnoreCase)))
            list.Add(s);
    }

    private static string GetHelloStatusSummary(BasicEngines? engines)
    {
        if (engines?.Variable == null) return "未偵測 VariableEngine";
        try
        {
            var helloNames = engines.Variable.GetAllVariableNames("Hello");
            if (helloNames.Any(n => n.Equals("Hello_Status", StringComparison.OrdinalIgnoreCase)))
            {
                string val = engines.Variable.Get("Hello_Status", "Hello");
                return $"Hello_Status = {val}";
            }
        }
        catch { }
        return "Hello_Status = (尚未執行 Hello@Init)";
    }

    private static List<string> CollectHelloMessagePresets(BasicEngines? engines)
    {
        var list = new List<string>
        {
            "hello world",
            "Hello ARCOS v3.0!",
            "System Startup OK",
            "[Batch] Inspection Ready"
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

    private static string DescribeHelloMessage(BasicEngines? engines, string? rawMsg)
    {
        if (string.IsNullOrWhiteSpace(rawMsg))
            return "ℹ 留空時執行期將輸出預設訊息: \"hello world\" -> SYSTEMLOG";
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
                    return $"✓ 參照變數 {m} (目前值: \"{cur}\") -> SYSTEMLOG";
                }
                catch { }
            }
            return $"ℹ 動態變數參照 {m} -> SYSTEMLOG";
        }
        return $"✓ 執行時將記錄 \"{m}\" 至日誌與 SYSTEMLOG";
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
