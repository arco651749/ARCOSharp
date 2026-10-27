using System.Globalization;
using MVCON.Core.Engines;

namespace MVCON.Core.Common;

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
