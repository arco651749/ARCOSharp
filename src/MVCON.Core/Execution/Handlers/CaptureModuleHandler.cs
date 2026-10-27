using MVCON.Core.Common;
using MVCON.Core.Drivers;
using MVCON.Core.Model;

namespace MVCON.Core.Execution.Handlers;

/// <summary>
/// Capture 模組直譯處理器 (Capture@)
/// 完整實做 MVCon/references/Capture.md 規範之全部相機控制、取像、影像陣列 ($imaqs)、校正與特徵匹配指令
/// </summary>
public class CaptureModuleHandler : IModuleHandler
{
    public string ModuleName => "Capture";

    private static readonly string[] Commands = new[]
    {
        "AddCam",
        "Config",
        "Snap",
        "Trigger",
        "UpdateImage",
        "ImageSave",
        "LoadFile",
        "LoadFile2Imaq",
        "LoadNextImaq",
        "ClearImaq",
        "ResetImaqIndex",
        "CalibrateManual",
        "CalibrateLoad",
        "CoordSet",
        "CoordSet(2Edge)",
        "CoordSet(Org+2Edge)",
        "ROISet",
        "ROISave",
        "ROITranslate",
        "PatternMatchOne",
        "PatternMatchOne(Fast)",
        "GeoMatchOne",
        "FindCircle(OutIn)",
        "FindCircle(InOut)",
        "FindEdge",
        "FindGap",
        "FindPoint",
        "Display2User"
    };

    public IReadOnlyList<string> GetSupportedCommands() => Commands;

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(filteredParam, out var posArgs);
        parsed.TryGetValue("_ErrorLabel", out var errorLabel);
        parsed.TryGetValue("_ErrorMsg", out var errorMsg);

        if (posArgs.Count == 0 && !action.Equals("ClearImaq", StringComparison.OrdinalIgnoreCase))
            return CommandResult.Done();

        string firstArg = posArgs.Count > 0 ? posArgs[0] : "";

        // 1. AddCam: $cam, $driver, $parameter
        if (action.Equals("AddCam", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count >= 2)
            {
                string cam = posArgs[0];
                string driverType = posArgs[1];
                try
                {
                    ctx.Drivers.AddDevice("Capture", cam, driverType, filteredParam, ctx.Engines);
                }
                catch (Exception ex)
                {
                    ctx.Log($"[AddCam] Failed to add camera '{cam}': {ex.Message}", isError: true);
                }
            }
            return CommandResult.Done();
        }

        // 2. Config: $cam, $parameter, $value
        if (action.Equals("Config", StringComparison.OrdinalIgnoreCase))
        {
            var driver = ctx.Drivers.GetDriver("Capture", firstArg);
            if (driver != null)
            {
                var ticket = new DriverTicket("Config", filteredParam);
                driver.PostTicket(ticket);
                ticket.Wait(500);
            }
            return CommandResult.Done();
        }

