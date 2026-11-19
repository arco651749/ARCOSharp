using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ARCOS.Core.Guidance;

namespace ARCOS.Core.Common;

/// <summary>
/// 單行指令參數解析結果（涵蓋必填區 ::、選填區 {...}、輸出區 ->、錯誤攔截 [[Label]]Msg 與舊版位置參數）
/// </summary>
public sealed class ParsedCommandParams
{
    public Dictionary<string, string> RequiredArgs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> OptionalArgs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> OutputArgs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> AllArgs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PositionalArgs { get; } = new();

    public string? ErrorLabel { get; set; }
    public string? ErrorMessage { get; set; }

    public bool IsLegacyFormat { get; set; }
    public bool HasOptionalBlock { get; set; }
    public bool HasOutputBlock { get; set; }
    public bool HasSyntaxError { get; set; }
    public string? SyntaxErrorMessage { get; set; }

    public bool TryGet(string key, out string value)
    {
        if (AllArgs.TryGetValue(key, out var v))
        {
            value = v;
            return true;
        }
        value = string.Empty;
        return false;
    }

    public string Get(string key, string defaultValue = "") =>
        AllArgs.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : defaultValue;

    public bool GetBool(string key, bool defaultValue = false)
    {
        if (!AllArgs.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
            return defaultValue;
        return ParamSpec.TryNormalizeBool(v, out bool parsed) ? parsed : defaultValue;
    }

    public double GetDouble(string key, double defaultValue = 0.0)
    {
        if (!AllArgs.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
            return defaultValue;
        return double.TryParse(v.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : defaultValue;
    }

    public int GetInt(string key, int defaultValue = 0)
    {
        if (!AllArgs.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
            return defaultValue;
        return int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : defaultValue;
    }

    /// <summary>
    /// 解析清單型參數值（支援 "[A;B;C]"、"A;B;C" 或 "A,B,C"）
    /// </summary>
    public List<string> GetList(string key)
    {
        if (!AllArgs.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return new List<string>();

        string s = raw.Trim();
        if (s.StartsWith('[') && s.EndsWith(']') && s.Length >= 2)
        {
            s = s.Substring(1, s.Length - 2);
        }

        char sep = s.Contains(';') ? ';' : ',';
        return s.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }
}

/// <summary>
/// ARCOS 指令語法 v2.0 單次狀態機詞法解析器（支援寬容合併與 100% 舊版位置參數向下相容）
/// 格式：&lt;RequiredArgs&gt; {&lt;OptionalOverrides&gt;} -&gt; &lt;OutputBindings&gt; [[ErrorLabel]]ErrorMsg
/// </summary>
public static class CommandParamParser
{
    private static readonly Regex IdentifierRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static ParsedCommandParams Parse(string? rawParam, CommandSchema? schema = null)
    {
        var result = new ParsedCommandParams();
        if (string.IsNullOrWhiteSpace(rawParam))
        {
            return result;
        }

        string work = rawParam.Trim();

        // 1. 擷取頂層 [[Label]]ErrorMsg（忽略引號內出現的 [[...]]）
        work = ExtractTopLevelErrorTrap(work, out string? errLabel, out string? errMsg);
        result.ErrorLabel = errLabel;
        result.ErrorMessage = errMsg;

        if (string.IsNullOrWhiteSpace(work))
        {
            return result;
        }

        // 2. 切分頂層區段：Required / {Optional} / -> Output
        if (!SplitTopLevelSections(work, out string mainRaw, out string? optionalRaw, out string? outputRaw, out string? syntaxError))
        {
            result.HasSyntaxError = true;
            result.SyntaxErrorMessage = syntaxError;
            return result;
        }

        result.HasOptionalBlock = optionalRaw != null;
        result.HasOutputBlock = outputRaw != null;

        // 3. 解析各區段內之鍵值對與位置參數
        bool anyNamedMain = ParseBindingSegment(mainRaw, result.RequiredArgs, result.PositionalArgs, result, allowPositional: true);

        if (optionalRaw != null)
        {
            var optPos = new List<string>();
            ParseBindingSegment(optionalRaw, result.OptionalArgs, optPos, result, allowPositional: false);
            if (optPos.Count > 0 && !result.HasSyntaxError)
            {
                result.HasSyntaxError = true;
                result.SyntaxErrorMessage = $"E005: Optional block '{{...}}' only accepts key=value bindings (unexpected '{optPos[0]}').";
            }
        }

        if (outputRaw != null)
        {
            var outPos = new List<string>();
            ParseBindingSegment(outputRaw, result.OutputArgs, outPos, result, allowPositional: false);
            if (outPos.Count > 0 && !result.HasSyntaxError)
            {
                result.HasSyntaxError = true;
                result.SyntaxErrorMessage = $"E005: Output block '->' only accepts key=value bindings (unexpected '{outPos[0]}').";
            }
        }

        result.IsLegacyFormat = !result.HasOptionalBlock && !result.HasOutputBlock && result.PositionalArgs.Count > 0;

        // 4. 寬容合併所有具名參數進 AllArgs（支援別名正規化）
        foreach (var kvp in result.RequiredArgs)
        {
            string normKey = NormalizeKey(kvp.Key, schema);
            result.AllArgs[normKey] = kvp.Value;
        }
        foreach (var kvp in result.OptionalArgs)
        {
            string normKey = NormalizeKey(kvp.Key, schema);
            result.AllArgs[normKey] = kvp.Value;
        }
        foreach (var kvp in result.OutputArgs)
        {
            string normKey = NormalizeKey(kvp.Key, schema);
            result.AllArgs[normKey] = kvp.Value;
        }

        // 5. 若有舊版位置參數，依 Schema 的 LegacyIndex 自動映射為具名鍵值
        if (result.PositionalArgs.Count > 0)
        {
            for (int i = 0; i < result.PositionalArgs.Count; i++)
            {
                result.AllArgs[$"Arg{i}"] = result.PositionalArgs[i];
            }

            if (schema != null)
            {
                MapLegacyPositionals(result, schema);
            }
        }

        return result;
    }

    private static void MapLegacyPositionals(ParsedCommandParams parsed, CommandSchema schema)
    {
        var indexedSpecs = schema.Parameters
            .Where(p => p.LegacyIndex.HasValue)
            .OrderBy(p => p.LegacyIndex!.Value)
            .ToList();

        if (indexedSpecs.Count == 0) return;

        // 支援清單收集型參數（如 IO@Shift 的 ios、Thread@Wait 的 threads、SubThreadWait 的 subs、ForeachLoop/Variable@Shift 的 vars、linked、patterns、wavs、ctrls）
        var variadicListSpec = indexedSpecs.LastOrDefault(p =>
            p.ValueType == ParamValueType.PortList ||
            p.Name.Equals("ios", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals("ports", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals("threads", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals("subs", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals("names", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals("vars", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals("arrays", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals("linked", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals("patterns", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals("wavs", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals("ctrls", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals("items", StringComparison.OrdinalIgnoreCase));

        foreach (var spec in indexedSpecs)
        {
            int idx = spec.LegacyIndex!.Value;
            if (idx < 0 || idx >= parsed.PositionalArgs.Count) continue;
            if (parsed.AllArgs.ContainsKey(spec.Name)) continue;

            if (variadicListSpec == spec && parsed.PositionalArgs.Count > idx + 1)
            {
                var items = parsed.PositionalArgs.Skip(idx).Where(x => !string.IsNullOrWhiteSpace(x));
                parsed.AllArgs[spec.Name] = $"[{string.Join(";", items)}]";
            }
            else
            {
                parsed.AllArgs[spec.Name] = parsed.PositionalArgs[idx];
            }
        }
    }

    private static string NormalizeKey(string key, CommandSchema? schema)
    {
        if (schema == null) return key;
        var spec = schema.FindParam(key);
        return spec != null ? spec.Name : key;
    }

    private static string ExtractTopLevelErrorTrap(string input, out string? label, out string? message)
    {
        label = null;
        message = null;
        bool inQuote = false;
        int braceDepth = 0;
        int parenDepth = 0;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (inQuote)
            {
                if (c == '\\' && i + 1 < input.Length) { i++; continue; }
                if (c == '"') inQuote = false;
                continue;
            }

            if (c == '"') { inQuote = true; continue; }
            if (c == '{') { braceDepth++; continue; }
            if (c == '}' && braceDepth > 0) { braceDepth--; continue; }
            if (c == '(') { parenDepth++; continue; }
            if (c == ')' && parenDepth > 0) { parenDepth--; continue; }

            if (braceDepth == 0 && parenDepth == 0 && c == '[' && i + 1 < input.Length && input[i + 1] == '[')
            {
                int closeIdx = input.IndexOf("]]", i + 2, StringComparison.Ordinal);
                if (closeIdx > i + 2)
                {
                    label = input.Substring(i + 2, closeIdx - (i + 2)).Trim();
                    string tail = input.Substring(closeIdx + 2).Trim();
                    message = string.IsNullOrEmpty(tail) ? null : tail;
                    return input.Substring(0, i).TrimEnd(',', ' ', '\t');
                }
            }
        }

        return input;
    }

    private static bool SplitTopLevelSections(
        string input,
        out string mainRaw,
        out string? optionalRaw,
        out string? outputRaw,
        out string? syntaxError)
    {
        mainRaw = string.Empty;
        optionalRaw = null;
        outputRaw = null;
        syntaxError = null;

        var mainSb = new StringBuilder();
        bool inQuote = false;
        int parenDepth = 0;
        int bracketDepth = 0;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];

            if (inQuote)
            {
                mainSb.Append(c);
                if (c == '\\' && i + 1 < input.Length)
                {
                    mainSb.Append(input[++i]);
                    continue;
                }
                if (c == '"') inQuote = false;
                continue;
            }

            if (c == '"')
            {
                inQuote = true;
                mainSb.Append(c);
                continue;
            }

            if (c == '(') { parenDepth++; mainSb.Append(c); continue; }
            if (c == ')' && parenDepth > 0) { parenDepth--; mainSb.Append(c); continue; }
            if (c == '[') { bracketDepth++; mainSb.Append(c); continue; }
            if (c == ']' && bracketDepth > 0) { bracketDepth--; mainSb.Append(c); continue; }

            if (parenDepth == 0 && bracketDepth == 0)
            {
                // 偵測頂層 '->' 輸出區段
                if (c == '-' && i + 1 < input.Length && input[i + 1] == '>')
                {
                    outputRaw = input.Substring(i + 2).Trim();
                    break;
                }

                // 偵測頂層 '{...}'：若緊接 '=' 後方則為 JSON/物件值，否則為選填覆寫區段
                if (c == '{')
                {
                    int closeIdx = FindMatchingBrace(input, i);
                    if (closeIdx < 0)
                    {
                        syntaxError = "E005: Unclosed '{' in command parameters.";
                        return false;
                    }

                    if (EndsWithAssignmentOperator(mainSb) || LooksLikeJsonObject(input, i, closeIdx))
                    {
                        mainSb.Append(input, i, closeIdx - i + 1);
                        i = closeIdx;
                        continue;
                    }

                    string inner = input.Substring(i + 1, closeIdx - i - 1).Trim();
                    optionalRaw = optionalRaw == null ? inner : $"{optionalRaw}, {inner}";
                    i = closeIdx;
                    continue;
                }
            }

            mainSb.Append(c);
        }

        if (inQuote)
        {
            syntaxError = "E005: Unclosed string literal '\"'.";
            return false;
        }

        mainRaw = mainSb.ToString().Trim().TrimEnd(',');
        return true;
    }

    private static bool EndsWithAssignmentOperator(StringBuilder sb)
    {
        for (int i = sb.Length - 1; i >= 0; i--)
        {
            char ch = sb[i];
            if (char.IsWhiteSpace(ch)) continue;
            return ch == '=';
        }
        return false;
    }

    private static bool LooksLikeJsonObject(string input, int openIdx, int closeIdx)
    {
        int p = openIdx + 1;
        while (p < closeIdx && char.IsWhiteSpace(input[p])) p++;
        if (p >= closeIdx) return true; // 空物件 {}
        if (input[p] != '"') return false;
        // 跳過 JSON 鍵字串，確認緊接 ':'
        p++;
        while (p < closeIdx)
        {
            if (input[p] == '\\' && p + 1 < closeIdx) { p += 2; continue; }
            if (input[p] == '"') { p++; break; }
            p++;
        }
        while (p < closeIdx && char.IsWhiteSpace(input[p])) p++;
        return p < closeIdx && input[p] == ':';
    }

    private static int FindMatchingBrace(string input, int openIdx)
    {
        bool inQuote = false;
        int depth = 0;
        for (int i = openIdx; i < input.Length; i++)
        {
            char c = input[i];
            if (inQuote)
            {
                if (c == '\\' && i + 1 < input.Length) { i++; continue; }
                if (c == '"') inQuote = false;
                continue;
            }
            if (c == '"') { inQuote = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static bool ParseBindingSegment(
        string segment,
        Dictionary<string, string> targetNamed,
        List<string> targetPositional,
        ParsedCommandParams result,
        bool allowPositional)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;

        bool anyNamed = false;
        var tokens = SplitTopLevelComma(segment, out bool quoteError);
        if (quoteError && !result.HasSyntaxError)
        {
            result.HasSyntaxError = true;
            result.SyntaxErrorMessage = "E005: Unclosed string literal '\"' inside parameters.";
            return false;
        }

        foreach (var rawToken in tokens)
        {
            string tok = rawToken.Trim();
            if (string.IsNullOrEmpty(tok)) continue;

            int eqIdx = FindTopLevelAssignment(tok);
            if (eqIdx > 0)
            {
                string key = tok.Substring(0, eqIdx).Trim();
                string valRaw = tok.Substring(eqIdx + 1).Trim();
                if (IdentifierRegex.IsMatch(key))
                {
                    if (!TryUnquoteValue(valRaw, out string unquoted, out string? err))
                    {
                        if (!result.HasSyntaxError)
                        {
                            result.HasSyntaxError = true;
                            result.SyntaxErrorMessage = err;
                        }
                        continue;
                    }
                    targetNamed[key] = unquoted;
                    anyNamed = true;
                    continue;
                }
            }

            if (TryUnquoteValue(tok, out string posVal, out string? posErr))
            {
                targetPositional.Add(posVal);
            }
            else if (!result.HasSyntaxError)
            {
                result.HasSyntaxError = true;
                result.SyntaxErrorMessage = posErr;
            }
        }

        return anyNamed;
    }

    private static List<string> SplitTopLevelComma(string segment, out bool unclosedQuote)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        bool inQuote = false;
        int parenDepth = 0;
        int bracketDepth = 0;
        int braceDepth = 0;

        for (int i = 0; i < segment.Length; i++)
        {
            char c = segment[i];
            if (inQuote)
            {
                current.Append(c);
                if (c == '\\' && i + 1 < segment.Length)
                {
                    current.Append(segment[++i]);
                    continue;
                }
                if (c == '"') inQuote = false;
                continue;
            }

            if (c == '"') { inQuote = true; current.Append(c); continue; }
            if (c == '(') { parenDepth++; current.Append(c); continue; }
            if (c == ')' && parenDepth > 0) { parenDepth--; current.Append(c); continue; }
            if (c == '[') { bracketDepth++; current.Append(c); continue; }
            if (c == ']' && bracketDepth > 0) { bracketDepth--; current.Append(c); continue; }
            if (c == '{') { braceDepth++; current.Append(c); continue; }
            if (c == '}' && braceDepth > 0) { braceDepth--; current.Append(c); continue; }

            if (c == ',' && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
            {
                items.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            items.Add(current.ToString());
        }

        unclosedQuote = inQuote;
        return items;
    }

    /// <summary>
    /// 尋找頂層單一賦值 '='（排除比較運算子 ==、!=、&lt;=、&gt;= 與引號/括號內部）
    /// </summary>
    private static int FindTopLevelAssignment(string token)
    {
        bool inQuote = false;
        int parenDepth = 0;
        int bracketDepth = 0;

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

            if (c == '=' && parenDepth == 0 && bracketDepth == 0)
            {
                char prev = i > 0 ? token[i - 1] : '\0';
                char next = i + 1 < token.Length ? token[i + 1] : '\0';
                if (next == '=' || prev == '!' || prev == '<' || prev == '>')
                {
                    if (next == '=') i++;
                    continue;
                }
                return i;
            }
        }
        return -1;
    }

    private static bool TryUnquoteValue(string raw, out string value, out string? error)
    {
        error = null;
        string s = raw.Trim();
        if (s.Length == 0)
        {
            value = string.Empty;
            return true;
        }

        if (s[0] != '"')
        {
            value = s;
            return true;
        }

        if (s.Length < 2 || s[^1] != '"')
        {
            value = s;
            error = $"E005: Unclosed quoted string '{s}'.";
            return false;
        }

        var sb = new StringBuilder(s.Length - 2);
        for (int i = 1; i < s.Length - 1; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length - 1)
            {
                char next = s[++i];
                switch (next)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    default:
                        sb.Append('\\');
                        sb.Append(next);
                        break;
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        value = sb.ToString();
        return true;
    }
}

/// <summary>
/// 診斷嚴重等級
/// </summary>
public enum DiagnosticSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

/// <summary>
/// 單筆指令靜態驗證診斷
/// </summary>
public sealed class CommandDiagnostic
{
    public string Code { get; init; } = string.Empty;
    public DiagnosticSeverity Severity { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? ParameterName { get; init; }
}

/// <summary>
/// 指令靜態驗證結果
/// </summary>
public sealed class CommandValidationResult
{
    public List<CommandDiagnostic> Diagnostics { get; } = new();
    public bool IsValid => Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
    public bool HasWarnings => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Warning);
}

/// <summary>
/// ARCOS 指令語法 v2.0 靜態驗證器（E002~E005, W001~W002）
/// </summary>
public static class CommandValidator
{
    public static CommandValidationResult Validate(string rawParam, CommandSchema schema)
    {
        var vr = new CommandValidationResult();
        var parsed = CommandParamParser.Parse(rawParam, schema);

        // E005: 詞法 / 括號 / 引號錯誤
        if (parsed.HasSyntaxError)
        {
            vr.Diagnostics.Add(new CommandDiagnostic
            {
                Code = "E005",
                Severity = DiagnosticSeverity.Error,
                Message = parsed.SyntaxErrorMessage ?? "Syntax error in command parameters."
            });
            return vr;
        }

        // W002: AI 審查警戒指令（如 Jump / RunNextCMD / SkipNextCMD）
        if (schema.AiAuditWarning)
        {
            vr.Diagnostics.Add(new CommandDiagnostic
            {
                Code = "W002",
                Severity = DiagnosticSeverity.Warning,
                Message = $"Command '{schema.CommandKey}' is flagged for human control-flow audit (prefer structured If/Foreach/Call)."
            });
        }

        // E002: 檢查所有必填輸入參數是否齊全
        foreach (var req in schema.GetRequiredInputs())
        {
            if (!parsed.AllArgs.TryGetValue(req.Name, out var val) || string.IsNullOrWhiteSpace(val))
            {
                vr.Diagnostics.Add(new CommandDiagnostic
                {
                    Code = "E002",
                    Severity = DiagnosticSeverity.Error,
                    ParameterName = req.Name,
                    Message = $"Missing required parameter '{req.Name}' for '{schema.CommandKey}'."
                });
            }
        }

        // E003 & W001: 檢查未知具名參數與區段歸位
        CheckSectionBindings(parsed.RequiredArgs, isOptionalSection: false, isOutputSection: false, schema, vr);
        CheckSectionBindings(parsed.OptionalArgs, isOptionalSection: true, isOutputSection: false, schema, vr);
        CheckSectionBindings(parsed.OutputArgs, isOptionalSection: false, isOutputSection: true, schema, vr);

        // E004: 靜態型別檢查（跳過含 $ 動態變數插值之值）
        foreach (var spec in schema.Parameters)
        {
            if (!parsed.AllArgs.TryGetValue(spec.Name, out var val) || string.IsNullOrWhiteSpace(val))
                continue;

            if (val.Contains('$')) continue;

            if (!IsValueCompatibleWithType(val, spec, out string? reason))
            {
                vr.Diagnostics.Add(new CommandDiagnostic
                {
                    Code = "E004",
                    Severity = DiagnosticSeverity.Error,
                    ParameterName = spec.Name,
                    Message = $"Parameter '{spec.Name}' value '{val}' is invalid for type {spec.ValueType}: {reason}"
                });
            }
        }

        return vr;
    }

    private static void CheckSectionBindings(
        Dictionary<string, string> bindings,
        bool isOptionalSection,
        bool isOutputSection,
        CommandSchema schema,
        CommandValidationResult vr)
    {
        foreach (var kvp in bindings)
        {
            var spec = schema.FindParam(kvp.Key);
            if (spec == null)
            {
                vr.Diagnostics.Add(new CommandDiagnostic
                {
                    Code = "E003",
                    Severity = DiagnosticSeverity.Error,
                    ParameterName = kvp.Key,
                    Message = $"Unknown parameter '{kvp.Key}' for '{schema.CommandKey}'."
                });
                continue;
            }

            if (isOutputSection && spec.Direction != ParamDirection.Output)
            {
                vr.Diagnostics.Add(new CommandDiagnostic
                {
                    Code = "W001",
                    Severity = DiagnosticSeverity.Warning,
                    ParameterName = spec.Name,
                    Message = $"Parameter '{spec.Name}' is an Input parameter but was placed in '->' output block."
                });
            }
            else if (!isOutputSection && spec.Direction == ParamDirection.Output)
            {
                vr.Diagnostics.Add(new CommandDiagnostic
                {
                    Code = "W001",
                    Severity = DiagnosticSeverity.Warning,
                    ParameterName = spec.Name,
                    Message = $"Parameter '{spec.Name}' is an Output parameter and should be placed after '->'."
                });
            }
            else if (isOptionalSection && spec.Required)
            {
                vr.Diagnostics.Add(new CommandDiagnostic
                {
                    Code = "W001",
                    Severity = DiagnosticSeverity.Warning,
                    ParameterName = spec.Name,
                    Message = $"Required parameter '{spec.Name}' was placed inside '{{...}}' optional block (auto-formattable)."
                });
            }
            else if (!isOptionalSection && !isOutputSection && !spec.Required)
            {
                vr.Diagnostics.Add(new CommandDiagnostic
                {
                    Code = "W001",
                    Severity = DiagnosticSeverity.Warning,
                    ParameterName = spec.Name,
                    Message = $"Optional parameter '{spec.Name}' was placed outside '{{...}}' block (auto-formattable)."
                });
            }
        }
    }

    private static bool IsValueCompatibleWithType(string val, ParamSpec spec, out string? reason)
    {
        reason = null;
        string s = val.Trim();
        switch (spec.ValueType)
        {
            case ParamValueType.Number:
                if (!double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                {
                    reason = "expected numeric value";
                    return false;
                }
                return true;
            case ParamValueType.Integer:
                if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    reason = "expected integer value";
                    return false;
                }
                return true;
            case ParamValueType.Boolean:
                if (!ParamSpec.TryNormalizeBool(s, out _))
                {
                    reason = "expected boolean (true/false/1/0/on/off)";
                    return false;
                }
                return true;
            case ParamValueType.Enum:
                if (spec.AllowedValues != null && spec.AllowedValues.Length > 0 &&
                    !spec.AllowedValues.Any(a => a.Equals(s, StringComparison.OrdinalIgnoreCase)))
                {
                    reason = $"must be one of [{string.Join(", ", spec.AllowedValues)}]";
                    return false;
                }
                return true;
            default:
                return true;
        }
    }
}

/// <summary>
/// Schema 驅動自動格式化器（統一歸位 :: 必填 / {...} 非預設選填 / -> 輸出 / [[Label]]Msg）
/// </summary>
public static class CommandFormatter
{
    public static string FormatValue(string? value) => FormatValue(value, null);

    public static string FormatValue(string? value, ParamSpec? spec)
    {
        if (value == null) return "\"\"";
        if (value.Length == 0) return "\"\"";

        bool isExpr = spec != null &&
                      (spec.ValueType == ParamValueType.LogicExpr ||
                       spec.Name.Equals("expr", StringComparison.OrdinalIgnoreCase));

        // 平衡的單行 '{...}' 物件/JSON 值（緊接 key= 後方剖析器會完整保留，免加外層雙引號與 \" 跳脫）
        if (IsBalancedSingleLineBraceObject(value))
        {
            return value;
        }

        bool needsQuote = false;
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            needsQuote = true;
        }
        else if (value.Contains("->", StringComparison.Ordinal) || value.Contains("[[", StringComparison.Ordinal))
        {
            needsQuote = true;
        }
        else
        {
            int parenDepth = 0;
            foreach (char c in value)
            {
                if (c == '{' || c == '}' || c == '"' || c == '\n' || c == '\r' || c == '\t')
                {
                    needsQuote = true;
                    break;
                }
                if (c == '(')
                {
                    parenDepth++;
                    continue;
                }
                if (c == ')')
                {
                    if (parenDepth == 0)
                    {
                        needsQuote = true;
                        break;
                    }
                    parenDepth--;
                    continue;
                }
                if (c == ',' && parenDepth == 0)
                {
                    needsQuote = true;
                    break;
                }
                if (!isExpr && parenDepth == 0 && (c == '=' || c == ' '))
                {
                    needsQuote = true;
                    break;
                }
            }
            if (parenDepth != 0)
            {
                needsQuote = true;
            }
        }

        if (!needsQuote) return value;

        var sb = new StringBuilder(value.Length + 4);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static bool IsBalancedSingleLineBraceObject(string value)
    {
        if (value.Length < 2 || value[0] != '{' || value[^1] != '}') return false;
        bool inQuote = false;
        int depth = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\n' || c == '\r' || c == '\t') return false;
            if (inQuote)
            {
                if (c == '\\' && i + 1 < value.Length) { i++; continue; }
                if (c == '"') inQuote = false;
                continue;
            }
            if (c == '"') { inQuote = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth < 0) return false;
                if (depth == 0 && i != value.Length - 1) return false;
            }
        }
        return !inQuote && depth == 0;
    }

    /// <summary>
    /// 依 Schema 將已解析之參數物件格式化為標準 v2.0 參數字串（含舊版升級）
    /// </summary>
    public static string FormatParameter(ParsedCommandParams parsed, CommandSchema? schema = null, bool includeDefaults = false)
    {
        var reqPairs = new List<string>();
        var optPairs = new List<string>();
        var outPairs = new List<string>();

        if (schema != null)
        {
            var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var spec in schema.GetRequiredInputs())
            {
                if (parsed.AllArgs.TryGetValue(spec.Name, out var val))
                {
                    reqPairs.Add($"{spec.Name}={FormatValue(val, spec)}");
                    consumed.Add(spec.Name);
                }
            }

            foreach (var spec in schema.GetOptionalInputs())
            {
                bool hasExplicit = parsed.AllArgs.TryGetValue(spec.Name, out var val);
                if (hasExplicit)
                {
                    consumed.Add(spec.Name);
                    if (includeDefaults || spec.AlwaysExplicit || !spec.IsDefaultValue(val))
                    {
                        optPairs.Add($"{spec.Name}={FormatValue(val, spec)}");
                    }
                }
                else if (includeDefaults && spec.DefaultValue != null)
                {
                    optPairs.Add($"{spec.Name}={FormatValue(spec.DefaultValue, spec)}");
                }
            }

            foreach (var spec in schema.GetOutputs())
            {
                if (parsed.AllArgs.TryGetValue(spec.Name, out var val) && !string.IsNullOrWhiteSpace(val))
                {
                    outPairs.Add($"{spec.Name}={FormatValue(val, spec)}");
                    consumed.Add(spec.Name);
                }
            }

            // 保留未知的額外具名參數（不靜默遺失使用者資料），排除自動生成的 Arg0/Arg1
            foreach (var kvp in parsed.AllArgs)
            {
                if (consumed.Contains(kvp.Key)) continue;
                if (kvp.Key.StartsWith("Arg", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(kvp.Key.Substring(3), out _))
                    continue;

                optPairs.Add($"{kvp.Key}={FormatValue(kvp.Value)}");
            }
        }
        else
        {
            foreach (var kvp in parsed.RequiredArgs)
                reqPairs.Add($"{kvp.Key}={FormatValue(kvp.Value)}");
            foreach (var kvp in parsed.OptionalArgs)
                optPairs.Add($"{kvp.Key}={FormatValue(kvp.Value)}");
            foreach (var kvp in parsed.OutputArgs)
                outPairs.Add($"{kvp.Key}={FormatValue(kvp.Value)}");
        }

        return AssembleSections(reqPairs, optPairs, outPairs, parsed.ErrorLabel, parsed.ErrorMessage, multiLineLayout: false);
    }

    public static string FormatParameter(string rawParam, CommandSchema? schema = null, bool includeDefaults = false)
    {
        var parsed = CommandParamParser.Parse(rawParam, schema);
        if (parsed.HasSyntaxError) return rawParam;
        return FormatParameter(parsed, schema, includeDefaults);
    }

    /// <summary>
    /// 格式化完整單行指令（自動決定是否需要 :: 分隔符）
    /// </summary>
    public static string FormatLine(string command, string rawParam, CommandSchema? schema = null, bool includeDefaults = false)
    {
        string formattedParam = FormatParameter(rawParam, schema, includeDefaults);
        if (string.IsNullOrWhiteSpace(formattedParam)) return command;

        // 若無必填區（直接以 {、-> 或 [[ 開頭），省略 :: 以維持極簡可讀
        if (formattedParam.StartsWith('{') ||
            formattedParam.StartsWith("->", StringComparison.Ordinal) ||
            formattedParam.StartsWith("[[", StringComparison.Ordinal))
        {
            return $"{command} {formattedParam}";
        }

        return $"{command} :: {formattedParam}";
    }

    internal static string AssembleSections(
        IReadOnlyList<string> reqPairs,
        IReadOnlyList<string> optPairs,
        IReadOnlyList<string> outPairs,
        string? errLabel,
        string? errMsg,
        bool multiLineLayout,
        string? inlayHint = null)
    {
        var parts = new List<string>();
        if (reqPairs.Count > 0)
        {
            parts.Add(string.Join(", ", reqPairs));
        }
        if (optPairs.Count > 0)
        {
            parts.Add("{" + string.Join(", ", optPairs) + "}");
        }
        if (outPairs.Count > 0)
        {
            parts.Add("-> " + string.Join(", ", outPairs));
        }
        if (!string.IsNullOrWhiteSpace(errLabel))
        {
            parts.Add(string.IsNullOrWhiteSpace(errMsg) ? $"[[{errLabel}]]" : $"[[{errLabel}]]{errMsg}");
        }
        if (!string.IsNullOrWhiteSpace(inlayHint))
        {
            parts.Add(inlayHint);
        }

        return multiLineLayout ? string.Join("\n", parts) : string.Join(" ", parts);
    }
}

/// <summary>
/// 多層級顯示投影器（L1 Compact / L2 Audit / L3 Canonical）
/// 嚴格落實「防隱藏安全鐵律」：Critical 非預設選填參數於任何顯示模式皆強制展開標示。
/// </summary>
public static class CommandDisplayProjector
{
    public static string ProjectParameter(
        string rawParam,
        CommandSchema? schema,
        CommandDisplayLevel level = CommandDisplayLevel.Audit,
        bool multiLineLayout = false)
    {
        if (string.IsNullOrWhiteSpace(rawParam) && schema == null)
            return string.Empty;

        var parsed = CommandParamParser.Parse(rawParam, schema);
        if (parsed.HasSyntaxError || schema == null)
            return rawParam ?? string.Empty;

        if (level == CommandDisplayLevel.Canonical)
        {
            string effects = BuildInlayEffects(schema, parsed);
            var reqAll = new List<string>();
            var optAll = new List<string>();
            var outAll = new List<string>();

            foreach (var spec in schema.GetRequiredInputs())
            {
                if (parsed.AllArgs.TryGetValue(spec.Name, out var v))
                    reqAll.Add($"{spec.Name}={FormatDisplayValue(v, spec, multiLineLayout)}");
            }
            foreach (var spec in schema.GetOptionalInputs())
            {
                if (parsed.AllArgs.TryGetValue(spec.Name, out var v))
                    optAll.Add($"{spec.Name}={FormatDisplayValue(v, spec, multiLineLayout)}");
                else if (spec.DefaultValue != null)
                    optAll.Add($"{spec.Name}={FormatDisplayValue(spec.DefaultValue, spec, multiLineLayout)}");
            }
            foreach (var spec in schema.GetOutputs())
            {
                if (parsed.AllArgs.TryGetValue(spec.Name, out var v) && !string.IsNullOrWhiteSpace(v))
                    outAll.Add($"{spec.Name}={FormatDisplayValue(v, spec, multiLineLayout)}");
            }

            return CommandFormatter.AssembleSections(reqAll, optAll, outAll, parsed.ErrorLabel, parsed.ErrorMessage, multiLineLayout, effects);
        }

        var reqPairs = new List<string>();
        var optPairs = new List<string>();
        var outPairs = new List<string>();

        foreach (var spec in schema.GetRequiredInputs())
        {
            if (parsed.AllArgs.TryGetValue(spec.Name, out var val))
            {
                string displayVal = FormatDisplayValue(val, spec, multiLineLayout);
                reqPairs.Add($"{spec.Name}={displayVal}");
            }
        }

        int foldedOptionalCount = 0;
        foreach (var spec in schema.GetOptionalInputs())
        {
            if (!parsed.AllArgs.TryGetValue(spec.Name, out var val)) continue;
            bool isDef = spec.IsDefaultValue(val);
            if (isDef && !spec.AlwaysExplicit) continue;

            bool isCritical = spec.DisplayTier == ParamDisplayTier.Critical;
            if (level == CommandDisplayLevel.Compact && !isCritical)
            {
                // L1 精簡模式：一般非 Critical 選填參數折疊計數
                foldedOptionalCount++;
            }
            else
            {
                // 防隱藏安全鐵律：Critical 非預設參數於 L1/L2 均強制展開並加上 ⚠ 標示
                string keyLabel = isCritical ? $"⚠{spec.Name}" : spec.Name;
                string displayVal = FormatDisplayValue(val, spec, multiLineLayout);
                optPairs.Add($"{keyLabel}={displayVal}");
            }
        }

        if (foldedOptionalCount > 0)
        {
            optPairs.Add($"+{foldedOptionalCount}");
        }

        foreach (var spec in schema.GetOutputs())
        {
            if (parsed.AllArgs.TryGetValue(spec.Name, out var val) && !string.IsNullOrWhiteSpace(val))
            {
                outPairs.Add($"{spec.Name}={FormatDisplayValue(val, spec, multiLineLayout)}");
            }
        }

        string? inlayHint = level == CommandDisplayLevel.Audit ? BuildInlayEffects(schema, parsed) : null;

        return CommandFormatter.AssembleSections(reqPairs, optPairs, outPairs, parsed.ErrorLabel, parsed.ErrorMessage, multiLineLayout, inlayHint);
    }

    private static string FormatDisplayValue(string value, ParamSpec? spec, bool multiLineLayout)
    {
        if (value.Contains('\n') || value.Contains('\r'))
        {
            var lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (multiLineLayout)
            {
                // 多行檢視 / Tooltip：直接展開為真正換行且無 \n 與 \" 噪音的縮排程式碼區塊
                var sb = new StringBuilder();
                foreach (var ln in lines)
                {
                    sb.Append("\n  │ ").Append(ln);
                }
                return sb.ToString();
            }

            // 單行檢視：折疊為首行摘要 + 總行數徽章，避免橫向爆表
            string first = lines[0].Trim();
            if (first.Length > 40) first = first.Substring(0, 40) + "…";
            return $"⟦{first} … ({lines.Length} lines)⟧";
        }

        return CommandFormatter.FormatValue(value, spec);
    }

    private static string BuildInlayEffects(CommandSchema schema, ParsedCommandParams parsed)
    {
        if (schema.ImplicitEffects == null || schema.ImplicitEffects.Count == 0)
            return string.Empty;

        var resolved = new List<string>(schema.ImplicitEffects.Count);
        foreach (var eff in schema.ImplicitEffects)
        {
            string text = eff;
            foreach (var kvp in parsed.AllArgs)
            {
                text = text.Replace($"$({kvp.Key})", kvp.Value, StringComparison.OrdinalIgnoreCase);
            }
            resolved.Add(text);
        }

        return $"[effect: {string.Join("; ", resolved)}]";
    }
}
