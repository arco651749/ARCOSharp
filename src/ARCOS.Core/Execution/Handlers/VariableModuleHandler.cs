using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using ARCOS.Core.Common;

namespace ARCOS.Core.Execution.Handlers;

/// <summary>
/// Variable 模組直譯處理器 (Variable@)
/// 完整實做 ARCOS/references/Variable.md 規範之全部數值運算、字串切剖、陣列主鍵檢索、集合運算與跨執行緒事件發布
/// </summary>
public class VariableModuleHandler : IModuleHandler
{
    public string ModuleName => "Variable";

    private static readonly Regex PointElementRegex = new(@"^([a-zA-Z0-9_]+)=([XYZUVWxyzuvw])\(([^)]+)\)$", RegexOptions.Compiled);

    private static readonly string[] Commands = new[]
    {
        "Set",
        "Inc",
        "Dec",
        "Calculate",
        "Delete",
        "Rename",
        "VariableSave",
        "VariableLoad",
        "Append",
        "Trim",
        "Slice",
        "Joint",
        "LResetIndex",
        "LSetIndexValue",
        "LInsert",
        "LPush",
        "RPush",
        "LPop",
        "RPop",
        "LRemove",
        "LReverse",
        "LGetIndexSize",
        "LFindIndexSet",
        "SAdd",
        "SUnion",
        "SDiff",
        "SIntersection",
        "GetPointString",
        "GetPointElement",
        "Select",
        "Shift",
        "Publish",
        "Subscribe",
        "Flush"
    };