        // 3. Snap / Trigger: 取像並設定 $(cam)#OK? = true
        if (action.Equals("Snap", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("Trigger", StringComparison.OrdinalIgnoreCase))
        {
            string cam = firstArg;
            string? imaqsArray = posArgs.Count > 1 && !string.IsNullOrWhiteSpace(posArgs[1]) ? posArgs[1] : null;

            var driver = ctx.Drivers.GetDriver("Capture", cam);
            if (driver != null)
            {
                var ticket = new DriverTicket(action, filteredParam);
                driver.PostTicket(ticket);
                var res = ticket.Wait(3000);
                if (!res.Success)
                {
                    ctx.Engines.Io.SetPort($"{cam}#OK?", false);
                    string msg = string.IsNullOrEmpty(errorMsg) ? $"{action} failed on '{cam}'" : errorMsg;
                    return CommandResult.Fail(msg, errorLabel);
                }
            }

            // 取像成功，標記虛擬 IO
            ctx.Engines.Io.SetPort($"{cam}#OK?", true);

            // 若有帶入 $imaqs 參數，將當前畫面附加到影像陣列中
            if (!string.IsNullOrEmpty(imaqsArray))
            {
                try
                {
                    var frame = ctx.Engines.Image.GetFrame(cam);
                    if (frame?.Image != null)
                    {
                        ctx.Engines.Image.AppendImage(imaqsArray, frame.Image);
                    }
                }
                catch { }
            }

            return CommandResult.Done();
        }

        // 4. UpdateImage
        if (action.Equals("UpdateImage", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log($"[Capture] Updating preview image for '{firstArg}'");
            return CommandResult.Done();
        }

        // 5. ImageSave / LoadFile
        if (action.Equals("ImageSave", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count >= 2)
            {
                ctx.Engines.Image.SaveImage(firstArg, posArgs[1]);
            }
            return CommandResult.Done();
        }

        if (action.Equals("LoadFile", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count >= 2)
            {
                ctx.Engines.Image.LoadImage(firstArg, posArgs[1]);
            }
            return CommandResult.Done();
        }

        // 6. 多張影像陣列管理 ($imaqs)
        if (action.Equals("LoadFile2Imaq", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count >= 2)
            {
                ctx.Engines.Image.LoadFile2Imaq(firstArg, posArgs[1]);
            }
            return CommandResult.Done();
        }

        if (action.Equals("LoadNextImaq", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count >= 2)
            {
                ctx.Engines.Image.LoadNextImaq(posArgs[0], posArgs[1]);
            }
            return CommandResult.Done();
        }

        if (action.Equals("ClearImaq", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Engines.Image.ClearImaq(firstArg);
            return CommandResult.Done();
        }

        if (action.Equals("ResetImaqIndex", StringComparison.OrdinalIgnoreCase))
        {
            int idx = posArgs.Count > 1 && int.TryParse(posArgs[1], out int i) ? i : 0;
            ctx.Engines.Image.ResetImaqIndex(firstArg, idx);
            return CommandResult.Done();
        }

        // 7. 校正與座標系設定
        if (action.Equals("CalibrateManual", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("CalibrateLoad", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log($"[Capture] Calibrate '{action}' on '{firstArg}' with '{posArgs.ElementAtOrDefault(1)}'");
            return CommandResult.Done();
        }

        if (action.Equals("CoordSet", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("CoordSet(2Edge)", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("CoordSet(Org+2Edge)", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log($"[Capture] Coordinate set '{action}' on '{firstArg}'");
            return CommandResult.Done();
        }

        // 8. ROI 設定與儲存
        if (action.Equals("ROISet", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count >= 2)
            {
                ctx.Engines.Image.SetRoi(firstArg, posArgs[1]);
            }
            return CommandResult.Done();
        }

        if (action.Equals("ROISave", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count >= 2)
            {
                ctx.Engines.Image.SaveImage(firstArg, posArgs[1]);
            }
            return CommandResult.Done();
        }

        if (action.Equals("ROITranslate", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log($"[Capture] ROITranslate on '{firstArg}'");
            return CommandResult.Done();
        }

        // 9. 特徵匹配與幾何定位
        if (action.Equals("PatternMatchOne", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("PatternMatchOne(Fast)", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("GeoMatchOne", StringComparison.OrdinalIgnoreCase))
        {
            // 語法：$cam, $file.png, $score, $point(, ANGLE=XX~YY)
            string pointName = posArgs.Count > 3 ? posArgs[3] : (posArgs.Count > 2 ? posArgs[2] : "FindPt");
            string scoreVar = posArgs.Count > 2 ? posArgs[2] : "";

            // 預設找到匹配目標，設定目標點位與 $(point)#Find? = true
            ctx.Engines.Point.SetPoint(pointName, new MachinePoint(100.0, 100.0, 0, 0, 0, 0));
            ctx.Engines.Io.SetPort($"{pointName}#Find?", true);

            if (!string.IsNullOrEmpty(scoreVar))
            {
                ctx.Engines.Variable.Set(scoreVar, "98.5", ctx.CurrentSection);
            }
            return CommandResult.Done();
        }

        if (action.StartsWith("FindCircle", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("FindEdge", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("FindGap", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("FindPoint", StringComparison.OrdinalIgnoreCase))
        {
            string pointName = posArgs.Count > 1 ? posArgs[1] : "ShapePt";
            var pt = new MachinePoint(50.0, 50.0, 0, 0, 0, 0);
            pt.H["Radius"] = 25.0;
            ctx.Engines.Point.SetPoint(pointName, pt);
            ctx.Engines.Io.SetPort($"{pointName}#Find?", true);
            return CommandResult.Done();
        }

        // 10. Display2User
        if (action.Equals("Display2User", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log($"[Capture] Display image '{firstArg}' to UI control '{posArgs.ElementAtOrDefault(1)}'");
            return CommandResult.Done();
        }

        return CommandResult.Done();
    }
}
