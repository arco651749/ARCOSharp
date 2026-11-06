using System.Globalization;
using ARCOS.Core.Engines;

namespace ARCOS.Core.Common;

/// <summary>
/// 複合邏輯表達式解析與運算工具 (對應 LabVIEW 的 IOOperator.vi)
/// 支援 &amp;, |, ~, ==, !=, &gt;, &lt;, &gt;=, &lt;=, 括號 (), NULL, EXIST
/// 自動向 IoEngine 與 VariableEngine 提取數值比對
/// </summary>
public class LogicEvaluator
{
    private readonly IoEngine _ioEngine;
    private readonly VariableEngine _varEngine;

    public LogicEvaluator(IoEngine ioEngine, VariableEngine varEngine)
    {
        _ioEngine = ioEngine ?? throw new ArgumentNullException(nameof(ioEngine));
        _varEngine = varEngine ?? throw new ArgumentNullException(nameof(varEngine));
    }

    /// <summary>
    /// 便捷靜態評估方法 (傳入 IoEngine 與 VariableEngine)
    /// </summary>
    public static bool EvaluateLogic(string logicExpr, IoEngine io, VariableEngine variable, string currentSection = "Global")
    {
        var eval = new LogicEvaluator(io, variable);
        return eval.Evaluate(logicExpr, currentSection);
    }

    /// <summary>
    /// 計算複合邏輯運算式結果
    /// </summary>
    public bool Evaluate(string logicExpr, string currentSection = "Global")
    {
        if (string.IsNullOrWhiteSpace(logicExpr)) return true;

        string expr = logicExpr.Trim();

        // 1. 特殊關鍵字 NULL / EXIST 檢查
        if (expr.StartsWith("EXIST ", StringComparison.OrdinalIgnoreCase))
        {
            string varName = expr.Substring(6).Trim();
            try { return !string.IsNullOrEmpty(_varEngine.Get(varName, currentSection)); }
            catch { return false; }
        }
        if (expr.StartsWith("NULL ", StringComparison.OrdinalIgnoreCase))
        {
            string varName = expr.Substring(5).Trim();
            try { return string.IsNullOrEmpty(_varEngine.Get(varName, currentSection)); }
            catch { return true; }
        }

        // 2. 透過遞迴下降剖析器解析 OR (|) 表達式
        var parser = new ExpressionParser(expr, token => ResolveToken(token, currentSection));
        return parser.Parse();
    }

