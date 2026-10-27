using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using MVCON.Core.Common;
using MVCON.Core.Drivers;
using MVCON.Core.Execution;
using MVCON.Core.Guidance;
using ExecutionContext = MVCON.Core.Execution.ExecutionContext;

namespace MVCON.Module.IO.Guidance;

/// <summary>
/// IO 指令參數導引與 C# 執行基底類別 (對標 LabVIEW Front Panel + Block Diagram)
/// </summary>
public abstract class BaseIoGuidance : IParameterGuidance
{
    public abstract string CommandKey { get; }

    public async Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
    {
        var livePorts = engines.Io.GetAllPortNames();
        var dialog = new IoGuidanceDialog(CommandKey, currentParam, livePorts);
        if (owner is Window win)
        {
            return await dialog.ShowDialog<string?>(win);
        }
        dialog.Show();
        return null;
    }

    /// <summary>
    /// 輕量指令業務邏輯 (供 GuidanceRunner 測試或簡易呼叫)
    /// </summary>
    public virtual bool Execute(BasicEngines engines, string parameter) => true;

    /// <summary>
    /// 全功能指令業務邏輯 (支援微步狀態機、逾時等待與完整 ExecutionContext)
    /// </summary>
    public virtual CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        bool ok = Execute(ctx.Engines, parameter);
        _sharedVm?.NotifyExecution(action, parameter, ctx.Engines);
        return ok ? CommandResult.Done() : CommandResult.Fail($"Guidance Execute returned false for {action}");
    }

    protected static Views.IoFrontPanelView? _sharedFrontPanel;
    protected static Views.IoFrontPanelViewModel? _sharedVm;

    public static Views.IoFrontPanelView GetSharedFrontPanel(BasicEngines? engines = null)
    {
        if (_sharedFrontPanel == null)
        {
            _sharedVm = new Views.IoFrontPanelViewModel();
            _sharedFrontPanel = new Views.IoFrontPanelView
            {
                DataContext = _sharedVm
            };
        }
        if (engines != null)
        {
            _sharedVm?.RefreshFromEngines(engines);
        }
        return _sharedFrontPanel;
    }

    /// <summary>
    /// 【支柱 3: User Panel / 即時前面板】
    /// 取得 IO 即時配電盤與驅動面板控制項 (供主畫面 SubPanel 嵌入)
    /// </summary>
    public virtual object? GetFrontPanelControl()
    {
        return GetSharedFrontPanel();
    }
}

// 1. IO@SetPort
public class IoSetPortGuidance : BaseIoGuidance
{
    public override string CommandKey => "IO@SetPort";

    public override bool Execute(BasicEngines engines, string parameter)
    {
        var parts = parameter.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        engines.Io.SetPort(parts[0].Trim(), true);
        _sharedVm?.NotifyExecution("SetPort", parameter, engines);
        return true;
    }

    public override CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        if (posArgs.Count == 0) return CommandResult.Done();
        string port = posArgs[0];

        double delaySec = posArgs.Count > 1 && double.TryParse(posArgs[1], out double s) ? s : 0;
        if (delaySec <= 0)
        {
            ctx.Engines.Io.SetPort(port, true);
            _sharedVm?.NotifyExecution("SetPort", parameter, ctx.Engines);
            return CommandResult.Done();
        }

        long targetMs = (long)(delaySec * 1000);
        if (ctx.State.Value == 0 && !ctx.State.HasData)
        {
            ctx.Engines.Io.SetPort(port, true);
            _sharedVm?.NotifyExecution("SetPort", parameter, ctx.Engines);
            ctx.State.Value = 1;
            ctx.State["targetMs"] = targetMs;
            ctx.State["startTicks"] = Environment.TickCount64;
            return CommandResult.Wait();
        }

        long startTicks = ctx.State["startTicks"]?.GetValue<long>() ?? Environment.TickCount64;
        if (Environment.TickCount64 - startTicks >= targetMs)
        {
            ctx.State.Reset();
            return CommandResult.Done();
        }
        return CommandResult.Wait();
    }
}

// 2. IO@ClrPort
public class IoClrPortGuidance : BaseIoGuidance
{
    public override string CommandKey => "IO@ClrPort";

    public override bool Execute(BasicEngines engines, string parameter)
    {
        var parts = parameter.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        engines.Io.SetPort(parts[0].Trim(), false);
        _sharedVm?.NotifyExecution("ClrPort", parameter, engines);
        return true;
    }

