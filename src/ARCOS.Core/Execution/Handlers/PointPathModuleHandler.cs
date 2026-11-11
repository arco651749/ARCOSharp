using System.Globalization;
using ARCOS.Core.Common;
using ARCOS.Core.Model;

namespace ARCOS.Core.Execution.Handlers;

/// <summary>
/// Point&Path 模組直譯處理器 (Point&Path@)
/// 完整實做 ARCOS/references/Point&Path.md 規範之全部點位運算、料盤幾何、座標轉換與微步替代指令
/// </summary>
public class PointPathModuleHandler : IModuleHandler
{
    public string ModuleName => "Point&Path";

    private static readonly string[] Commands = new[]
    {
        "Calculate",
        "Select(X=A?B:C)",
        "EnableAxis",
        "InsertOnePoint",
        "ClearPoint",
        "Pallet1D",
        "Pallet2D",
        "Circle2DPath",
        "ResetPalletIndex",
        "SelfMotion",
        "TransImaqPoint",
        "Point2WS",
        "WS2Point",
        "CoordWS(2Edge)",
        "AddXYZ",
        "SubXYZ",
        "AddUVW",
        "SubUVW",
        "ReplaceXYZ",
        "ReplaceUVW",
        "ReplaceH",
        "ChkInRangeXY"
    };

    public IReadOnlyList<string> GetSupportedCommands() => Commands;

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        if (action.Equals("Calculate", StringComparison.OrdinalIgnoreCase))
        {
            ExecuteCalculate(filteredParam, ctx);
            return CommandResult.Done();
        }

        if (action.Equals("Select(X=A?B:C)", StringComparison.OrdinalIgnoreCase) ||
            action.StartsWith("Select", StringComparison.OrdinalIgnoreCase))
        {
            int eqIdx = filteredParam.IndexOf('=');
            int qIdx = filteredParam.IndexOf('?');
            int cIdx = filteredParam.IndexOf(':');

            if (eqIdx > 0 && qIdx > eqIdx && cIdx > qIdx)
            {
                string dest = filteredParam.Substring(0, eqIdx).Trim().TrimStart('$');
                string logic = filteredParam.Substring(eqIdx + 1, qIdx - eqIdx - 1).Trim();
                string p1 = filteredParam.Substring(qIdx + 1, cIdx - qIdx - 1).Trim();
                string p2 = filteredParam.Substring(cIdx + 1).Trim();

                bool cond = LogicEvaluator.EvaluateLogic(logic, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                var selected = ResolvePointOrCoords(cond ? p1 : p2, ctx);
                ctx.Engines.Point.SetPoint(dest, selected.Clone());
            }
            return CommandResult.Done();
        }

        if (action.Equals("EnableAxis", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                ctx.Engines.Point.EnableAxis(parts[0].TrimStart('$'), parts[1]);
            }
            return CommandResult.Done();
        }

        if (action.Equals("InsertOnePoint", StringComparison.OrdinalIgnoreCase))
        {
            int eqIdx = filteredParam.IndexOf('=');
            if (eqIdx > 0)
            {
                string dest = filteredParam.Substring(0, eqIdx).Trim().TrimStart('$');
                string expr = filteredParam.Substring(eqIdx + 1).Trim();
                var pt = ResolvePointOrCoords(expr, ctx);
                ctx.Engines.Point.AppendPoint(dest, pt);
            }
            else
            {
                var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                {
                    string dest = parts[0].TrimStart('$');
                    var pt = ResolvePointOrCoords(parts[1], ctx);
                    ctx.Engines.Point.AppendPoint(dest, pt);
                }
            }
            return CommandResult.Done();
        }

        if (action.Equals("ClearPoint", StringComparison.OrdinalIgnoreCase))
        {
            string pt = filteredParam.Trim().TrimStart('$');
            ctx.Engines.Point.ClearPoint(pt);
            return CommandResult.Done();
        }

        if (action.Equals("Pallet1D", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out int n))
            {
                ctx.Engines.Point.GeneratePallet1D(parts[0].TrimStart('$'), n);
            }
            return CommandResult.Done();
        }

