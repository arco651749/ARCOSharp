using System.Globalization;
using System.Threading.Channels;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;
using ARCOS.Core.Model;

namespace ARCOS.Driver.MockMotion;

/// <summary>
/// 模擬運動與機械手臂控制器驅動 (MockMotionDriver)
/// 支援：
/// 1. GetPos 同時寫入笛卡爾座標 ($motor) 與關節座標 ($motor#Joint)
/// 2. Driver 自主分析點位為笛卡爾座標或 #Joint 關節座標 (含 Path 內混用)，自動切換對應 Robot 指令
/// 3. GetState 自動寫入 $(motor)#Busy?、$(motor)#Alarm? 與控制器本身原生 ErrorCode (Motion#ErrorCode)
/// 4. SetTool 設定 Robot 工具座標系 ($motor, $toolpoint)
/// 5. ExtraCMD 執行各品牌 Robot 原生控制器指令
/// 6. SetDO / GetDI / GetDO API 供 IO Driver 跨驅動讀寫單一連線 Robot 上的自帶 I/O
/// </summary>
public class MockMotionDriver : IDriver
{
    private BasicEngines? _engines;
    private readonly Channel<DriverTicket> _ticketQueue = Channel.CreateUnbounded<DriverTicket>();
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    public string DeviceName { get; private set; } = string.Empty;
    public string Category => "Motion";
    public string DriverType => "MockMotion";

    /// <summary>
    /// 當前笛卡爾座標 (Cartesian: X, Y, Z, U, V, W)
    /// </summary>
    public MachinePoint CurrentPos { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>
    /// 當前關節座標 (Joint: J1~J6 儲存於 X~W，且 H["Coord"] = "Joint")
    /// </summary>
    public MachinePoint CurrentJointPos { get; } = new(0, 0, 0, 0, 0, 0, "Coord=Joint");

    public MachinePoint ToGoPos { get; } = new(0, 0, 0, 0, 0, 0);
    public List<MachinePoint> PathQueue { get; } = new();

    public bool IsBusy { get; private set; } = false;
    public bool IsHomed { get; private set; } = false;
    public bool IsAlarm { get; set; } = false;

    /// <summary>
    /// 控制器本身的原生錯誤碼 (0 代表正常，非 0 為控制器原生 Error Code)
    /// </summary>
    public int ControllerErrorCode { get; set; } = 0;

    /// <summary>
    /// 當前啟用的工具座標名稱與偏置 (由 Motion@SetTool 設定)
    /// </summary>
    public string CurrentToolName { get; private set; } = "Tool0";
    public MachinePoint CurrentToolOffset { get; private set; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>
    /// 最後一次執行的 Robot 運動指令型態 ("LINEAR_MOVE" 或 "JOINT_MOVE")
    /// </summary>
    public string LastRobotMoveMode { get; private set; } = "LINEAR_MOVE";

    /// <summary>
    /// 最後一次透過 ExtraCMD 下達至 Robot 控制器的原生指令字串
    /// </summary>
    public string LastExtraCommand { get; private set; } = string.Empty;

    /// <summary>
    /// 透過 Config 指令設定之控制器內部組態參數表
    /// </summary>
    public Dictionary<string, string> ConfigParameters { get; } = new(StringComparer.OrdinalIgnoreCase);

    // 手臂控制器自帶之專屬虛擬 DI / DO (供 IO Driver 透過 SetDO/GetDI/GetDO API 存取)
    private readonly Dictionary<int, bool> _digitalInputs = new();
    private readonly Dictionary<int, bool> _digitalOutputs = new();

    public event Action? MotionStateUpdated;

    public void Init(string deviceName, string param, BasicEngines engines)
    {
        DeviceName = deviceName;
        _engines = engines;
        CurrentJointPos.H["Coord"] = "Joint";

        // 初始化狀態回寫至 IoEngine 與 PointEngine (同時建立 $motor 與 $motor#Joint)
        UpdateEngines();

        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
    }

    public void PostTicket(DriverTicket ticket)
    {
        _ticketQueue.Writer.TryWrite(ticket);
    }

    public object? CreateDriverView()
    {
        return new MockMotionView(this);
    }

    private async Task WorkerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var ticket = await _ticketQueue.Reader.ReadAsync(token);
                await ExecuteCaseAsync(ticket, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // 容錯防禦
            }
        }
    }

