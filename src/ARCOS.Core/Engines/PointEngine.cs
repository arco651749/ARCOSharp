using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ARCOS.Core.Model;

namespace ARCOS.Core.Engines;

/// <summary>
/// 全域點位陣列庫引擎 (PointEngine)
/// 管理所有機構與料盤點位 (MachinePoint)，支援多軸 inf 屏蔽、料盤游標推進、
/// 幾何路徑生成，並自動連動 IoEngine 的 $(PointName)#Last? 虛擬點位
/// </summary>
public class PointEngine
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, PointArrayEntry> _points = new(StringComparer.OrdinalIgnoreCase);
    private readonly IoEngine? _ioEngine;

    private static readonly Regex PointIndexRegex = new(@"^([a-zA-Z0-9_]+)\[(\d+)\]$", RegexOptions.Compiled);
    private static readonly Regex PointFilterRegex = new(@"^([a-zA-Z0-9_]+)@([a-zA-Z]+)(?:\(([^)]+)\))?$", RegexOptions.Compiled);

    public PointEngine(IoEngine? ioEngine = null)
    {
        _ioEngine = ioEngine;
    }

    /// <summary>
    /// 解析點位表達式：支援 "P1"、"Tray[3]"、"P1@XY"、"P1@Z(50)"
    /// </summary>
    public MachinePoint GetPoint(string pointExpr)
    {
        if (string.IsNullOrWhiteSpace(pointExpr)) return new MachinePoint();
        string expr = pointExpr.Trim();

        // 1. 檢查軸過濾或特定軸覆寫語法：P1@XY 或 P1@Z(50)
        var filterMatch = PointFilterRegex.Match(expr);
        if (filterMatch.Success)
        {
            string baseName = filterMatch.Groups[1].Value;
            string axes = filterMatch.Groups[2].Value.ToUpperInvariant();
            string? overrideValStr = filterMatch.Groups[3].Success ? filterMatch.Groups[3].Value : null;

            var basePt = GetPoint(baseName).Clone();
            var result = MachinePoint.AllInf();

            // 若有覆寫數值，例如 P1@Z(50)
            if (!string.IsNullOrEmpty(overrideValStr) && double.TryParse(overrideValStr, out double overrideVal))
            {
                if (axes.Contains('X')) result.X = overrideVal;
                if (axes.Contains('Y')) result.Y = overrideVal;
                if (axes.Contains('Z')) result.Z = overrideVal;
                if (axes.Contains('U')) result.U = overrideVal;
                if (axes.Contains('V')) result.V = overrideVal;
                if (axes.Contains('W')) result.W = overrideVal;
            }
            else
            {
                // 保留指定軸向，其餘設為 inf
                if (axes.Contains('X')) result.X = basePt.X;
                if (axes.Contains('Y')) result.Y = basePt.Y;
                if (axes.Contains('Z')) result.Z = basePt.Z;
                if (axes.Contains('U')) result.U = basePt.U;
                if (axes.Contains('V')) result.V = basePt.V;
                if (axes.Contains('W')) result.W = basePt.W;
            }
            result.H = basePt.H;
            return result;
        }

        // 2. 檢查指定索引語法：Tray[3]
        var indexMatch = PointIndexRegex.Match(expr);
        if (indexMatch.Success)
        {
            string name = indexMatch.Groups[1].Value;
            int idx = int.Parse(indexMatch.Groups[2].Value);
            if (_points.TryGetValue(name, out var entry) && entry.Points.Count > 0)
            {
                int clamped = Math.Clamp(idx, 0, entry.Points.Count - 1);
                return entry.Points[clamped].Clone();
            }
            throw new KeyNotFoundException($"Point array '{name}' was not found.");
        }

        // 3. 一般名稱：取出目前游標指示之點位
        if (_points.TryGetValue(expr, out var ptEntry))
        {
            return ptEntry.CurrentPoint.Clone();
        }

        throw new KeyNotFoundException($"Point '{pointExpr}' was not found.");
    }

    public IReadOnlyList<MachinePoint> GetPointArray(string name)
    {
        if (_points.TryGetValue(name, out var entry))
        {
            return entry.Points.ToList();
        }
        return Array.Empty<MachinePoint>();
    }

    /// <summary>
    /// 覆寫為長度 1 的單點陣列，游標歸 0 (對應 Calculate::$P=(...))
    /// </summary>
    public void SetPoint(string name, MachinePoint point)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_lock)
        {
            var entry = _points.GetOrAdd(name, _ => new PointArrayEntry());
            entry.Points.Clear();
            entry.Points.Add(point.Clone());
            entry.CurrentIndex = 0;
            UpdateIoLastFlag(name, true);
        }
    }

    /// <summary>
    /// 將點位壓入陣列尾端 (對應 Calculate::$P<=(...))
    /// </summary>
    public void AppendPoint(string name, MachinePoint point)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_lock)
        {
            var entry = _points.GetOrAdd(name, _ => new PointArrayEntry());
            entry.Points.Add(point.Clone());
            CheckPalletLast(name);
        }
    }

    /// <summary>
    /// 取出目前游標點位並將游標推進，抵達末點時將 $(name)#Last? 設為 true (供 GoPallet 使用)
    /// </summary>
    public MachinePoint GetAndMoveNextPallet(string name)
    {
        lock (_lock)
        {
            if (!_points.TryGetValue(name, out var entry) || entry.IsEmpty)
            {
                throw new KeyNotFoundException($"Pallet point array '{name}' not found or empty.");
            }

            var pt = entry.CurrentPoint.Clone();

            if (entry.CurrentIndex < entry.Points.Count - 1)
            {
                entry.CurrentIndex++;
                UpdateIoLastFlag(name, entry.CurrentIndex >= entry.Points.Count - 1);
            }
            else
            {
                UpdateIoLastFlag(name, true);
            }

            return pt;
        }
    }

    /// <summary>
    /// 重設料盤游標 (對應 ResetPalletIndex)
    /// </summary>
    public void ResetPalletIndex(string name, int index = 0)
    {
        lock (_lock)
        {
            if (_points.TryGetValue(name, out var entry))
            {
                entry.CurrentIndex = Math.Clamp(index, 0, Math.Max(0, entry.Points.Count - 1));
                UpdateIoLastFlag(name, entry.CurrentIndex >= entry.Points.Count - 1);
            }
        }
    }

    /// <summary>
    /// 檢查是否在最後一點並同步 $(name)#Last? (對應 CheckPalletLast?)
    /// </summary>
    public bool CheckPalletLast(string name)
    {
        if (_points.TryGetValue(name, out var entry))
        {
            bool isLast = entry.Points.Count == 0 || entry.CurrentIndex >= entry.Points.Count - 1;
            UpdateIoLastFlag(name, isLast);
            return isLast;
        }
        return true;
    }

    /// <summary>
    /// 保留指定軸向，其餘軸設為 inf (對應 EnableAxis)
    /// </summary>
    public void EnableAxis(string name, string enabledAxes)
    {
        lock (_lock)
        {
            if (_points.TryGetValue(name, out var entry))
            {
                string axes = enabledAxes.ToUpperInvariant();
                foreach (var pt in entry.Points)
                {
                    if (!axes.Contains('X')) pt.X = double.PositiveInfinity;
                    if (!axes.Contains('Y')) pt.Y = double.PositiveInfinity;
                    if (!axes.Contains('Z')) pt.Z = double.PositiveInfinity;
                    if (!axes.Contains('U')) pt.U = double.PositiveInfinity;
                    if (!axes.Contains('V')) pt.V = double.PositiveInfinity;
                    if (!axes.Contains('W')) pt.W = double.PositiveInfinity;
                }
            }
        }
    }

    /// <summary>
    /// 依 [0]起點、[1]終點等距產生 1D 點位陣列 (對應 Pallet1D)
    /// </summary>
    public void GeneratePallet1D(string name, int totalCount)
    {
        if (totalCount <= 1) return;

        lock (_lock)
        {
            if (!_points.TryGetValue(name, out var entry) || entry.Points.Count < 2)
            {
                throw new InvalidOperationException($"Pallet1D requires point array '{name}' with at least [0] Start and [1] End points.");
            }

            var pStart = entry.Points[0];
            var pEnd = entry.Points[1];
            var list = new List<MachinePoint>(totalCount);

            for (int i = 0; i < totalCount; i++)
            {
                double ratio = (double)i / (totalCount - 1);
                var pt = new MachinePoint(
                    Interpolate(pStart.X, pEnd.X, ratio),
                    Interpolate(pStart.Y, pEnd.Y, ratio),
                    Interpolate(pStart.Z, pEnd.Z, ratio),
                    Interpolate(pStart.U, pEnd.U, ratio),
                    Interpolate(pStart.V, pEnd.V, ratio),
                    Interpolate(pStart.W, pEnd.W, ratio)
                );
                list.Add(pt);
            }

            entry.Points.Clear();
            entry.Points.AddRange(list);
            entry.CurrentIndex = 0;
            UpdateIoLastFlag(name, false);
        }
    }

    /// <summary>
    /// 依 [0]原點、[1]X終點、[2]Y終點產生 2D 矩陣陣列 (對應 Pallet2D)
    /// </summary>
    public void GeneratePallet2D(string name, int countX, int countY)
    {
        if (countX <= 1 || countY <= 1) return;

        lock (_lock)
        {
            if (!_points.TryGetValue(name, out var entry) || entry.Points.Count < 3)
            {
                throw new InvalidOperationException($"Pallet2D requires point array '{name}' with [0] Origin, [1] X-End, and [2] Y-End.");
            }

            var pOrigin = entry.Points[0];
            var pXEnd = entry.Points[1];
            var pYEnd = entry.Points[2];

            double deltaXx = (pXEnd.X - pOrigin.X) / (countX - 1);
            double deltaXy = (pXEnd.Y - pOrigin.Y) / (countX - 1);
            double deltaYx = (pYEnd.X - pOrigin.X) / (countY - 1);
            double deltaYy = (pYEnd.Y - pOrigin.Y) / (countY - 1);

            var list = new List<MachinePoint>(countX * countY);

            for (int r = 0; r < countY; r++)
            {
                for (int c = 0; c < countX; c++)
                {
                    var pt = new MachinePoint(
                        pOrigin.X + c * deltaXx + r * deltaYx,
                        pOrigin.Y + c * deltaXy + r * deltaYy,
                        pOrigin.Z,
                        pOrigin.U,
                        pOrigin.V,
                        pOrigin.W
                    );
                    list.Add(pt);
                }
            }

            entry.Points.Clear();
            entry.Points.AddRange(list);
            entry.CurrentIndex = 0;
            UpdateIoLastFlag(name, false);
        }
    }

    /// <summary>
    /// 依 [0]圓心、[1]半徑參考點產生圓弧分割路徑 (對應 Circle2DPath)
    /// </summary>
    public void GenerateCircle2DPath(string name, double startDeg, double endDeg, int segments)
    {
        if (segments <= 1) return;

        lock (_lock)
        {
            if (!_points.TryGetValue(name, out var entry) || entry.Points.Count < 2)
            {
                throw new InvalidOperationException($"Circle2DPath requires point array '{name}' with [0] Center and [1] Radius point.");
            }

            var center = entry.Points[0];
            var radiusPt = entry.Points[1];
            double radius = Math.Sqrt(Math.Pow(radiusPt.X - center.X, 2) + Math.Pow(radiusPt.Y - center.Y, 2));

            var list = new List<MachinePoint>(segments);
            double angleStep = (endDeg - startDeg) / (segments - 1);

            for (int i = 0; i < segments; i++)
            {
                double rad = (startDeg + i * angleStep) * Math.PI / 180.0;
                var pt = new MachinePoint(
                    center.X + radius * Math.Cos(rad),
                    center.Y + radius * Math.Sin(rad),
                    center.Z,
                    center.U,
                    center.V,
                    center.W
                );
                list.Add(pt);
            }

            entry.Points.Clear();
            entry.Points.AddRange(list);
            entry.CurrentIndex = 0;
            UpdateIoLastFlag(name, false);
        }
    }

    /// <summary>
    /// 機構座標與工作區座標偏移轉換 (Point2WS)
    /// </summary>
    public void Point2WS(string pointName, string wsName)
    {
        lock (_lock)
        {
            if (_points.TryGetValue(pointName, out var ptEntry) && _points.TryGetValue(wsName, out var wsEntry))
            {
                var ws = wsEntry.CurrentPoint;
                foreach (var pt in ptEntry.Points)
                {
                    if (!double.IsInfinity(pt.X) && !double.IsInfinity(ws.X)) pt.X -= ws.X;
                    if (!double.IsInfinity(pt.Y) && !double.IsInfinity(ws.Y)) pt.Y -= ws.Y;
                    if (!double.IsInfinity(pt.Z) && !double.IsInfinity(ws.Z)) pt.Z -= ws.Z;
                }
            }
        }
    }

    public void WS2Point(string pointName, string wsName)
    {
        lock (_lock)
        {
            if (_points.TryGetValue(pointName, out var ptEntry) && _points.TryGetValue(wsName, out var wsEntry))
            {
                var ws = wsEntry.CurrentPoint;
                foreach (var pt in ptEntry.Points)
                {
                    if (!double.IsInfinity(pt.X) && !double.IsInfinity(ws.X)) pt.X += ws.X;
                    if (!double.IsInfinity(pt.Y) && !double.IsInfinity(ws.Y)) pt.Y += ws.Y;
                    if (!double.IsInfinity(pt.Z) && !double.IsInfinity(ws.Z)) pt.Z += ws.Z;
                }
            }
        }
    }

    /// <summary>
    /// 剛體位移偏差補償 (SelfMotion)：Offset = NewPt - BasePt, Point += Offset
    /// </summary>
    public void SelfMotion(string pointName, string basePtName, string newPtName)
    {
        lock (_lock)
        {
            var basePt = GetPoint(basePtName);
            var newPt = GetPoint(newPtName);

            double dx = newPt.X - basePt.X;
            double dy = newPt.Y - basePt.Y;
            double dz = newPt.Z - basePt.Z;

            if (_points.TryGetValue(pointName, out var targetEntry))
            {
                foreach (var pt in targetEntry.Points)
                {
                    if (!double.IsInfinity(pt.X)) pt.X += dx;
                    if (!double.IsInfinity(pt.Y)) pt.Y += dy;
                    if (!double.IsInfinity(pt.Z)) pt.Z += dz;
                }
            }
        }
    }

    /// <summary>
    /// 檢查點位是否在矩形範圍內，寫入 $(pointName)#InRange?
    /// </summary>
    public bool CheckInRangeXY(string pointName, string rangePtName)
    {
        var pt = GetPoint(pointName);
        var rangePt = GetPoint(rangePtName);

        bool inRange = Math.Abs(pt.X) <= Math.Abs(rangePt.X) && Math.Abs(pt.Y) <= Math.Abs(rangePt.Y);
        _ioEngine?.SetPort($"{pointName}#InRange?", inRange);
        return inRange;
    }

    public IReadOnlyList<string> GetAllNames()
    {
        return _points.Keys.ToList();
    }

    private void UpdateIoLastFlag(string name, bool isLast)
    {
        _ioEngine?.SetPort($"{name}#Last?", isLast);
    }

    private static double Interpolate(double start, double end, double ratio)
    {
        if (double.IsInfinity(start) || double.IsInfinity(end)) return double.PositiveInfinity;
        return start + (end - start) * ratio;
    }
}
