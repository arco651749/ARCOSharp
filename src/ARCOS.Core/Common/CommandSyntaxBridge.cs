using System;
using System.Collections.Generic;
using System.Linq;
using ARCOS.Core.Execution;
using ARCOS.Core.Guidance;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Core.Common;

/// <summary>
/// ARCOS 指令語法 v2.0 與既有 66 條先導指令（IO / SYSTEM / THREAD）之間的雙向無損橋接器：
/// 1. NormalizeForExecution：將 v2.0 語法精準轉為既有執行引擎參數格式（若輸入已是舊格式則原樣透傳，100% 零回歸）
/// 2. FormatToV2：將舊式或導引視窗輸出參數統一升級為 v2.0 標準三段式格式（:: 必填 {...} 選填 -> 輸出）
/// 3. ApplyOutputBinding：將 -> 區段之顯式輸出目標寫回 VariableEngine
/// </summary>
public static class CommandSyntaxBridge
{
    /// <summary>
    /// 將執行期參數字串（可能是 v2.0 或舊版格式）正規化為指令內部邏輯可直接使用之格式。
    /// 若輸入已是舊版格式（未含 v2.0 區塊或 Schema 具名鍵），直接原樣回傳。
    /// </summary>
    public static string NormalizeForExecution(string commandKey, string? rawParam)
    {
        if (string.IsNullOrWhiteSpace(rawParam)) return rawParam ?? string.Empty;

        var schema = StandardCommandSchemas.Get(commandKey);
        if (schema == null) return rawParam;

        var parsed = CommandParamParser.Parse(rawParam, schema);
        if (parsed.HasSyntaxError) return rawParam;

        // 判斷是否為 v2.0 語法：
        // - 對含舊式 Dest=... 賦值語法之指令，須含 -> / {...} 或明確出現專屬輸入鍵（防誤判 Dest=Expr）
        // - 對 Python@Script，舊式原生腳本內常含 '='（如 x = 1），須明確含 code=/script= 才視為 v2.0
        string key = schema.CommandKey;
        bool isLegacyAssignCommand = key is
            "Variable@Calculate" or "Variable@Select" or "Variable@GetPointElement" or
            "Point&Path@Calculate" or "Point&Path@Select" or "Point&Path@InsertOnePoint" or
            "DAQ@Calculate";

        bool isV2;
        if (key == "Python@Script")
        {
            isV2 = parsed.PositionalArgs.Count == 0 &&
                   (parsed.RequiredArgs.ContainsKey("code") || parsed.RequiredArgs.ContainsKey("script"));
        }
        else if (isLegacyAssignCommand)
        {
            isV2 = parsed.HasOutputBlock || parsed.HasOptionalBlock ||
                   (parsed.PositionalArgs.Count == 0 && parsed.RequiredArgs.Count >= 2 &&
                    parsed.RequiredArgs.Keys.All(k => schema.FindParam(k) != null));
        }
        else
        {
            isV2 = parsed.HasOptionalBlock || parsed.HasOutputBlock ||
                   (parsed.PositionalArgs.Count == 0 && parsed.RequiredArgs.Count > 0 &&
                    parsed.RequiredArgs.Keys.Any(k => schema.FindParam(k) != null));
        }

        if (!isV2)
        {
            return rawParam;
        }

        string tail = FormatErrorTrap(parsed.ErrorLabel, parsed.ErrorMessage);

        return key switch
        {
            // ======================== IO (14) ========================
            "IO@AddIODriver" => BuildAddIoDriverLegacy(parsed),
            "IO@InitIOPort" => parsed.Get("card"),
            "IO@ForceUpdate" => parsed.Get("card"),
            "IO@SetPortName" => JoinNonEmpty(", ", parsed.Get("io"), parsed.Get("alias")),
            "IO@AddVirtualIO" => $"{parsed.Get("io")}, {parsed.Get("logic", "False")}",
            "IO@SetPort" or "IO@ClrPort" => BuildSetClrPortLegacy(parsed),
            "IO@Flash(OnOff)" => JoinNonEmpty(", ", parsed.Get("io"), parsed.Get("onSec"), parsed.Get("offSec")),
            "IO@Flash(OffOn)" => JoinNonEmpty(", ", parsed.Get("io"), parsed.Get("offSec"), parsed.Get("onSec")),
            "IO@Shift" => BuildIoShiftLegacy(parsed),
            "IO@WaitPortOn" or "IO@WaitPortOff" => BuildWaitLegacy(parsed.Get("io"), parsed, tail),
            "IO@CheckLogicOn" or "IO@CheckLogicOff" => BuildWaitLegacy(parsed.Get("logic"), parsed, tail),

            // ======================== SYSTEM (21) ========================
            "System@Delay" => parsed.Get("sec"),
            "System@Log Message" => parsed.Get("msg"),
            "System@Log Error" => parsed.Get("msg"),
            "System@Clear Message" => string.Empty,
            "System@Clear Error" => string.Empty,
            "System@Note" => parsed.Get("text"),
            "System@MessageBox" => parsed.Get("msg"),
            "System@WarningBox" => parsed.TryGet("logic", out var wbCond) && !string.IsNullOrWhiteSpace(wbCond)
                ? $"{wbCond}, {parsed.Get("msg")}"
                : parsed.Get("msg"),
            "System@Pause" or "System@Resume" => BuildPauseResumeLegacy(parsed),
            "System@TimerReset" => parsed.TryGet("timeoutSec", out var tSec) && !string.IsNullOrWhiteSpace(tSec)
                ? $"{parsed.Get("timer")}, {tSec}"
                : parsed.Get("timer"),
            "System@TimerCheck" => parsed.Get("timer"),
            "System@CounterReset" => parsed.TryGet("target", out var cTarget) && !string.IsNullOrWhiteSpace(cTarget)
                ? $"{parsed.Get("counter")}, {cTarget}"
                : parsed.Get("counter"),
            "System@CounterCheck" => parsed.Get("counter"),
            "System@Record" => parsed.TryGet("msg", out var recMsg) && !string.IsNullOrWhiteSpace(recMsg)
                ? $"{parsed.Get("rec")}, {recMsg}"
                : parsed.Get("rec"),
            "System@LogRecordCycleTime" => parsed.TryGet("prefix", out var ctPre) && !string.IsNullOrWhiteSpace(ctPre)
                ? $"{parsed.Get("rec")}, {ctPre}"
                : parsed.Get("rec"),
            "System@ProgramLoad" => parsed.Get("path"),
            "System@ProgramSection" => parsed.TryGet("title", out var psTitle) && !string.IsNullOrWhiteSpace(psTitle)
                ? $"{parsed.Get("section")}, {psTitle}"
                : parsed.Get("section"),
            "System@IncludeModule" => parsed.Get("module"),
            "System@UserPanelOpen" => BuildUserPanelOpenLegacy(parsed),
            "System@UserPanelClose" => parsed.Get("panel"),

            // ======================== THREAD (31) ========================
            "Thread@If" or "Thread@ElseIf" => parsed.Get("logic"),
            "Thread@Else" or "Thread@EndIf" or "Thread@Return" => string.Empty,
            "Thread@ForeachStart" => parsed.Get("label"),
            "Thread@ForeachLoop" => BuildForeachLoopLegacy(parsed),
            "Thread@ForeachBreak" or "Thread@ForeachContinue" => parsed.Get("logic"),
            "Thread@Call" => BuildWithOptionalCondition(parsed.Get("prog"), parsed),
            "Thread@Run" => BuildThreadRunLegacy(parsed),
            "Thread@Wait" => string.Join(", ", parsed.GetList("threads")),
            "Thread@Suspend" or "Thread@Resume" => BuildWithOptionalCondition(parsed.Get("thread"), parsed),
            "Thread@Stop" => BuildThreadStopLegacy(parsed),
            "Thread@SubThreadStart" => BuildWithOptionalCondition(parsed.Get("sub"), parsed),
            "Thread@SubThreadEnd" or "Thread@SubThreadSuspend" => parsed.Get("sub"),
            "Thread@SubThreadScript" => JoinNonEmpty(", ", parsed.Get("sub"), parsed.Get("file")),
            "Thread@SubThreadWait" => BuildSubThreadWaitLegacy(parsed),
            "Thread@KeySectionLock" => BuildKeySectionLockLegacy(parsed),
            "Thread@KeySectionUnLock" => parsed.Get("lock"),
            "Thread@GoOn" or "Thread@Repeat" or "Thread@RunNextCMD" or "Thread@SkipNextCMD" => parsed.Get("logic"),
            "Thread@MutePopupMessage" => parsed.GetBool("enabled", false) ? "1" : "0",
            "Thread@ErrorHandler" => string.IsNullOrWhiteSpace(parsed.Get("label")) ? string.Empty : $"[[{parsed.Get("label").Trim('[', ']')}]]",
            "Thread@ErrorRetry" => BuildErrorRetryLegacy(parsed),
            "Thread@Label" => parsed.Get("label"),
            "Thread@Jump" => BuildWithOptionalCondition(parsed.Get("label"), parsed),

            // ======================== VARIABLE (34) ========================
            "Variable@Set" => AppendOptionalSection(JoinNonEmpty(", ", parsed.Get("var"), parsed.Get("val")), parsed),
            "Variable@Delete" => AppendOptionalSection(parsed.Get("var"), parsed),
            "Variable@Rename" => AppendOptionalSection(JoinNonEmpty(", ", parsed.Get("var"), parsed.Get("to")), parsed),
            "Variable@Inc" or "Variable@Dec" => BuildVarStepLegacy(parsed),
            "Variable@Calculate" => AppendOptionalSection($"{parsed.Get("var")}={parsed.Get("expr")}", parsed),
            "Variable@Select" => AppendOptionalSection($"{parsed.Get("var")}={parsed.Get("logic")}?{parsed.Get("true")}:{parsed.Get("false")}", parsed),
            "Variable@Append" => AppendOptionalSection(JoinNonEmpty(", ", parsed.Get("var"), parsed.Get("val")), parsed),
            "Variable@Trim" => AppendOptionalSection(JoinNonEmpty(", ", parsed.Get("var"), parsed.Get("start"), parsed.Get("end")), parsed),
            "Variable@Slice" or "Variable@Joint" => AppendOptionalSection(JoinNonEmpty(", ", parsed.Get("var"), parsed.Get("sep")), parsed),
            "Variable@LPush" or "Variable@RPush" or "Variable@LRemove" or "Variable@SAdd" =>
                AppendOptionalSection(JoinNonEmpty(", ", parsed.Get("var"), parsed.Get("val")), parsed),
            "Variable@LPop" or "Variable@RPop" or "Variable@LReverse" or "Variable@LGetIndexSize" =>
                AppendOptionalSection(parsed.Get("var"), parsed),
            "Variable@LInsert" => AppendOptionalSection(JoinNonEmpty(", ", parsed.Get("var"), parsed.Get("val"), parsed.Get("condition")), parsed),
            "Variable@LResetIndex" => BuildVarResetIndexLegacy(parsed),
            "Variable@LSetIndexValue" => AppendOptionalSection(JoinNonEmpty(", ", parsed.Get("var"), parsed.Get("index"), parsed.Get("val")), parsed),
            "Variable@LFindIndexSet" => BuildVarFindIndexSetLegacy(parsed),
            "Variable@Shift" => BuildVarShiftLegacy(parsed),
            "Variable@SUnion" or "Variable@SDiff" or "Variable@SIntersection" =>
                AppendOptionalSection(JoinNonEmpty(", ", parsed.Get("var"), parsed.Get("varA"), parsed.Get("varB")), parsed),
            "Variable@GetPointElement" => AppendOptionalSection($"{parsed.Get("var")}={parsed.Get("axis")}({parsed.Get("pt")})", parsed),
            "Variable@GetPointString" => AppendOptionalSection(JoinNonEmpty(", ", parsed.Get("var"), parsed.Get("pt")), parsed),
            "Variable@Publish" => string.Join(", ", parsed.GetList("vars")),
            "Variable@Subscribe" or "Variable@Flush" => parsed.Get("var"),
            "Variable@VariableLoad" => parsed.Get("path"),
            "Variable@VariableSave" => BuildVarSaveLegacy(parsed),

            // ======================== POINT&PATH (22) ========================
            "Point&Path@AddXYZ" or "Point&Path@AddUVW" or "Point&Path@SubXYZ" or "Point&Path@SubUVW" =>
                JoinNonEmpty(", ", parsed.Get("pt"), parsed.Get("delta")),
            "Point&Path@ReplaceXYZ" or "Point&Path@ReplaceUVW" or "Point&Path@InsertOnePoint" =>
                JoinNonEmpty(", ", parsed.Get("pt"), parsed.Get("val")),
            "Point&Path@ReplaceH" => JoinNonEmpty(", ", parsed.Get("pt"), parsed.Get("h")),
            "Point&Path@Calculate" => parsed.GetBool("append", false)
                ? $"{parsed.Get("pt")}<={parsed.Get("expr")}"
                : $"{parsed.Get("pt")}={parsed.Get("expr")}",
            "Point&Path@ClearPoint" => parsed.Get("pt"),
            "Point&Path@EnableAxis" => JoinNonEmpty(", ", parsed.Get("pt"), parsed.Get("axes")),
            "Point&Path@Select" => $"{parsed.Get("pt")}={parsed.Get("logic")}?{parsed.Get("true")}:{parsed.Get("false")}",
            "Point&Path@Pallet1D" => JoinNonEmpty(", ", parsed.Get("pt"), parsed.Get("count")),
            "Point&Path@Pallet2D" => JoinNonEmpty(", ", parsed.Get("pt"), parsed.Get("countX"), parsed.Get("countY")),
            "Point&Path@ResetPalletIndex" => parsed.TryGet("index", out var pIdx) && !string.IsNullOrWhiteSpace(pIdx)
                ? $"{parsed.Get("pt")}, {pIdx}"
                : parsed.Get("pt"),
            "Point&Path@Circle2DPath" => JoinNonEmpty(", ", parsed.Get("pt"), parsed.Get("startDeg"), parsed.Get("endDeg"), parsed.Get("segments")),
            "Point&Path@CoordWS(2Edge)" => JoinNonEmpty(", ", parsed.Get("ws"), parsed.Get("xEnd"), parsed.Get("yEnd")),
            "Point&Path@Point2WS" or "Point&Path@WS2Point" => JoinNonEmpty(", ", parsed.Get("pt"), parsed.Get("ws")),
            "Point&Path@SelfMotion" => JoinNonEmpty(", ", parsed.Get("pt"), parsed.Get("basePt"), parsed.Get("newPt")),
            "Point&Path@ChkInRangeXY" => parsed.TryGet("var", out var chkVar) && !string.IsNullOrWhiteSpace(chkVar)
                ? $"{parsed.Get("pt")}, {parsed.Get("rangePt")}, {chkVar}"
                : JoinNonEmpty(", ", parsed.Get("pt"), parsed.Get("rangePt")),
            "Point&Path@TransImaqPoint" => JoinNonEmpty(", ", parsed.Get("imaq"), parsed.Get("pt")),

            // ======================== MOTION (21) ========================
            "Motion@AddMotion" => BuildDriverInitLegacy(parsed, "mot", ("axes", "Axes"), ("speed", "Speed"), ("acc", "Acc"), ("dec", "Dec")),
            "Motion@Home" => JoinNonEmpty(", ", parsed.Get("mot"), parsed.Get("axes"), parsed.Get("mode")),
            "Motion@Stop" => parsed.Get("mot"),
            "Motion@Go" or "Motion@Go(SingleAxis)" => BuildMotionGoLegacy(parsed),
            "Motion@GoPoint" or "Motion@GoPoint(SingleAxis)" or "Motion@GoPallet" or "Motion@AddPath" =>
                JoinNonEmpty(", ", parsed.Get("mot"), parsed.Get("speed"), parsed.Get("pt")),
            "Motion@GoSafe" or "Motion@GoSafe(Pallet)" => BuildMotionGoSafeLegacy(parsed, tail),
            "Motion@ClearPath" or "Motion@GetPos" => parsed.Get("mot"),
            "Motion@RunPath" => JoinNonEmpty(", ", parsed.Get("mot"), parsed.Get("options")),
            "Motion@GetState" => JoinNonEmpty(", ", parsed.Get("mot"), tail),
            "Motion@WaitMotion" or "Motion@WaitMotion(Error)" => BuildDeviceTimeoutLegacy(parsed, "mot", tail),
            "Motion@WaitPosition" => JoinNonEmpty(", ", parsed.Get("mot"), parsed.Get("tol"), parsed.Get("pt")),
            "Motion@SetTool" => JoinNonEmpty(", ", parsed.Get("mot"), parsed.Get("pt"), tail),
            "Motion@Config" => JoinNonEmpty(", ", parsed.Get("mot"), parsed.Get("key"), parsed.Get("val"), tail),
            "Motion@ExtraCMD" => JoinNonEmpty(", ", parsed.Get("mot"), parsed.Get("cmd"), tail),

            // ======================== CAPTURE (28) ========================
            "Capture@AddCam" => BuildDriverInitLegacy(parsed, "cam", ("width", "Width"), ("height", "Height"), ("exposure", "Exposure"), ("file", "File")),
            "Capture@Config" => JoinNonEmpty(", ", parsed.Get("cam"), parsed.Get("key"), parsed.Get("val"), tail),
            "Capture@Snap" or "Capture@Trigger" => JoinNonEmpty(", ", parsed.Get("cam"), parsed.Get("imaqs"), tail),
            "Capture@UpdateImage" => parsed.Get("cam"),
            "Capture@LoadFile" or "Capture@ROISave" or "Capture@CalibrateLoad" =>
                JoinNonEmpty(", ", parsed.Get("imaq"), parsed.Get("path")),
            "Capture@ImageSave" => parsed.GetBool("burnOverlays", false)
                ? JoinNonEmpty(", ", parsed.Get("imaq"), parsed.Get("path"), "BurnOverlays=true")
                : JoinNonEmpty(", ", parsed.Get("imaq"), parsed.Get("path")),
            "Capture@ClearImaq" => parsed.Get("imaq"),
            "Capture@LoadFile2Imaq" => JoinNonEmpty(", ", parsed.Get("imaqs"), parsed.Get("path")),
            "Capture@LoadNextImaq" => JoinNonEmpty(", ", parsed.Get("imaqs"), parsed.Get("imaq")),
            "Capture@ResetImaqIndex" => JoinNonEmpty(", ", parsed.Get("imaqs"), parsed.Get("index")),
            "Capture@Display2User" => JoinNonEmpty(", ", parsed.Get("imaq"), parsed.Get("ctrl")),
            "Capture@ROISet" => JoinNonEmpty(", ", parsed.Get("imaq"), parsed.Get("roi")),
            "Capture@ROITranslate" => JoinNonEmpty(", ", parsed.Get("imaq"), parsed.Get("basePt"), parsed.Get("newPt")),
            "Capture@CalibrateManual" => BuildCalibrateManualLegacy(parsed),
            "Capture@CoordSet" => JoinNonEmpty(", ", parsed.Get("imaq"), parsed.Get("pt")),
            "Capture@CoordSet(2Edge)" => JoinNonEmpty(", ", parsed.Get("imaq"), parsed.Get("xPt"), parsed.Get("yPt")),
            "Capture@CoordSet(Org+2Edge)" => JoinNonEmpty(", ", parsed.Get("imaq"), parsed.Get("orgPt"), parsed.Get("xPt"), parsed.Get("yPt")),
            "Capture@FindCircle(InOut)" or "Capture@FindCircle(OutIn)" or "Capture@FindEdge" or "Capture@FindGap" or "Capture@FindPoint" =>
                JoinNonEmpty(", ", parsed.Get("imaq"), parsed.Get("pt")),
            "Capture@GeoMatchOne" or "Capture@PatternMatchOne" or "Capture@PatternMatchOne(Fast)" =>
                BuildCaptureMatchLegacy(parsed),

            // ======================== DAQ (18) ========================
            "DAQ@AddDAQ" => BuildDriverInitLegacy(parsed, "daq", ("rate", "Rate"), ("count", "Count"), ("wave", "Wave")),
            "DAQ@Start" or "DAQ@Stop" => JoinNonEmpty(", ", parsed.Get("daq"), tail),
            "DAQ@Wait" => BuildDeviceTimeoutLegacy(parsed, "daq", tail),
            "DAQ@AnalogIn" => JoinNonEmpty(", ", parsed.Get("daq"), parsed.Get("wav"), tail),
            "DAQ@AnalogInWaveForm" => JoinNonEmpty(", ", parsed.Get("daq"), parsed.Get("sec"), parsed.Get("wav"), tail),
            "DAQ@AnalogOut" => JoinNonEmpty(", ", parsed.Get("daq"), parsed.Get("val"), tail),
            "DAQ@AnalogOutWaveForm" => JoinNonEmpty(", ", parsed.Get("daq"), parsed.Get("wav"), tail),
            "DAQ@WaveFormGenerate" => BuildWaveFormGenerateLegacy(parsed),
            "DAQ@WaveFormLoad" or "DAQ@WaveFormSave" => JoinNonEmpty(", ", parsed.Get("wav"), parsed.Get("path")),
            "DAQ@Calculate" => $"{parsed.Get("wav")} = {parsed.Get("expr")}",
            "DAQ@Filter" => JoinNonEmpty(", ", parsed.Get("wav"), parsed.Get("type"), parsed.Get("window")),
            "DAQ@MergeWaveForm" => BuildMergeWaveFormLegacy(parsed),
            "DAQ@CompareAvgInRange" or "DAQ@CompareMaxMinInRange" => BuildDaqCompareRangeLegacy(parsed),
            "DAQ@CompareCoverBy" => JoinNonEmpty(", ", parsed.Get("wav"), parsed.Get("upper"), parsed.Get("lower")),
            "DAQ@Display2User" => JoinNonEmpty(", ", parsed.Get("wav"), parsed.Get("ctrl")),

            // ======================== USER (21) ========================
            "User@SwitchUserPanel" => parsed.Get("panel"),
            "User@Param2UserPanel" => string.Empty,
            "User@ShowControl" or "User@HideControl" or "User@EnableControl" or "User@DisableControl" =>
                string.Join(", ", parsed.GetList("ctrls")),
            "User@GetControlValue" => JoinNonEmpty(", ", parsed.Get("ctrl"), parsed.Get("var")),
            "User@SetControlValue" => $"{parsed.Get("ctrl")}, {parsed.Get("val")}",
            "User@QueryControls" => BuildUserQueryControlsLegacy(parsed),
            "User@ButtonValue" or "User@ButtonTrigger" or "User@ButtonBlinking" or "User@TextBoxBlinking" or "User@TextRingBlinking" =>
                $"{parsed.Get("ctrl")}, {(parsed.GetBool("on", true) ? "1" : "0")}",
            "User@ButtonText" or "User@TextBoxValue" or "User@TextBoxTrigger" =>
                $"{parsed.Get("ctrl")}, {parsed.Get("text")}",
            "User@TextRingValue" or "User@TextRingTrigger" =>
                $"{parsed.Get("ctrl")}, {parsed.Get("val")}",
            "User@TabValue" or "User@TabTrigger" =>
                $"{parsed.Get("ctrl")}, {parsed.Get("page")}",

            // ======================== PYTHON (12) ========================
            "Python@Init" => BuildPythonSessionOnlyLegacy(parsed),
            "Python@Reset" => string.Empty,
            "Python@ResetSession" => parsed.Get("session"),
            "Python@File" => AppendOptionalPythonSession(parsed.Get("path"), parsed),
            "Python@Script" => BuildPythonScriptLegacy(parsed, tail),
            "Python@SetVariable" => BuildPythonSetVariableLegacy(parsed),
            "Python@GetVariable" => BuildPythonGetLegacy(parsed, "var"),
            "Python@SetPoint" => BuildPythonSetLegacy(parsed, "pt"),
            "Python@GetPoint" => BuildPythonGetLegacy(parsed, "pt"),
            "Python@SetCapture" => BuildPythonSetLegacy(parsed, "imaq"),
            "Python@SetCaptureFile" => AppendOptionalPythonSession(JoinNonEmpty(", ", parsed.Get("imaq"), parsed.Get("path"), parsed.Get("py")), parsed),
            "Python@GetCapture" => BuildPythonGetLegacy(parsed, "imaq"),

            // ======================== REDIS (9) ========================
            "Redis@CreateSession" => JoinNonEmpty(", ", parsed.Get("session"), parsed.Get("endpoint")),
            "Redis@Set" => AppendOptionalSessionKey(JoinNonEmpty(", ", parsed.Get("key"), parsed.Get("val")), parsed),
            "Redis@Get" => AppendOptionalSessionKey(JoinNonEmpty(", ", parsed.Get("key"), parsed.Get("var")), parsed),
            "Redis@LPush" => AppendOptionalSessionKey(JoinNonEmpty(", ", parsed.Get("key"), parsed.Get("val")), parsed),
            "Redis@LPop" => AppendOptionalSessionKey(parsed.Get("key"), parsed),
            "Redis@BLPop" => BuildRedisBlPopLegacy(parsed, tail),
            "Redis@Publish" => AppendOptionalSessionKey(JoinNonEmpty(", ", parsed.Get("key"), parsed.Get("val")), parsed),
            "Redis@Subscribe" => AppendOptionalSessionKey(parsed.Get("key"), parsed),
            "Redis@CMD" => AppendOptionalSessionKey(parsed.Get("cmd"), parsed),

            // ======================== MULTISTATION (15) ========================
            // 15 條 MultiStation 指令直接採用 CommandParamParser 透過 MultiStationEngine 執行，原樣保留 v2.0 結構
            "MultiStation@AddStation" or
            "MultiStation@SetStationON" or "MultiStation@SetStationOFF" or
            "MultiStation@SetStationOK" or "MultiStation@SetStationNG" or
            "MultiStation@SetData" or "MultiStation@GetData" or "MultiStation@GetPoint" or
            "MultiStation@ChkStationRunState" or "MultiStation@StateShift" or
            "MultiStation@Transfer" or "MultiStation@FindFirstOK" or "MultiStation@FindEmpty" or
            "MultiStation@ResetDefault" or "MultiStation@UpdateIndicator" => rawParam,

            // ======================== AIAGENT (3) ========================
            "AIAgent@SuperviseStart" => BuildAiAgentPromptLegacy(parsed, ""),
            "AIAgent@SuperviseEnd" => tail,
            "AIAgent@ErrorHandle" => BuildAiAgentPromptLegacy(parsed, tail),

            // ======================== HELLO (2) ========================
            "Hello@Init" => string.Empty,
            "Hello@HelloWorld" => parsed.Get("msg"),

            _ => rawParam
        };
    }

