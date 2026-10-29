using ARCOS.Core.Common;
using ARCOS.Core.Drivers;
using ARCOS.Core.Execution;
using ARCOS.Core.Model;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Module.DAQ;

/// <summary>
/// DAQ 模組直譯處理器 (DAQ@)
/// </summary>
public sealed class DaqModuleHandler : IModuleHandler
{
    public string ModuleName => "DAQ";

    private static readonly string[] Commands = new[]
    {
        "AddDAQ",
        "Start",
        "Stop",
        "Wait",
        "AnalogIn",
        "AnalogInWaveForm",
        "AnalogOut",
        "AnalogOutWaveForm",
        "Filter",
        "CompareMaxMinInRange",
        "CompareCoverBy",
        "Display2User"
    };

    public IReadOnlyList<string> GetSupportedCommands() => Commands;

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(filteredParam, out var posArgs);
        parsed.TryGetValue("_ErrorLabel", out var errorLabel);
        parsed.TryGetValue("_ErrorMsg", out var errorMsg);

        if (posArgs.Count == 0) return CommandResult.Done();
        string daq = posArgs[0];

        // 1. AnalogIn / Sample / AnalogOut 轉發至驅動
        if (action.Equals("AnalogIn", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("AnalogInWaveForm", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("AnalogOut", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("AnalogOutWaveForm", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("Start", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("Stop", StringComparison.OrdinalIgnoreCase))
        {
            var driver = ctx.Drivers.GetDriver("DAQ", daq);
            if (driver != null)
            {
                // 對齊 MockDaq 的 "Sample" 呼叫
                string cmdName = action.Equals("AnalogInWaveForm", StringComparison.OrdinalIgnoreCase) ? "Sample" : action;
                var ticket = new DriverTicket(cmdName, filteredParam);
                driver.PostTicket(ticket);
                var res = ticket.Wait(2000);
                if (!res.Success)
                {
                    string msg = string.IsNullOrEmpty(errorMsg) ? $"{action} failed on '{daq}'" : errorMsg;
                    return CommandResult.Fail(msg, errorLabel);
                }
            }
            return CommandResult.Done();
        }

        // 2. Wait 採樣完成檢查
        if (action.Equals("Wait", StringComparison.OrdinalIgnoreCase))
        {
            string okIo = $"{daq}#OK?";
            bool isOk = ctx.Engines.Io.GetPort(okIo);
            if (isOk)
            {
                return CommandResult.Done();
            }

            double timeoutSec = 5.0;
            if (parsed.TryGetValue("TimeOut", out var tStr) && double.TryParse(tStr, out double t))
            {
                timeoutSec = t;
            }
            long targetTimeoutMs = (long)(timeoutSec * 1000);

            if (ctx.State.Value == 0 && !ctx.State.HasData)
            {
                ctx.State.Value = 1;
                ctx.State["timeoutMs"] = targetTimeoutMs;
                ctx.State["startTicks"] = Environment.TickCount64;
                return CommandResult.Wait();
            }

            long startTicks = ctx.State["startTicks"]?.GetValue<long>() ?? Environment.TickCount64;
            long elapsed = Environment.TickCount64 - startTicks;

            if (elapsed >= targetTimeoutMs)
            {
                string msg = string.IsNullOrEmpty(errorMsg) ? $"DAQ Wait timeout on '{daq}' after {timeoutSec}s" : errorMsg;
                return CommandResult.Fail(msg, errorLabel);
            }

            return CommandResult.Wait();
        }

        // 3. Filter
        if (action.Equals("Filter", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count >= 2 && Enum.TryParse<DaqFilterType>(posArgs[1], true, out var fType))
            {
                double p = posArgs.Count > 2 && double.TryParse(posArgs[2], out double v) ? v : 0;
                ctx.Engines.Daq.ApplyFilter(daq, fType, p);
            }
            return CommandResult.Done();
        }

        // 4. CompareMaxMinInRange
        if (action.Equals("CompareMaxMinInRange", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count >= 3 && double.TryParse(posArgs[1], out double max) && double.TryParse(posArgs[2], out double min))
            {
                ctx.Engines.Daq.CompareMaxMinInRange(daq, max, min);
            }
            return CommandResult.Done();
        }

        // 5. CompareCoverBy
        if (action.Equals("CompareCoverBy", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count >= 3)
            {
                ctx.Engines.Daq.CompareCoverBy(daq, posArgs[1], posArgs[2]);
            }
            return CommandResult.Done();
        }

        return CommandResult.Done();
    }
}
