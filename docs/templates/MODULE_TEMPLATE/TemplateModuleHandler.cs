using System;
using System.Collections.Generic;
using MVCON.Core.Common;
using MVCON.Core.Drivers;
using MVCON.Core.Execution;
using ExecutionContext = MVCON.Core.Execution.ExecutionContext;

namespace MVCON.Module.Template;

/// <summary>
/// 模組標準指令直譯處理常式範本 (Template Module Handler)
/// 實作 IModuleHandler 介面，支援指令探索、微步狀態流動與基礎引擎調用。
/// </summary>
public sealed class TemplateModuleHandler : IModuleHandler
{
    public string ModuleName => "Template";

    private static readonly string[] SupportedCommands = new[]
    {
        "ActionOne",
        "ActionTwo",
        "WaitDone",
        "getCMD"
    };

    public IReadOnlyList<string> GetSupportedCommands() => SupportedCommands;

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // 1. 響應指令探索契約 (@getCMD)
        if (action.Equals("getCMD", StringComparison.OrdinalIgnoreCase))
        {
            string cmdList = string.Join(",", SupportedCommands);
            ctx.Engines.Variable.Set($"CMDLIST_{ModuleName}", cmdList, ctx.CurrentSection);
            return CommandResult.Done();
        }

        // 2. 範例動作一：即時同步執行 (ActionOne)
        if (action.Equals("ActionOne", StringComparison.OrdinalIgnoreCase))
        {
            string target = ParseValue(filteredParam, "Target") ?? "DefaultTarget";
            double delay = ParseDouble(filteredParam, "Delay", 0.0);

            // 直接存取 BasicEngines 進行業務處理
            ctx.Engines.Variable.Set("Template_LastTarget", target, ctx.CurrentSection);
            if (delay > 0)
            {
                ctx.Engines.Io.SetPort("Template_Active_Pin", true);
            }

            return CommandResult.Done();
        }

        // 3. 範例動作二：非阻塞微步狀態輪詢 (ActionTwo / WaitDone)
        if (action.Equals("ActionTwo", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("WaitDone", StringComparison.OrdinalIgnoreCase))
        {
            double timeoutSec = ParseDouble(filteredParam, "Timeout", 3.0);

            switch (ctx.State.Value)
            {
                case 0:
                    // Step 0: 發出動作指令並記錄時間標記 (存於 State 字典)
                    ctx.State["StartTime"] = DateTime.UtcNow.Ticks;
                    ctx.State.Value = 1;
                    return CommandResult.Wait();

                case 1:
                    // Step 1: 檢查完成狀態 (可向 Driver 票券查詢或讀取 IO)
                    bool isFinished = ctx.Engines.Io.GetPort("Template_Done_Sensor");
                    if (isFinished)
                    {
                        ctx.State.Reset();
                        return CommandResult.Done();
                    }

                    // 超時自癒或報錯
                    long startTicks = (long)(ctx.State["StartTime"] ?? 0L);
                    double elapsed = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - startTicks).TotalSeconds;
                    if (elapsed > timeoutSec)
                    {
                        ctx.State.Reset();
                        return CommandResult.Fail($"Action timed out ({timeoutSec}s)");
                    }

                    return CommandResult.Wait();
            }
        }

        return CommandResult.Fail($"Module '{ModuleName}' does not recognize action '{action}'");
    }

    private static string? ParseValue(string param, string key)
    {
        if (string.IsNullOrWhiteSpace(param)) return null;
        foreach (var seg in param.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = seg.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return pair[1].Trim();
            }
        }
        return null;
    }

    private static double ParseDouble(string param, string key, double defaultVal)
    {
        string? v = ParseValue(param, key);
        return double.TryParse(v, out double res) ? res : defaultVal;
    }
}