    /// <summary>
    /// 將舊格式或導引視窗輸出結果正規化為標準 v2.0 三段式參數字串
    /// </summary>
    public static string FormatToV2(string commandKey, string? rawParam)
    {
        if (string.IsNullOrWhiteSpace(rawParam)) return string.Empty;

        var schema = StandardCommandSchemas.Get(commandKey);
        if (schema == null) return rawParam.Trim();

        var parsed = CommandParamParser.Parse(rawParam, schema);
        if (parsed.HasSyntaxError) return rawParam.Trim();

        string key = schema.CommandKey;

        // 針對舊版特例格式做精準補位映射，確保舊 -> v2.0 轉換零失真
        if (parsed.IsLegacyFormat)
        {
            if (key is "IO@SetPort" or "IO@ClrPort")
            {
                bool hasForce = parsed.PositionalArgs.Any(a => a.Equals("ForceUpdate", StringComparison.OrdinalIgnoreCase));
                if (hasForce)
                {
                    parsed.AllArgs["forceUpdate"] = "true";
                    if (parsed.AllArgs.TryGetValue("holdSec", out var hs) &&
                        hs.Equals("ForceUpdate", StringComparison.OrdinalIgnoreCase))
                    {
                        parsed.AllArgs.Remove("holdSec");
                    }
                }
            }
            else if (key == "IO@AddVirtualIO")
            {
                // 保留完整 $logic 表達式（僅將純布林常數 1/0/On/Off 正規化為 True/False）
                if (parsed.AllArgs.TryGetValue("logic", out var rawLogic) &&
                    ParamSpec.TryNormalizeBool(rawLogic, out bool b))
                {
                    parsed.AllArgs["logic"] = b ? "True" : "False";
                }
            }
            else if (key == "System@WarningBox" && parsed.PositionalArgs.Count == 1)
            {
                parsed.AllArgs["msg"] = parsed.PositionalArgs[0];
                parsed.AllArgs.Remove("logic");
            }
            else if (key is "System@Pause" or "System@Resume")
            {
                if (parsed.PositionalArgs.Count > 1)
                {
                    parsed.AllArgs["threads"] = $"[{string.Join(";", parsed.PositionalArgs)}]";
                }
                else if (parsed.PositionalArgs.Count == 1)
                {
                    parsed.AllArgs["threads"] = parsed.PositionalArgs[0];
                }
            }
            else if (key == "System@UserPanelOpen")
            {
                var (csviPath, panelName, activate) = ARCOS.Core.Engines.UserPanelEngine.ParseOpenSpec(rawParam);
                parsed.AllArgs["panel"] = csviPath;
                bool hasExplicitAlias = parsed.AllArgs.ContainsKey("alias") ||
                                        rawParam.Contains("Name=", StringComparison.OrdinalIgnoreCase) ||
                                        rawParam.Contains("Alias=", StringComparison.OrdinalIgnoreCase);
                if (hasExplicitAlias && !string.IsNullOrWhiteSpace(panelName))
                {
                    parsed.AllArgs["alias"] = panelName;
                }
                if (!activate)
                {
                    parsed.AllArgs["activate"] = "false";
                }
            }
            else if (key == "Thread@SubThreadWait")
            {
                if (parsed.AllArgs.TryGetValue("skipNotFound", out var snf))
                {
                    parsed.AllArgs["skipNotFound"] = ParamSpec.TryNormalizeBool(snf, out bool b) && b ? "true" : "false";
                }
            }
            else if (key == "Thread@MutePopupMessage")
            {
                parsed.AllArgs["enabled"] = parsed.GetBool("enabled", false) ? "true" : "false";
            }
            else if (key == "Thread@ErrorHandler")
            {
                string lbl = parsed.ErrorLabel ?? parsed.Get("label").Trim('[', ']');
                if (!string.IsNullOrWhiteSpace(lbl))
                {
                    parsed.AllArgs["label"] = lbl;
                    parsed.ErrorLabel = null;
                    parsed.ErrorMessage = null;
                }
            }
        }
        else if (key == "Thread@ErrorHandler" && !string.IsNullOrWhiteSpace(parsed.ErrorLabel) && !parsed.AllArgs.ContainsKey("label"))
        {
            parsed.AllArgs["label"] = parsed.ErrorLabel.Trim('[', ']');
            parsed.ErrorLabel = null;
            parsed.ErrorMessage = null;
        }

        // 針對 VARIABLE / POINT&PATH 舊式特殊賦值語法（Dest=Expr、Dest=A?B:C、Var=X(Pt)、Pt<=Expr）進行無損升級
        TryNormalizeLegacyExpressionToV2(key, rawParam, parsed);

        // 針對舊式位置參數將目標寫在第 0 欄的集合/點位指令（SUnion/SDiff/SIntersection/GetPointString/ChkInRangeXY）補齊輸出映射
        if (parsed.IsLegacyFormat)
        {
            if (key is "Variable@SUnion" or "Variable@SDiff" or "Variable@SIntersection" && parsed.PositionalArgs.Count >= 3)
            {
                parsed.AllArgs["var"] = parsed.PositionalArgs[0].TrimStart('$');
            }
            else if (key == "Variable@GetPointString" && parsed.PositionalArgs.Count >= 2)
            {
                parsed.AllArgs["var"] = parsed.PositionalArgs[0].TrimStart('$');
            }
            else if (key == "Point&Path@ChkInRangeXY" && parsed.PositionalArgs.Count >= 3)
            {
                parsed.AllArgs["var"] = parsed.PositionalArgs[2].TrimStart('$');
            }
            else if (key is "Point&Path@AddXYZ" or "Point&Path@AddUVW" or "Point&Path@SubXYZ" or "Point&Path@SubUVW" or
                     "Point&Path@ReplaceXYZ" or "Point&Path@ReplaceUVW" or "Point&Path@InsertOnePoint")
            {
                // 處理舊格式如 "P1, (10,20,30)" 或 "P1, 10, 20, 30"
                int comma = rawParam.IndexOf(',');
                if (comma > 0)
                {
                    string pt = rawParam.Substring(0, comma).Trim().TrimStart('$');
                    string rest = rawParam.Substring(comma + 1).Trim();
                    parsed.AllArgs["pt"] = pt;
                    string rhsKey = key.StartsWith("Point&Path@Replace", StringComparison.OrdinalIgnoreCase) || key == "Point&Path@InsertOnePoint" ? "val" : "delta";
                    parsed.AllArgs[rhsKey] = rest;
                }
            }
            else if (key == "Point&Path@ReplaceH")
            {
                int comma = rawParam.IndexOf(',');
                if (comma > 0)
                {
                    parsed.AllArgs["pt"] = rawParam.Substring(0, comma).Trim().TrimStart('$');
                    parsed.AllArgs["h"] = rawParam.Substring(comma + 1).Trim();
                }
            }
            else if (key is "Motion@AddMotion" or "Capture@AddCam" or "DAQ@AddDAQ")
            {
                NormalizeLegacyDriverExtrasToV2(schema, parsed);
            }
            else if (key is "Motion@Go" or "Motion@Go(SingleAxis)" && parsed.PositionalArgs.Count > 4)
            {
                parsed.AllArgs["coords"] = string.Join(",", parsed.PositionalArgs.Skip(3));
            }
            else if (key is "Motion@GoSafe" or "Motion@GoSafe(Pallet)")
            {
                foreach (var ax in new[] { "Z", "X", "Y" })
                {
                    if (parsed.RequiredArgs.TryGetValue(ax, out var axVal) || parsed.OptionalArgs.TryGetValue(ax, out axVal))
                    {
                        parsed.AllArgs["safeAxis"] = ax;
                        parsed.AllArgs["safePos"] = axVal;
                        break;
                    }
                }
            }
            else if (key == "Capture@ROISet" && parsed.PositionalArgs.Count > 2)
            {
                parsed.AllArgs["roi"] = string.Join(",", parsed.PositionalArgs.Skip(1));
            }
            else if (key is "Capture@GeoMatchOne" or "Capture@PatternMatchOne" or "Capture@PatternMatchOne(Fast)")
            {
                if (parsed.PositionalArgs.Count == 3)
                {
                    parsed.AllArgs["pt"] = parsed.PositionalArgs[2].TrimStart('$');
                    parsed.AllArgs.Remove("var");
                }
                if (parsed.AllArgs.TryGetValue("var", out var sv)) parsed.AllArgs["var"] = sv.TrimStart('$');
                if (parsed.AllArgs.TryGetValue("pt", out var pv)) parsed.AllArgs["pt"] = pv.TrimStart('$');
            }
            else if (key == "DAQ@AnalogInWaveForm" && parsed.PositionalArgs.Count == 2 &&
                     !double.TryParse(parsed.PositionalArgs[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                parsed.AllArgs["wav"] = parsed.PositionalArgs[1];
                parsed.AllArgs.Remove("sec");
            }
            else if (key is "User@ButtonText" or "User@SetControlValue" or "User@TextBoxValue" or "User@TextBoxTrigger")
            {
                int comma = rawParam.IndexOf(',');
                if (comma > 0)
                {
                    parsed.AllArgs["ctrl"] = rawParam.Substring(0, comma).Trim();
                    string rhs = rawParam.Substring(comma + 1).Trim();
                    parsed.AllArgs[key == "User@SetControlValue" ? "val" : "text"] = rhs;
                }
            }
            else if (key is "User@ButtonValue" or "User@ButtonTrigger" or "User@ButtonBlinking" or "User@TextBoxBlinking" or "User@TextRingBlinking")
            {
                if (parsed.AllArgs.TryGetValue("on", out var onRaw))
                {
                    parsed.AllArgs["on"] = ARCOS.Core.Engines.UserPanelEngine.ParseLogicOn(onRaw) ? "true" : "false";
                }
            }
            else if (key is "User@GetControlValue" or "User@QueryControls")
            {
                if (parsed.AllArgs.TryGetValue("var", out var uv)) parsed.AllArgs["var"] = uv.TrimStart('$');
            }
            else if (key == "Redis@Get")
            {
                if (parsed.AllArgs.TryGetValue("var", out var rv)) parsed.AllArgs["var"] = rv.TrimStart('$');
            }
            else if (key == "Redis@CMD" && parsed.PositionalArgs.Count >= 2)
            {
                parsed.AllArgs["session"] = parsed.PositionalArgs[0];
                parsed.AllArgs["cmd"] = string.Join(", ", parsed.PositionalArgs.Skip(1));
            }
            else if (key == "MultiStation@StateShift" && parsed.PositionalArgs.Count == 1)
            {
                string only = parsed.PositionalArgs[0].Trim();
                if (int.TryParse(only, out _) ||
                    only.Equals("CW", StringComparison.OrdinalIgnoreCase) ||
                    only.Equals("CCW", StringComparison.OrdinalIgnoreCase) ||
                    only.Equals("Left", StringComparison.OrdinalIgnoreCase) ||
                    only.Equals("Right", StringComparison.OrdinalIgnoreCase))
                {
                    parsed.AllArgs["shift"] = only;
                    parsed.AllArgs.Remove("station");
                }
            }
            else if (key == "MultiStation@GetData" || key == "MultiStation@FindFirstOK" || key == "MultiStation@FindEmpty")
            {
                if (parsed.AllArgs.TryGetValue("var", out var mv)) parsed.AllArgs["var"] = mv.TrimStart('$');
                if (parsed.AllArgs.TryGetValue("pt", out var mp)) parsed.AllArgs["pt"] = mp.TrimStart('$');
            }
            else if (key == "MultiStation@GetPoint")
            {
                if (parsed.AllArgs.TryGetValue("pt", out var mp)) parsed.AllArgs["pt"] = mp.TrimStart('$');
            }
            else if (key is "AIAgent@SuperviseStart" or "AIAgent@ErrorHandle")
            {
                if (parsed.AllArgs.TryGetValue("prompt", out var rawPrompt) && rawPrompt.StartsWith('~'))
                {
                    parsed.AllArgs["prompt"] = rawPrompt.Substring(1).Trim();
                    parsed.AllArgs["bypass"] = "true";
                }
            }
        }

        // 針對 Python@Script 舊格式（原生 Python 腳本，含或不含首行 Session=xxx）進行無損升級
        if (key == "Python@Script" && !parsed.RequiredArgs.ContainsKey("code") && !parsed.RequiredArgs.ContainsKey("script"))
        {
            string body = rawParam.Trim();
            string? sess = null;
            if (body.StartsWith("Session=", StringComparison.OrdinalIgnoreCase))
            {
                int sep = body.IndexOfAny(new[] { ',', '\n', '\r' });
                if (sep > 0)
                {
                    sess = body.Substring(8, sep - 8).Trim();
                    body = body.Substring(sep + 1).Trim();
                }
            }
            parsed.AllArgs.Clear();
            parsed.AllArgs["code"] = body;
            if (!string.IsNullOrWhiteSpace(sess)) parsed.AllArgs["session"] = sess;
        }
        else if (key == "DAQ@Calculate" && !parsed.HasOptionalBlock && !parsed.HasOutputBlock &&
                 !parsed.RequiredArgs.ContainsKey("wav") && !parsed.RequiredArgs.ContainsKey("expr"))
        {
            int eqIdx = rawParam.IndexOf('=');
            if (eqIdx > 0 && eqIdx < rawParam.Length - 1)
            {
                parsed.AllArgs.Clear();
                parsed.AllArgs["wav"] = rawParam.Substring(0, eqIdx).Trim().TrimStart('$');
                parsed.AllArgs["expr"] = rawParam.Substring(eqIdx + 1).Trim();
            }
        }

        return CommandFormatter.FormatParameter(parsed, schema);
    }

    /// <summary>
    /// 將 v2.0 `->` 區段之顯式輸出目標寫回 VariableEngine（支援標準鍵名與別名，例如 -> var=$CycleSec 或 -> sec=$CycleSec）
    /// </summary>
    public static void ApplyOutputBinding(string commandKey, string? rawParam, ExecutionContext ctx, string outputParamName, string value)
    {
        if (string.IsNullOrWhiteSpace(rawParam) || ctx?.Engines?.Variable == null) return;
        var schema = StandardCommandSchemas.Get(commandKey);
        var parsed = CommandParamParser.Parse(rawParam, schema);
        if (!parsed.HasOutputBlock) return;

        string? targetExpr = null;
        var spec = schema?.FindParam(outputParamName);
        if (spec != null)
        {
            if (!parsed.OutputArgs.TryGetValue(spec.Name, out targetExpr))
            {
                foreach (var alias in spec.Aliases)
                {
                    if (parsed.OutputArgs.TryGetValue(alias, out targetExpr)) break;
                }
            }
        }
        else
        {
            parsed.OutputArgs.TryGetValue(outputParamName, out targetExpr);
        }

        if (string.IsNullOrWhiteSpace(targetExpr)) return;

        string varName = targetExpr.Trim();
        if (varName.StartsWith("$(") && varName.EndsWith(')') && varName.Length > 3)
        {
            varName = varName.Substring(2, varName.Length - 3).Trim();
        }
        else if (varName.StartsWith('$'))
        {
            varName = varName.Substring(1).Trim();
        }

        if (string.IsNullOrWhiteSpace(varName)) return;

        string targetSection = ctx.CurrentSection;
        int atIdx = varName.IndexOf('@');
        if (atIdx > 0)
        {
            targetSection = varName.Substring(atIdx + 1).Trim();
            varName = varName.Substring(0, atIdx).Trim();
        }

        ctx.Engines.Variable.Set(varName, value, targetSection);
    }

    // ======================== Internal Legacy Builders ========================

    private static string BuildAddIoDriverLegacy(ParsedCommandParams parsed)
    {
        var items = new List<string>();
        string card = parsed.Get("card");
        string driver = parsed.Get("driver");
        if (!string.IsNullOrEmpty(card)) items.Add(card);
        if (!string.IsNullOrEmpty(driver)) items.Add(driver);

        void AddOpt(string key, string legacyUpper)
        {
            if (parsed.TryGet(key, out var v) && !string.IsNullOrWhiteSpace(v))
                items.Add($"{legacyUpper}={v}");
        }
        AddOpt("port", "DEVICE");
        AddOpt("outStart", "OUTSTART");
        AddOpt("outPorts", "OUTPORTS");
        AddOpt("inStart", "INSTART");
        AddOpt("inPorts", "INPORTS");
        return string.Join(", ", items);
    }

    private static string BuildSetClrPortLegacy(ParsedCommandParams parsed)
    {
        string io = parsed.Get("io");
        bool hasHold = parsed.TryGet("holdSec", out var holdSec) && !string.IsNullOrWhiteSpace(holdSec);
        bool force = parsed.GetBool("forceUpdate", false);

        if (force)
        {
            return $"{io}, {(hasHold ? holdSec : "0")}, ForceUpdate";
        }
        return hasHold ? $"{io}, {holdSec}" : io;
    }

    private static string BuildIoShiftLegacy(ParsedCommandParams parsed)
    {
        var ios = parsed.GetList("ios");
        int shift = parsed.GetInt("shift", 1);
        ios.Add($"Shift={shift}");
        return string.Join(", ", ios);
    }

    private static string BuildWaitLegacy(string target, ParsedCommandParams parsed, string tail)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(target)) parts.Add(target);
        if (parsed.TryGet("timeoutSec", out var tSec) && !string.IsNullOrWhiteSpace(tSec))
        {
            parts.Add(tSec);
        }
        else if (!string.IsNullOrEmpty(tail))
        {
            parts.Add("0");
        }
        if (!string.IsNullOrEmpty(tail)) parts.Add(tail);
        return string.Join(", ", parts);
    }