    public override CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        if (posArgs.Count == 0) return CommandResult.Done();
        string port = posArgs[0];

        double delaySec = posArgs.Count > 1 && double.TryParse(posArgs[1], out double s) ? s : 0;
        if (delaySec <= 0)
        {
            ctx.Engines.Io.SetPort(port, false);
            _sharedVm?.NotifyExecution("ClrPort", parameter, ctx.Engines);
            return CommandResult.Done();
        }

        long targetMs = (long)(delaySec * 1000);
        if (ctx.State.Value == 0 && !ctx.State.HasData)
        {
            ctx.Engines.Io.SetPort(port, false);
            _sharedVm?.NotifyExecution("ClrPort", parameter, ctx.Engines);
            ctx.State.Value = 1;
            ctx.State["targetMs"] = targetMs;
            ctx.State["startTicks"] = Environment.TickCount64;
            return CommandResult.Wait();
        }

        long startTicks = ctx.State["startTicks"]?.GetValue<long>() ?? Environment.TickCount64;
        if (Environment.TickCount64 - startTicks >= targetMs)
        {
            ctx.State.Reset();
            return CommandResult.Done();
        }
        return CommandResult.Wait();
    }
}

// 3. IO@WaitPortOn
public class IoWaitPortOnGuidance : BaseIoGuidance
{
    public override string CommandKey => "IO@WaitPortOn";

    public override bool Execute(BasicEngines engines, string parameter)
    {
        var parts = parameter.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        return engines.Io.GetPort(parts[0].Trim());
    }

    public override CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        parsed.TryGetValue("_ErrorLabel", out var errorLabel);
        parsed.TryGetValue("_ErrorMsg", out var errorMsg);
        if (posArgs.Count == 0) return CommandResult.Done();
        string port = posArgs[0];

        if (ctx.Engines.Io.GetPort(port))
        {
            ctx.State.Reset();
            return CommandResult.Done();
        }

        double timeoutSec = posArgs.Count > 1 && double.TryParse(posArgs[1], out double s) ? s : 5.0;
        long targetTimeoutMs = (long)(timeoutSec * 1000);

        if (ctx.State.Value == 0 && !ctx.State.HasData)
        {
            ctx.State.Value = 1;
            ctx.State["timeoutMs"] = targetTimeoutMs;
            ctx.State["startTicks"] = Environment.TickCount64;
            return CommandResult.Wait();
        }

        long startTicks = ctx.State["startTicks"]?.GetValue<long>() ?? Environment.TickCount64;
        if (Environment.TickCount64 - startTicks >= targetTimeoutMs)
        {
            ctx.State.Reset();
            string msg = string.IsNullOrEmpty(errorMsg) ? $"WaitPortOn timeout on '{port}' after {timeoutSec}s" : errorMsg;
            return CommandResult.Fail(msg, errorLabel);
        }

        return CommandResult.Wait();
    }
}

// 4. IO@WaitPortOff
public class IoWaitPortOffGuidance : BaseIoGuidance
{
    public override string CommandKey => "IO@WaitPortOff";

    public override bool Execute(BasicEngines engines, string parameter)
    {
        var parts = parameter.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        return !engines.Io.GetPort(parts[0].Trim());
    }

    public override CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        parsed.TryGetValue("_ErrorLabel", out var errorLabel);
        parsed.TryGetValue("_ErrorMsg", out var errorMsg);
        if (posArgs.Count == 0) return CommandResult.Done();
        string port = posArgs[0];

        if (!ctx.Engines.Io.GetPort(port))
        {
            ctx.State.Reset();
            return CommandResult.Done();
        }

        double timeoutSec = posArgs.Count > 1 && double.TryParse(posArgs[1], out double s) ? s : 5.0;
        long targetTimeoutMs = (long)(timeoutSec * 1000);

        if (ctx.State.Value == 0 && !ctx.State.HasData)
        {
            ctx.State.Value = 1;
            ctx.State["timeoutMs"] = targetTimeoutMs;
            ctx.State["startTicks"] = Environment.TickCount64;
            return CommandResult.Wait();
        }

