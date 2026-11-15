using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;

namespace ARCOS.App.Dialogs;

public sealed class LogicIoItem
{
    public string PrimaryName { get; init; } = string.Empty;
    public string SubLabel { get; init; } = string.Empty;
    public bool IsNamedOrVirtual { get; init; }
    public bool IsOn { get; init; }
    public string StateText => IsOn ? "ON" : "OFF";
    public IBrush StateBg => IsOn ? Brush.Parse("#DCFCE7") : Brush.Parse("#F1F5F9");
    public IBrush StateBorder => IsOn ? Brush.Parse("#86EFAC") : Brush.Parse("#CBD5E1");
    public IBrush StateFg => IsOn ? Brush.Parse("#15803D") : Brush.Parse("#64748B");
}

public sealed class LogicVarItem
{
    public string RawKey { get; init; } = string.Empty;
    public string Section { get; init; } = "Global";
    public string DisplayKey => Section.Equals("Global", StringComparison.OrdinalIgnoreCase)
        ? RawKey
        : $"{RawKey}@{Section}";
    public string PreviewValue { get; init; } = string.Empty;
}

public partial class LogicEditorWindow : Window
{
    private readonly BasicEngines? _engines;
    private readonly List<LogicIoItem> _allIoItems = new();
    private readonly List<LogicVarItem> _allVarItems = new();
    private readonly Stack<string> _undoHistory = new();
    private bool _suppressUndoPush;
    private string _lastCommittedText = string.Empty;

    public LogicEditorWindow() : this(string.Empty, null)
    {
    }

    public LogicEditorWindow(string initialExpr, BasicEngines? engines)
    {
        _engines = engines;
        InitializeComponent();

        InitStaticPickers();
        LoadLiveEngineData();
        WireAllEvents();

        string initial = initialExpr?.Trim() ?? string.Empty;
        _suppressUndoPush = true;
        TxtExpression.Text = initial;
        TxtExpression.CaretIndex = initial.Length;
        _lastCommittedText = initial;
        _suppressUndoPush = false;

        ValidateCurrentExpression();
    }

    /// <summary>
    /// 供 LogicEditorService 呼叫的非同步彈窗入口
    /// </summary>
    public static async Task<string?> ShowAsync(string currentExpr, BasicEngines? engines, object? owner = null)
    {
        var dlg = new LogicEditorWindow(currentExpr, engines);
        Window? ownerWindow = owner as Window;
        if (ownerWindow == null && owner is Visual visual)
        {
            ownerWindow = TopLevel.GetTopLevel(visual) as Window;
        }
        ownerWindow ??= Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        if (ownerWindow != null)
        {
            return await dlg.ShowDialog<string?>(ownerWindow);
        }

        dlg.Show();
        return null;
    }

    private void InitStaticPickers()
    {
        CmbIoMode.ItemsSource = new[] { "全部點位", "別名/虛擬", "僅 ON" };
        CmbIoMode.SelectedIndex = 0;

        CmbQuickOp.ItemsSource = new[]
        {
            "==",
            "!=",
            ">",
            ">=",
            "<",
            "<=",
            "<> (區間)",
            "</> (區間外)",
            "==NULL",
            "!=NULL",
            "==EXIST",
            "!=EXIST",
            "==[]",
            "!=[]",
            "==[;;]",
            "!=[;;]"
        };
        CmbQuickOp.SelectedIndex = 0;
    }