        if (action.Equals("Pallet2D", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 3 && int.TryParse(parts[1], out int nx) && int.TryParse(parts[2], out int ny))
            {
                ctx.Engines.Point.GeneratePallet2D(parts[0].TrimStart('$'), nx, ny);
            }
            return CommandResult.Done();
        }

        if (action.Equals("Circle2DPath", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 4 &&
                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double d1) &&
                double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double d2) &&
                int.TryParse(parts[3], out int segs))
            {
                ctx.Engines.Point.GenerateCircle2DPath(parts[0].TrimStart('$'), d1, d2, segs);
            }
            else if (parts.Length >= 2 && int.TryParse(parts[1], out int segsShort))
            {
                double endAngle = 360.0;
                for (int i = 2; i < parts.Length; i++)
                {
                    if (parts[i].StartsWith("Angle=", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(parts[i].Substring(6), NumberStyles.Any, CultureInfo.InvariantCulture, out double a))
                    {
                        endAngle = a;
                    }
                }
                ctx.Engines.Point.GenerateCircle2DPath(parts[0].TrimStart('$'), 0.0, endAngle, segsShort);
            }
            return CommandResult.Done();
        }

        if (action.Equals("ResetPalletIndex", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            string pt = parts[0].TrimStart('$');
            int idx = parts.Length > 1 && int.TryParse(parts[1], out int i) ? i : 0;
            ctx.Engines.Point.ResetPalletIndex(pt, idx);
            return CommandResult.Done();
        }

        if (action.Equals("Point2WS", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2) ctx.Engines.Point.Point2WS(parts[0].TrimStart('$'), parts[1].TrimStart('$'));
            return CommandResult.Done();
        }

        if (action.Equals("WS2Point", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2) ctx.Engines.Point.WS2Point(parts[0].TrimStart('$'), parts[1].TrimStart('$'));
            return CommandResult.Done();
        }

        if (action.Equals("SelfMotion", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 3) ctx.Engines.Point.SelfMotion(parts[0].TrimStart('$'), parts[1].TrimStart('$'), parts[2].TrimStart('$'));
            return CommandResult.Done();
        }

        if (action.Equals("TransImaqPoint", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log($"[Point&Path] TransImaqPoint is not implemented yet. (param: {filteredParam})");
            return CommandResult.Done();
        }

        if (action.Equals("CoordWS(2Edge)", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 3, StringSplitOptions.TrimEntries);
            if (parts.Length >= 3)
            {
                string wsName = parts[0].TrimStart('$');
                var xEnd = ResolvePointOrCoords(parts[1], ctx);
                var yEnd = ResolvePointOrCoords(parts[2], ctx);
                ctx.Engines.Point.CoordWS2Edge(wsName, xEnd, yEnd);
            }
            return CommandResult.Done();
        }

        if (action.Equals("AddXYZ", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("SubXYZ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                string dest = parts[0].TrimStart('$');
                var (dx, dy, dz) = ParseTripletOrPoint(parts[1], isUvw: false, ctx);
                var basePt = ctx.Engines.Point.GetPoint(dest);
                double sign = action.Equals("AddXYZ", StringComparison.OrdinalIgnoreCase) ? 1.0 : -1.0;
                basePt.X += sign * dx;
                basePt.Y += sign * dy;
                basePt.Z += sign * dz;
                ctx.Engines.Point.SetPoint(dest, basePt);
            }
            return CommandResult.Done();
        }

        if (action.Equals("AddUVW", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("SubUVW", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                string dest = parts[0].TrimStart('$');
                var (du, dv, dw) = ParseTripletOrPoint(parts[1], isUvw: true, ctx);
                var basePt = ctx.Engines.Point.GetPoint(dest);
                double sign = action.Equals("AddUVW", StringComparison.OrdinalIgnoreCase) ? 1.0 : -1.0;
                basePt.U += sign * du;
                basePt.V += sign * dv;
                basePt.W += sign * dw;
                ctx.Engines.Point.SetPoint(dest, basePt);
            }
            return CommandResult.Done();
        }

        if (action.Equals("ReplaceXYZ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                string dest = parts[0].TrimStart('$');
                var (rx, ry, rz) = ParseTripletOrPoint(parts[1], isUvw: false, ctx);
                var basePt = ctx.Engines.Point.GetPoint(dest);
                basePt.X = rx;
                basePt.Y = ry;
                basePt.Z = rz;
                ctx.Engines.Point.SetPoint(dest, basePt);
            }
            return CommandResult.Done();
        }

        if (action.Equals("ReplaceUVW", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                string dest = parts[0].TrimStart('$');
                var (ru, rv, rw) = ParseTripletOrPoint(parts[1], isUvw: true, ctx);
                var basePt = ctx.Engines.Point.GetPoint(dest);
                basePt.U = ru;
                basePt.V = rv;
                basePt.W = rw;
                ctx.Engines.Point.SetPoint(dest, basePt);
            }
            return CommandResult.Done();
        }

        if (action.Equals("ReplaceH", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                string dest = parts[0].TrimStart('$');
                var basePt = ctx.Engines.Point.GetPoint(dest);
                basePt.SetH(parts[1]);
                ctx.Engines.Point.SetPoint(dest, basePt);
            }
            return CommandResult.Done();
        }

        if (action.Equals("ChkInRangeXY", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2) ctx.Engines.Point.CheckInRangeXY(parts[0].TrimStart('$'), parts[1].TrimStart('$'));
            return CommandResult.Done();
        }

        return CommandResult.Done();
    }

    public static void ExecuteCalculate(string param, ExecutionContext ctx)
    {
        int appendIdx = param.IndexOf("<=");
        if (appendIdx > 0)
        {
            string dest = param.Substring(0, appendIdx).Trim().TrimStart('$');
            string expr = param.Substring(appendIdx + 2).Trim();
            var pt = ResolvePointOrCoords(expr, ctx);
            ctx.Engines.Point.AppendPoint(dest, pt);
            return;
        }

        int eqIdx = param.IndexOf('=');
        if (eqIdx > 0)
        {
            string dest = param.Substring(0, eqIdx).Trim().TrimStart('$');
            string expr = param.Substring(eqIdx + 1).Trim();

            if (expr.StartsWith('(') && expr.EndsWith(')'))
            {
                var pt = MachinePoint.ParseCoords(expr);
                ctx.Engines.Point.SetPoint(dest, pt);
            }
            else
            {
                var pt = EvaluatePointMath(expr, ctx);
                ctx.Engines.Point.SetPoint(dest, pt);
            }
        }
    }

    public static MachinePoint ResolvePointOrCoords(string expr, ExecutionContext ctx)
    {
        string trimmed = expr.Trim();
        if (trimmed.StartsWith('('))
        {
            return MachinePoint.ParseCoords(trimmed);
        }
        return ctx.Engines.Point.GetPoint(trimmed.TrimStart('$'));
    }

    public static (double A, double B, double C) ParseTripletOrPoint(string expr, bool isUvw, ExecutionContext ctx)
    {
        string trimmed = expr.Trim();
        if (trimmed.StartsWith('(') && trimmed.EndsWith(')'))
        {
            string inner = trimmed.Substring(1, trimmed.Length - 2);
            var tokens = inner.Split(',', StringSplitOptions.TrimEntries);
            if (tokens.Length <= 3)
            {
                double v0 = tokens.Length > 0 && double.TryParse(tokens[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var d0) ? d0 : 0;
                double v1 = tokens.Length > 1 && double.TryParse(tokens[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var d1) ? d1 : 0;
                double v2 = tokens.Length > 2 && double.TryParse(tokens[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var d2) ? d2 : 0;
                return (v0, v1, v2);
            }
            var parsedPt = MachinePoint.ParseCoords(trimmed);
            return isUvw ? (parsedPt.U, parsedPt.V, parsedPt.W) : (parsedPt.X, parsedPt.Y, parsedPt.Z);
        }

        var rawTokens = trimmed.Split(',', StringSplitOptions.TrimEntries);
        if (rawTokens.Length >= 2 || double.TryParse(rawTokens[0], NumberStyles.Any, CultureInfo.InvariantCulture, out _))
        {
            double v0 = rawTokens.Length > 0 && double.TryParse(rawTokens[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var d0) ? d0 : 0;
            double v1 = rawTokens.Length > 1 && double.TryParse(rawTokens[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var d1) ? d1 : 0;
            double v2 = rawTokens.Length > 2 && double.TryParse(rawTokens[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var d2) ? d2 : 0;
            return (v0, v1, v2);
        }

        var pt = ctx.Engines.Point.GetPoint(trimmed.TrimStart('$'));
        return isUvw ? (pt.U, pt.V, pt.W) : (pt.X, pt.Y, pt.Z);
    }

    public static MachinePoint EvaluatePointMath(string expr, ExecutionContext ctx)
    {
        char op = '+';
        int opIdx = expr.IndexOf('+');
        if (opIdx < 0) { opIdx = expr.IndexOf('-', 1); op = '-'; }
        if (opIdx < 0) { opIdx = expr.IndexOf('*'); op = '*'; }
        if (opIdx < 0) { opIdx = expr.IndexOf('/'); op = '/'; }

        if (opIdx > 0)
        {
            string leftName = expr.Substring(0, opIdx).Trim().TrimStart('$');
            string rightExpr = expr.Substring(opIdx + 1).Trim().TrimStart('$');

            var leftPt = ctx.Engines.Point.GetPoint(leftName);
            if (double.TryParse(rightExpr, NumberStyles.Any, CultureInfo.InvariantCulture, out double scalar))
            {
                var res = op switch
                {
                    '+' => new MachinePoint(leftPt.X + scalar, leftPt.Y + scalar, leftPt.Z + scalar, leftPt.U + scalar, leftPt.V + scalar, leftPt.W + scalar),
                    '-' => new MachinePoint(leftPt.X - scalar, leftPt.Y - scalar, leftPt.Z - scalar, leftPt.U - scalar, leftPt.V - scalar, leftPt.W - scalar),
                    '*' => new MachinePoint(leftPt.X * scalar, leftPt.Y * scalar, leftPt.Z * scalar, leftPt.U * scalar, leftPt.V * scalar, leftPt.W * scalar),
                    '/' => new MachinePoint(
                        scalar != 0 ? leftPt.X / scalar : leftPt.X,
                        scalar != 0 ? leftPt.Y / scalar : leftPt.Y,
                        scalar != 0 ? leftPt.Z / scalar : leftPt.Z,
                        scalar != 0 ? leftPt.U / scalar : leftPt.U,
                        scalar != 0 ? leftPt.V / scalar : leftPt.V,
                        scalar != 0 ? leftPt.W / scalar : leftPt.W),
                    _ => leftPt.Clone()
                };
                res.H = leftPt.Clone().H;
                return res;
            }
            else
            {
                var rightPt = ResolvePointOrCoords(rightExpr, ctx);
                var res = op switch
                {
                    '+' => new MachinePoint(leftPt.X + rightPt.X, leftPt.Y + rightPt.Y, leftPt.Z + rightPt.Z, leftPt.U + rightPt.U, leftPt.V + rightPt.V, leftPt.W + rightPt.W),
                    '-' => new MachinePoint(leftPt.X - rightPt.X, leftPt.Y - rightPt.Y, leftPt.Z - rightPt.Z, leftPt.U - rightPt.U, leftPt.V - rightPt.V, leftPt.W - rightPt.W),
                    '*' => new MachinePoint(leftPt.X * rightPt.X, leftPt.Y * rightPt.Y, leftPt.Z * rightPt.Z, leftPt.U * rightPt.U, leftPt.V * rightPt.V, leftPt.W * rightPt.W),
                    '/' => new MachinePoint(
                        rightPt.X != 0 ? leftPt.X / rightPt.X : leftPt.X,
                        rightPt.Y != 0 ? leftPt.Y / rightPt.Y : leftPt.Y,
                        rightPt.Z != 0 ? leftPt.Z / rightPt.Z : leftPt.Z,
                        rightPt.U != 0 ? leftPt.U / rightPt.U : leftPt.U,
                        rightPt.V != 0 ? leftPt.V / rightPt.V : leftPt.V,
                        rightPt.W != 0 ? leftPt.W / rightPt.W : leftPt.W),
                    _ => leftPt.Clone()
                };
                res.H = leftPt.Clone().H;
                return res;
            }
        }

        return ctx.Engines.Point.GetPoint(expr.Trim().TrimStart('$'));
    }
}