        long startTicks = ctx.State["startTicks"]?.GetValue<long>() ?? Environment.TickCount64;
        if (Environment.TickCount64 - startTicks >= targetTimeoutMs)
        {
            ctx.State.Reset();
            string msg = string.IsNullOrEmpty(errorMsg) ? $"WaitPortOff timeout on '{port}' after {timeoutSec}s" : errorMsg;
            return CommandResult.Fail(msg, errorLabel);
        }

        return CommandResult.Wait();
    }
}

// 5. IO@Flash(OnOff)
public class IoFlashOnOffGuidance : BaseIoGuidance
{
    public override string CommandKey => "IO@Flash(OnOff)";

    public override bool Execute(BasicEngines engines, string parameter)
    {
        var parts = parameter.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        engines.Io.SetPort(parts[0].Trim(), true);
        return true;
    }

    public override CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        if (posArgs.Count < 3) return CommandResult.Done();
        string port = posArgs[0];
        double t1 = double.TryParse(posArgs[1], out double s1) ? s1 : 0.2;
        double t2 = double.TryParse(posArgs[2], out double s2) ? s2 : 0.2;

        long t1Ms = (long)(t1 * 1000);
        long t2Ms = (long)(t2 * 1000);

        if (ctx.State.Value == 0 && !ctx.State.HasData)
        {
            ctx.Engines.Io.SetPort(port, true);
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
                ctx.Engines.Io.SetPort(port, false);
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
}

// 6. IO@Flash(OffOn)
public class IoFlashOffOnGuidance : BaseIoGuidance
{
    public override string CommandKey => "IO@Flash(OffOn)";

    public override bool Execute(BasicEngines engines, string parameter)
    {
        var parts = parameter.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        engines.Io.SetPort(parts[0].Trim(), false);
        return true;
    }

    public override CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        if (posArgs.Count < 3) return CommandResult.Done();
        string port = posArgs[0];
        double t1 = double.TryParse(posArgs[1], out double s1) ? s1 : 0.2;
        double t2 = double.TryParse(posArgs[2], out double s2) ? s2 : 0.2;

        long t1Ms = (long)(t1 * 1000);
        long t2Ms = (long)(t2 * 1000);

        if (ctx.State.Value == 0 && !ctx.State.HasData)
        {
            ctx.Engines.Io.SetPort(port, false);
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
                ctx.Engines.Io.SetPort(port, true);
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
}

// 7. IO@CheckLogicOn
public class IoCheckLogicOnGuidance : BaseIoGuidance
{
    public override string CommandKey => "IO@CheckLogicOn";

    public override bool Execute(BasicEngines engines, string parameter)
    {
        var parts = parameter.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        return LogicEvaluator.EvaluateLogic(parts[0].Trim(), engines.Io, engines.Variable, "");
    }

    public override CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        parsed.TryGetValue("_ErrorLabel", out var errorLabel);
        parsed.TryGetValue("_ErrorMsg", out var errorMsg);
        if (posArgs.Count == 0) return CommandResult.Done();
        string logicExpr = posArgs[0];

        bool eval = LogicEvaluator.EvaluateLogic(logicExpr, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
        if (eval)
        {
            ctx.State.Reset();
            return CommandResult.Done();
        }

        double timeoutSec = posArgs.Count > 1 && double.TryParse(posArgs[1], out double s) ? s : 5.0;
        long targetTimeoutMs = (long)(timeoutSec * 1000);

        if (ctx.State.Value == 0 && !ctx.State.HasData)
        {
            ctx.State.Value = 1;
            ctx.State["timeoutMs"] = targetTimeoutMs;
            ctx.State["startTicks"] = Environment.TickCount64;
            return CommandResult.Wait();
        }

        long startTicks = ctx.State["startTicks"]?.GetValue<long>() ?? Environment.TickCount64;
        if (Environment.TickCount64 - startTicks >= targetTimeoutMs)
        {
            ctx.State.Reset();
            string msg = string.IsNullOrEmpty(errorMsg) ? $"CheckLogicOn timeout on logic '{logicExpr}' after {timeoutSec}s" : errorMsg;
            return CommandResult.Fail(msg, errorLabel);
        }

        return CommandResult.Wait();
    }
}

// 8. IO@CheckLogicOff
public class IoCheckLogicOffGuidance : BaseIoGuidance
{
    public override string CommandKey => "IO@CheckLogicOff";

    public override bool Execute(BasicEngines engines, string parameter)
    {
        var parts = parameter.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        return !LogicEvaluator.EvaluateLogic(parts[0].Trim(), engines.Io, engines.Variable, "");
    }

