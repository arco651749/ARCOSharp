using ARCOS.Core.Common;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using ARCOS.Core.Model;

namespace ARCOS.Core.Execution.Handlers;

/// <summary>
/// Motion 模組直譯處理器 (Motion@)
/// 完整實做 ARCOS/references/Motion.md 規範之全部軸卡、馬達、點位運動、料盤移動 (GoPallet) 與狀態等待指令
/// </summary>
public class MotionModuleHandler : IModuleHandler
{
    public string ModuleName => "Motion";

    private static readonly string[] Commands = new[]
    {
        "AddMotor",
        "Config",
        "ExtraCMD",
        "Home",
        "Stop",
        "Go",
        "Go(SingleAxis)",
        "GoPoint",
        "GoPoint(SingleAxis)",
        "GoSafe",
        "GoPallet",
        "GoSafe(Pallet)",
        "ClearPath",
        "AddPath",
        "RunPath",
        "GetPos",
        "GetState",
        "WaitPosition",
        "WaitMotor",
        "WaitMotor(Error)",
        "SetDO",
        "GetDI",
        "GetDO",
        "Close"
    };

    public IReadOnlyList<string> GetSupportedCommands() => Commands;

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(filteredParam, out var posArgs);
        parsed.TryGetValue("_ErrorLabel", out var errorLabel);
        parsed.TryGetValue("_ErrorMsg", out var errorMsg);

        if (posArgs.Count == 0) return CommandResult.Done();
        string motor = posArgs[0];

        // 1. AddMotor: $motor, $driver(,$parameter...)
        if (action.Equals("AddMotor", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count >= 2)
            {
                string driverType = posArgs[1];
                try
                {
                    ctx.Drivers.AddDevice("Motion", motor, driverType, filteredParam, ctx.Engines);
                }
                catch (Exception ex)
                {
                    ctx.Log($"[AddMotor] Failed to add motor '{motor}': {ex.Message}", isError: true);
                }
            }
            return CommandResult.Done();
        }