    private static string BuildPauseResumeLegacy(ParsedCommandParams parsed)
    {
        var parts = new List<string>();
        var threads = parsed.GetList("threads");
        foreach (var t in threads)
        {
            if (!t.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(t);
            }
        }
        if (parsed.TryGet("logic", out var cond) && !string.IsNullOrWhiteSpace(cond) &&
            !cond.Equals("True", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"CONDITION={cond}");
        }
        return string.Join(", ", parts);
    }

    private static string BuildUserPanelOpenLegacy(ParsedCommandParams parsed)
    {
        var parts = new List<string> { parsed.Get("panel") };
        if (parsed.TryGet("alias", out var alias) && !string.IsNullOrWhiteSpace(alias))
            parts.Add($"Name={alias}");
        if (parsed.TryGet("activate", out _) && !parsed.GetBool("activate", true))
            parts.Add("Activate=false");
        return string.Join(", ", parts);
    }

    private static string BuildForeachLoopLegacy(ParsedCommandParams parsed)
    {
        var parts = new List<string> { parsed.Get("label") };
        parts.AddRange(parsed.GetList("vars"));
        return string.Join(", ", parts);
    }

    private static string BuildThreadRunLegacy(ParsedCommandParams parsed)
    {
        string prog = parsed.Get("prog");
        string thr = parsed.Get("thread");
        string startLine = parsed.Get("startLine", "1");
        bool hasCond = parsed.TryGet("logic", out var cond) && !string.IsNullOrWhiteSpace(cond) &&
                       !cond.Equals("True", StringComparison.OrdinalIgnoreCase);

        if (hasCond) return $"{prog}, {thr}, {startLine}, {cond}";
        if (parsed.TryGet("startLine", out _)) return $"{prog}, {thr}, {startLine}";
        return $"{prog}, {thr}";
    }

