using System.Text.RegularExpressions;

namespace ARCOS.Core.Common;

/// <summary>
/// 參數拆解工具 (對應 LabVIEW 的 ArgParaFind.vi)
/// 拆解逗號分隔的位置參數、Key=Value 具名參數，以及結尾的 [[Label]] 異常自癒標籤
/// </summary>
public static partial class ArgParaHelper
{
    private static readonly Regex ErrorLabelRegex = new(@"\[\[([^\]]+)\]\](.*)$", RegexOptions.Compiled);

    public static Dictionary<string, string> ParseKeyValueArgs(string paramStr, out List<string> positionalArgs)
    {
        positionalArgs = new List<string>();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(paramStr))
        {
            return result;
        }

        string workStr = paramStr.Trim();

        // 1. 檢查結尾是否有 [[Label]]ErrMsg 錯誤攔截標記
        var errorMatch = ErrorLabelRegex.Match(workStr);
        if (errorMatch.Success)
        {
            result["_ErrorLabel"] = errorMatch.Groups[1].Value.Trim();
            result["_ErrorMsg"] = errorMatch.Groups[2].Value.Trim();
            workStr = workStr.Substring(0, errorMatch.Index).TrimEnd(',', ' ', '\t');
        }

        if (string.IsNullOrWhiteSpace(workStr))
        {
            return result;
        }

        // 2. 依頂層逗號切分參數（保護 (...)、[...]、{...} 與 "..." 內之逗號，完整支援 (100,200,300,90,180,90) 立即值座標）
        var tokens = SplitTopLevelCsv(workStr);
        int posIdx = 0;

        foreach (var rawToken in tokens)
        {
            string token = rawToken.Trim();
            if (string.IsNullOrEmpty(token)) continue;

            int eqIdx = FindTopLevelEquals(token);
            if (eqIdx > 0)
            {
                string key = token.Substring(0, eqIdx).Trim();
                string val = token.Substring(eqIdx + 1).Trim();
                result[key] = val;
            }
            else
            {
                positionalArgs.Add(token);
                result[$"Arg{posIdx}"] = token;
                posIdx++;
            }
        }

        return result;
    }

    private static List<string> SplitTopLevelCsv(string input)
    {
        var items = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuote = false;
        int parenDepth = 0, bracketDepth = 0, braceDepth = 0;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (inQuote)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < input.Length) { sb.Append(input[++i]); continue; }
                if (c == '"') inQuote = false;
                continue;
            }

            if (c == '"') { inQuote = true; sb.Append(c); continue; }
            if (c == '(') { parenDepth++; sb.Append(c); continue; }
            if (c == ')' && parenDepth > 0) { parenDepth--; sb.Append(c); continue; }
            if (c == '[') { bracketDepth++; sb.Append(c); continue; }
            if (c == ']' && bracketDepth > 0) { bracketDepth--; sb.Append(c); continue; }
            if (c == '{') { braceDepth++; sb.Append(c); continue; }
            if (c == '}' && braceDepth > 0) { braceDepth--; sb.Append(c); continue; }

            if (c == ',' && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
            {
                items.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }

        if (sb.Length > 0) items.Add(sb.ToString());
        return items;
    }

    private static int FindTopLevelEquals(string token)
    {
        bool inQuote = false;
        int parenDepth = 0, bracketDepth = 0, braceDepth = 0;
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            if (inQuote)
            {
                if (c == '\\' && i + 1 < token.Length) { i++; continue; }
                if (c == '"') inQuote = false;
                continue;
            }
            if (c == '"') { inQuote = true; continue; }
            if (c == '(') { parenDepth++; continue; }
            if (c == ')' && parenDepth > 0) { parenDepth--; continue; }
            if (c == '[') { bracketDepth++; continue; }
            if (c == ']' && bracketDepth > 0) { bracketDepth--; continue; }
            if (c == '{') { braceDepth++; continue; }
            if (c == '}' && braceDepth > 0) { braceDepth--; continue; }

            if (c == '=' && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
            {
                return i;
            }
        }
        return -1;
    }
}