    private void LoadLiveEngineData()
    {
        // 1. 載入 IO 點位 (別名 + 虛擬點 + 實體埠，含即時狀態)
        _allIoItems.Clear();
        if (_engines?.Io != null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var snapshots = _engines.Io.GetAllSnapshot();
            foreach (var snap in snapshots)
            {
                bool hasAlias = !string.IsNullOrWhiteSpace(snap.Alias);
                string primary = (!snap.IsVirtual && hasAlias) ? snap.Alias.Trim() : snap.Port;
                if (string.IsNullOrWhiteSpace(primary) || !seen.Add(primary)) continue;

                string sub = snap.IsVirtual
                    ? (hasAlias ? $"({snap.Alias})" : "(Virtual)")
                    : (hasAlias ? $"({snap.Port})" : string.Empty);

                _allIoItems.Add(new LogicIoItem
                {
                    PrimaryName = primary,
                    SubLabel = sub,
                    IsNamedOrVirtual = snap.IsVirtual || hasAlias,
                    IsOn = snap.Value
                });
            }

            foreach (var name in _engines.Io.GetConfiguredPortNames())
            {
                if (!seen.Add(name)) continue;
                bool val = false;
                try { val = _engines.Io.GetPort(name); } catch { }
                _allIoItems.Add(new LogicIoItem
                {
                    PrimaryName = name,
                    SubLabel = name.StartsWith("P", StringComparison.OrdinalIgnoreCase) && name.Contains('.') ? string.Empty : "(IO)",
                    IsNamedOrVirtual = !(name.StartsWith("P", StringComparison.OrdinalIgnoreCase) && name.Contains('.')),
                    IsOn = val
                });
            }
        }
        else
        {
            for (int byteIdx = 0; byteIdx < 4; byteIdx++)
            {
                for (int bitIdx = 0; bitIdx < 8; bitIdx++)
                {
                    _allIoItems.Add(new LogicIoItem { PrimaryName = $"P{byteIdx}.{bitIdx}" });
                }
            }
        }

        ApplyIoFilter();

        // 2. 載入 Sections 與系統變數
        string prevSection = CmbVarSection.SelectedItem?.ToString() ?? "Global";
        var sections = new List<string> { "Global" };
        if (_engines?.Variable != null)
        {
            foreach (var sec in _engines.Variable.GetAllSections())
            {
                if (!sections.Contains(sec, StringComparer.OrdinalIgnoreCase))
                {
                    sections.Add(sec);
                }
            }
        }
        CmbVarSection.ItemsSource = sections;
        CmbVarSection.SelectedItem = sections.FirstOrDefault(s => s.Equals(prevSection, StringComparison.OrdinalIgnoreCase)) ?? sections[0];

        LoadVariablesForSelectedSection();
    }

    private void LoadVariablesForSelectedSection()
    {
        _allVarItems.Clear();
        string section = CmbVarSection.SelectedItem?.ToString() ?? "Global";

        if (_engines?.Variable != null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in _engines.Variable.GetAllVariableNames(section))
            {
                if (!seen.Add(name)) continue;
                string preview = string.Empty;
                try { preview = _engines.Variable.GetRaw(name, section); }
                catch { preview = string.Empty; }

                _allVarItems.Add(new LogicVarItem
                {
                    RawKey = name,
                    Section = section,
                    PreviewValue = preview
                });
            }
        }