    public override CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        parsed.TryGetValue("_ErrorLabel", out var errorLabel);
        parsed.TryGetValue("_ErrorMsg", out var errorMsg);
        if (posArgs.Count == 0) return CommandResult.Done();
        string logicExpr = posArgs[0];

        bool eval = LogicEvaluator.EvaluateLogic(logicExpr, ctx.Engines.Io, ctx.Engines.Variable, ctx.CurrentSection);
        if (!eval)
        {
            ctx.State.Reset();
            return CommandResult.Done();
        }

        double timeoutSec = posArgs.Count > 1 && double.TryParse(posArgs[1], out double s) ? s : 5.0;
        long targetTimeoutMs = (long)(timeoutSec * 1000);

        if (ctx.State.Value == 0 && !ctx.State.HasData)
        {
            ctx.State.Value = 1;
            ctx.State["timeoutMs"] = targetTimeoutMs;
            ctx.State["startTicks"] = Environment.TickCount64;
            return CommandResult.Wait();
        }

        long startTicks = ctx.State["startTicks"]?.GetValue<long>() ?? Environment.TickCount64;
        if (Environment.TickCount64 - startTicks >= targetTimeoutMs)
        {
            ctx.State.Reset();
            string msg = string.IsNullOrEmpty(errorMsg) ? $"CheckLogicOff timeout on logic '{logicExpr}' after {timeoutSec}s" : errorMsg;
            return CommandResult.Fail(msg, errorLabel);
        }

        return CommandResult.Wait();
    }
}

// 9. IO@SetPortName
public class IoSetPortNameGuidance : BaseIoGuidance
{
    public override string CommandKey => "IO@SetPortName";

    public override bool Execute(BasicEngines engines, string parameter)
    {
        var parts = parameter.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            engines.Io.SetPortName(parts[0].Trim(), parts[1].Trim());
        }
        return true;
    }

    public override CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        Execute(ctx.Engines, parameter);
        return CommandResult.Done();
    }
}

// 10. IO@AddVirtualIO
public class IoAddVirtualIoGuidance : BaseIoGuidance
{
    public override string CommandKey => "IO@AddVirtualIO";

    public override bool Execute(BasicEngines engines, string parameter)
    {
        var parts = parameter.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            string port = parts[0].Trim();
            string valStr = parts[1].Trim();
            bool val = valStr.Equals("On", StringComparison.OrdinalIgnoreCase) ||
                       valStr.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                       valStr.Equals("True", StringComparison.OrdinalIgnoreCase);
            engines.Io.AddVirtualIO(port, val);
        }
        return true;
    }

    public override CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        Execute(ctx.Engines, parameter);
        return CommandResult.Done();
    }
}

// 11. IO@AddIODriver
public class IoAddIoDriverGuidance : BaseIoGuidance
{
    public override string CommandKey => "IO@AddIODriver";

    public override bool Execute(BasicEngines engines, string parameter) => true;

    public override CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        if (posArgs.Count >= 2)
        {
            string ioName = posArgs[0];
            string driverType = posArgs[1];
            try
            {
                ctx.Drivers.AddDevice("IO", ioName, driverType, parameter, ctx.Engines);
            }
            catch (Exception ex)
            {
                ctx.Log($"[AddIODriver] Failed to add driver '{driverType}' for '{ioName}': {ex.Message}", isError: true);
            }
        }
        return CommandResult.Done();
    }
}

// 12. IO@InitIOPort
public class IoInitIoPortGuidance : BaseIoGuidance
{
    public override string CommandKey => "IO@InitIOPort";

    public override bool Execute(BasicEngines engines, string parameter) => true;

    public override CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
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
}

// 13. IO@ForceUpdate
public class IoForceUpdateGuidance : BaseIoGuidance
{
    public override string CommandKey => "IO@ForceUpdate";

    public override bool Execute(BasicEngines engines, string parameter) => true;

    public override CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
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
}

// 14. IO@Shift
public class IoShiftGuidance : BaseIoGuidance
{
    public override string CommandKey => "IO@Shift";

    public override bool Execute(BasicEngines engines, string parameter)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
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
            engines.Io.Shift(ioList, shiftAmount);
        }
        return true;
    }

    public override CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        Execute(ctx.Engines, parameter);
        return CommandResult.Done();
    }
}
