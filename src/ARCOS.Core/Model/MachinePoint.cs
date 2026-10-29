using System.Text.Json.Nodes;

namespace ARCOS.Core.Model;

/// <summary>
/// 機器人/機構點位結構 (MachinePoint)
/// 包含 6 軸座標 (X, Y, Z, U, V, W) 與 JSON H 字典 (製程工藝參數/夾爪狀態等)
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
    /// 自訂製程參數 JSON 字典 (對應 LabVIEW 的 H 字典)
    /// </summary>
    public JsonObject H { get; set; } = new();

    public MachinePoint() { }

    public MachinePoint(double x, double y, double z = 0, double u = 0, double v = 0, double w = 0)
    {
        X = x;
        Y = y;
        Z = z;
        U = u;
        V = v;
        W = w;
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
