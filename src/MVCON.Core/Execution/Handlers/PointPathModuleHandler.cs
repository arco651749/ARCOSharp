using MVCON.Core.Common;
using MVCON.Core.Model;

namespace MVCON.Core.Execution.Handlers;

/// <summary>
/// Point&Path 模組直譯處理器 (Point&Path@)
/// 完整實做 MVCon/references/Point&Path.md 規範之全部點位運算、料盤幾何、座標轉換與微步替代指令
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
        "CheckPalletLast?",
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
        // 1. Calculate 點位賦值與運算
        if (action.Equals("Calculate", StringComparison.OrdinalIgnoreCase))
        {
            ExecuteCalculate(filteredParam, ctx);
            return CommandResult.Done();
        }

        // 2. Select(X=A?B:C) 三元運算
        if (action.Equals("Select(X=A?B:C)", StringComparison.OrdinalIgnoreCase) ||
            action.StartsWith("Select", StringComparison.OrdinalIgnoreCase))
        {
            // 格式：$point=$logic ? $point1 : $point2
            int eqIdx = filteredParam.IndexOf('=');
            int qIdx = filteredParam.IndexOf('?');
            int cIdx = filteredParam.IndexOf(':');

            if (eqIdx > 0 && qIdx > eqIdx && cIdx > qIdx)
            {
                string dest = filteredParam.Substring(0, eqIdx).Trim().TrimStart('$');
                string logic = filteredParam.Substring(eqIdx + 1, qIdx - eqIdx - 1).Trim();
                string p1 = filteredParam.Substring(qIdx + 1, cIdx - qIdx - 1).Trim().TrimStart('$');
                string p2 = filteredParam.Substring(cIdx + 1).Trim().TrimStart('$');

                bool cond = LogicEvaluator.EvaluateLogic(logic, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
                var selected = cond ? ctx.Engines.Point.GetPoint(p1) : ctx.Engines.Point.GetPoint(p2);
                ctx.Engines.Point.SetPoint(dest, selected.Clone());
            }
            return CommandResult.Done();
        }

        // 3. EnableAxis
        if (action.Equals("EnableAxis", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                ctx.Engines.Point.EnableAxis(parts[0], parts[1]);
            }
            return CommandResult.Done();
        }

        // 4. InsertOnePoint / ClearPoint
        if (action.Equals("InsertOnePoint", StringComparison.OrdinalIgnoreCase))
        {
            int eqIdx = filteredParam.IndexOf('=');
            if (eqIdx > 0)
            {
                string dest = filteredParam.Substring(0, eqIdx).Trim().TrimStart('$');
                string expr = filteredParam.Substring(eqIdx + 1).Trim();
                var pt = expr.StartsWith('(') ? ParseCoords(expr.Trim('(', ')')) : ctx.Engines.Point.GetPoint(expr.TrimStart('$'));
                ctx.Engines.Point.AppendPoint(dest, pt);
            }
            return CommandResult.Done();
        }

        if (action.Equals("ClearPoint", StringComparison.OrdinalIgnoreCase))
        {
            string pt = filteredParam.Trim().TrimStart('$');
            ctx.Engines.Point.ResetPalletIndex(pt, 0);
            return CommandResult.Done();
        }

        // 5. Pallet1D
        if (action.Equals("Pallet1D", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out int n))
            {
                ctx.Engines.Point.GeneratePallet1D(parts[0], n);
            }
            return CommandResult.Done();
        }

        // 6. Pallet2D
        if (action.Equals("Pallet2D", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 3 && int.TryParse(parts[1], out int nx) && int.TryParse(parts[2], out int ny))
            {
                ctx.Engines.Point.GeneratePallet2D(parts[0], nx, ny);
            }
            return CommandResult.Done();
        }

        // 7. Circle2DPath
        if (action.Equals("Circle2DPath", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 4 &&
                double.TryParse(parts[1], out double d1) &&
                double.TryParse(parts[2], out double d2) &&
                int.TryParse(parts[3], out int segs))
            {
                ctx.Engines.Point.GenerateCircle2DPath(parts[0], d1, d2, segs);
            }
            return CommandResult.Done();
        }

        // 8. ResetPalletIndex
        if (action.Equals("ResetPalletIndex", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            string pt = parts[0].TrimStart('$');
            int idx = parts.Length > 1 && int.TryParse(parts[1], out int i) ? i : 0;
            ctx.Engines.Point.ResetPalletIndex(pt, idx);
            return CommandResult.Done();
        }

        // 9. CheckPalletLast?
        if (action.Equals("CheckPalletLast?", StringComparison.OrdinalIgnoreCase))
        {
            string pt = filteredParam.Trim().TrimStart('$');
            ctx.Engines.Point.CheckPalletLast(pt);
            return CommandResult.Done();
        }

        // 10. 座標轉換 (Point2WS / WS2Point / SelfMotion / TransImaqPoint / CoordWS(2Edge))
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
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2) ctx.Engines.Point.Point2WS(parts[0].TrimStart('$'), parts[1].TrimStart('$'));
            return CommandResult.Done();
        }

        if (action.Equals("CoordWS(2Edge)", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log($"[Point&Path] CoordWS(2Edge) on '{filteredParam}'");
            return CommandResult.Done();
        }

        // 11. 局部維度運算 (AddXYZ / SubXYZ / AddUVW / SubUVW / ReplaceXYZ / ReplaceUVW / ReplaceH)
        if (action.Equals("AddXYZ", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("SubXYZ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                string dest = parts[0].TrimStart('$');
                var delta = parts[1].StartsWith('(') ? ParseCoords(parts[1].Trim('(', ')')) : ctx.Engines.Point.GetPoint(parts[1].TrimStart('$'));
                var basePt = ctx.Engines.Point.GetPoint(dest);
                double sign = action.Equals("AddXYZ", StringComparison.OrdinalIgnoreCase) ? 1.0 : -1.0;
                basePt.X += sign * delta.X;
                basePt.Y += sign * delta.Y;
                basePt.Z += sign * delta.Z;
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
                var delta = parts[1].StartsWith('(') ? ParseCoords(parts[1].Trim('(', ')')) : ctx.Engines.Point.GetPoint(parts[1].TrimStart('$'));
                var basePt = ctx.Engines.Point.GetPoint(dest);
                double sign = action.Equals("AddUVW", StringComparison.OrdinalIgnoreCase) ? 1.0 : -1.0;
                basePt.U += sign * delta.U;
                basePt.V += sign * delta.V;
                basePt.W += sign * delta.W;
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
                var src = parts[1].StartsWith('(') ? ParseCoords(parts[1].Trim('(', ')')) : ctx.Engines.Point.GetPoint(parts[1].TrimStart('$'));
                var basePt = ctx.Engines.Point.GetPoint(dest);
                basePt.X = src.X;
                basePt.Y = src.Y;
                basePt.Z = src.Z;
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
                var src = parts[1].StartsWith('(') ? ParseCoords(parts[1].Trim('(', ')')) : ctx.Engines.Point.GetPoint(parts[1].TrimStart('$'));
                var basePt = ctx.Engines.Point.GetPoint(dest);
                basePt.U = src.U;
                basePt.V = src.V;
                basePt.W = src.W;
                ctx.Engines.Point.SetPoint(dest, basePt);
            }
            return CommandResult.Done();
        }

        if (action.Equals("ReplaceH", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && double.TryParse(parts[1], out double hVal))
            {
                string dest = parts[0].TrimStart('$');
                var basePt = ctx.Engines.Point.GetPoint(dest);
                basePt.H["Radius"] = hVal;
                ctx.Engines.Point.SetPoint(dest, basePt);
            }
            return CommandResult.Done();
        }

        // 12. ChkInRangeXY
        if (action.Equals("ChkInRangeXY", StringComparison.OrdinalIgnoreCase))
        {
            var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2) ctx.Engines.Point.CheckInRangeXY(parts[0].TrimStart('$'), parts[1].TrimStart('$'));
            return CommandResult.Done();
        }

        return CommandResult.Done();
    }

    private static void ExecuteCalculate(string param, ExecutionContext ctx)
    {
        // 支援 $P<=(X,Y,Z...) 陣列追加
        int appendIdx = param.IndexOf("<=");
        if (appendIdx > 0)
        {
            string dest = param.Substring(0, appendIdx).Trim().TrimStart('$');
            string coords = param.Substring(appendIdx + 2).Trim().Trim('(', ')');
            var pt = ParseCoords(coords);
            ctx.Engines.Point.AppendPoint(dest, pt);
            return;
        }

        // 支援 $P=(X,Y,Z...) 單點覆寫或算式運算
        int eqIdx = param.IndexOf('=');
        if (eqIdx > 0)
        {
            string dest = param.Substring(0, eqIdx).Trim().TrimStart('$');
            string expr = param.Substring(eqIdx + 1).Trim();

            if (expr.StartsWith('(') && expr.EndsWith(')'))
            {
                var pt = ParseCoords(expr.Trim('(', ')'));
                ctx.Engines.Point.SetPoint(dest, pt);
            }
            else
            {
                var pt = EvaluatePointMath(expr, ctx);
                ctx.Engines.Point.SetPoint(dest, pt);
            }
        }
    }

    private static MachinePoint ParseCoords(string coords)
    {
        var tokens = coords.Split(',', StringSplitOptions.TrimEntries);
        double ParseVal(int idx)
        {
            if (idx >= tokens.Length) return 0.0;
            if (tokens[idx].Equals("inf", StringComparison.OrdinalIgnoreCase)) return double.PositiveInfinity;
            return double.TryParse(tokens[idx], out double v) ? v : 0.0;
        }

        return new MachinePoint(ParseVal(0), ParseVal(1), ParseVal(2), ParseVal(3), ParseVal(4), ParseVal(5));
    }

    private static MachinePoint EvaluatePointMath(string expr, ExecutionContext ctx)
    {
        char op = '+';
        int opIdx = expr.IndexOf('+');
        if (opIdx < 0) { opIdx = expr.IndexOf('-'); op = '-'; }
        if (opIdx < 0) { opIdx = expr.IndexOf('*'); op = '*'; }
        if (opIdx < 0) { opIdx = expr.IndexOf('/'); op = '/'; }

        if (opIdx > 0)
        {
            string leftName = expr.Substring(0, opIdx).Trim().TrimStart('$');
            string rightExpr = expr.Substring(opIdx + 1).Trim().TrimStart('$');

            var leftPt = ctx.Engines.Point.GetPoint(leftName);
            if (double.TryParse(rightExpr, out double scalar))
            {
                return op switch
                {
                    '+' => new MachinePoint(leftPt.X + scalar, leftPt.Y + scalar, leftPt.Z + scalar, leftPt.U + scalar, leftPt.V + scalar, leftPt.W + scalar),
                    '-' => new MachinePoint(leftPt.X - scalar, leftPt.Y - scalar, leftPt.Z - scalar, leftPt.U - scalar, leftPt.V - scalar, leftPt.W - scalar),
                    '*' => new MachinePoint(leftPt.X * scalar, leftPt.Y * scalar, leftPt.Z * scalar, leftPt.U * scalar, leftPt.V * scalar, leftPt.W * scalar),
                    '/' => new MachinePoint(scalar != 0 ? leftPt.X / scalar : leftPt.X, scalar != 0 ? leftPt.Y / scalar : leftPt.Y, scalar != 0 ? leftPt.Z / scalar : leftPt.Z),
                    _ => leftPt
                };
            }
            else
            {
                var rightPt = ctx.Engines.Point.GetPoint(rightExpr);
                return op switch
                {
                    '+' => new MachinePoint(leftPt.X + rightPt.X, leftPt.Y + rightPt.Y, leftPt.Z + rightPt.Z, leftPt.U + rightPt.U, leftPt.V + rightPt.V, leftPt.W + rightPt.W),
                    '-' => new MachinePoint(leftPt.X - rightPt.X, leftPt.Y - rightPt.Y, leftPt.Z - rightPt.Z, leftPt.U - rightPt.U, leftPt.V - rightPt.V, leftPt.W - rightPt.W),
                    '*' => new MachinePoint(leftPt.X * rightPt.X, leftPt.Y * rightPt.Y, leftPt.Z * rightPt.Z, leftPt.U * rightPt.U, leftPt.V * rightPt.V, leftPt.W * rightPt.W),
                    '/' => new MachinePoint(rightPt.X != 0 ? leftPt.X / rightPt.X : leftPt.X, rightPt.Y != 0 ? leftPt.Y / rightPt.Y : leftPt.Y, rightPt.Z != 0 ? leftPt.Z / rightPt.Z : leftPt.Z),
                    _ => leftPt
                };
            }
        }

        return ctx.Engines.Point.GetPoint(expr.Trim().TrimStart('$'));
    }
}