    private async Task ExecuteCaseAsync(DriverTicket ticket, CancellationToken token)
    {
        string cmd = ticket.Command.Trim();

        switch (cmd)
        {
            case "Home":
                SetBusy(true);
                await Task.Delay(60, token);
                CurrentPos.X = 0; CurrentPos.Y = 0; CurrentPos.Z = 0;
                CurrentPos.U = 0; CurrentPos.V = 0; CurrentPos.W = 0;
                SyncJointFromCartesian();
                IsHomed = true;
                IsAlarm = false;
                ControllerErrorCode = 0;
                SetBusy(false);
                ticket.SetCompleted();
                break;

            case "Go":
            case "Go(SingleAxis)":
                SetBusy(true);
                try
                {
                    var targetPt = ParseGoCommandParam(ticket.Parameter);
                    LastRobotMoveMode = "LINEAR_MOVE";
                    RecordMoveMode(LastRobotMoveMode, "Go");
                    ApplyInfAxisMask(targetPt, isJoint: false);
                    await SimulateInterpolationAsync(isJoint: false, token);
                }
                finally
                {
                    SetBusy(false);
                    ticket.SetCompleted();
                }
                break;

            case "GoPoint":
            case "GoPoint(SingleAxis)":
                SetBusy(true);
                try
                {
                    var pt = ResolvePointFromTicketParam(ticket.Parameter, out bool isJoint, out string ptLabel);
                    LastRobotMoveMode = isJoint ? "JOINT_MOVE" : "LINEAR_MOVE";
                    RecordMoveMode(LastRobotMoveMode, ptLabel);
                    ApplyInfAxisMask(pt, isJoint);
                    await SimulateInterpolationAsync(isJoint, token);
                }
                finally
                {
                    SetBusy(false);
                    ticket.SetCompleted();
                }
                break;

            case "ClearPath":
                PathQueue.Clear();
                ticket.SetCompleted();
                break;

            case "AddPath":
                {
                    var pt = ResolvePointFromTicketParam(ticket.Parameter, out bool isJoint, out _);
                    var cloned = pt.Clone();
                    if (isJoint)
                    {
                        cloned.H["Coord"] = "Joint";
                    }
                    PathQueue.Add(cloned);
                    ticket.SetCompleted();
                }
                break;

            case "RunPath":
                SetBusy(true);
                try
                {
                    foreach (var pt in PathQueue.ToList())
                    {
                        if (token.IsCancellationRequested) break;
                        bool isJoint = IsJointPoint(pt, null);
                        LastRobotMoveMode = isJoint ? "JOINT_MOVE" : "LINEAR_MOVE";
                        RecordMoveMode(LastRobotMoveMode, isJoint ? "Path#Joint" : "Path#Cartesian");
                        ApplyInfAxisMask(pt, isJoint);
                        await SimulateInterpolationAsync(isJoint, token);
                    }
                    PathQueue.Clear();
                }
                finally
                {
                    SetBusy(false);
                    ticket.SetCompleted();
                }
                break;

            case "Stop":
                SetBusy(false);
                ticket.SetCompleted();
                break;

            case "GetPos":
                ExecuteGetPosCommand(ticket);
                break;

            case "GetState":
                ExecuteGetStateCommand(ticket);
                break;

            case "Config":
                ExecuteConfigCommand(ticket.Parameter, ticket);
                break;

            case "SetTool":
                ExecuteSetToolCommand(ticket.Parameter, ticket);
                break;

            case "ExtraCMD":
                await ExecuteExtraCommandAsync(ticket.Parameter, ticket, token);
                break;

            case "SetDO":
                ExecuteSetDOCommand(ticket.Parameter, ticket);
                break;

            case "GetDI":
                ExecuteGetDICommand(ticket.Parameter, ticket);
                break;

            case "GetDO":
                ExecuteGetDOCommand(ticket.Parameter, ticket);
                break;

            case "Close":
                Shutdown();
                ticket.SetCompleted();
                break;

            case "UpdateInfo":
            default:
                UpdateEngines();
                ticket.SetCompleted();
                break;
        }

        MotionStateUpdated?.Invoke();
    }