    private static string BuildThreadStopLegacy(ParsedCommandParams parsed)
    {
        string cond = parsed.Get("logic", "True");
        string msg = parsed.Get("msg");
        if (!string.IsNullOrWhiteSpace(msg)) return $"{cond}, {msg}";
        if (parsed.TryGet("logic", out _)) return cond;
        return string.Empty;
    }

    private static string BuildSubThreadWaitLegacy(ParsedCommandParams parsed)
    {
        var parts = parsed.GetList("subs");
        if (parsed.GetBool("skipNotFound", false))
            parts.Add("SKIPNOTFIND=1");
        return string.Join(", ", parts);
    }

    private static string BuildKeySectionLockLegacy(ParsedCommandParams parsed)
    {
        string lockName = parsed.Get("lock");
        string timeout = parsed.Get("timeoutSec", "5.0");
        string msg = parsed.Get("msg");
        if (!string.IsNullOrWhiteSpace(msg)) return $"{lockName}, {timeout}, {msg}";
        if (parsed.TryGet("timeoutSec", out _)) return $"{lockName}, {timeout}";
        return lockName;
    }

    private static string BuildErrorRetryLegacy(ParsedCommandParams parsed)
    {
        string cond = parsed.Get("logic", "True");
        if (parsed.TryGet("offset", out var off) && !string.IsNullOrWhiteSpace(off))
            return $"{cond}, {off}";
        if (parsed.TryGet("logic", out _))
            return cond;
        return string.Empty;
    }

