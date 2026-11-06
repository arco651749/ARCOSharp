using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;

namespace ARCOS.Module.IO.Guidance;

public partial class LogicEditorDialog : Window
{
    private readonly BasicEngines? _engines;
    private readonly List<string> _allIoItems = new();

    public LogicEditorDialog() : this(string.Empty, null)
    {
    }

    /// <summary>
    /// 便捷靜態調用入口，供各模組與指令引導視窗調用 $logic 編輯器
    /// </summary>
    public static async Task<string?> ShowLogicEditorDialogAsync(string currentExpr, BasicEngines? engines = null, object? owner = null)
    {
        var dlg = new LogicEditorDialog(currentExpr, engines);
        if (owner is Window winOwner)
        {
            return await dlg.ShowDialog<string?>(winOwner);
        }
        dlg.Show();
        return await Task.FromResult<string?>(null);
    }

    public LogicEditorDialog(string initialExpr, BasicEngines? engines = null)
    {
        InitializeComponent();
        _engines = engines;

        // 1. 初始化 IO 訊號清單
        InitIoList();

        // 2. 初始化 Variable 運算元清單
        InitVarControls();

        // 3. 設定初始表達式
        TxtExpression.Text = initialExpr?.Trim() ?? string.Empty;

        // 4. 事件綁定
        WireEvents();

        // 5. 初始語法校驗
        ValidateCurrentExpression();
    }

    private void InitIoList()
    {
        _allIoItems.Clear();

        if (_engines?.Io != null)
        {
            var portNames = _engines.Io.GetAllPortNames();
            foreach (var p in portNames)
            {
                if (!_allIoItems.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    _allIoItems.Add(p);
                }
            }
        }

        // 若無動態載入的點位，提供常用標準點位與範例別名
        if (_allIoItems.Count == 0)
        {
            for (int byteIdx = 0; byteIdx < 4; byteIdx++)
            {
                for (int bitIdx = 0; bitIdx < 8; bitIdx++)
                {
                    _allIoItems.Add($"P{byteIdx}.{bitIdx}");
                }
            }
            _allIoItems.Add("Sensor1?");
            _allIoItems.Add("Sensor2?");
            _allIoItems.Add("SafetyDoor?");
            _allIoItems.Add("PartIn?");
            _allIoItems.Add("Cyl_Push");
        }

        UpdateIoCombo(_allIoItems);
    }

    private void UpdateIoCombo(IEnumerable<string> items)
    {
        CmbIoList.ItemsSource = items.ToList();
        if (CmbIoList.ItemCount > 0 && CmbIoList.SelectedIndex < 0)
        {
            CmbIoList.SelectedIndex = 0;
        }
    }

    private void InitVarControls()
    {
        var varNames = new List<string>();
        if (_engines?.Variable != null)
        {
            var names = _engines.Variable.GetAllVariableNames();
            varNames.AddRange(names);
        }

        if (varNames.Count == 0)
        {
            varNames.AddRange(new[] { "Count", "Temp", "State", "ErrorCode", "RetryCount", "BatchID" });
        }
        CmbVarNames.ItemsSource = varNames;
        CmbVarNames.SelectedIndex = 0;

        CmbVarOps.ItemsSource = new[]
        {
            "== (等於)",
            "!= (不等於)",
            "> (大於)",
            "< (小於)",
            ">= (大於等於)",
            "<= (小於等於)",
            "<> (區間內 min~max)",
            "</> (區間外 min~max)",
            "==[] (空陣列/空值)",
            "!=[] (非空陣列/有值)",
            "EXIST (變數存在)",
            "NULL (變數不存在/空)"
        };
        CmbVarOps.SelectedIndex = 0;
    }

