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
    /// 清除指定點位的點資料記憶 (對應 ClearPoint)
    /// </summary>
    public void ClearPoint(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_lock)
        {
            if (_points.TryGetValue(name.Trim(), out var entry))
            {
                entry.Points.Clear();
                entry.CurrentIndex = 0;
                UpdateIoLastFlag(name.Trim(), true);
            }
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
            double deltaXz = (pXEnd.Z - pOrigin.Z) / (countX - 1);
            double deltaYx = (pYEnd.X - pOrigin.X) / (countY - 1);
            double deltaYy = (pYEnd.Y - pOrigin.Y) / (countY - 1);
            double deltaYz = (pYEnd.Z - pOrigin.Z) / (countY - 1);

            var list = new List<MachinePoint>(countX * countY);

            for (int r = 0; r < countY; r++)
            {
                for (int c = 0; c < countX; c++)
                {
                    var pt = new MachinePoint(
                        pOrigin.X + c * deltaXx + r * deltaYx,
                        pOrigin.Y + c * deltaXy + r * deltaYy,
                        pOrigin.Z + c * deltaXz + r * deltaYz,
                        pOrigin.U,
                        pOrigin.V,
                        pOrigin.W
                    );
                    pt.H = pOrigin.Clone().H;
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
    /// 依 [0]圓心 (可由 center.Radius 或 [1]半徑參考點決定半徑) 產生圓弧分割路徑 (對應 Circle2DPath)
    /// </summary>
    public void GenerateCircle2DPath(string name, double startDeg, double endDeg, int segments)
    {
        if (segments <= 1) return;

        lock (_lock)
        {
            if (!_points.TryGetValue(name, out var entry) || entry.Points.Count < 1)
            {
                throw new InvalidOperationException($"Circle2DPath requires point array '{name}' with at least [0] Center.");
            }

            var center = entry.Points[0];
            double radius = center.Radius;
            if (radius <= 0 && entry.Points.Count >= 2)
            {
                var radiusPt = entry.Points[1];
                radius = Math.Sqrt(Math.Pow(radiusPt.X - center.X, 2) + Math.Pow(radiusPt.Y - center.Y, 2));
                center.Radius = radius;
            }

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
                pt.Radius = radius;
                list.Add(pt);
            }

            entry.Points.Clear();
            entry.Points.AddRange(list);
            entry.CurrentIndex = 0;
            UpdateIoLastFlag(name, false);
        }
    }

    /// <summary>
    /// 以現有原點 ws[0] 及指定的 X 終點、Y 終點建立三點工作區 (對應 CoordWS(2Edge))
    /// </summary>
    public void CoordWS2Edge(string wsName, MachinePoint xEndPt, MachinePoint yEndPt)
    {
        if (string.IsNullOrWhiteSpace(wsName)) return;
        lock (_lock)
        {
            var entry = _points.GetOrAdd(wsName.Trim(), _ => new PointArrayEntry());
            var origin = entry.Points.Count > 0 ? entry.Points[0].Clone() : new MachinePoint();
            entry.Points.Clear();
            entry.Points.Add(origin);
            entry.Points.Add(xEndPt.Clone());
            entry.Points.Add(yEndPt.Clone());
            entry.CurrentIndex = 0;
            UpdateIoLastFlag(wsName.Trim(), false);
        }
    }

    /// <summary>
    /// 機構座標轉換為工作區座標 (Point2WS)
    /// 當 ws 含 3 點 ([0]=Origin, [1]=X_End, [2]=Y_End) 時執行基底向量逆投影；僅 1 點時執行純平移
    /// </summary>
    public void Point2WS(string pointName, string wsName)
    {
        lock (_lock)
        {
            if (!_points.TryGetValue(pointName, out var ptEntry) || !_points.TryGetValue(wsName, out var wsEntry) || wsEntry.Points.Count == 0)
                return;

            var origin = wsEntry.Points[0];
            if (wsEntry.Points.Count >= 3)
            {
                var xEnd = wsEntry.Points[1];
                var yEnd = wsEntry.Points[2];

                double vxX = xEnd.X - origin.X;
                double vxY = xEnd.Y - origin.Y;
                double lenX = Math.Sqrt(vxX * vxX + vxY * vxY);
                double exX = lenX > 1e-9 ? vxX / lenX : 1.0;
                double exY = lenX > 1e-9 ? vxY / lenX : 0.0;

                double vyX = yEnd.X - origin.X;
                double vyY = yEnd.Y - origin.Y;
                double lenY = Math.Sqrt(vyX * vyX + vyY * vyY);
                double eyX = lenY > 1e-9 ? vyX / lenY : 0.0;
                double eyY = lenY > 1e-9 ? vyY / lenY : 1.0;

                double det = exX * eyY - exY * eyX;

                foreach (var pt in ptEntry.Points)
                {
                    if (!double.IsInfinity(pt.X) && !double.IsInfinity(pt.Y))
                    {
                        double dx = pt.X - origin.X;
                        double dy = pt.Y - origin.Y;
                        if (Math.Abs(det) > 1e-9)
                        {
                            pt.X = (dx * eyY - dy * eyX) / det;
                            pt.Y = (-dx * exY + dy * exX) / det;
                        }
                        else
                        {
                            pt.X = dx;
                            pt.Y = dy;
                        }
                    }
                    if (!double.IsInfinity(pt.Z) && !double.IsInfinity(origin.Z))
                    {
                        pt.Z -= origin.Z;
                    }
                }
            }
            else
            {
                foreach (var pt in ptEntry.Points)
                {
                    if (!double.IsInfinity(pt.X) && !double.IsInfinity(origin.X)) pt.X -= origin.X;
                    if (!double.IsInfinity(pt.Y) && !double.IsInfinity(origin.Y)) pt.Y -= origin.Y;
                    if (!double.IsInfinity(pt.Z) && !double.IsInfinity(origin.Z)) pt.Z -= origin.Z;
                }
            }
        }
    }

    /// <summary>
    /// 工作區座標轉換回機構座標 (WS2Point)
    /// 當 ws 含 3 點 ([0]=Origin, [1]=X_End, [2]=Y_End) 時執行 P = Origin + u * ex + v * ey
    /// </summary>
    public void WS2Point(string pointName, string wsName)
    {
        lock (_lock)
        {
            if (!_points.TryGetValue(pointName, out var ptEntry) || !_points.TryGetValue(wsName, out var wsEntry) || wsEntry.Points.Count == 0)
                return;

            var origin = wsEntry.Points[0];
            if (wsEntry.Points.Count >= 3)
            {
                var xEnd = wsEntry.Points[1];
                var yEnd = wsEntry.Points[2];

                double vxX = xEnd.X - origin.X;
                double vxY = xEnd.Y - origin.Y;
                double lenX = Math.Sqrt(vxX * vxX + vxY * vxY);
                double exX = lenX > 1e-9 ? vxX / lenX : 1.0;
                double exY = lenX > 1e-9 ? vxY / lenX : 0.0;

                double vyX = yEnd.X - origin.X;
                double vyY = yEnd.Y - origin.Y;
                double lenY = Math.Sqrt(vyX * vyX + vyY * vyY);
                double eyX = lenY > 1e-9 ? vyX / lenY : 0.0;
                double eyY = lenY > 1e-9 ? vyY / lenY : 1.0;

                foreach (var pt in ptEntry.Points)
                {
                    if (!double.IsInfinity(pt.X) && !double.IsInfinity(pt.Y))
                    {
                        double u = pt.X;
                        double v = pt.Y;
                        pt.X = origin.X + u * exX + v * eyX;
                        pt.Y = origin.Y + u * exY + v * eyY;
                    }
                    if (!double.IsInfinity(pt.Z) && !double.IsInfinity(origin.Z))
                    {
                        pt.Z += origin.Z;
                    }
                }
            }
            else
            {
                foreach (var pt in ptEntry.Points)
                {
                    if (!double.IsInfinity(pt.X) && !double.IsInfinity(origin.X)) pt.X += origin.X;
                    if (!double.IsInfinity(pt.Y) && !double.IsInfinity(origin.Y)) pt.Y += origin.Y;
                    if (!double.IsInfinity(pt.Z) && !double.IsInfinity(origin.Z)) pt.Z += origin.Z;
                }
            }
        }
    }

    /// <summary>
    /// 剛體旋轉與平移偏差補償 (SelfMotion)
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
            double duDeg = (!double.IsInfinity(newPt.U) && !double.IsInfinity(basePt.U)) ? (newPt.U - basePt.U) : 0.0;
            double rad = duDeg * Math.PI / 180.0;
            double cosA = Math.Cos(rad);
            double sinA = Math.Sin(rad);

            if (_points.TryGetValue(pointName, out var targetEntry))
            {
                foreach (var pt in targetEntry.Points)
                {
                    if (Math.Abs(duDeg) > 1e-9 && !double.IsInfinity(pt.X) && !double.IsInfinity(pt.Y))
                    {
                        double rx = pt.X - basePt.X;
                        double ry = pt.Y - basePt.Y;
                        pt.X = basePt.X + (rx * cosA - ry * sinA) + dx;
                        pt.Y = basePt.Y + (rx * sinA + ry * cosA) + dy;
                        if (!double.IsInfinity(pt.U)) pt.U += duDeg;
                    }
                    else
                    {
                        if (!double.IsInfinity(pt.X)) pt.X += dx;
                        if (!double.IsInfinity(pt.Y)) pt.Y += dy;
                    }
                    if (!double.IsInfinity(pt.Z)) pt.Z += dz;
                }
            }
        }
    }

    /// <summary>
    /// 檢查點位是否在矩形範圍內，寫入 $(pointName)#InRange?
    /// 若 range 具有 [0] 與 [1] 兩點，判斷是否落在 [0]~(1) 構成的邊界矩形內；若僅 1 點則判斷 |X|<=|r.X| & |Y|<=|r.Y|
    /// </summary>
    public bool CheckInRangeXY(string pointName, string rangePtName)
    {
        var pt = GetPoint(pointName);
        bool inRange;

        if (_points.TryGetValue(rangePtName, out var rangeEntry) && rangeEntry.Points.Count >= 2)
        {
            var r0 = rangeEntry.Points[0];
            var r1 = rangeEntry.Points[1];
            double minX = Math.Min(r0.X, r1.X);
            double maxX = Math.Max(r0.X, r1.X);
            double minY = Math.Min(r0.Y, r1.Y);
            double maxY = Math.Max(r0.Y, r1.Y);
            inRange = pt.X >= minX && pt.X <= maxX && pt.Y >= minY && pt.Y <= maxY;
        }
        else
        {
            var rangePt = GetPoint(rangePtName);
            inRange = Math.Abs(pt.X) <= Math.Abs(rangePt.X) && Math.Abs(pt.Y) <= Math.Abs(rangePt.Y);
        }

        _ioEngine?.SetPort($"{pointName}#InRange?", inRange);
        _ioEngine?.SetPort($"{pointName}#InRangeXY?", !inRange);
        return inRange;
    }

    public IReadOnlyList<string> GetAllNames()
    {
        return _points.Keys.ToList();
    }

    public IReadOnlyDictionary<string, MachinePoint> GetAllPoints()
    {
        var dict = new Dictionary<string, MachinePoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _points)
        {
            dict[kvp.Key] = kvp.Value.CurrentPoint.Clone();
        }
        return dict;
    }

    /// <summary>
    /// 將所有已註冊點位儲存至 POINT&PATH/ 目錄下的檔案
    /// </summary>
    public void SaveToFile(string? filePath = null)
    {
        string targetPath = filePath ?? Path.Combine(ARCOS.Core.Common.ArcosPaths.PointAndPathDirectory, "Points.ini");
        if (!Path.IsPathRooted(targetPath))
        {
            targetPath = Path.Combine(ARCOS.Core.Common.ArcosPaths.PointAndPathDirectory, targetPath);
        }

        string dir = Path.GetDirectoryName(targetPath) ?? "";
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[Points]");
        foreach (var kvp in _points)
        {
            var entry = kvp.Value;
            if (entry.Points.Count == 1)
            {
                var pt = entry.Points[0];
                sb.AppendLine($"{kvp.Key}={pt.X},{pt.Y},{pt.Z},{pt.U},{pt.V},{pt.W}");
            }
            else if (entry.Points.Count > 1)
            {
                string pts = string.Join(";", entry.Points.Select(pt => $"({pt.X},{pt.Y},{pt.Z},{pt.U},{pt.V},{pt.W})"));
                sb.AppendLine($"{kvp.Key}={pts}");
            }
        }

        File.WriteAllText(targetPath, sb.ToString(), System.Text.Encoding.UTF8);
    }

    /// <summary>
    /// 從檔案載入點位至 PointEngine
    /// </summary>
    public void LoadFromFile(string? filePath = null)
    {
        string targetPath = filePath ?? Path.Combine(ARCOS.Core.Common.ArcosPaths.PointAndPathDirectory, "Points.ini");
        if (!Path.IsPathRooted(targetPath) && !File.Exists(targetPath))
        {
            targetPath = Path.Combine(ARCOS.Core.Common.ArcosPaths.PointAndPathDirectory, targetPath);
        }

        if (!File.Exists(targetPath)) return;

        var lines = File.ReadAllLines(targetPath, System.Text.Encoding.UTF8);

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith(';') || line.StartsWith('[')) continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line.Substring(0, eq).Trim();
            string val = line.Substring(eq + 1).Trim();

            if (val.Contains('('))
            {
                // 多點陣列格式：(x,y,z,u,v,w);(x,y,z,u,v,w)
                var pts = new List<MachinePoint>();
                var tokens = val.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var tok in tokens)
                {
                    string trimmed = tok.Trim('(', ')');
                    var parts = trimmed.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length >= 6 &&
                        double.TryParse(parts[0], out double x) &&
                        double.TryParse(parts[1], out double y) &&
                        double.TryParse(parts[2], out double z) &&
                        double.TryParse(parts[3], out double u) &&
                        double.TryParse(parts[4], out double v) &&
                        double.TryParse(parts[5], out double w))
                    {
                        pts.Add(new MachinePoint(x, y, z, u, v, w));
                    }
                }
                if (pts.Count > 0)
                {
                    SetPoint(key, pts[0]);
                    for (int i = 1; i < pts.Count; i++) AppendPoint(key, pts[i]);
                }
            }
            else
            {
                // 單點格式：x,y,z,u,v,w
                var parts = val.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 6 &&
                    double.TryParse(parts[0], out double x) &&
                    double.TryParse(parts[1], out double y) &&
                    double.TryParse(parts[2], out double z) &&
                    double.TryParse(parts[3], out double u) &&
                    double.TryParse(parts[4], out double v) &&
                    double.TryParse(parts[5], out double w))
                {
                    SetPoint(key, new MachinePoint(x, y, z, u, v, w));
                }
            }
        }
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