        ApplyVariableFilter();
    }

    private void ApplyIoFilter()
    {
        string kw = TxtIoFilter.Text?.Trim() ?? string.Empty;
        int mode = CmbIoMode.SelectedIndex;

        IEnumerable<LogicIoItem> query = _allIoItems;
        if (mode == 1) query = query.Where(x => x.IsNamedOrVirtual);
        else if (mode == 2) query = query.Where(x => x.IsOn);

        if (!string.IsNullOrEmpty(kw))
        {
            query = query.Where(x =>
                x.PrimaryName.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                x.SubLabel.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = query
            .OrderByDescending(x => x.IsNamedOrVirtual)
            .ThenBy(x => x.PrimaryName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LstIoPorts.ItemsSource = filtered;
    }

    private void ApplyVariableFilter()
    {
        string kw = TxtVarFilter.Text?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrEmpty(kw)
            ? _allVarItems.ToList()
            : _allVarItems.Where(x => x.RawKey.Contains(kw, StringComparison.OrdinalIgnoreCase)).ToList();

        LstVariables.ItemsSource = filtered;
        TxtVarCount.Text = $"{filtered.Count} 項";
    }

    private void WireAllEvents()
    {
        // 篩選與刷新
        TxtIoFilter.TextChanged += (_, _) => ApplyIoFilter();
        CmbIoMode.SelectionChanged += (_, _) => ApplyIoFilter();
        TxtVarFilter.TextChanged += (_, _) => ApplyVariableFilter();
        CmbVarSection.SelectionChanged += (_, _) => LoadVariablesForSelectedSection();
        this.FindControl<Button>("BtnRefreshLive")!.Click += (_, _) => LoadLiveEngineData();

        // 範例快速填入
        this.FindControl<Button>("BtnSampleIo")!.Click += (_, _) => SetExpression("(Sensor_Home? & ~Alarm?)");
        this.FindControl<Button>("BtnSampleRange")!.Click += (_, _) => SetExpression("(Temp <> 20~80)");
        this.FindControl<Button>("BtnSampleJson")!.Click += (_, _) => SetExpression("(Data::status == OK) & (Queue != [])");

        // IO 插入按鈕與雙擊
        this.FindControl<Button>("BtnInsertIo")!.Click += (_, _) =>
        {
            if (LstIoPorts.SelectedItem is LogicIoItem item)
                InsertAtCaret(item.PrimaryName, asOperand: true);
        };
        this.FindControl<Button>("BtnInsertNotIo")!.Click += (_, _) =>
        {
            if (LstIoPorts.SelectedItem is LogicIoItem item)
                InsertAtCaret($"~{item.PrimaryName}", asOperand: true);
        };
        LstIoPorts.DoubleTapped += (_, _) =>
        {
            if (LstIoPorts.SelectedItem is LogicIoItem item)
                InsertAtCaret(item.PrimaryName, asOperand: true);
        };

        // 變數點選帶入快速條件器、雙擊插入表達式
        LstVariables.SelectionChanged += (_, _) =>
        {
            if (LstVariables.SelectedItem is LogicVarItem item)
                TxtQuickVar.Text = item.DisplayKey;
        };
        LstVariables.DoubleTapped += (_, _) =>
        {
            if (LstVariables.SelectedItem is LogicVarItem item)
                InsertAtCaret(item.DisplayKey, asOperand: true);
        };

        // 邏輯與括號運算子按鈕
        this.FindControl<Button>("BtnOpAnd")!.Click += (_, _) => InsertOperator(" & ");
        this.FindControl<Button>("BtnOpOr")!.Click += (_, _) => InsertOperator(" | ");
        this.FindControl<Button>("BtnOpNot")!.Click += (_, _) => InsertRawAtCaret("~");
        this.FindControl<Button>("BtnOpOpen")!.Click += (_, _) => InsertRawAtCaret("(");
        this.FindControl<Button>("BtnOpClose")!.Click += (_, _) => InsertRawAtCaret(")");
        this.FindControl<Button>("BtnOpWrap")!.Click += (_, _) => WrapSelectionWithParentheses();

        // 比較與存在運算子按鈕
        this.FindControl<Button>("BtnOpEq")!.Click += (_, _) => InsertRawAtCaret(" == ");
        this.FindControl<Button>("BtnOpNeq")!.Click += (_, _) => InsertRawAtCaret(" != ");
        this.FindControl<Button>("BtnOpGt")!.Click += (_, _) => InsertRawAtCaret(" > ");
        this.FindControl<Button>("BtnOpGte")!.Click += (_, _) => InsertRawAtCaret(" >= ");
        this.FindControl<Button>("BtnOpLt")!.Click += (_, _) => InsertRawAtCaret(" < ");
        this.FindControl<Button>("BtnOpLte")!.Click += (_, _) => InsertRawAtCaret(" <= ");
        this.FindControl<Button>("BtnOpInRange")!.Click += (_, _) => InsertRawAtCaret(" <> ");
        this.FindControl<Button>("BtnOpOutRange")!.Click += (_, _) => InsertRawAtCaret(" </> ");
        this.FindControl<Button>("BtnOpTildeRange")!.Click += (_, _) => InsertRawAtCaret("~");

        this.FindControl<Button>("BtnOpEqNull")!.Click += (_, _) => InsertRawAtCaret("==NULL");
        this.FindControl<Button>("BtnOpNeqNull")!.Click += (_, _) => InsertRawAtCaret("!=NULL");
        this.FindControl<Button>("BtnOpEqExist")!.Click += (_, _) => InsertRawAtCaret("==EXIST");
        this.FindControl<Button>("BtnOpNeqExist")!.Click += (_, _) => InsertRawAtCaret("!=EXIST");
        this.FindControl<Button>("BtnOpEqEmptyArr")!.Click += (_, _) => InsertRawAtCaret("==[]");
        this.FindControl<Button>("BtnOpNeqEmptyArr")!.Click += (_, _) => InsertRawAtCaret("!=[]");
        this.FindControl<Button>("BtnOpEqEmptyMat")!.Click += (_, _) => InsertRawAtCaret("==[;;]");
        this.FindControl<Button>("BtnOpNeqEmptyMat")!.Click += (_, _) => InsertRawAtCaret("!=[;;]");

        this.FindControl<Button>("BtnOpAtSection")!.Click += (_, _) =>
        {
            string sec = CmbVarSection.SelectedItem?.ToString() ?? "Global";
            InsertRawAtCaret(sec.Equals("Global", StringComparison.OrdinalIgnoreCase) ? "@" : $"@{sec}");
        };
        this.FindControl<Button>("BtnOpJsonPath")!.Click += (_, _) => InsertRawAtCaret("::");

        // 快速變數條件組裝
        CmbQuickOp.SelectionChanged += (_, _) =>
        {
            string op = CmbQuickOp.SelectedItem?.ToString() ?? string.Empty;
            bool isUnary = op.Contains("NULL", StringComparison.Ordinal) ||
                           op.Contains("EXIST", StringComparison.Ordinal) ||
                           op.Contains("[]", StringComparison.Ordinal) ||
                           op.Contains("[;;]", StringComparison.Ordinal);
            TxtQuickVal.IsEnabled = !isUnary;
            if (isUnary) TxtQuickVal.Text = string.Empty;
        };

        this.FindControl<Button>("BtnInsertQuickCond")!.Click += (_, _) =>
        {
            string varName = TxtQuickVar.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(varName))
            {
                if (LstVariables.SelectedItem is LogicVarItem sel) varName = sel.DisplayKey;
            }
            if (string.IsNullOrEmpty(varName)) return;

            string opText = CmbQuickOp.SelectedItem?.ToString() ?? "==";
            string rawOp = opText.Split(' ')[0];
            string val = TxtQuickVal.Text?.Trim() ?? string.Empty;

            string cond;
            if (rawOp is "==NULL" or "!=NULL" or "==EXIST" or "!=EXIST" or "==[]" or "!=[]" or "==[;;]" or "!=[;;]")
            {
                cond = $"({varName}{rawOp})";
            }
            else
            {
                cond = $"({varName} {rawOp} {val})";
            }

            InsertAtCaret(cond, asOperand: true);
        };

        // 編輯框歷史與按鈕
        TxtExpression.TextChanged += (_, _) =>
        {
            string cur = TxtExpression.Text ?? string.Empty;
            if (!_suppressUndoPush && cur != _lastCommittedText)
            {
                _undoHistory.Push(_lastCommittedText);
                _lastCommittedText = cur;
            }
            ValidateCurrentExpression();
        };

        TxtExpression.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                TryApplyAndClose();
                e.Handled = true;
            }
        };

        this.FindControl<Button>("BtnUndoExpr")!.Click += (_, _) =>
        {
            if (_undoHistory.Count > 0)
            {
                string prev = _undoHistory.Pop();
                _suppressUndoPush = true;
                TxtExpression.Text = prev;
                TxtExpression.CaretIndex = prev.Length;
                _lastCommittedText = prev;
                _suppressUndoPush = false;
                ValidateCurrentExpression();
            }
        };

        this.FindControl<Button>("BtnClearExpr")!.Click += (_, _) => SetExpression(string.Empty);
        this.FindControl<Button>("BtnEvalLive")!.Click += (_, _) => EvaluateCurrentExpressionLive();
        this.FindControl<Button>("BtnCancel")!.Click += (_, _) => Close(null);
        this.FindControl<Button>("BtnApply")!.Click += (_, _) => TryApplyAndClose();
    }

    private void SetExpression(string text)
    {
        TxtExpression.Text = text;
        TxtExpression.CaretIndex = text.Length;
        TxtExpression.Focus();
    }

    private void InsertRawAtCaret(string fragment)
    {
        string text = TxtExpression.Text ?? string.Empty;
        int start = Math.Clamp(TxtExpression.SelectionStart, 0, text.Length);
        int end = Math.Clamp(TxtExpression.SelectionEnd, 0, text.Length);
        int min = Math.Min(start, end);
        int max = Math.Max(start, end);

        string updated = text.Substring(0, min) + fragment + text.Substring(max);
        TxtExpression.Text = updated;
        TxtExpression.SelectionStart = TxtExpression.SelectionEnd = min + fragment.Length;
        TxtExpression.CaretIndex = min + fragment.Length;
        TxtExpression.Focus();
    }

    private void InsertOperator(string opWithSpaces)
    {
        string text = TxtExpression.Text ?? string.Empty;
        int pos = Math.Clamp(Math.Min(TxtExpression.SelectionStart, TxtExpression.SelectionEnd), 0, text.Length);
        string before = text.Substring(0, pos);

        // 若游標前已有空白或 ( ，不重複前置空白
        string fragment = (string.IsNullOrEmpty(before) || before.EndsWith(' ') || before.EndsWith('('))
            ? opWithSpaces.TrimStart()
            : opWithSpaces;

        InsertRawAtCaret(fragment);
    }

    private void InsertAtCaret(string operand, bool asOperand)
    {
        string text = TxtExpression.Text ?? string.Empty;
        int start = Math.Clamp(TxtExpression.SelectionStart, 0, text.Length);
        int end = Math.Clamp(TxtExpression.SelectionEnd, 0, text.Length);
        int min = Math.Min(start, end);

        if (!asOperand || min == 0)
        {
            InsertRawAtCaret(operand);
            return;
        }

        string before = text.Substring(0, min).TrimEnd();
        bool needsJoiner = !string.IsNullOrEmpty(before) &&
                           !before.EndsWith('&') &&
                           !before.EndsWith('|') &&
                           !before.EndsWith('(') &&
                           !before.EndsWith('~') &&
                           !before.EndsWith('!') &&
                           !before.EndsWith("==") &&
                           !before.EndsWith("!=") &&
                           !before.EndsWith(">=") &&
                           !before.EndsWith("<=") &&
                           !before.EndsWith('>') &&
                           !before.EndsWith('<');

        string prefix = needsJoiner ? " & " : (text[min - 1] is '(' or '~' or '!' or ' ' ? string.Empty : " ");
        InsertRawAtCaret(prefix + operand);
    }

    private void WrapSelectionWithParentheses()
    {
        string text = TxtExpression.Text ?? string.Empty;
        int start = Math.Clamp(TxtExpression.SelectionStart, 0, text.Length);
        int end = Math.Clamp(TxtExpression.SelectionEnd, 0, text.Length);
        int min = Math.Min(start, end);
        int max = Math.Max(start, end);

        if (max > min)
        {
            string selected = text.Substring(min, max - min);
            string updated = text.Substring(0, min) + $"({selected})" + text.Substring(max);
            TxtExpression.Text = updated;
            TxtExpression.CaretIndex = min + selected.Length + 2;
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            string updated = $"({text.Trim()})";
            TxtExpression.Text = updated;
            TxtExpression.CaretIndex = updated.Length;
        }
        else
        {
            InsertRawAtCaret("()");
            TxtExpression.CaretIndex = 1;
        }
        TxtExpression.Focus();
    }

    private void ValidateCurrentExpression()
    {
        string expr = TxtExpression.Text?.Trim() ?? string.Empty;
        var (isValid, errMsg) = LogicEvaluator.ValidateSyntax(expr);
        if (isValid)
        {
            TxtSyntaxStatus.Text = string.IsNullOrEmpty(expr) ? "提示：請組裝或輸入邏輯表達式" : "✓ 語法有效 (Syntax Valid)";
            TxtSyntaxStatus.Foreground = string.IsNullOrEmpty(expr) ? Brush.Parse("#64748B") : Brush.Parse("#15803D");
        }
        else
        {
            TxtSyntaxStatus.Text = $"✗ 語法錯誤：{errMsg}";
            TxtSyntaxStatus.Foreground = Brush.Parse("#DC2626");
        }
    }

    private void EvaluateCurrentExpressionLive()
    {
        if (_engines == null)
        {
            TxtEvalResult.Text = "試算狀態：未連接系統引擎";
            TxtEvalResult.Foreground = Brush.Parse("#64748B");
            return;
        }

        string expr = TxtExpression.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(expr))
        {
            TxtEvalResult.Text = "試算狀態：表達式為空";
            TxtEvalResult.Foreground = Brush.Parse("#64748B");
            return;
        }

        string section = CmbVarSection.SelectedItem?.ToString() ?? "Global";
        try
        {
            bool res = LogicEvaluator.EvaluateLogic(expr, _engines.Io, _engines.Variable, section);
            TxtEvalResult.Text = res ? "⚡ 試算結果：TRUE (條件成立)" : "⚡ 試算結果：FALSE (條件未成立)";
            TxtEvalResult.Foreground = res ? Brush.Parse("#15803D") : Brush.Parse("#DC2626");
        }
        catch (Exception ex)
        {
            TxtEvalResult.Text = $"試算異常：{ex.Message}";
            TxtEvalResult.Foreground = Brush.Parse("#DC2626");
        }
    }

    private void TryApplyAndClose()
    {
        string expr = TxtExpression.Text?.Trim() ?? string.Empty;
        var (isValid, errMsg) = LogicEvaluator.ValidateSyntax(expr);
        if (!isValid)
        {
            TxtSyntaxStatus.Text = $"✗ 無法套用：{errMsg}";
            TxtSyntaxStatus.Foreground = Brush.Parse("#DC2626");
            TxtExpression.Focus();
            return;
        }
        Close(expr);
    }
}
