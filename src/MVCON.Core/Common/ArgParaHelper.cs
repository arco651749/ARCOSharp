using System.Text.RegularExpressions;

namespace MVCON.Core.Common;

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

        // 2. 依逗號切分參數（考慮括號或引號內的逗號可日後擴展，此處標準逗號分割）
        var tokens = workStr.Split(',', StringSplitOptions.TrimEntries);
        int posIdx = 0;

        foreach (var token in tokens)
        {
            if (string.IsNullOrEmpty(token)) continue;

            int eqIdx = token.IndexOf('=');
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
}
