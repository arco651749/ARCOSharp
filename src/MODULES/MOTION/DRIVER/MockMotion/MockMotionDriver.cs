using System.Threading.Channels;
using MVCON.Core.Common;
using MVCON.Core.Drivers;
using MVCON.Core.Model;

namespace MVCON.Driver.MockMotion;

/// <summary>
/// 模擬運動控制器驅動 (MockMotionDriver)
/// 100% 對照 ARCOS 原版 TestCMD.vi 的 16 大標準指令 Case 分支，
/// 支援 Inf 軸屏蔽微步插補、Path 軌跡佇列、手臂 DO/DI、以及 #Busy? / #Done? 狀態回寫
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

    public MachinePoint CurrentPos { get; } = new(0, 0, 0, 0, 0, 0);
    public MachinePoint ToGoPos { get; } = new(0, 0, 0, 0, 0, 0);
    public List<MachinePoint> PathQueue { get; } = new();

    public bool IsBusy { get; private set; } = false;
    public bool IsHomed { get; private set; } = false;
    public bool IsAlarm { get; private set; } = false;

    // 手臂控制器自帶之專屬虛擬 DI / DO
    private readonly Dictionary<int, bool> _digitalInputs = new();
    private readonly Dictionary<int, bool> _digitalOutputs = new();

    public event Action? MotionStateUpdated;

    public void Init(string deviceName, string param, BasicEngines engines)
    {
        DeviceName = deviceName;
        _engines = engines;

        // 初始化狀態回寫至 IoEngine 與 PointEngine
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
                // 阻斷式等待來自 MVCON 呼叫端的私有信號 Ticket
                var ticket = await _ticketQueue.Reader.ReadAsync(token);
                await ExecuteCaseAsync(ticket, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 容錯防禦
            }
        }
    }

    /// <summary>
    /// 對照 TestCMD.vi 的 16 大 Case 分支處理
    /// </summary>
    private async Task ExecuteCaseAsync(DriverTicket ticket, CancellationToken token)
    {
        string cmd = ticket.Command.Trim();

        switch (cmd)
        {
            case "Home":
                SetBusy(true);
                await Task.Delay(100, token); // 模擬復歸動作
                CurrentPos.X = 0; CurrentPos.Y = 0; CurrentPos.Z = 0;
                CurrentPos.U = 0; CurrentPos.V = 0; CurrentPos.W = 0;
                IsHomed = true;
                SetBusy(false);
                ticket.SetCompleted();
                break;

            case "Go":
                SetBusy(true);
                // 參數為具體座標 X=...,Y=...
                var targetPt = ParsePointFromParam(ticket.Parameter);
                ApplyInfAxisMask(targetPt);
                await SimulateInterpolationAsync(targetPt, token);
                SetBusy(false);
                ticket.SetCompleted();
                break;

            case "GoPoint":
                SetBusy(true);
                // 參數為點位名稱 (如 P1, Tray[2])
                if (_engines != null)
                {
                    var pt = _engines.Point.GetPoint(ticket.Parameter);
                    ApplyInfAxisMask(pt);
                    await SimulateInterpolationAsync(pt, token);
                }
                SetBusy(false);
                ticket.SetCompleted();
                break;

            case "ClearPath":
                PathQueue.Clear();
                ticket.SetCompleted();
                break;

            case "AddPath":
                if (_engines != null)
                {
                    var pt = _engines.Point.GetPoint(ticket.Parameter);
                    PathQueue.Add(pt);
                }
                ticket.SetCompleted();
                break;

            case "RunPath":
                SetBusy(true);
                foreach (var pt in PathQueue.ToList())
                {
                    if (token.IsCancellationRequested) break;
                    ApplyInfAxisMask(pt);
                    await SimulateInterpolationAsync(pt, token);
                }
                PathQueue.Clear();
                SetBusy(false);
                ticket.SetCompleted();
                break;

            case "Stop":
                SetBusy(false);
                ticket.SetCompleted();
                break;

            case "GetPos":
                ticket.SetCompleted(DriverResponse.Ok(CurrentPos.Clone()));
                break;

            case "GetState":
                var stateDict = new Dictionary<string, object>
                {
                    ["IsBusy"] = IsBusy,
                    ["IsHomed"] = IsHomed,
                    ["IsAlarm"] = IsAlarm
                };
                ticket.SetCompleted(DriverResponse.Ok(stateDict));
                break;

            case "SetTool":
                // 設定工具座標系
                ticket.SetCompleted();
                break;

            case "ExtraCMD":
                // 執行手臂專屬指令或腳本
                await Task.Delay(50, token);
                ticket.SetCompleted();
                break;

            case "SetDO":
                // 格式：Index=0,Value=1
                var doArgs = ArgParaHelper.ParseKeyValueArgs(ticket.Parameter, out _);
                if (doArgs.TryGetValue("Index", out var doIdx) && int.TryParse(doIdx, out int dIdx) &&
                    doArgs.TryGetValue("Value", out var doVal) && int.TryParse(doVal, out int dVal))
                {
                    _digitalOutputs[dIdx] = dVal != 0;
                    ticket.SetCompleted();
                }
                else ticket.SetFailed("INVALID_PARAM", "SetDO requires Index and Value.");
                break;

            case "GetDI":
                var diArgs = ArgParaHelper.ParseKeyValueArgs(ticket.Parameter, out _);
                if (diArgs.TryGetValue("Index", out var diIdx) && int.TryParse(diIdx, out int iIdx))
                {
                    bool val = _digitalInputs.TryGetValue(iIdx, out bool v) && v;
                    ticket.SetCompleted(DriverResponse.Ok(val));
                }
                else ticket.SetFailed("INVALID_PARAM", "GetDI requires Index.");
                break;

            case "GetDO":
                var gDoArgs = ArgParaHelper.ParseKeyValueArgs(ticket.Parameter, out _);
                if (gDoArgs.TryGetValue("Index", out var gIdx) && int.TryParse(gIdx, out int gdIdx))
                {
                    bool val = _digitalOutputs.TryGetValue(gdIdx, out bool v) && v;
                    ticket.SetCompleted(DriverResponse.Ok(val));
                }
                else ticket.SetFailed("INVALID_PARAM", "GetDO requires Index.");
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
    /// 100% 還原 TestCMD.vi 核心：Inf 軸屏蔽比對
    /// 若目標座標為 Inf 則保留 CurrentPos 原位，否則更新
    /// </summary>
    private void ApplyInfAxisMask(MachinePoint target)
    {
        if (!double.IsInfinity(target.X)) ToGoPos.X = target.X; else ToGoPos.X = CurrentPos.X;
        if (!double.IsInfinity(target.Y)) ToGoPos.Y = target.Y; else ToGoPos.Y = CurrentPos.Y;
        if (!double.IsInfinity(target.Z)) ToGoPos.Z = target.Z; else ToGoPos.Z = CurrentPos.Z;
        if (!double.IsInfinity(target.U)) ToGoPos.U = target.U; else ToGoPos.U = CurrentPos.U;
        if (!double.IsInfinity(target.V)) ToGoPos.V = target.V; else ToGoPos.V = CurrentPos.V;
        if (!double.IsInfinity(target.W)) ToGoPos.W = target.W; else ToGoPos.W = CurrentPos.W;

        _engines?.Point.SetPoint($"{DeviceName}_ToGo", ToGoPos);
    }

    /// <summary>
    /// 模擬平滑插補運動
    /// </summary>
    private async Task SimulateInterpolationAsync(MachinePoint target, CancellationToken token)
    {
        int steps = 5;
        double dx = (ToGoPos.X - CurrentPos.X) / steps;
        double dy = (ToGoPos.Y - CurrentPos.Y) / steps;
        double dz = (ToGoPos.Z - CurrentPos.Z) / steps;
        double du = (ToGoPos.U - CurrentPos.U) / steps;
        double dv = (ToGoPos.V - CurrentPos.V) / steps;
        double dw = (ToGoPos.W - CurrentPos.W) / steps;

        for (int i = 0; i < steps; i++)
        {
            if (token.IsCancellationRequested) break;
            CurrentPos.X += dx;
            CurrentPos.Y += dy;
            CurrentPos.Z += dz;
            CurrentPos.U += du;
            CurrentPos.V += dv;
            CurrentPos.W += dw;

            UpdateEngines();
            MotionStateUpdated?.Invoke();
            await Task.Delay(15, token);
        }

        CurrentPos.X = ToGoPos.X;
        CurrentPos.Y = ToGoPos.Y;
        CurrentPos.Z = ToGoPos.Z;
        CurrentPos.U = ToGoPos.U;
        CurrentPos.V = ToGoPos.V;
        CurrentPos.W = ToGoPos.W;
        UpdateEngines();
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        UpdateEngines();
    }

    private void UpdateEngines()
    {
        if (_engines != null)
        {
            _engines.Io.SetPort($"{DeviceName}#Busy?", IsBusy);
            _engines.Io.SetPort($"{DeviceName}#Done?", !IsBusy);
            _engines.Io.SetPort($"{DeviceName}#Homed?", IsHomed);
            _engines.Io.SetPort($"{DeviceName}#Alarm?", IsAlarm);
            _engines.Point.SetPoint($"{DeviceName}_CurPos", CurrentPos);
        }
    }

    private static MachinePoint ParsePointFromParam(string param)
    {
        var pt = MachinePoint.AllInf();
        var args = ArgParaHelper.ParseKeyValueArgs(param, out _);
        if (args.TryGetValue("X", out var x) && double.TryParse(x, out double dx)) pt.X = dx;
        if (args.TryGetValue("Y", out var y) && double.TryParse(y, out double dy)) pt.Y = dy;
        if (args.TryGetValue("Z", out var z) && double.TryParse(z, out double dz)) pt.Z = dz;
        if (args.TryGetValue("U", out var u) && double.TryParse(u, out double du)) pt.U = du;
        if (args.TryGetValue("V", out var v) && double.TryParse(v, out double dv)) pt.V = dv;
        if (args.TryGetValue("W", out var w) && double.TryParse(w, out double dw)) pt.W = dw;
        return pt;
    }

    public void Shutdown()
    {
        _cts?.Cancel();
        try { _workerTask?.Wait(300); } catch { }
    }

    public void Dispose()
    {
        Shutdown();
        _cts?.Dispose();
    }
}
