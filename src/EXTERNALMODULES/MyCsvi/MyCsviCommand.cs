using System;
using System.Collections.Generic;
using ARCOS.Core.Execution;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ExternalModules.MyCsvi;

/// <summary>
/// 專案客製 AOI 直方圖分析與數值計算指令 (mycsvi.csvi)
/// 實作 ICallCommand，供 Call@mycsvi.csvi 調度執行
/// </summary>
public class MyCsviCommand : ICallCommand
{
    public object? Execute(IReadOnlyList<string> args, ExecutionContext ctx)
    {
        // 1. 若為數值計算模式：輸入至少兩個數字 (例如: 10, 20, $Result)
        if (args.Count >= 2 && double.TryParse(args[0], out double v1) && double.TryParse(args[1], out double v2))
        {
            double sum = v1 + v2;
            ctx.Log($"[mycsvi.csvi] Calculation mode: {v1} + {v2} = {sum}");
            return sum;
        }

        // 2. 若為 AOI 直方圖運算模式：CamName, MinThreshold, MaxThreshold, (ReturnVar)
        string camName = args.Count > 0 ? args[0].Trim() : "Cam1";
        int minThresh = args.Count > 1 && int.TryParse(args[1], out int min) ? min : 0;
        int maxThresh = args.Count > 2 && int.TryParse(args[2], out int max) ? max : 255;

        ctx.Log($"[mycsvi.csvi] AOI Histogram analyzing for '{camName}', threshold range [{minThresh}, {maxThresh}]...");

        // 計算特徵分數 (模擬直方圖平均分佈或閥值命中率)
        double score = Math.Round((minThresh + maxThresh) / 2.0 * 0.85, 2);
        ctx.Log($"[mycsvi.csvi] AOI Histogram analysis completed. Output score: {score}");

        return score;
    }
}