    /// <summary>
    /// GetPos：同時寫入笛卡爾座標 ($motor) 與關節座標 ($motor#Joint)
    /// </summary>
    public virtual void ExecuteGetPosCommand(DriverTicket ticket)
    {
        UpdateEngines();
        if (_engines != null && !string.IsNullOrEmpty(DeviceName))
        {
            var cartClone = CurrentPos.Clone();
            var jointClone = CurrentJointPos.Clone();
            jointClone.H["Coord"] = "Joint";

            _engines.Point.SetPoint(DeviceName, cartClone);
            _engines.Point.SetPoint($"{DeviceName}#Joint", jointClone);
        }
        ticket.SetCompleted(DriverResponse.Ok(CurrentPos.Clone()));
    }

    /// <summary>
    /// GetState：寫入 IO 的 $(motor)#Busy? 與 $(motor)#Alarm?，並回報控制器本身的 ErrorCode
    /// </summary>
    public virtual void ExecuteGetStateCommand(DriverTicket ticket)
    {
        if (_engines != null && !string.IsNullOrEmpty(DeviceName))
        {
            _engines.Io.SetPort($"{DeviceName}#Busy?", IsBusy);
            _engines.Io.SetPort($"{DeviceName}#Done?", !IsBusy);
            _engines.Io.SetPort($"{DeviceName}#Alarm?", IsAlarm);
            _engines.Variable.Set("Motion#ErrorCode", ControllerErrorCode.ToString(CultureInfo.InvariantCulture), "Global");
        }

        var stateDict = new Dictionary<string, object>
        {
            ["IsBusy"] = IsBusy,
            ["IsHomed"] = IsHomed,
            ["IsAlarm"] = IsAlarm,
            ["ErrorCode"] = ControllerErrorCode
        };

        if (IsAlarm || ControllerErrorCode != 0)
        {
            int errCode = ControllerErrorCode != 0 ? ControllerErrorCode : -1;
            ticket.SetFailed(errCode.ToString(CultureInfo.InvariantCulture), $"Controller Error Code: {errCode}");
            return;
        }

        ticket.SetCompleted(DriverResponse.Ok(stateDict));
    }

