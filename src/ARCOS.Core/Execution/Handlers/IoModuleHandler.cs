using ARCOS.Core.Common;
using ARCOS.Core.Drivers;

namespace ARCOS.Core.Execution.Handlers;

/// <summary>
/// IO 模組直譯處理器 (IO@)
/// 完整實做 ARCOS/references/IO.md 規範之全部 IO 控制、等待、閃爍與驅動註冊指令
/// </summary>
public class IoModuleHandler : IModuleHandler
{
    public string ModuleName => "IO";

    private static readonly string[] Commands = new[]
    {
        "SetPort",
        "ClrPort",
        "Flash(OnOff)",
        "Flash(OffOn)",
        "WaitPortOn",
        "WaitPortOff",
        "CheckLogicOn",
        "CheckLogicOff",
        "SetPortName",
        "AddVirtualIO",
        "AddIODriver",
        "InitIOPort",
        "ForceUpdate",
        "Shift"
    };

    public virtual IReadOnlyList<string> GetSupportedCommands() => Commands;

    public virtual CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(filteredParam, out var posArgs);
        parsed.TryGetValue("_ErrorLabel", out var errorLabel);
        parsed.TryGetValue("_ErrorMsg", out var errorMsg);

        // 1. SetPort / ClrPort (支援選填 $second 延遲)
        if (action.Equals("SetPort", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("ClrPort", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string port = posArgs[0];
            bool val = action.Equals("SetPort", StringComparison.OrdinalIgnoreCase);

            double delaySec = posArgs.Count > 1 && double.TryParse(posArgs[1], out double s) ? s : 0;
            if (delaySec <= 0)
            {
                ctx.Engines.Io.SetPort(port, val);
                return CommandResult.Done();
            }

            long targetMs = (long)(delaySec * 1000);
            if (ctx.State.Value == 0 && !ctx.State.HasData)
            {
                ctx.Engines.Io.SetPort(port, val);
                ctx.State.Value = 1;
                ctx.State["targetMs"] = targetMs;
                ctx.State["startTicks"] = Environment.TickCount64;
                return CommandResult.Wait();
            }

            long startTicks = ctx.State["startTicks"]?.GetValue<long>() ?? Environment.TickCount64;
            long elapsed = Environment.TickCount64 - startTicks;
            if (elapsed >= targetMs)
            {
                ctx.State.Reset();
                return CommandResult.Done();
            }
            return CommandResult.Wait();
        }

        // 2. Flash(OnOff) / Flash(OffOn) 雙階閃爍控制
        if (action.Equals("Flash(OnOff)", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("Flash(OffOn)", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count < 3) return CommandResult.Done();
            string port = posArgs[0];
            double t1 = double.TryParse(posArgs[1], out double s1) ? s1 : 0.2;
            double t2 = double.TryParse(posArgs[2], out double s2) ? s2 : 0.2;

            bool isOnOff = action.StartsWith("Flash(OnOff", StringComparison.OrdinalIgnoreCase);
            bool initialVal = isOnOff;
            bool secondVal = !initialVal;

            long t1Ms = (long)(t1 * 1000);
            long t2Ms = (long)(t2 * 1000);

            if (ctx.State.Value == 0 && !ctx.State.HasData)
            {
                ctx.Engines.Io.SetPort(port, initialVal);
                ctx.State.Value = 1;
                ctx.State["phase"] = 1;
                ctx.State["startTicks"] = Environment.TickCount64;
                ctx.State["durationMs"] = t1Ms;
                return CommandResult.Wait();
            }

            int phase = ctx.State["phase"]?.GetValue<int>() ?? 1;
            long startTicks = ctx.State["startTicks"]?.GetValue<long>() ?? Environment.TickCount64;
            long durMs = ctx.State["durationMs"]?.GetValue<long>() ?? 0;
            long elapsed = Environment.TickCount64 - startTicks;

            if (phase == 1)
            {
                if (elapsed >= durMs)
                {
                    ctx.Engines.Io.SetPort(port, secondVal);
                    ctx.State["phase"] = 2;
                    ctx.State["startTicks"] = Environment.TickCount64;
                    ctx.State["durationMs"] = t2Ms;
                }
                return CommandResult.Wait();
            }
            else if (phase == 2)
            {
                if (elapsed >= durMs)
                {
                    ctx.State.Reset();
                    return CommandResult.Done();
                }
                return CommandResult.Wait();
            }

            return CommandResult.Done();
        }

        // 3. WaitPortOn / WaitPortOff (非阻塞微步等待)
        if (action.Equals("WaitPortOn", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("WaitPortOff", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string port = posArgs[0];
            bool targetVal = action.Equals("WaitPortOn", StringComparison.OrdinalIgnoreCase);

            bool currentVal = ctx.Engines.Io.GetPort(port);
            if (currentVal == targetVal)
            {
                ctx.State.Reset();
                return CommandResult.Done();
            }

            double timeoutSec = 5.0;
            if (posArgs.Count > 1 && double.TryParse(posArgs[1], out double s))
            {
                timeoutSec = s;
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
                ctx.State.Reset();
                string msg = string.IsNullOrEmpty(errorMsg) ? $"{action} timeout on '{port}' after {timeoutSec}s" : errorMsg;
                return CommandResult.Fail(msg, errorLabel);
            }

            return CommandResult.Wait();
        }

        // 4. CheckLogicOn / CheckLogicOff
        if (action.Equals("CheckLogicOn", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("CheckLogicOff", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string logicExpr = posArgs[0];
            bool targetState = action.Equals("CheckLogicOn", StringComparison.OrdinalIgnoreCase);

            bool eval = LogicEvaluator.EvaluateLogic(logicExpr, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
            if (eval == targetState)
            {
                ctx.State.Reset();
                return CommandResult.Done();
            }

            double timeoutSec = 5.0;
            if (posArgs.Count > 1 && double.TryParse(posArgs[1], out double s))
            {
                timeoutSec = s;
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
                ctx.State.Reset();
                string msg = string.IsNullOrEmpty(errorMsg) ? $"{action} timeout on logic '{logicExpr}' after {timeoutSec}s" : errorMsg;
                return CommandResult.Fail(msg, errorLabel);
            }

            return CommandResult.Wait();
        }

        // 5. SetPortName
        if (action.Equals("SetPortName", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count >= 2)
            {
                ctx.Engines.Io.SetPortName(posArgs[0], posArgs[1]);
            }
            return CommandResult.Done();
        }

        // 6. AddVirtualIO
        if (action.Equals("AddVirtualIO", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count >= 2)
            {
                string port = posArgs[0];
                string valStr = posArgs[1];
                bool val = valStr.Equals("On", StringComparison.OrdinalIgnoreCase) ||
                           valStr.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                           valStr.Equals("True", StringComparison.OrdinalIgnoreCase);
                ctx.Engines.Io.AddVirtualIO(port, val);
            }
            return CommandResult.Done();
        }

        // 7. AddIODriver
        if (action.Equals("AddIODriver", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count >= 1)
            {
                string ioName = posArgs[0];
                string driverType = posArgs.Count >= 2 ? posArgs[1] : (parsed.TryGetValue("Driver", out var dt) ? dt : "MockIO");
                try
                {
                    ctx.Drivers.AddDevice("IO", ioName, driverType, filteredParam, ctx.Engines);
                }
                catch (Exception ex)
                {
                    ctx.Log($"[AddIODriver] Failed to add driver '{driverType}' for '{ioName}': {ex.Message}", isError: true);
                }
            }
            return CommandResult.Done();
        }

        // 8. InitIOPort / ForceUpdate
        if (action.Equals("InitIOPort", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("ForceUpdate", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count > 0)
            {
                var driver = ctx.Drivers.GetDriver("IO", posArgs[0]);
                if (driver != null)
                {
                    var ticket = new DriverTicket(action);
                    driver.PostTicket(ticket);
                    ticket.Wait(500);
                }
            }
            return CommandResult.Done();
        }

        // 9. Shift
        if (action.Equals("Shift", StringComparison.OrdinalIgnoreCase))
        {
            int shiftAmount = 1;
            if (parsed.TryGetValue("Shift", out var sStr) && int.TryParse(sStr, out int s))
            {
                shiftAmount = s;
            }

            var ioList = new List<string>();
            foreach (var arg in posArgs)
            {
                if (!arg.StartsWith("Shift=", StringComparison.OrdinalIgnoreCase))
                {
                    ioList.Add(arg);
                }
            }

            if (ioList.Count > 0)
            {
                ctx.Engines.Io.Shift(ioList, shiftAmount);
            }
            return CommandResult.Done();
        }

        return CommandResult.Done();
    }
}
