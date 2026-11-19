using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ARCOS.Core.Guidance;

/// <summary>
/// 指令參數方向分類
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ParamDirection
{
    /// <summary>
    /// 輸入參數（位於 :: 必填區或 {...} 選填覆寫區）
    /// </summary>
    Input = 0,

    /// <summary>
    /// 使用者顯式指定的輸出目標（位於 -> 輸出區）
    /// </summary>
    Output = 1,

    /// <summary>
    /// 系統固定副作用（不寫入原始碼，僅於審查模式以 Inlay Hint 呈現）
    /// </summary>
    ImplicitEffect = 2
}

/// <summary>
/// 參數在人類審查與多層級顯示投影中的風險與顯露層級
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ParamDisplayTier
{
    /// <summary>
    /// 核心參數：L1 / L2 / L3 皆顯示
    /// </summary>
    Primary = 0,

    /// <summary>
    /// 輔助選填參數：L1 精簡模式可折疊為 {+N}
    /// </summary>
    Secondary = 1,

    /// <summary>
    /// 安全/高風險參數（防隱藏鐵律）：凡非預設值，於任何顯示模式皆強制展開並高亮
    /// </summary>
    Critical = 2
}

/// <summary>
/// 參數型別提示（同時驅動靜態型別驗證、MVCON 導引控制項與 REPL 動態補全）
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ParamValueType
{
    String = 0,
    Number = 1,
    Integer = 2,
    Boolean = 3,
    LogicExpr = 4,
    PortName = 5,
    PortList = 6,
    DriverName = 7,
    ThreadTarget = 8,
    ProgramTarget = 9,
    LabelName = 10,
    VariableName = 11,
    FilePath = 12,
    Enum = 13,
    MultiLineText = 14
}

/// <summary>
/// 人類閱讀與審查顯示密度（View Projection Level）
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommandDisplayLevel
{
    /// <summary>
    /// L1 流程總覽：僅顯示必填、顯式輸出與 Critical 非預設參數，其餘一般選填折疊為 {+N}
    /// </summary>
    Compact = 0,

    /// <summary>
    /// L2 審查模式（預設推薦）：展開所有非預設覆寫參數，並自動呈現系統固定副作用 Inlay Hint
    /// </summary>
    Audit = 1,

    /// <summary>
    /// L3 原始完整模式：完整顯示規範格式與副作用
    /// </summary>
    Canonical = 2
}

/// <summary>
/// 單一指令參數之標準 Schema 規格定義
/// </summary>
public sealed class ParamSpec
{
    public string Name { get; set; } = string.Empty;
    public string[] Aliases { get; set; } = Array.Empty<string>();
    public int? LegacyIndex { get; set; }
    public ParamDirection Direction { get; set; } = ParamDirection.Input;
    public bool Required { get; set; }
    public string? DefaultValue { get; set; }
    public bool AlwaysExplicit { get; set; }
    public ParamValueType ValueType { get; set; } = ParamValueType.String;
    public string[]? AllowedValues { get; set; }
    public ParamDisplayTier DisplayTier { get; set; } = ParamDisplayTier.Primary;
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 判斷給定鍵名是否符合此參數的標準名稱或任一相容別名（大小寫不敏感）
    /// </summary>
    public bool MatchesName(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (Name.Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
        if (Aliases != null)
        {
            for (int i = 0; i < Aliases.Length; i++)
            {
                if (Aliases[i].Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 判斷給定值是否等同於本參數的安全預設值
    /// </summary>
    public bool IsDefaultValue(string? value)
    {
        if (Required || DefaultValue == null) return false;
        string actual = (value ?? string.Empty).Trim();
        string def = DefaultValue.Trim();

        if (actual.Equals(def, StringComparison.OrdinalIgnoreCase)) return true;

        if (ValueType == ParamValueType.Boolean)
        {
            if (TryNormalizeBool(actual, out bool a) && TryNormalizeBool(def, out bool d))
                return a == d;
        }
        else if (ValueType == ParamValueType.Number || ValueType == ParamValueType.Integer)
        {
            if (double.TryParse(actual, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double a) &&
                double.TryParse(def, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d))
            {
                return Math.Abs(a - d) < 1e-9;
            }
        }

        return false;
    }

    internal static bool TryNormalizeBool(string raw, out bool result)
    {
        string s = raw.Trim();
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }
        if (s.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }
        result = false;
        return false;
    }
}

/// <summary>
/// 單一 CSVI 指令之完整語意、輸入、輸出與副作用 Schema（Single Source of Truth）
/// </summary>
public sealed class CommandSchema
{
    public string CommandKey { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public bool SupportsErrorTrap { get; set; }
    public bool AiAuditWarning { get; set; }
    public List<ParamSpec> Parameters { get; set; } = new();
    public List<string> ImplicitEffects { get; set; } = new();

    public ParamSpec? FindParam(string keyOrAlias)
    {
        if (string.IsNullOrWhiteSpace(keyOrAlias)) return null;
        return Parameters.FirstOrDefault(p => p.MatchesName(keyOrAlias));
    }

    public IEnumerable<ParamSpec> GetRequiredInputs() =>
        Parameters.Where(p => p.Direction == ParamDirection.Input && p.Required);

    public IEnumerable<ParamSpec> GetOptionalInputs() =>
        Parameters.Where(p => p.Direction == ParamDirection.Input && !p.Required);

    public IEnumerable<ParamSpec> GetOutputs() =>
        Parameters.Where(p => p.Direction == ParamDirection.Output);
}
