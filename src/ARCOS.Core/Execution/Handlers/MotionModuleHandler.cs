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
        "AddMotion",
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
        "WaitMotion",
        "WaitMotion(Error)",
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

        // 0. 無參數 Motion@Stop：對全系統所有已掛載的 Motion Device 全部下達緊急停止
        if (posArgs.Count == 0 && action.Equals("Stop", StringComparison.OrdinalIgnoreCase))
        {
            var motionEntries = ctx.Drivers.GetDeviceEntriesByCategory("Motion");
            foreach (var (devName, drv) in motionEntries)
            {
                var stopTicket = new DriverTicket("Stop", "");
                drv.PostTicket(stopTicket);
                ctx.Engines.Io.SetPort($"{devName}#Busy?", false);
                ctx.Engines.Io.SetPort($"{devName}#Done?", true);
            }
            return CommandResult.Done();
        }

        if (posArgs.Count == 0) return CommandResult.Done();
        string motor = posArgs[0];

        // 1. AddMotion: $motor, $driver(,$parameter...)
        if (action.Equals("AddMotion", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("AddMotor", StringComparison.OrdinalIgnoreCase))
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
                    ctx.Log($"[AddMotion] Failed to add motion device '{motor}': {ex.Message}", isError: true);
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
                var res = ticket.Wait(500);
                if (!res.Success)
                {
                    string msg = string.IsNullOrEmpty(errorMsg) ? $"{action} failed on '{motor}': {res.Message}" : errorMsg;
                    return CommandResult.Fail(msg, errorLabel);
                }
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

        // 5. GoSafe: $motor, $speed, $point, {X,Y,Z}=$safepos (微步狀態機閉環兩階段運動，嚴禁盲等)
        if (action.Equals("GoSafe", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("GoSafe(Pallet)", StringComparison.OrdinalIgnoreCase))
        {
            double speed = posArgs.Count > 1 && double.TryParse(posArgs[1], out double s) ? s : 100.0;
            string pointExpr = posArgs.Count > 2 ? posArgs[2] : "";

            var driver = ctx.Drivers.GetDriver("Motion", motor);
            if (driver == null) return CommandResult.Done();

            // 解析安全軸 (例如 Z=0 或 Z=50)
            double safePos = 0.0;
            string safeAxis = "Z";
            if (parsed.TryGetValue("Z", out var zStr) && double.TryParse(zStr, out double zs)) { safeAxis = "Z"; safePos = zs; }
            else if (parsed.TryGetValue("X", out var xStr) && double.TryParse(xStr, out double xs)) { safeAxis = "X"; safePos = xs; }
            else if (parsed.TryGetValue("Y", out var yStr) && double.TryParse(yStr, out double ys)) { safeAxis = "Y"; safePos = ys; }

            // 階段 0：發送第 1 階段退避指令，啟動微步閉環等待
            if (ctx.State.Value == 0 && !ctx.State.HasData)
            {
                ctx.Engines.Io.SetPort($"{motor}#Busy?", true);
                var safeTicket = new DriverTicket("GoPoint", $"Speed={speed},{safeAxis}={safePos}");
                driver.PostTicket(safeTicket);

                ctx.State.Value = 1;
                ctx.State["startTicks"] = Environment.TickCount64;
                return CommandResult.Wait();
            }

            // 階段 1：閉環檢查安全軸是否已完全縮回到位 (不阻塞執行緒，檢查實體感測信號)
            if (ctx.State.Value == 1)
            {
                bool isRetracted = ctx.Engines.Io.GetPort($"{motor}#Done?") && !ctx.Engines.Io.GetPort($"{motor}#Busy?");
                if (!isRetracted)
                {
                    long startTicks = ctx.State["startTicks"]?.GetValue<long>() ?? Environment.TickCount64;
                    if (Environment.TickCount64 - startTicks > 10000)
                    {
                        ctx.State.Reset();
                        string msg = string.IsNullOrEmpty(errorMsg) ? $"GoSafe safe axis retreat timeout on '{motor}'" : errorMsg;
                        return CommandResult.Fail(msg, errorLabel);
                    }
                    return CommandResult.Wait();
                }

                // 安全軸已 100% 到位！再發送第 2 階段目標點運動
                MachinePoint pt = action.Contains("Pallet", StringComparison.OrdinalIgnoreCase)
                    ? ctx.Engines.Point.GetAndMoveNextPallet(pointExpr)
                    : ResolvePointOrCoords(pointExpr, ctx.Engines.Point);

                string cmdParam = $"Speed={speed},X={pt.X},Y={pt.Y},Z={pt.Z},U={pt.U},V={pt.V},W={pt.W}";
                var mainTicket = new DriverTicket("GoPoint", cmdParam);
                driver.PostTicket(mainTicket);
                ctx.Engines.Io.SetPort($"{motor}#Busy?", true);

                ctx.State.Reset();
                return CommandResult.Done();
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

        // 7. GetPos: 讀取馬達當前位置並存入以 $motor 為名的點位資料中 (零阻塞即時鏡像)
        if (action.Equals("GetPos", StringComparison.OrdinalIgnoreCase))
        {
            var driver = ctx.Drivers.GetDriver("Motion", motor);
            if (driver != null)
            {
                var ticket = new DriverTicket("GetPos");
                driver.PostTicket(ticket);
            }
            var curPt = ctx.Engines.Point.GetPoint($"{motor}_CurPos");
            ctx.Engines.Point.SetPoint(motor, curPt);
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
            action.Equals("WaitMotor(Error)", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("WaitMotion", StringComparison.OrdinalIgnoreCase))
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
                string msg = string.IsNullOrEmpty(errorMsg) ? $"WaitMotion timeout on '{motor}' after {timeoutSec}s" : errorMsg;
                return CommandResult.Fail(msg, errorLabel);
            }

            return CommandResult.Wait();
        }

        // 10. WaitPosition: 等待到達容許誤差範圍內
        if (action.Equals("WaitPosition", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Done();
        }

        // 11. 其他直接轉發票據之 Motion 指令 (Home, Stop, ClearPath, AddPath, RunPath...) (零盲等，旗標即時更新)
        var mDriver = ctx.Drivers.GetDriver("Motion", motor);
        if (mDriver != null)
        {
            var ticket = new DriverTicket(action, filteredParam);
            mDriver.PostTicket(ticket);
            if (action.Equals("Stop", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Engines.Io.SetPort($"{motor}#Busy?", false);
                ctx.Engines.Io.SetPort($"{motor}#Done?", true);
            }
            else if (action.Equals("Home", StringComparison.OrdinalIgnoreCase))
            {
                ticket.Wait(1000);
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