    private void WireEvents()
    {
        // IO 搜尋過濾
        TxtIoSearch.KeyUp += (_, _) =>
        {
            string keyword = TxtIoSearch.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(keyword))
            {
                UpdateIoCombo(_allIoItems);
            }
            else
            {
                var filtered = _allIoItems.Where(x => x.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                UpdateIoCombo(filtered);
            }
        };

        // 插入 IO 原樣
        BtnInsertIo.Click += (_, _) =>
        {
            string? io = CmbIoList.SelectedItem?.ToString();
            if (!string.IsNullOrEmpty(io))
            {
                AppendToken(io);
            }
        };

        // 插入 NOT IO
        BtnInsertNotIo.Click += (_, _) =>
        {
            string? io = CmbIoList.SelectedItem?.ToString();
            if (!string.IsNullOrEmpty(io))
            {
                AppendToken($"~{io}");
            }
        };

        // 變數運算子切換連動
        CmbVarOps.SelectionChanged += (_, _) =>
        {
            string opText = CmbVarOps.SelectedItem?.ToString() ?? string.Empty;
            if (opText.StartsWith("==[]") || opText.StartsWith("!=[]") ||
                opText.StartsWith("EXIST") || opText.StartsWith("NULL"))
            {
                TxtVarVal.IsEnabled = false;
                TxtVarVal.Text = string.Empty;
                TxtVarSyntaxHint.Text = "此運算子無需數值參數";
            }
            else if (opText.StartsWith("<>") || opText.StartsWith("</>"))
            {
                TxtVarVal.IsEnabled = true;
                TxtVarSyntaxHint.Text = "區間格式請使用：min~max (例如 10~50)";
            }
            else
            {
                TxtVarVal.IsEnabled = true;
                TxtVarSyntaxHint.Text = "請輸入比較目標數值或字串 (例如 10 或 \"READY\")";
            }
        };

        // 插入變數條件式
        BtnInsertVarCond.Click += (_, _) =>
        {
            string varName = !string.IsNullOrWhiteSpace(TxtCustomVar.Text)
                ? TxtCustomVar.Text.Trim()
                : (CmbVarNames.SelectedItem?.ToString() ?? string.Empty);

            if (string.IsNullOrWhiteSpace(varName)) return;

            string opText = CmbVarOps.SelectedItem?.ToString() ?? string.Empty;
            string val = TxtVarVal.Text?.Trim() ?? string.Empty;

            string expr;
            if (opText.StartsWith("EXIST"))
            {
                expr = $"EXIST {varName}";
            }
            else if (opText.StartsWith("NULL"))
            {
                expr = $"NULL {varName}";
            }
            else if (opText.StartsWith("==[]"))
            {
                expr = $"{varName} == []";
            }
            else if (opText.StartsWith("!=[]"))
            {
                expr = $"{varName} != []";
            }
            else
            {
                string rawOp = opText.Split(' ')[0];
                expr = $"{varName} {rawOp} {val}";
            }

            AppendToken($"({expr})");
        };

        // 運算子工具列
        BtnOpAnd.Click += (_, _) => AppendToken("&");
        BtnOpOr.Click += (_, _) => AppendToken("|");
        BtnOpNot.Click += (_, _) => AppendToken("~");
        BtnOpOpenParen.Click += (_, _) => AppendToken("(");
        BtnOpCloseParen.Click += (_, _) => AppendToken(")");

        BtnBackspace.Click += (_, _) =>
        {
            string text = TxtExpression.Text ?? string.Empty;
            if (text.Length > 0)
            {
                text = text.TrimEnd();
                int lastSpace = text.LastIndexOf(' ');
                TxtExpression.Text = lastSpace > 0 ? text.Substring(0, lastSpace).TrimEnd() : string.Empty;
            }
        };

        BtnClear.Click += (_, _) =>
        {
            TxtExpression.Text = string.Empty;
        };

        // 即時語法校驗
        TxtExpression.TextChanged += (_, _) => ValidateCurrentExpression();

        // 即時狀態試算
        BtnEvalLive.Click += (_, _) =>
        {
            if (_engines == null)
            {
                TxtEvalResult.Text = "試算狀態: 未注入 Engines 實例";
                TxtEvalResult.Foreground = Brushes.SlateGray;
                return;
            }

            string expr = TxtExpression.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(expr))
            {
                TxtEvalResult.Text = "試算狀態: 表達式為空";
                TxtEvalResult.Foreground = Brushes.SlateGray;
                return;
            }

            try
            {
                bool result = LogicEvaluator.EvaluateLogic(expr, _engines.Io, _engines.Variable);
                TxtEvalResult.Text = result ? "⚡ 試算結果: TRUE (條件成立)" : "⚡ 試算結果: FALSE (條件未成立)";
                TxtEvalResult.Foreground = result ? Brushes.Green : Brushes.Crimson;
            }
            catch (Exception ex)
            {
                TxtEvalResult.Text = $"試算異常: {ex.Message}";
                TxtEvalResult.Foreground = Brushes.Red;
            }
        };

        // 底部按鈕
        BtnApply.Click += (_, _) =>
        {
            string expr = TxtExpression.Text?.Trim() ?? string.Empty;
            var (isValid, errMsg) = LogicEvaluator.ValidateSyntax(expr);
            if (!isValid)
            {
                TxtValidationStatus.Text = $"❌ 無法帶入：{errMsg}";
                TxtValidationStatus.Foreground = Brushes.Red;
                return;
            }
            Close(expr);
        };

        BtnCancel.Click += (_, _) => Close(null);
    }

    private void AppendToken(string token)
    {
        string current = TxtExpression.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(current))
        {
            TxtExpression.Text = token;
        }
        else
        {
            if (token == "&" || token == "|")
            {
                TxtExpression.Text = $"{current} {token} ";
            }
            else if (token == ")" || current.EndsWith("(") || current.EndsWith("~"))
            {
                TxtExpression.Text = $"{current}{token}";
            }
            else if (current.EndsWith("& ") || current.EndsWith("| "))
            {
                TxtExpression.Text = $"{current}{token}";
            }
            else
            {
                TxtExpression.Text = $"{current} & {token}";
            }
        }
    }

    private void ValidateCurrentExpression()
    {
        string expr = TxtExpression.Text?.Trim() ?? string.Empty;
        var (isValid, errMsg) = LogicEvaluator.ValidateSyntax(expr);
        if (isValid)
        {
            TxtValidationStatus.Text = "✅ 語法有效 (Syntax Valid)";
            TxtValidationStatus.Foreground = Brushes.SeaGreen;
        }
        else
        {
            TxtValidationStatus.Text = $"❌ 語法錯誤: {errMsg}";
            TxtValidationStatus.Foreground = Brushes.Crimson;
        }
    }
}