    /// <summary>
    /// 檢驗邏輯表達式語法是否合法，並回傳錯誤訊息
    /// </summary>
    public static (bool IsValid, string? ErrorMessage) ValidateSyntax(string? logicExpr)
    {
        if (string.IsNullOrWhiteSpace(logicExpr))
            return (true, null);

        string expr = logicExpr.Trim();

        // 1. 括號平衡檢查
        int balance = 0;
        foreach (char c in expr)
        {
            if (c == '(') balance++;
            else if (c == ')')
            {
                balance--;
                if (balance < 0) return (false, "括號不匹配：有多餘的右括號 ')'");
            }
        }
        if (balance > 0) return (false, "括號不匹配：缺少右括號 ')'");

        // 2. 特殊關鍵字檢查
        if (expr.StartsWith("EXIST ", StringComparison.OrdinalIgnoreCase) ||
            expr.StartsWith("NULL ", StringComparison.OrdinalIgnoreCase))
        {
            int spaceIdx = expr.IndexOf(' ');
            string target = expr.Substring(spaceIdx + 1).Trim();
            if (string.IsNullOrWhiteSpace(target))
                return (false, "語法錯誤：關鍵字後缺少目標變數名稱");
            return (true, null);
        }

        // 3. 語法樹剖析測試
        try
        {
            var parser = new ExpressionParser(expr, _ => true);
            parser.Parse();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private bool ResolveToken(string rawToken, string section)
    {
        string token = rawToken.Trim();

        // 1. 檢查內部是否為比較式：如 $Count > 5 或 Status == "OK"
        if (TryEvaluateComparison(token, section, out bool compResult))
        {
            return compResult;
        }

        // 2. 布林常數
        if (string.Equals(token, "True", StringComparison.OrdinalIgnoreCase) || token == "1") return true;
        if (string.Equals(token, "False", StringComparison.OrdinalIgnoreCase) || token == "0") return false;

        // 3. 變數引用：以 $ 開頭
        if (token.StartsWith('$'))
        {
            string varKey = token.TrimStart('$');
            try
            {
                string val = _varEngine.Get(varKey, section);
                if (bool.TryParse(val, out bool bVal)) return bVal;
                if (double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out double dVal)) return dVal != 0;
                return !string.IsNullOrEmpty(val);
            }
            catch
            {
                return false;
            }
        }

        // 4. IO 點位引用（含別名、實體點位 Pn.m、虛擬旗標 #XXX?）
        try
        {
            return _ioEngine.GetPort(token);
        }
        catch
        {
            return false;
        }
    }

    private bool TryEvaluateComparison(string token, string section, out bool result)
    {
        result = false;

        // 0. 陣列/變數空值判定 ==[] 或 !=[] (如 Var == [] 或 Var != [])
        if (token.Contains("==[]") || token.Contains("== []"))
        {
            int idx = token.IndexOf("==", StringComparison.Ordinal);
            string leftStr = token.Substring(0, idx).Trim();
            string val = ResolveValue(leftStr, section);
            result = IsEmptyArrayOrValue(val);
            return true;
        }
        if (token.Contains("!=[]") || token.Contains("!= []"))
        {
            int idx = token.IndexOf("!=", StringComparison.Ordinal);
            string leftStr = token.Substring(0, idx).Trim();
            string val = ResolveValue(leftStr, section);
            result = !IsEmptyArrayOrValue(val);
            return true;
        }

        // 1. 區間判定：</> (區間外) 與 <> (區間內)
        if (token.Contains("</>"))
        {
            int idx = token.IndexOf("</>", StringComparison.Ordinal);
            string leftStr = token.Substring(0, idx).Trim();
            string rightStr = token.Substring(idx + 3).Trim();
            string leftVal = ResolveValue(leftStr, section);
            if (TryParseRange(rightStr, out double min, out double max) &&
                double.TryParse(leftVal, NumberStyles.Any, CultureInfo.InvariantCulture, out double num))
            {
                result = num < min || num > max;
                return true;
            }
        }
        if (token.Contains("<>"))
        {
            int idx = token.IndexOf("<>", StringComparison.Ordinal);
            string leftStr = token.Substring(0, idx).Trim();
            string rightStr = token.Substring(idx + 2).Trim();
            string leftVal = ResolveValue(leftStr, section);
            if (TryParseRange(rightStr, out double min, out double max) &&
                double.TryParse(leftVal, NumberStyles.Any, CultureInfo.InvariantCulture, out double num))
            {
                result = num >= min && num <= max;
                return true;
            }
        }

        string[] ops = new[] { "==", "!=", ">=", "<=", ">", "<" };

        foreach (var op in ops)
        {
            int idx = token.IndexOf(op, StringComparison.Ordinal);
            if (idx > 0)
            {
                string leftStr = token.Substring(0, idx).Trim();
                string rightStr = token.Substring(idx + op.Length).Trim();

                string leftVal = ResolveValue(leftStr, section);
                string rightVal = ResolveValue(rightStr, section);

                bool isLeftNum = double.TryParse(leftVal, NumberStyles.Any, CultureInfo.InvariantCulture, out double leftNum);
                bool isRightNum = double.TryParse(rightVal, NumberStyles.Any, CultureInfo.InvariantCulture, out double rightNum);

                if (isLeftNum && isRightNum)
                {
                    result = op switch
                    {
                        "==" => Math.Abs(leftNum - rightNum) < 1e-9,
                        "!=" => Math.Abs(leftNum - rightNum) >= 1e-9,
                        ">" => leftNum > rightNum,
                        "<" => leftNum < rightNum,
                        ">=" => leftNum >= rightNum,
                        "<=" => leftNum <= rightNum,
                        _ => false
                    };
                }
                else
                {
                    // 字串比較
                    int cmp = string.Compare(leftVal, rightVal, StringComparison.OrdinalIgnoreCase);
                    result = op switch
                    {
                        "==" => cmp == 0,
                        "!=" => cmp != 0,
                        ">" => cmp > 0,
                        "<" => cmp < 0,
                        ">=" => cmp >= 0,
                        "<=" => cmp <= 0,
                        _ => false
                    };
                }
                return true;
            }
        }

        return false;
    }

    private string ResolveValue(string raw, string section)
    {
        string trimmed = raw.Trim().Trim('"', '\'');
        string varKey = trimmed.StartsWith('$') ? trimmed.TrimStart('$') : trimmed;
        try
        {
            return _varEngine.Get(varKey, section);
        }
        catch
        {
            return trimmed;
        }
    }

    private static bool IsEmptyArrayOrValue(string val)
    {
        if (string.IsNullOrWhiteSpace(val)) return true;
        string trimmed = val.Trim();
        if (trimmed == "[]") return true;
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            string inner = trimmed.Substring(1, trimmed.Length - 2).Trim();
            if (string.IsNullOrEmpty(inner) || inner.All(c => c == ';' || char.IsWhiteSpace(c)))
                return true;
        }
        return false;
    }