    /// <summary>
    /// SetTool：設定 Robot 工具座標系 ($motion, $toolpoint)
    /// </summary>
    public virtual void ExecuteSetToolCommand(string rawParameter, DriverTicket ticket)
    {
        ArgParaHelper.ParseKeyValueArgs(rawParameter, out var posArgs);
        string toolExpr = string.Empty;
        if (posArgs.Count >= 2 && string.Equals(posArgs[0], DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            toolExpr = posArgs[1];
        }
        else if (posArgs.Count >= 1)
        {
            toolExpr = posArgs[^1];
        }

        if (string.IsNullOrWhiteSpace(toolExpr))
        {
            ticket.SetFailed("INVALID_TOOL", "SetTool requires a tool point name or coordinate.");
            return;
        }

        CurrentToolName = toolExpr.Trim();
        if (_engines != null)
        {
            try
            {
                if (CurrentToolName.StartsWith('(') && CurrentToolName.EndsWith(')'))
                {
                    CurrentToolOffset = MachinePoint.ParseCoords(CurrentToolName);
                }
                else
                {
                    CurrentToolOffset = _engines.Point.GetPoint(CurrentToolName).Clone();
                }
            }
            catch
            {
                CurrentToolOffset = new MachinePoint(0, 0, 0, 0, 0, 0);
            }

            _engines.Variable.Set($"{DeviceName}#Tool", CurrentToolName, "Global");
            _engines.Point.SetPoint($"{DeviceName}#Tool", CurrentToolOffset.Clone());
            _engines.Record.Record("SYSTEMLOG", $"[MockMotion:{DeviceName}] [SetTool] Active tool set to '{CurrentToolName}' ({CurrentToolOffset.X},{CurrentToolOffset.Y},{CurrentToolOffset.Z})");
        }

        ticket.SetCompleted(DriverResponse.Ok(CurrentToolName));
    }

    /// <summary>
    /// 專供 Motion@ExtraCMD 調用之 Robot 原生控制器指令處理函式。
    /// 支援 MockMotion 原生指令：
    /// - PWR ON / PWR OFF
    /// - SPEED &lt;val&gt;
    /// - ACCEL &lt;val&gt;
    /// - DOUT &lt;idx&gt;,&lt;0|1&gt;
    /// - ERR &lt;code&gt; / RST
    /// - 其他任意原生字串透傳
    /// </summary>
    public virtual async Task ExecuteExtraCommandAsync(string rawParameter, DriverTicket ticket, CancellationToken token)
    {
        string nativeCmd = ExtractNativePayload(rawParameter);
        LastExtraCommand = nativeCmd;

        // 解析 MockMotion 原生控制器測試指令
        if (nativeCmd.StartsWith("ERR ", StringComparison.OrdinalIgnoreCase))
        {
            string codeStr = nativeCmd.Substring(4).Trim();
            if (int.TryParse(codeStr, out int errCode))
            {
                ControllerErrorCode = errCode;
                IsAlarm = errCode != 0;
            }
        }
        else if (nativeCmd.Equals("RST", StringComparison.OrdinalIgnoreCase) ||
                 nativeCmd.Equals("RESET", StringComparison.OrdinalIgnoreCase))
        {
            ControllerErrorCode = 0;
            IsAlarm = false;
        }
        else if (nativeCmd.StartsWith("SPEED ", StringComparison.OrdinalIgnoreCase))
        {
            ConfigParameters["Speed"] = nativeCmd.Substring(6).Trim();
        }
        else if (nativeCmd.StartsWith("ACCEL ", StringComparison.OrdinalIgnoreCase))
        {
            ConfigParameters["Accel"] = nativeCmd.Substring(6).Trim();
        }
        else if (nativeCmd.StartsWith("DOUT ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = nativeCmd.Substring(5).Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out int dIdx) && int.TryParse(parts[1], out int dVal))
            {
                SetDigitalOutput(dIdx, dVal != 0);
            }
        }

        UpdateEngines();

        if (_engines != null)
        {
            _engines.Variable.Set($"{DeviceName}#LastExtraCMD", nativeCmd, "Global");
            _engines.Record.Record("SYSTEMLOG", $"[MockMotion:{DeviceName}] [ExtraCMD] Native robot command executed: '{nativeCmd}'");
        }

        await Task.Delay(20, token);
        ticket.SetCompleted(DriverResponse.Ok(nativeCmd));
    }

    /// <summary>
    /// 專供 Motion@Config 調用之控制器參數組態設定函式 ($motor, $parameter, $value)
    /// </summary>
    public virtual void ExecuteConfigCommand(string rawParameter, DriverTicket ticket)
    {
        var kv = ArgParaHelper.ParseKeyValueArgs(rawParameter, out var posArgs);
        foreach (var pair in kv)
        {
            if (!pair.Key.StartsWith('_'))
            {
                ConfigParameters[pair.Key] = pair.Value;
            }
        }

        if (posArgs.Count >= 3)
        {
            ConfigParameters[posArgs[1]] = posArgs[2];
        }
        else if (posArgs.Count == 2 && !string.Equals(posArgs[0], DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            ConfigParameters[posArgs[0]] = posArgs[1];
        }

        ticket.SetCompleted(DriverResponse.Ok());
    }

    #region Robot 內部 DO / DI API (供 IO Driver 跨驅動調用)

    public void SetDigitalOutput(int index, bool value)
    {
        _digitalOutputs[index] = value;
        _engines?.Io.SetPort($"{DeviceName}#DO{index}", value);
    }

    public bool GetDigitalOutput(int index)
    {
        return _digitalOutputs.TryGetValue(index, out bool val) && val;
    }

    public void SetDigitalInput(int index, bool value)
    {
        _digitalInputs[index] = value;
        _engines?.Io.SetPort($"{DeviceName}#DI{index}", value);
    }

    public bool GetDigitalInput(int index)
    {
        return _digitalInputs.TryGetValue(index, out bool val) && val;
    }

    private void ExecuteSetDOCommand(string parameter, DriverTicket ticket)
    {
        var doArgs = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        if (doArgs.TryGetValue("Index", out var doIdx) && int.TryParse(doIdx, out int dIdx) &&
            doArgs.TryGetValue("Value", out var doVal))
        {
            bool bVal = doVal.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                        doVal.Equals("On", StringComparison.OrdinalIgnoreCase) ||
                        (int.TryParse(doVal, out int iVal) && iVal != 0);
            SetDigitalOutput(dIdx, bVal);
            ticket.SetCompleted(DriverResponse.Ok(bVal));
        }
        else if (posArgs.Count >= 2 && int.TryParse(posArgs[^2], out int pIdx))
        {
            string vStr = posArgs[^1];
            bool bVal = vStr.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                        vStr.Equals("On", StringComparison.OrdinalIgnoreCase) ||
                        (int.TryParse(vStr, out int iVal) && iVal != 0);
            SetDigitalOutput(pIdx, bVal);
            ticket.SetCompleted(DriverResponse.Ok(bVal));
        }
        else
        {
            ticket.SetFailed("INVALID_PARAM", "SetDO requires Index and Value.");
        }
    }

    private void ExecuteGetDICommand(string parameter, DriverTicket ticket)
    {
        var diArgs = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        if (diArgs.TryGetValue("Index", out var diIdx) && int.TryParse(diIdx, out int iIdx))
        {
            bool val = GetDigitalInput(iIdx);
            ticket.SetCompleted(DriverResponse.Ok(val));
        }
        else if (posArgs.Count >= 1 && int.TryParse(posArgs[^1], out int pIdx))
        {
            bool val = GetDigitalInput(pIdx);
            ticket.SetCompleted(DriverResponse.Ok(val));
        }
        else
        {
            ticket.SetFailed("INVALID_PARAM", "GetDI requires Index.");
        }
    }

    private void ExecuteGetDOCommand(string parameter, DriverTicket ticket)
    {
        var gDoArgs = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        if (gDoArgs.TryGetValue("Index", out var gIdx) && int.TryParse(gIdx, out int gdIdx))
        {
            bool val = GetDigitalOutput(gdIdx);
            ticket.SetCompleted(DriverResponse.Ok(val));
        }
        else if (posArgs.Count >= 1 && int.TryParse(posArgs[^1], out int pIdx))
        {
            bool val = GetDigitalOutput(pIdx);
            ticket.SetCompleted(DriverResponse.Ok(val));
        }
        else
        {
            ticket.SetFailed("INVALID_PARAM", "GetDO requires Index.");
        }
    }

    #endregion

    /// <summary>
    /// Driver 自主解析點位與判定是否為 #Joint 關節座標：
    /// 支援：
    /// 1. 原始腳本參數："$motor, $speed, $point" 或 "$motor, $speed, ($X,$Y,$Z,$U,$V,$W)"
    /// 2. 直接點位名稱："P1"、"P1#Joint"、"Tray[2]"
    /// 3. Key=Value 座標："Speed=100,X=10,Y=20..." (可含 Coord=Joint)
    /// </summary>
    private MachinePoint ResolvePointFromTicketParam(string rawParam, out bool isJoint, out string resolvedLabel)
    {
        isJoint = false;
        resolvedLabel = rawParam?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawParam)) return MachinePoint.AllInf();

        string trimmed = rawParam.Trim();

        // 情況 A：Key=Value 格式 (如 GoSafe 第 1 階段 "Speed=100,Z=0" 或直接指定 X=...,Y=...)
        if (trimmed.Contains("X=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Y=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Z=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("U=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("V=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("W=", StringComparison.OrdinalIgnoreCase))
        {
            var pt = ParsePointFromParam(trimmed, out bool kvJoint);
            isJoint = kvJoint;
            return pt;
        }

        // 情況 B：檢查是否含括號座標 "$motor, $speed, (x,y,z,...)"
        int openParen = trimmed.IndexOf('(');
        int closeParen = trimmed.LastIndexOf(')');
        if (openParen >= 0 && closeParen > openParen)
        {
            string prefix = trimmed.Substring(0, openParen).Trim();
            // 若是 P1@Z(50) 這種軸覆寫語法，不是直接括號座標
            if (prefix.EndsWith('@') || (prefix.Contains('@') && !prefix.EndsWith(',')))
            {
                // 交給下方點位名稱解析
            }
            else
            {
                string coordStr = trimmed.Substring(openParen, closeParen - openParen + 1);
                var pt = MachinePoint.ParseCoords(coordStr);
                isJoint = IsJointPoint(pt, coordStr);
                resolvedLabel = coordStr;
                return pt;
            }
        }

        // 情況 C：拆解位置參數 (例如 "$motor, $speed, $point" 或 "$point")
        ArgParaHelper.ParseKeyValueArgs(trimmed, out var posArgs);
        string pointExpr = trimmed;
        if (posArgs.Count >= 3 && string.Equals(posArgs[0], DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            pointExpr = posArgs[2];
        }
        else if (posArgs.Count == 2 && string.Equals(posArgs[0], DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            pointExpr = posArgs[1];
        }
        else if (posArgs.Count >= 1)
        {
            pointExpr = posArgs[^1];
        }

        resolvedLabel = pointExpr.Trim();
        if (_engines != null && !string.IsNullOrEmpty(resolvedLabel))
        {
            var pt = _engines.Point.GetPoint(resolvedLabel);
            isJoint = IsJointPoint(pt, resolvedLabel);
            if (isJoint)
            {
                pt.H["Coord"] = "Joint";
            }
            return pt;
        }

        isJoint = resolvedLabel.Contains("#Joint", StringComparison.OrdinalIgnoreCase);
        return MachinePoint.AllInf();
    }

    private static bool IsJointPoint(MachinePoint pt, string? pointName)
    {
        if (!string.IsNullOrWhiteSpace(pointName) &&
            pointName.Contains("#Joint", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string coordTag = pt.GetHString("Coord");
        if (coordTag.Equals("Joint", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string valTag = pt.GetHString("Value");
        if (valTag.Equals("Joint", StringComparison.OrdinalIgnoreCase) ||
            valTag.Equals("#Joint", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private void RecordMoveMode(string moveMode, string targetLabel)
    {
        if (_engines != null && !string.IsNullOrEmpty(DeviceName))
        {
            _engines.Variable.Set($"{DeviceName}#LastMoveMode", moveMode == "JOINT_MOVE" ? "Joint" : "Cartesian", "Global");
            _engines.Record.Record("SYSTEMLOG", $"[MockMotion:{DeviceName}] [{moveMode}] Target='{targetLabel}' Tool='{CurrentToolName}'");
        }
    }

    private MachinePoint ParseGoCommandParam(string rawParam)
    {
        if (rawParam.Contains("X=", StringComparison.OrdinalIgnoreCase) ||
            rawParam.Contains("Y=", StringComparison.OrdinalIgnoreCase) ||
            rawParam.Contains("Z=", StringComparison.OrdinalIgnoreCase))
        {
            return ParsePointFromParam(rawParam, out _);
        }

        // 支援原始 "$motor, $axis, $speed, $X(,$Y,$Z,...)"
        ArgParaHelper.ParseKeyValueArgs(rawParam, out var posArgs);
        var pt = MachinePoint.AllInf();
        if (posArgs.Count >= 4)
        {
            string axis = posArgs[1].ToUpperInvariant();
            int valIdx = 3;
            char[] axisOrder = { 'X', 'Y', 'Z', 'U', 'V', 'W' };
            foreach (char ax in axisOrder)
            {
                if (axis.Contains(ax) && valIdx < posArgs.Count &&
                    double.TryParse(posArgs[valIdx], NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                {
                    switch (ax)
                    {
                        case 'X': pt.X = v; break;
                        case 'Y': pt.Y = v; break;
                        case 'Z': pt.Z = v; break;
                        case 'U': pt.U = v; break;
                        case 'V': pt.V = v; break;
                        case 'W': pt.W = v; break;
                    }
                    valIdx++;
                }
            }
        }
        return pt;
    }

    private string ExtractNativePayload(string rawParameter)
    {
        if (string.IsNullOrWhiteSpace(rawParameter)) return string.Empty;
        string trimmed = rawParameter.Trim();
        int commaIdx = trimmed.IndexOf(',');
        if (commaIdx > 0)
        {
            string firstToken = trimmed.Substring(0, commaIdx).Trim();
            if (string.Equals(firstToken, DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Substring(commaIdx + 1).Trim();
            }
        }
        return trimmed;
    }

    /// <summary>
    /// Inf 軸屏蔽比對：支援笛卡爾基準或 Joint 關節基準
    /// </summary>
    private void ApplyInfAxisMask(MachinePoint target, bool isJoint)
    {
        var basePos = isJoint ? CurrentJointPos : CurrentPos;
        ToGoPos.X = !double.IsInfinity(target.X) ? target.X : basePos.X;
        ToGoPos.Y = !double.IsInfinity(target.Y) ? target.Y : basePos.Y;
        ToGoPos.Z = !double.IsInfinity(target.Z) ? target.Z : basePos.Z;
        ToGoPos.U = !double.IsInfinity(target.U) ? target.U : basePos.U;
        ToGoPos.V = !double.IsInfinity(target.V) ? target.V : basePos.V;
        ToGoPos.W = !double.IsInfinity(target.W) ? target.W : basePos.W;

        _engines?.Point.SetPoint($"{DeviceName}_ToGo", ToGoPos);
    }

    /// <summary>
    /// 模擬平滑插補運動 (依據 isJoint 決定更新關節座標或笛卡爾座標，並同步另一座標系)
    /// </summary>
    private async Task SimulateInterpolationAsync(bool isJoint, CancellationToken token)
    {
        int steps = 5;
        var activePos = isJoint ? CurrentJointPos : CurrentPos;

        double dx = (ToGoPos.X - activePos.X) / steps;
        double dy = (ToGoPos.Y - activePos.Y) / steps;
        double dz = (ToGoPos.Z - activePos.Z) / steps;
        double du = (ToGoPos.U - activePos.U) / steps;
        double dv = (ToGoPos.V - activePos.V) / steps;
        double dw = (ToGoPos.W - activePos.W) / steps;

        for (int i = 0; i < steps; i++)
        {
            if (token.IsCancellationRequested) break;
            activePos.X += dx;
            activePos.Y += dy;
            activePos.Z += dz;
            activePos.U += du;
            activePos.V += dv;
            activePos.W += dw;

            if (isJoint) SyncCartesianFromJoint();
            else SyncJointFromCartesian();

            UpdateEngines();
            MotionStateUpdated?.Invoke();
            await Task.Delay(15, token);
        }

        activePos.X = ToGoPos.X;
        activePos.Y = ToGoPos.Y;
        activePos.Z = ToGoPos.Z;
        activePos.U = ToGoPos.U;
        activePos.V = ToGoPos.V;
        activePos.W = ToGoPos.W;

        if (isJoint) SyncCartesianFromJoint();
        else SyncJointFromCartesian();

        UpdateEngines();
    }

    private void SyncJointFromCartesian()
    {
        CurrentJointPos.X = CurrentPos.X;
        CurrentJointPos.Y = CurrentPos.Y;
        CurrentJointPos.Z = CurrentPos.Z;
        CurrentJointPos.U = CurrentPos.U;
        CurrentJointPos.V = CurrentPos.V;
        CurrentJointPos.W = CurrentPos.W;
        CurrentJointPos.H["Coord"] = "Joint";
    }

    private void SyncCartesianFromJoint()
    {
        CurrentPos.X = CurrentJointPos.X;
        CurrentPos.Y = CurrentJointPos.Y;
        CurrentPos.Z = CurrentJointPos.Z;
        CurrentPos.U = CurrentJointPos.U;
        CurrentPos.V = CurrentJointPos.V;
        CurrentPos.W = CurrentJointPos.W;
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        UpdateEngines();
    }

    private void UpdateEngines()
    {
        if (_engines != null && !string.IsNullOrEmpty(DeviceName))
        {
            _engines.Io.SetPort($"{DeviceName}#Busy?", IsBusy);
            _engines.Io.SetPort($"{DeviceName}#Done?", !IsBusy);
            _engines.Io.SetPort($"{DeviceName}#Homed?", IsHomed);
            _engines.Io.SetPort($"{DeviceName}#Alarm?", IsAlarm);

            var cartClone = CurrentPos.Clone();
            var jointClone = CurrentJointPos.Clone();
            jointClone.H["Coord"] = "Joint";

            _engines.Point.SetPoint($"{DeviceName}_CurPos", cartClone);
            _engines.Point.SetPoint($"{DeviceName}_CurJoint", jointClone);
            _engines.Point.SetPoint(DeviceName, cartClone);
            _engines.Point.SetPoint($"{DeviceName}#Joint", jointClone);
        }
    }

    private static MachinePoint ParsePointFromParam(string param, out bool isJoint)
    {
        var pt = MachinePoint.AllInf();
        var args = ArgParaHelper.ParseKeyValueArgs(param, out _);
        if (args.TryGetValue("X", out var x) && double.TryParse(x, NumberStyles.Any, CultureInfo.InvariantCulture, out double dx)) pt.X = dx;
        if (args.TryGetValue("Y", out var y) && double.TryParse(y, NumberStyles.Any, CultureInfo.InvariantCulture, out double dy)) pt.Y = dy;
        if (args.TryGetValue("Z", out var z) && double.TryParse(z, NumberStyles.Any, CultureInfo.InvariantCulture, out double dz)) pt.Z = dz;
        if (args.TryGetValue("U", out var u) && double.TryParse(u, NumberStyles.Any, CultureInfo.InvariantCulture, out double du)) pt.U = du;
        if (args.TryGetValue("V", out var v) && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out double dv)) pt.V = dv;
        if (args.TryGetValue("W", out var w) && double.TryParse(w, NumberStyles.Any, CultureInfo.InvariantCulture, out double dw)) pt.W = dw;

        isJoint = args.TryGetValue("Coord", out var coordVal) &&
                  coordVal.Equals("Joint", StringComparison.OrdinalIgnoreCase);
        if (isJoint)
        {
            pt.H["Coord"] = "Joint";
        }
        return pt;
    }

    public void Shutdown()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        Shutdown();
        _cts?.Dispose();
    }
}