    private static string BuildWithOptionalCondition(string first, ParsedCommandParams parsed)
    {
        if (parsed.TryGet("logic", out var cond) && !string.IsNullOrWhiteSpace(cond) &&
            !cond.Equals("True", StringComparison.OrdinalIgnoreCase))
        {
            return $"{first}, {cond}";
        }
        return first;
    }

    private static string FormatErrorTrap(string? label, string? msg)
    {
        if (string.IsNullOrWhiteSpace(label)) return string.Empty;
        return string.IsNullOrWhiteSpace(msg) ? $"[[{label}]]" : $"[[{label}]]{msg}";
    }

    private static string JoinNonEmpty(string sep, params string[] items) =>
        string.Join(sep, items.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string AppendOptionalSection(string baseStr, ParsedCommandParams parsed)
    {
        if (parsed.TryGet("section", out var sec) && !string.IsNullOrWhiteSpace(sec))
            return string.IsNullOrEmpty(baseStr) ? sec : $"{baseStr}, {sec}";
        return baseStr;
    }

    private static string BuildVarStepLegacy(ParsedCommandParams parsed)
    {
        string varName = parsed.Get("var");
        string step = parsed.Get("step", "1");
        bool hasSec = parsed.TryGet("section", out var sec) && !string.IsNullOrWhiteSpace(sec);
        if (hasSec) return $"{varName}, {step}, {sec}";
        if (parsed.TryGet("step", out _)) return $"{varName}, {step}";
        return varName;
    }

    private static string BuildVarResetIndexLegacy(ParsedCommandParams parsed)
    {
        string varName = parsed.Get("var");
        string idx = parsed.Get("index", "0");
        bool hasSec = parsed.TryGet("section", out var sec) && !string.IsNullOrWhiteSpace(sec);
        if (hasSec) return $"{varName}, {idx}, {sec}";
        if (parsed.TryGet("index", out _)) return $"{varName}, {idx}";
        return varName;
    }

    private static string BuildVarFindIndexSetLegacy(ParsedCommandParams parsed)
    {
        var parts = new List<string> { parsed.Get("var"), parsed.Get("val") };
        parts.AddRange(parsed.GetList("linked"));
        return string.Join(", ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string BuildVarShiftLegacy(ParsedCommandParams parsed)
    {
        var vars = parsed.GetList("vars");
        int shift = parsed.GetInt("shift", 1);
        vars.Add($"Shift={shift}");
        return string.Join(", ", vars);
    }

    private static string BuildVarSaveLegacy(ParsedCommandParams parsed)
    {
        var parts = new List<string> { parsed.Get("path") };
        parts.AddRange(parsed.GetList("patterns"));
        return string.Join(", ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static void TryNormalizeLegacyExpressionToV2(string key, string rawParam, ParsedCommandParams parsed)
    {
        if (parsed.HasOutputBlock || parsed.HasOptionalBlock || parsed.RequiredArgs.Count >= 2) return;
        string s = rawParam.Trim();

        if (key == "Variable@Calculate")
        {
            int eqIdx = s.IndexOf('=');
            if (eqIdx > 0 && !s.StartsWith("var=", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("expr=", StringComparison.OrdinalIgnoreCase))
            {
                string dest = s.Substring(0, eqIdx).Trim().TrimStart('$');
                string formula = s.Substring(eqIdx + 1).Trim();
                string sec = "";
                int commaIdx = formula.LastIndexOf(',');
                if (commaIdx > 0)
                {
                    sec = formula.Substring(commaIdx + 1).Trim();
                    formula = formula.Substring(0, commaIdx).Trim();
                }
                parsed.AllArgs.Clear();
                parsed.AllArgs["var"] = dest;
                parsed.AllArgs["expr"] = formula;
                if (!string.IsNullOrWhiteSpace(sec)) parsed.AllArgs["section"] = sec;
            }
        }
        else if (key == "Variable@Select")
        {
            int eqIdx = s.IndexOf('=');
            int cIdx = s.LastIndexOf(':');
            int qIdx = (eqIdx >= 0 && cIdx > eqIdx + 1) ? s.LastIndexOf('?', cIdx - 1) : -1;
            if (eqIdx > 0 && qIdx > eqIdx && cIdx > qIdx &&
                !s.StartsWith("var=", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("logic=", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("condition=", StringComparison.OrdinalIgnoreCase))
            {
                string dest = s.Substring(0, eqIdx).Trim().TrimStart('$');
                string logic = s.Substring(eqIdx + 1, qIdx - eqIdx - 1).Trim();
                string v1 = s.Substring(qIdx + 1, cIdx - qIdx - 1).Trim();
                string v2 = s.Substring(cIdx + 1).Trim();
                parsed.AllArgs.Clear();
                parsed.AllArgs["var"] = dest;
                parsed.AllArgs["logic"] = logic;
                parsed.AllArgs["true"] = v1;
                parsed.AllArgs["false"] = v2;
            }
        }
        else if (key == "Variable@GetPointElement")
        {
            string work = s;
            string sec = "";
            int commaIdx = work.LastIndexOf(',');
            if (commaIdx > 0)
            {
                sec = work.Substring(commaIdx + 1).Trim();
                work = work.Substring(0, commaIdx).Trim();
            }
            var m = System.Text.RegularExpressions.Regex.Match(work, @"^([a-zA-Z0-9_$]+)=([XYZUVWHxyzuvwh](?:\.[a-zA-Z0-9_]+)?)\(([^)]+)\)$");
            if (m.Success)
            {
                parsed.AllArgs.Clear();
                parsed.AllArgs["var"] = m.Groups[1].Value.Trim().TrimStart('$');
                parsed.AllArgs["pt"] = m.Groups[3].Value.Trim().TrimStart('$');
                parsed.AllArgs["axis"] = m.Groups[2].Value.Trim();
                if (!string.IsNullOrWhiteSpace(sec)) parsed.AllArgs["section"] = sec;
            }
        }
        else if (key == "Point&Path@Calculate")
        {
            int appendIdx = s.IndexOf("<=", StringComparison.Ordinal);
            if (appendIdx > 0)
            {
                parsed.AllArgs.Clear();
                parsed.AllArgs["pt"] = s.Substring(0, appendIdx).Trim().TrimStart('$');
                parsed.AllArgs["expr"] = s.Substring(appendIdx + 2).Trim();
                parsed.AllArgs["append"] = "true";
                return;
            }
            int eqIdx = s.IndexOf('=');
            if (eqIdx > 0 && !s.StartsWith("pt=", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("expr=", StringComparison.OrdinalIgnoreCase))
            {
                parsed.AllArgs.Clear();
                parsed.AllArgs["pt"] = s.Substring(0, eqIdx).Trim().TrimStart('$');
                parsed.AllArgs["expr"] = s.Substring(eqIdx + 1).Trim();
            }
        }
        else if (key == "Point&Path@Select")
        {
            int eqIdx = s.IndexOf('=');
            int cIdx = s.LastIndexOf(':');
            int qIdx = (eqIdx >= 0 && cIdx > eqIdx + 1) ? s.LastIndexOf('?', cIdx - 1) : -1;
            if (eqIdx > 0 && qIdx > eqIdx && cIdx > qIdx &&
                !s.StartsWith("pt=", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("logic=", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("condition=", StringComparison.OrdinalIgnoreCase))
            {
                parsed.AllArgs.Clear();
                parsed.AllArgs["pt"] = s.Substring(0, eqIdx).Trim().TrimStart('$');
                parsed.AllArgs["logic"] = s.Substring(eqIdx + 1, qIdx - eqIdx - 1).Trim();
                parsed.AllArgs["true"] = s.Substring(qIdx + 1, cIdx - qIdx - 1).Trim();
                parsed.AllArgs["false"] = s.Substring(cIdx + 1).Trim();
            }
        }
        else if (key == "Point&Path@InsertOnePoint")
        {
            int eqIdx = s.IndexOf('=');
            if (eqIdx > 0 && !s.StartsWith("pt=", StringComparison.OrdinalIgnoreCase))
            {
                parsed.AllArgs.Clear();
                parsed.AllArgs["pt"] = s.Substring(0, eqIdx).Trim().TrimStart('$');
                parsed.AllArgs["val"] = s.Substring(eqIdx + 1).Trim();
            }
        }
    }

    // ======================== MOTION / CAPTURE / DAQ / USER / PYTHON Builders ========================

    private static string BuildDriverInitLegacy(ParsedCommandParams parsed, string deviceKey, params (string Key, string LegacyKey)[] mappedOpts)
    {
        var items = new List<string>();
        string dev = parsed.Get(deviceKey);
        string drv = parsed.Get("driver");
        if (!string.IsNullOrWhiteSpace(dev)) items.Add(dev);
        if (!string.IsNullOrWhiteSpace(drv)) items.Add(drv);

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { deviceKey, "driver", "options" };
        foreach (var (k, legacyK) in mappedOpts)
        {
            known.Add(k);
            if (parsed.TryGet(k, out var v) && !string.IsNullOrWhiteSpace(v))
                items.Add($"{legacyK}={v}");
        }

        if (parsed.TryGet("options", out var opts) && !string.IsNullOrWhiteSpace(opts))
            items.Add(opts);

        foreach (var kv in parsed.AllArgs)
        {
            if (kv.Key.StartsWith("Arg", StringComparison.OrdinalIgnoreCase) || known.Contains(kv.Key)) continue;
            if (!string.IsNullOrWhiteSpace(kv.Value))
                items.Add($"{kv.Key}={kv.Value}");
        }

        return string.Join(", ", items);
    }

    private static void NormalizeLegacyDriverExtrasToV2(CommandSchema schema, ParsedCommandParams parsed)
    {
        var extras = new List<string>();
        foreach (var kv in parsed.RequiredArgs.Concat(parsed.OptionalArgs))
        {
            if (schema.FindParam(kv.Key) == null && !string.IsNullOrWhiteSpace(kv.Value))
            {
                extras.Add($"{kv.Key}={kv.Value}");
            }
        }
        if (extras.Count > 0)
        {
            string existing = parsed.Get("options");
            parsed.AllArgs["options"] = string.IsNullOrWhiteSpace(existing)
                ? string.Join(", ", extras)
                : $"{existing}, {string.Join(", ", extras)}";
        }
    }

    private static string BuildMotionGoLegacy(ParsedCommandParams parsed)
    {
        string coords = parsed.Get("coords").Trim();
        if (coords.StartsWith('(') && coords.EndsWith(')') && coords.Length >= 2)
            coords = coords.Substring(1, coords.Length - 2).Trim();
        return JoinNonEmpty(", ", parsed.Get("mot"), parsed.Get("axes"), parsed.Get("speed"), coords);
    }

    private static string BuildMotionGoSafeLegacy(ParsedCommandParams parsed, string tail)
    {
        var items = new List<string>();
        string mot = parsed.Get("mot");
        string speed = parsed.Get("speed");
        string pt = parsed.Get("pt");
        if (!string.IsNullOrWhiteSpace(mot)) items.Add(mot);
        if (!string.IsNullOrWhiteSpace(speed)) items.Add(speed);
        if (!string.IsNullOrWhiteSpace(pt)) items.Add(pt);

        string safeAxis = parsed.Get("safeAxis", "Z").Trim().ToUpperInvariant();
        if (safeAxis != "X" && safeAxis != "Y") safeAxis = "Z";
        if (parsed.TryGet("safePos", out var safePos) && !string.IsNullOrWhiteSpace(safePos))
        {
            items.Add($"{safeAxis}={safePos}");
        }
        else if (!safeAxis.Equals("Z", StringComparison.OrdinalIgnoreCase))
        {
            items.Add($"{safeAxis}=0");
        }

        if (!string.IsNullOrWhiteSpace(tail)) items.Add(tail);
        return string.Join(", ", items);
    }

    private static string BuildDeviceTimeoutLegacy(ParsedCommandParams parsed, string deviceKey, string tail)
    {
        var items = new List<string>();
        string dev = parsed.Get(deviceKey);
        if (!string.IsNullOrWhiteSpace(dev)) items.Add(dev);
        if (parsed.TryGet("timeoutSec", out var tSec) && !string.IsNullOrWhiteSpace(tSec))
            items.Add($"TimeOut={tSec}");
        if (!string.IsNullOrWhiteSpace(tail)) items.Add(tail);
        return string.Join(", ", items);
    }

    private static string BuildCalibrateManualLegacy(ParsedCommandParams parsed)
    {
        var items = new List<string>();
        string imaq = parsed.Get("imaq");
        if (!string.IsNullOrWhiteSpace(imaq)) items.Add(imaq);
        if (parsed.TryGet("scale", out var sc) && !string.IsNullOrWhiteSpace(sc)) items.Add($"Scale={sc}");
        if (parsed.TryGet("originX", out var ox) && !string.IsNullOrWhiteSpace(ox)) items.Add($"OriginX={ox}");
        if (parsed.TryGet("originY", out var oy) && !string.IsNullOrWhiteSpace(oy)) items.Add($"OriginY={oy}");
        if (parsed.TryGet("angle", out var ang) && !string.IsNullOrWhiteSpace(ang)) items.Add($"Angle={ang}");
        return string.Join(", ", items);
    }

    private static string BuildCaptureMatchLegacy(ParsedCommandParams parsed)
    {
        var items = new List<string>();
        string imaq = parsed.Get("imaq");
        string tpl = parsed.Get("template");
        string scoreVar = parsed.Get("var");
        string pt = parsed.Get("pt");
        string angle = parsed.Get("angle");

        if (!string.IsNullOrWhiteSpace(imaq)) items.Add(imaq);
        if (!string.IsNullOrWhiteSpace(tpl)) items.Add(tpl);
        if (!string.IsNullOrWhiteSpace(scoreVar))
        {
            items.Add(scoreVar);
            if (!string.IsNullOrWhiteSpace(pt)) items.Add(pt);
        }
        else if (!string.IsNullOrWhiteSpace(pt))
        {
            // 若僅指定 pt 未指定分數變數，保留 3 參數相容模式
            items.Add(pt);
        }
        if (!string.IsNullOrWhiteSpace(angle)) items.Add(angle);
        return string.Join(", ", items);
    }

    private static string BuildWaveFormGenerateLegacy(ParsedCommandParams parsed)
    {
        var items = new List<string>();
        string wav = parsed.Get("wav");
        if (!string.IsNullOrWhiteSpace(wav)) items.Add(wav);
        if (parsed.TryGet("type", out var t) && !string.IsNullOrWhiteSpace(t)) items.Add(t);
        if (parsed.TryGet("rate", out var r) && !string.IsNullOrWhiteSpace(r)) items.Add($"Rate={r}");
        if (parsed.TryGet("count", out var c) && !string.IsNullOrWhiteSpace(c)) items.Add($"Count={c}");
        if (parsed.TryGet("amp", out var a) && !string.IsNullOrWhiteSpace(a)) items.Add($"Amp={a}");
        if (parsed.TryGet("offset", out var o) && !string.IsNullOrWhiteSpace(o)) items.Add($"Offset={o}");
        return string.Join(", ", items);
    }

    private static string BuildMergeWaveFormLegacy(ParsedCommandParams parsed)
    {
        var items = new List<string>();
        string wav = parsed.Get("wav");
        if (!string.IsNullOrWhiteSpace(wav)) items.Add(wav);
        items.AddRange(parsed.GetList("wavs"));
        return string.Join(", ", items);
    }

    private static string BuildDaqCompareRangeLegacy(ParsedCommandParams parsed)
    {
        var items = new List<string>();
        string wav = parsed.Get("wav");
        string max = parsed.Get("max");
        string min = parsed.Get("min");
        if (!string.IsNullOrWhiteSpace(wav)) items.Add(wav);
        if (!string.IsNullOrWhiteSpace(max)) items.Add(max);
        if (!string.IsNullOrWhiteSpace(min)) items.Add(min);
        bool hasEnd = parsed.TryGet("end", out var endVal) && !string.IsNullOrWhiteSpace(endVal);
        bool hasStart = parsed.TryGet("start", out var startVal) && !string.IsNullOrWhiteSpace(startVal);
        if (hasStart || hasEnd)
        {
            items.Add(hasStart ? startVal : "0");
            if (hasEnd) items.Add(endVal);
        }
        return string.Join(", ", items);
    }

    private static string BuildUserQueryControlsLegacy(ParsedCommandParams parsed)
    {
        var items = new List<string>();
        string outVar = parsed.Get("var");
        if (!string.IsNullOrWhiteSpace(outVar)) items.Add(outVar);
        if (parsed.TryGet("panel", out var p) && !string.IsNullOrWhiteSpace(p) && !p.Equals("Active", StringComparison.OrdinalIgnoreCase))
            items.Add($"Panel={p}");
        if (parsed.TryGet("role", out var r) && !string.IsNullOrWhiteSpace(r) && !r.Equals("All", StringComparison.OrdinalIgnoreCase))
            items.Add($"Role={r}");
        if (parsed.TryGet("kind", out var k) && !string.IsNullOrWhiteSpace(k) && !k.Equals("All", StringComparison.OrdinalIgnoreCase))
            items.Add($"Kind={k}");
        return string.Join(", ", items);
    }

    private static string BuildPythonSessionOnlyLegacy(ParsedCommandParams parsed)
    {
        string sess = parsed.Get("session");
        return string.IsNullOrWhiteSpace(sess) ? string.Empty : $"Session={sess}";
    }

    private static string AppendOptionalPythonSession(string body, ParsedCommandParams parsed)
    {
        string sess = parsed.Get("session");
        if (string.IsNullOrWhiteSpace(sess)) return body;
        return string.IsNullOrWhiteSpace(body) ? $"Session={sess}" : $"{body}, Session={sess}";
    }

    private static string BuildPythonScriptLegacy(ParsedCommandParams parsed, string tail)
    {
        string code = parsed.Get("code");
        string sess = parsed.Get("session");
        string res = string.IsNullOrWhiteSpace(sess) ? code : $"Session={sess}, {code}";
        if (!string.IsNullOrWhiteSpace(tail)) res = $"{res}\n{tail}";
        return res;
    }

    private static string BuildPythonSetVariableLegacy(ParsedCommandParams parsed)
    {
        var items = new List<string>();
        string v = parsed.Get("var");
        if (!string.IsNullOrWhiteSpace(v)) items.Add(v);
        if (parsed.TryGet("val", out var val) && !string.IsNullOrWhiteSpace(val)) items.Add(val);
        if (parsed.TryGet("py", out var py) && !string.IsNullOrWhiteSpace(py)) items.Add($"PythonName={py}");
        if (parsed.TryGet("session", out var s) && !string.IsNullOrWhiteSpace(s)) items.Add($"Session={s}");
        return string.Join(", ", items);
    }

    private static string BuildPythonSetLegacy(ParsedCommandParams parsed, string srcKey)
    {
        var items = new List<string>();
        string src = parsed.Get(srcKey);
        if (!string.IsNullOrWhiteSpace(src)) items.Add(src);
        if (parsed.TryGet("py", out var py) && !string.IsNullOrWhiteSpace(py)) items.Add($"PythonName={py}");
        if (parsed.TryGet("session", out var s) && !string.IsNullOrWhiteSpace(s)) items.Add($"Session={s}");
        return string.Join(", ", items);
    }

    private static string BuildPythonGetLegacy(ParsedCommandParams parsed, string outKey)
    {
        var items = new List<string>();
        string py = parsed.Get("py");
        if (!string.IsNullOrWhiteSpace(py)) items.Add(py);
        if (parsed.TryGet(outKey, out var arcos) && !string.IsNullOrWhiteSpace(arcos)) items.Add($"ARCOSName={arcos}");
        if (parsed.TryGet("session", out var s) && !string.IsNullOrWhiteSpace(s)) items.Add($"Session={s}");
        return string.Join(", ", items);
    }

    private static string AppendOptionalSessionKey(string body, ParsedCommandParams parsed)
    {
        string sess = parsed.Get("session");
        if (string.IsNullOrWhiteSpace(sess)) return body;
        return string.IsNullOrWhiteSpace(body) ? $"Session={sess}" : $"{body}, Session={sess}";
    }

    private static string BuildRedisBlPopLegacy(ParsedCommandParams parsed, string tail)
    {
        var items = new List<string>();
        string k = parsed.Get("key");
        if (!string.IsNullOrWhiteSpace(k)) items.Add(k);
        if (parsed.TryGet("session", out var sess) && !string.IsNullOrWhiteSpace(sess))
            items.Add($"Session={sess}");
        if (parsed.TryGet("timeoutSec", out var tSec) && !string.IsNullOrWhiteSpace(tSec))
            items.Add($"TimeOut={tSec}");
        if (!string.IsNullOrWhiteSpace(tail)) items.Add(tail);
        return string.Join(", ", items);
    }

    private static string BuildAiAgentPromptLegacy(ParsedCommandParams parsed, string tail)
    {
        string prompt = parsed.Get("prompt");
        if (parsed.GetBool("bypass", false) && !prompt.StartsWith('~'))
            prompt = "~" + prompt;
        return JoinNonEmpty(", ", prompt, tail);
    }
}