    private static bool TryParseRange(string rangeStr, out double min, out double max)
    {
        min = 0; max = 0;
        if (string.IsNullOrWhiteSpace(rangeStr)) return false;
        string[] parts = rangeStr.Split('~');
        if (parts.Length != 2) return false;
        return double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out min) &&
               double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out max);
    }

    private class ExpressionParser
    {
        private readonly string _text;
        private readonly Func<string, bool> _resolver;
        private int _pos = 0;

        public ExpressionParser(string text, Func<string, bool> resolver)
        {
            _text = text;
            _resolver = resolver;
        }

        public bool Parse()
        {
            bool val = ParseOr();
            SkipWhitespace();
            if (_pos < _text.Length)
            {
                throw new FormatException($"語法錯誤：未預期的字符 '{_text[_pos]}' 位於第 {_pos + 1} 個字元處");
            }
            return val;
        }

        // OR (|)
        private bool ParseOr()
        {
            bool left = ParseAnd();
            while (_pos < _text.Length)
            {
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == '|')
                {
                    _pos++;
                    bool right = ParseAnd();
                    left = left || right;
                }
                else
                {
                    break;
                }
            }
            return left;
        }

        // AND (&)
        private bool ParseAnd()
        {
            bool left = ParseUnary();
            while (_pos < _text.Length)
            {
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == '&')
                {
                    _pos++;
                    bool right = ParseUnary();
                    left = left && right;
                }
                else
                {
                    break;
                }
            }
            return left;
        }

        // NOT (~ 或 !)
        private bool ParseUnary()
        {
            SkipWhitespace();
            if (_pos < _text.Length && (_text[_pos] == '~' || _text[_pos] == '!'))
            {
                _pos++;
                return !ParseUnary();
            }
            return ParsePrimary();
        }

        // 括號 () 或原子 Token
        private bool ParsePrimary()
        {
            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == '(')
            {
                _pos++;
                bool val = ParseOr();
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == ')')
                {
                    _pos++;
                }
                return val;
            }

            // 提取 token 直到遇到 &、|、) 或字尾
            int start = _pos;
            while (_pos < _text.Length && _text[_pos] != '&' && _text[_pos] != '|' && _text[_pos] != ')')
            {
                _pos++;
            }
            string token = _text.Substring(start, _pos - start).Trim();
            if (string.IsNullOrEmpty(token))
            {
                throw new FormatException("語法錯誤：運算元缺失，運算子兩側需有合法的表達式");
            }
            return _resolver(token);
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
            {
                _pos++;
            }
        }
    }
}
