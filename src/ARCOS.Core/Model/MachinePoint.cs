using System.Globalization;
using System.Text.Json.Nodes;

namespace ARCOS.Core.Model;

/// <summary>
/// 機器人/機構點位結構 (MachinePoint)
/// 包含 6 軸座標 (X, Y, Z, U, V, W) 與智慧型 H 字典 (預設支援 Radius 半徑數值，並可同時攜帶 IO 控制、機器人姿態等擴充屬性)
/// 數值為 double.PositiveInfinity (inf) 代表該軸屏蔽、保持原位不運動
/// </summary>
public class MachinePoint
{
    public double X { get; set; } = 0.0;
    public double Y { get; set; } = 0.0;
    public double Z { get; set; } = 0.0;
    public double U { get; set; } = 0.0;
    public double V { get; set; } = 0.0;
    public double W { get; set; } = 0.0;

    /// <summary>
    /// 自訂製程參數與半徑 JSON 字典 (對應 H 欄位：預設數值存入 H["Radius"]，並支援任意 Key=Value 屬性)
    /// </summary>
    public JsonObject H { get; set; } = new();

    /// <summary>
    /// 便捷讀取或設定圓半徑 (對應 H["Radius"])
    /// </summary>
    public double Radius
    {
        get
        {
            if (H.TryGetPropertyValue("Radius", out var node) && node != null)
            {
                try { return node.GetValue<double>(); } catch { }
                if (double.TryParse(node.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                    return d;
            }
            return 0.0;
        }
        set
        {
            H["Radius"] = value;
        }
    }

    public MachinePoint() { }

    public MachinePoint(double x, double y, double z = 0, double u = 0, double v = 0, double w = 0, string? rawH = null)
    {
        X = x;
        Y = y;
        Z = z;
        U = u;
        V = v;
        W = w;
        if (!string.IsNullOrWhiteSpace(rawH))
        {
            SetH(rawH);
        }
    }

    /// <summary>
    /// 智慧解析並寫入 H 內容：
    /// 1. 純數字 (如 "25.5") -> H["Radius"] = 25.5
    /// 2. JSON 物件字串 (如 "{\"Radius\":10,\"Pose\":\"Left\"}") -> 合併寫入 H
    /// 3. Key=Value 清單 (如 "SetIO=P0.0;Pose=Left;Radius=15") -> 拆解寫入 H
    /// 4. 單一文字標籤 -> H["Value"] = trimmed
    /// </summary>
    public void SetH(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        string trimmed = raw.Trim();

        // 1. 純數字 -> 直接作為半徑 Radius
        if (double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out double numVal))
        {
            H["Radius"] = numVal;
            return;
        }

        // 2. JSON Object
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            try
            {
                if (JsonNode.Parse(trimmed) is JsonObject obj)
                {
                    foreach (var kv in obj)
                    {
                        H[kv.Key] = kv.Value?.DeepClone();
                    }
                    return;
                }
            }
            catch { }
        }

        // 3. Key=Value 格式 (支援 ';' 或 ',' 分隔)
        if (trimmed.Contains('='))
        {
            var sep = trimmed.Contains(';') ? ';' : ',';
            var pairs = trimmed.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var pair in pairs)
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                {
                    string k = pair.Substring(0, eq).Trim();
                    string v = pair.Substring(eq + 1).Trim();
                    if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out double dv))
                        H[k] = dv;
                    else if (bool.TryParse(v, out bool bv))
                        H[k] = bv;
                    else
                        H[k] = v;
                }
            }
            return;
        }

        // 4. 其他單一字串標記
        H["Value"] = trimmed;
    }

    /// <summary>
    /// 取得 H 的字串表示：
    /// - 若指定 subKey (例如 "Radius" 或 "Pose")，回傳 H[subKey] 的字串值
    /// - 若未指定 subKey：若 H 僅含 "Radius"，回傳純數字字串；若含多個屬性，回傳 JSON 字串
    /// </summary>
    public string GetHString(string? subKey = null)
    {
        if (!string.IsNullOrWhiteSpace(subKey))
        {
            if (H.TryGetPropertyValue(subKey.Trim(), out var node) && node != null)
            {
                if (node is JsonValue jv && jv.TryGetValue<double>(out double d))
                    return d.ToString(CultureInfo.InvariantCulture);
                if (node is JsonValue jvs && jvs.TryGetValue<string>(out string? s) && s != null)
                    return s;
                return node.ToString();
            }
            return string.Empty;
        }

        if (H.Count == 0) return "0";
        if (H.Count == 1 && H.TryGetPropertyValue("Radius", out var rNode) && rNode != null)
        {
            if (rNode is JsonValue jv && jv.TryGetValue<double>(out double d))
                return d.ToString(CultureInfo.InvariantCulture);
            return rNode.ToString();
        }
        if (H.Count == 1 && H.TryGetPropertyValue("Value", out var vNode) && vNode != null)
        {
            if (vNode is JsonValue jv && jv.TryGetValue<string>(out string? s) && s != null)
                return s;
            return vNode.ToString();
        }

        return H.ToJsonString();
    }

    /// <summary>
    /// 從 "(X, Y, Z, U, V, W, H)" 座標字串解析出 MachinePoint
    /// </summary>
    public static MachinePoint ParseCoords(string coords)
    {
        string inner = coords.Trim();
        if (inner.StartsWith('(') && inner.EndsWith(')'))
        {
            inner = inner.Substring(1, inner.Length - 2).Trim();
        }

        // 前 6 個逗號分隔為 X, Y, Z, U, V, W，第 7 個欄位起為 H (可含 ';' 或 Key=Value)
        var tokens = inner.Split(',', 7, StringSplitOptions.TrimEntries);
        double ParseVal(int idx)
        {
            if (idx >= tokens.Length || string.IsNullOrWhiteSpace(tokens[idx])) return 0.0;
            if (tokens[idx].Equals("inf", StringComparison.OrdinalIgnoreCase)) return double.PositiveInfinity;
            return double.TryParse(tokens[idx], NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : 0.0;
        }

        var pt = new MachinePoint(ParseVal(0), ParseVal(1), ParseVal(2), ParseVal(3), ParseVal(4), ParseVal(5));
        if (tokens.Length >= 7 && !string.IsNullOrWhiteSpace(tokens[6]))
        {
            pt.SetH(tokens[6]);
        }
        return pt;
    }

    /// <summary>
    /// 複製一份獨立的點位副本
    /// </summary>
    public MachinePoint Clone()
    {
        var copy = new MachinePoint(X, Y, Z, U, V, W);
        if (H.Count > 0)
        {
            var jsonStr = H.ToJsonString();
            copy.H = JsonNode.Parse(jsonStr)?.AsObject() ?? new JsonObject();
        }
        return copy;
    }

    public static MachinePoint AllInf()
    {
        return new MachinePoint(
            double.PositiveInfinity,
            double.PositiveInfinity,
            double.PositiveInfinity,
            double.PositiveInfinity,
            double.PositiveInfinity,
            double.PositiveInfinity
        );
    }
}

public class PointArrayEntry
{
    public List<MachinePoint> Points { get; set; } = new();
    public int CurrentIndex { get; set; } = 0;

    public bool IsEmpty => Points.Count == 0;
    public int Count => Points.Count;

    public MachinePoint CurrentPoint
    {
        get
        {
            if (Points.Count == 0) return new MachinePoint();
            int idx = Math.Clamp(CurrentIndex, 0, Points.Count - 1);
            return Points[idx];
        }
    }
}