    public IReadOnlyList<string> GetSupportedCommands() => Commands;

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        var parts = filteredParam.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 && !action.Equals("Calculate", StringComparison.OrdinalIgnoreCase)) return CommandResult.Done();

        // 1. Set
        if (action.Equals("Set", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2)
            {
                string key = parts[0].TrimStart('$');
                string val = parts[1];
                string sec = parts.Length > 2 ? parts[2] : ctx.CurrentSection;
                ctx.Engines.Variable.Set(key, val, sec);
            }
            return CommandResult.Done();
        }

        // 2. Inc / Dec
        if (action.Equals("Inc", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("Dec", StringComparison.OrdinalIgnoreCase))
        {
            string key = parts[0].TrimStart('$');
            double step = parts.Length > 1 && double.TryParse(parts[1], out double s) ? s : 1.0;
            string sec = parts.Length > 2 ? parts[2] : ctx.CurrentSection;

            if (action.Equals("Inc", StringComparison.OrdinalIgnoreCase))
                ctx.Engines.Variable.Inc(key, step, sec);
            else
                ctx.Engines.Variable.Dec(key, step, sec);

            return CommandResult.Done();
        }

        // 3. Calculate 算式賦值 (如 Total=A+B*2)
        if (action.Equals("Calculate", StringComparison.OrdinalIgnoreCase))
        {
            int eqIdx = filteredParam.IndexOf('=');
            if (eqIdx > 0)
            {
                string varName = filteredParam.Substring(0, eqIdx).Trim().TrimStart('$');
                string formula = filteredParam.Substring(eqIdx + 1).Trim();

                string sec = ctx.CurrentSection;
                int commaIdx = formula.LastIndexOf(',');
                if (commaIdx > 0)
                {
                    string candidateSec = formula.Substring(commaIdx + 1).Trim();
                    formula = formula.Substring(0, commaIdx).Trim();
                    sec = candidateSec;
                }

                try
                {
                    var dt = new DataTable();
                    var computeVal = dt.Compute(formula, null);
                    ctx.Engines.Variable.Set(varName, computeVal?.ToString() ?? "0", sec);
                }
                catch
                {
                    ctx.Engines.Variable.Set(varName, formula, sec);
                }
            }
            return CommandResult.Done();
        }

        // 4. Delete / Rename / Append / Trim / Slice / Joint
        if (action.Equals("Delete", StringComparison.OrdinalIgnoreCase))
        {
            string sec = parts.Length > 1 ? parts[1] : ctx.CurrentSection;
            ctx.Engines.Variable.Delete(parts[0].TrimStart('$'), sec);
            return CommandResult.Done();
        }

        if (action.Equals("Rename", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2)
            {
                string sec = parts.Length > 2 ? parts[2] : ctx.CurrentSection;
                ctx.Engines.Variable.Rename(parts[0].TrimStart('$'), parts[1].TrimStart('$'), sec);
            }
            return CommandResult.Done();
        }

        if (action.Equals("Append", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2)
            {
                string sec = parts.Length > 2 ? parts[2] : ctx.CurrentSection;
                ctx.Engines.Variable.Append(parts[0].TrimStart('$'), parts[1], sec);
            }
            return CommandResult.Done();
        }

        if (action.Equals("Trim", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 3 && int.TryParse(parts[1], out int start) && int.TryParse(parts[2], out int len))
            {
                string sec = parts.Length > 3 ? parts[3] : ctx.CurrentSection;
                ctx.Engines.Variable.Trim(parts[0].TrimStart('$'), start, len, sec);
            }
            return CommandResult.Done();
        }

        if (action.Equals("Slice", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2)
            {
                string sec = parts.Length > 2 ? parts[2] : ctx.CurrentSection;
                ctx.Engines.Variable.Slice(parts[0].TrimStart('$'), parts[1], sec);
            }
            return CommandResult.Done();
        }

        if (action.Equals("Joint", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2)
            {
                string sec = parts.Length > 2 ? parts[2] : ctx.CurrentSection;
                ctx.Engines.Variable.Joint(parts[0].TrimStart('$'), parts[1], sec);
            }
            return CommandResult.Done();
        }

        // 5. 陣列相關指令
        if (action.Equals("LResetIndex", StringComparison.OrdinalIgnoreCase))
        {
            int idx = parts.Length > 1 && int.TryParse(parts[1], out int i) ? i : 0;
            string sec = parts.Length > 2 ? parts[2] : ctx.CurrentSection;
            ctx.Engines.Variable.LResetIndex(parts[0].TrimStart('$'), idx, sec);
            return CommandResult.Done();
        }

        if (action.Equals("LSetIndexValue", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 3 && int.TryParse(parts[1], out int i))
            {
                string sec = parts.Length > 3 ? parts[3] : ctx.CurrentSection;
                ctx.Engines.Variable.LSetIndexValue(parts[0].TrimStart('$'), i, parts[2], sec);
            }
            return CommandResult.Done();
        }

        if (action.Equals("LInsert", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 3)
            {
                string key = parts[0].TrimStart('$');
                string val = parts[1];
                string cond = parts[2];
                string sec = parts.Length > 3 ? parts[3] : ctx.CurrentSection;
                ctx.Engines.Variable.LInsert(key, val, cond, sec);
            }
            return CommandResult.Done();
        }

        if (action.Equals("LPush", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("RPush", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2)
            {
                string sec = parts.Length > 2 ? parts[2] : ctx.CurrentSection;
                if (action.Equals("LPush", StringComparison.OrdinalIgnoreCase))
                    ctx.Engines.Variable.LPush(parts[0].TrimStart('$'), parts[1], sec);
                else
                    ctx.Engines.Variable.RPush(parts[0].TrimStart('$'), parts[1], sec);
            }
            return CommandResult.Done();
        }

        if (action.Equals("LPop", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("RPop", StringComparison.OrdinalIgnoreCase))
        {
            string sec = parts.Length > 1 ? parts[1] : ctx.CurrentSection;
            if (action.Equals("LPop", StringComparison.OrdinalIgnoreCase))
                ctx.Engines.Variable.LPop(parts[0].TrimStart('$'), sec);
            else
                ctx.Engines.Variable.RPop(parts[0].TrimStart('$'), sec);
            return CommandResult.Done();
        }

        if (action.Equals("LRemove", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2)
            {
                string sec = parts.Length > 2 ? parts[2] : ctx.CurrentSection;
                ctx.Engines.Variable.LRemove(parts[0].TrimStart('$'), parts[1], sec);
            }
            return CommandResult.Done();
        }

        if (action.Equals("LReverse", StringComparison.OrdinalIgnoreCase))
        {
            string sec = parts.Length > 1 ? parts[1] : ctx.CurrentSection;
            ctx.Engines.Variable.LReverse(parts[0].TrimStart('$'), sec);
            return CommandResult.Done();
        }

        if (action.Equals("LGetIndexSize", StringComparison.OrdinalIgnoreCase))
        {
            string sec = parts.Length > 1 ? parts[1] : ctx.CurrentSection;
            var (idx, size) = ctx.Engines.Variable.LGetIndexSize(parts[0].TrimStart('$'), sec);
            ctx.Engines.Variable.Set($"{parts[0]}#Index", idx.ToString(), sec);
            ctx.Engines.Variable.Set($"{parts[0]}#Size", size.ToString(), sec);
            return CommandResult.Done();
        }

        if (action.Equals("LFindIndexSet", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2)
            {
                string key = parts[0].TrimStart('$');
                string val = parts[1];
                var linked = parts.Skip(2).Select(p => p.TrimStart('$')).ToList();
                ctx.Engines.Variable.LFindIndexSet(key, val, linked, ctx.CurrentSection);
            }
            return CommandResult.Done();
        }

        // 6. 集合運算 (SAdd / SUnion / SDiff / SIntersection)
        if (action.Equals("SAdd", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2)
            {
                string sec = parts.Length > 2 ? parts[2] : ctx.CurrentSection;
                ctx.Engines.Variable.SAdd(parts[0].TrimStart('$'), parts[1], sec);
            }
            return CommandResult.Done();
        }

        if (action.Equals("SUnion", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 3)
            {
                string sec = parts.Length > 3 ? parts[3] : ctx.CurrentSection;
                ctx.Engines.Variable.SUnion(parts[0].TrimStart('$'), parts[1].TrimStart('$'), parts[2].TrimStart('$'), sec);
            }
            return CommandResult.Done();
        }

        if (action.Equals("SDiff", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 3)
            {
                string sec = parts.Length > 3 ? parts[3] : ctx.CurrentSection;
                ctx.Engines.Variable.SDiff(parts[0].TrimStart('$'), parts[1].TrimStart('$'), parts[2].TrimStart('$'), sec);
            }
            return CommandResult.Done();
        }

        if (action.Equals("SIntersection", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 3)
            {
                string sec = parts.Length > 3 ? parts[3] : ctx.CurrentSection;
                ctx.Engines.Variable.SIntersection(parts[0].TrimStart('$'), parts[1].TrimStart('$'), parts[2].TrimStart('$'), sec);
            }
            return CommandResult.Done();
        }

        // 7. 點位轉換與取值 (GetPointString / GetPointElement)
        if (action.Equals("GetPointString", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2)
            {
                string varName = parts[0].TrimStart('$');
                string ptName = parts[1].TrimStart('$');
                string sec = parts.Length > 2 ? parts[2] : ctx.CurrentSection;
                var pt = ctx.Engines.Point.GetPoint(ptName);
                ctx.Engines.Variable.Set(varName, $"({pt.X},{pt.Y},{pt.Z},{pt.U},{pt.V},{pt.W})", sec);
            }
            return CommandResult.Done();
        }

        if (action.Equals("GetPointElement", StringComparison.OrdinalIgnoreCase))
        {
            var m = PointElementRegex.Match(filteredParam.Trim());
            if (m.Success)
            {
                string varName = m.Groups[1].Value.TrimStart('$');
                char axis = char.ToUpperInvariant(m.Groups[2].Value[0]);
                string ptName = m.Groups[3].Value.TrimStart('$');

                var pt = ctx.Engines.Point.GetPoint(ptName);
                double val = axis switch
                {
                    'X' => pt.X,
                    'Y' => pt.Y,
                    'Z' => pt.Z,
                    'U' => pt.U,
                    'V' => pt.V,
                    'W' => pt.W,
                    _ => 0.0
                };
                ctx.Engines.Variable.Set(varName, val.ToString(CultureInfo.InvariantCulture), ctx.CurrentSection);
            }
            return CommandResult.Done();
        }

        // 8. 條件選擇 (Select)
        if (action.Equals("Select", StringComparison.OrdinalIgnoreCase))
        {
            // 語法：$var=$logic ? $var1 : $var2
            int eqIdx = filteredParam.IndexOf('=');
            int qIdx = filteredParam.IndexOf('?');
            int cIdx = filteredParam.IndexOf(':');

            if (eqIdx > 0 && qIdx > eqIdx && cIdx > qIdx)
            {
                string dest = filteredParam.Substring(0, eqIdx).Trim().TrimStart('$');
                string logic = filteredParam.Substring(eqIdx + 1, qIdx - eqIdx - 1).Trim();
                string v1 = filteredParam.Substring(qIdx + 1, cIdx - qIdx - 1).Trim();
                string v2 = filteredParam.Substring(cIdx + 1).Trim();

                bool cond = LogicEvaluator.EvaluateLogic(logic, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);

                string ResolveVal(string expr)
                {
                    string t = expr.Trim().TrimStart('$');
                    try
                    {
                        return ctx.Engines.Variable.Get(t, ctx.CurrentSection);
                    }
                    catch
                    {
                        return expr.Trim();
                    }
                }

                string chosen = cond ? ResolveVal(v1) : ResolveVal(v2);
                ctx.Engines.Variable.Set(dest, chosen, ctx.CurrentSection);
            }
            return CommandResult.Done();
        }

        // 9. 變數資料位移 (Shift)
        if (action.Equals("Shift", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ArgParaHelper.ParseKeyValueArgs(filteredParam, out var posArgs);
            int shiftAmount = 1;
            if (parsed.TryGetValue("Shift", out var sStr) && int.TryParse(sStr, out int s))
            {
                shiftAmount = s;
            }

            var varList = new List<string>();
            foreach (var arg in posArgs)
            {
                if (!arg.StartsWith("Shift=", StringComparison.OrdinalIgnoreCase))
                {
                    varList.Add(arg.TrimStart('$'));
                }
            }

            if (varList.Count > 1)
            {
                ctx.Engines.Variable.ShiftVariables(varList, shiftAmount, ctx.CurrentSection);
            }
            return CommandResult.Done();
        }

        // 10. 事件發送與接收機制 (Publish / Subscribe / Flush)
        if (action.Equals("Publish", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var p in parts)
            {
                ctx.Engines.Variable.Publish(p.TrimStart('$'));
            }
            return CommandResult.Done();
        }

        if (action.Equals("Subscribe", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length > 0)
            {
                ctx.Engines.Variable.Subscribe(parts[0].TrimStart('$'), 5000);
            }
            return CommandResult.Done();
        }

        if (action.Equals("Flush", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length > 0)
            {
                ctx.Engines.Variable.Flush(parts[0].TrimStart('$'));
            }
            return CommandResult.Done();
        }

        // 11. 存取檔
        if (action.Equals("VariableSave", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Engines.Variable.SaveToFile(parts[0]);
            return CommandResult.Done();
        }

        if (action.Equals("VariableLoad", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Engines.Variable.LoadFromFile(parts[0]);
            return CommandResult.Done();
        }

        return CommandResult.Done();
    }
}