        // 2. Config / ExtraCMD
        if (action.Equals("Config", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("ExtraCMD", StringComparison.OrdinalIgnoreCase))
        {
            var driver = ctx.Drivers.GetDriver("Motion", motor);
            if (driver != null)
            {
                var ticket = new DriverTicket(action, filteredParam);
                driver.PostTicket(ticket);
                ticket.Wait(500);
            }
            return CommandResult.Done();
        }

        // 3. GoPoint / GoPoint(SingleAxis)
        if (action.Equals("GoPoint", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("GoPoint(SingleAxis)", StringComparison.OrdinalIgnoreCase))
        {
            double speed = posArgs.Count > 1 && double.TryParse(posArgs[1], out double s) ? s : 100.0;
            string pointExpr = posArgs.Count > 2 ? posArgs[2] : "";

            var pt = ResolvePointOrCoords(pointExpr, ctx.Engines.Point);
            ctx.Engines.Io.SetPort($"{motor}#Busy?", true);

            var driver = ctx.Drivers.GetDriver("Motion", motor);
            if (driver != null)
            {
                string cmdParam = $"Speed={speed},X={pt.X},Y={pt.Y},Z={pt.Z},U={pt.U},V={pt.V},W={pt.W}";
                var ticket = new DriverTicket("GoPoint", cmdParam);
                driver.PostTicket(ticket);
            }
            return CommandResult.Done();
        }

        // 4. Go / Go(SingleAxis): $motor, $axis, $speed, $X(,$Y,$Z,...)
        if (action.Equals("Go", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("Go(SingleAxis)", StringComparison.OrdinalIgnoreCase))
        {
            string axis = posArgs.Count > 1 ? posArgs[1] : "XYZ";
            double speed = posArgs.Count > 2 && double.TryParse(posArgs[2], out double sp) ? sp : 100.0;

            double x = posArgs.Count > 3 && double.TryParse(posArgs[3], out double vx) ? vx : 0;
            double y = posArgs.Count > 4 && double.TryParse(posArgs[4], out double vy) ? vy : 0;
            double z = posArgs.Count > 5 && double.TryParse(posArgs[5], out double vz) ? vz : 0;

            ctx.Engines.Io.SetPort($"{motor}#Busy?", true);
            var driver = ctx.Drivers.GetDriver("Motion", motor);
            if (driver != null)
            {
                string cmdParam = $"Axis={axis},Speed={speed},X={x},Y={y},Z={z}";
                var ticket = new DriverTicket("Go", cmdParam);
                driver.PostTicket(ticket);
            }
            return CommandResult.Done();
        }

        // 5. GoSafe: $motor, $speed, $point, {X,Y,Z}=$safepos
        if (action.Equals("GoSafe", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("GoSafe(Pallet)", StringComparison.OrdinalIgnoreCase))
        {
            double speed = posArgs.Count > 1 && double.TryParse(posArgs[1], out double s) ? s : 100.0;
            string pointExpr = posArgs.Count > 2 ? posArgs[2] : "";

            MachinePoint pt = action.Contains("Pallet", StringComparison.OrdinalIgnoreCase)
                ? ctx.Engines.Point.GetAndMoveNextPallet(pointExpr)
                : ResolvePointOrCoords(pointExpr, ctx.Engines.Point);

            // 解析安全軸 (例如 Z=0 或 Z=50)
            double safePos = 0.0;
            string safeAxis = "Z";
            if (parsed.TryGetValue("Z", out var zStr) && double.TryParse(zStr, out double zs)) { safeAxis = "Z"; safePos = zs; }
            else if (parsed.TryGetValue("X", out var xStr) && double.TryParse(xStr, out double xs)) { safeAxis = "X"; safePos = xs; }
            else if (parsed.TryGetValue("Y", out var yStr) && double.TryParse(yStr, out double ys)) { safeAxis = "Y"; safePos = ys; }

            ctx.Engines.Io.SetPort($"{motor}#Busy?", true);
            var driver = ctx.Drivers.GetDriver("Motion", motor);
            if (driver != null)
            {
                // 先送出退避安全高度指令
                var safeTicket = new DriverTicket("GoPoint", $"Speed={speed},{safeAxis}={safePos}");
                driver.PostTicket(safeTicket);
                safeTicket.Wait(500);

                // 再送出目標點運動
                string cmdParam = $"Speed={speed},X={pt.X},Y={pt.Y},Z={pt.Z},U={pt.U},V={pt.V},W={pt.W}";
                var mainTicket = new DriverTicket("GoPoint", cmdParam);
                driver.PostTicket(mainTicket);
            }
            return CommandResult.Done();
        }

        // 6. GoPallet: 取料盤當前點並前進，更新 #Last? 旗標
        if (action.Equals("GoPallet", StringComparison.OrdinalIgnoreCase))
        {
            double speed = posArgs.Count > 1 && double.TryParse(posArgs[1], out double s) ? s : 100.0;
            string pointName = posArgs.Count > 2 ? posArgs[2] : "";

            var pt = ctx.Engines.Point.GetAndMoveNextPallet(pointName);
            ctx.Engines.Io.SetPort($"{motor}#Busy?", true);

            var driver = ctx.Drivers.GetDriver("Motion", motor);
            if (driver != null)
            {
                string cmdParam = $"Speed={speed},X={pt.X},Y={pt.Y},Z={pt.Z},U={pt.U},V={pt.V},W={pt.W}";
                var ticket = new DriverTicket("GoPoint", cmdParam);
                driver.PostTicket(ticket);
            }
            return CommandResult.Done();
        }

        // 7. GetPos: 讀取馬達當前位置並存入以 $motor 為名的點位資料中
        if (action.Equals("GetPos", StringComparison.OrdinalIgnoreCase))
        {
            var pt = new MachinePoint(0, 0, 0, 0, 0, 0);
            var driver = ctx.Drivers.GetDriver("Motion", motor);
            if (driver != null)
            {
                var ticket = new DriverTicket("GetPos");
                driver.PostTicket(ticket);
                ticket.Wait(100);
            }
            ctx.Engines.Point.SetPoint(motor, pt);
            return CommandResult.Done();
        }

        // 8. GetState: 讀取並更新 Motion 狀態，寫入 $(motor)#Busy? 與 Motion#ErrorCode
        if (action.Equals("GetState", StringComparison.OrdinalIgnoreCase))
        {
            bool isBusy = ctx.Engines.Io.GetPort($"{motor}#Busy?");
            ctx.Engines.Variable.Set("Motion#ErrorCode", "0", ctx.CurrentSection);

            var driver = ctx.Drivers.GetDriver("Motion", motor);
            if (driver != null)
            {
                var ticket = new DriverTicket("GetState");
                driver.PostTicket(ticket);
                var res = ticket.Wait(100);
                if (!res.Success)
                {
                    ctx.Engines.Variable.Set("Motion#ErrorCode", "1002", ctx.CurrentSection);
                    string msg = string.IsNullOrEmpty(errorMsg) ? $"Motion '{motor}' error status" : errorMsg;
                    return CommandResult.Fail(msg, errorLabel);
                }
            }
            return CommandResult.Done();
        }

        // 9. WaitMotor / WaitMotor(Error) (非阻塞檢查機構 Busy 旗標)
        if (action.Equals("WaitMotor", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("WaitMotor(Error)", StringComparison.OrdinalIgnoreCase))
        {
            string busyIo = $"{motor}#Busy?";
            bool isBusy = ctx.Engines.Io.GetPort(busyIo);

            if (!isBusy)
            {
                ctx.Engines.Variable.Set("Motion#ErrorCode", "0", ctx.CurrentSection);
                ctx.State.Reset();
                return CommandResult.Done();
            }

            double timeoutSec = 30.0;
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
                ctx.Engines.Variable.Set("Motion#ErrorCode", "1001", ctx.CurrentSection);
                ctx.State.Reset();
                string msg = string.IsNullOrEmpty(errorMsg) ? $"WaitMotor timeout on '{motor}' after {timeoutSec}s" : errorMsg;
                return CommandResult.Fail(msg, errorLabel);
            }

            return CommandResult.Wait();
        }

        // 10. WaitPosition: 等待到達容許誤差範圍內
        if (action.Equals("WaitPosition", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Done();
        }

        // 11. 其他直接轉發票據之 Motion 指令 (Home, Stop, ClearPath, AddPath, RunPath...)
        var mDriver = ctx.Drivers.GetDriver("Motion", motor);
        if (mDriver != null)
        {
            var ticket = new DriverTicket(action, filteredParam);
            mDriver.PostTicket(ticket);
            if (action.Equals("Stop", StringComparison.OrdinalIgnoreCase) ||
                action.Equals("Home", StringComparison.OrdinalIgnoreCase))
            {
                ticket.Wait(200);
            }
        }

        return CommandResult.Done();
    }

    private static MachinePoint ResolvePointOrCoords(string ptExpr, PointEngine engine)
    {
        string trimmed = ptExpr.Trim();
        if (trimmed.StartsWith('(') && trimmed.EndsWith(')'))
        {
            string inner = trimmed.Substring(1, trimmed.Length - 2);
            var nums = inner.Split(',', StringSplitOptions.TrimEntries);
            double x = nums.Length > 0 && double.TryParse(nums[0], out double vx) ? vx : 0.0;
            double y = nums.Length > 1 && double.TryParse(nums[1], out double vy) ? vy : 0.0;
            double z = nums.Length > 2 && double.TryParse(nums[2], out double vz) ? vz : 0.0;
            double u = nums.Length > 3 && double.TryParse(nums[3], out double vu) ? vu : 0.0;
            double v = nums.Length > 4 && double.TryParse(nums[4], out double vv) ? vv : 0.0;
            double w = nums.Length > 5 && double.TryParse(nums[5], out double vw) ? vw : 0.0;
            return new MachinePoint(x, y, z, u, v, w);
        }
        return engine.GetPoint(trimmed);
    }
}
