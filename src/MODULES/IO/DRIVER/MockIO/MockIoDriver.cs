using System.Threading.Channels;
using MVCON.Core.Common;
using MVCON.Core.Drivers;

namespace MVCON.Driver.MockIO;

/// <summary>
/// 模擬 IO 硬體搬運工 (MockIoDriver)
/// 定時自 IoEngine.GetBatch 讀取 Output 狀態更新面板，
/// 並將面板手動撥動的 Input 透過 IoEngine.SetBatch 寫回
/// </summary>
public class MockIoDriver : IDriver
{
    private BasicEngines? _engines;
    private readonly Channel<DriverTicket> _ticketQueue = Channel.CreateUnbounded<DriverTicket>();
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    public string DeviceName { get; private set; } = string.Empty;
    public string Category => "IO";
    public string DriverType => "MockIO";

    public string OutStart { get; private set; } = "P0.0";
    public int OutPorts { get; private set; } = 16;
    public string InStart { get; private set; } = "P1.0";
    public int InPorts { get; private set; } = 16;
    public int IntervalMs { get; private set; } = 25;

    public bool[] SimulatedInputs { get; private set; } = Array.Empty<bool>();
    public bool[] ReadbackOutputs { get; private set; } = Array.Empty<bool>();

    public event Action? IoStateUpdated;

    public void Init(string deviceName, string param, BasicEngines engines)
    {
        DeviceName = deviceName;
        _engines = engines;

        // 解析參數：OutStart=P0.0,OutPorts=16,InStart=P1.0,InPorts=16,Interval=25
        var args = ArgParaHelper.ParseKeyValueArgs(param, out _);
        if (args.TryGetValue("OutStart", out var outStart)) OutStart = outStart;
        if (args.TryGetValue("OutPorts", out var outPorts) && int.TryParse(outPorts, out int op)) OutPorts = op;
        if (args.TryGetValue("InStart", out var inStart)) InStart = inStart;
        if (args.TryGetValue("InPorts", out var inPorts) && int.TryParse(inPorts, out int ip)) InPorts = ip;
        if (args.TryGetValue("Interval", out var interval) && int.TryParse(interval, out int iv)) IntervalMs = iv;

        SimulatedInputs = new bool[InPorts];
        ReadbackOutputs = new bool[OutPorts];

        // 雙向同步監聽：若外部 (UI 點擊手動調試、腳本或 Telnet) 變更屬於 InStart 範圍內的點位，
        // 自動同步至 SimulatedInputs 緩衝區，杜絕 WorkerLoop 背景每 25ms 掃描強制覆寫回 false
        _engines.Io.IoChanged += (port, _, val) =>
        {
            if (TryGetPortOffset(InStart, port, out int offset) && offset >= 0 && offset < SimulatedInputs.Length)
            {
                SimulatedInputs[offset] = val;
                IoStateUpdated?.Invoke();
            }
        };

        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
    }

    private static bool TryGetPortOffset(string startPort, string targetPort, out int offset)
    {
        offset = -1;
        if (!MVCON.Core.Engines.IoEngine.TryParsePhysicalPort(startPort, out int sp, out int sb)) return false;
        if (!MVCON.Core.Engines.IoEngine.TryParsePhysicalPort(targetPort, out int tp, out int tb)) return false;

        int startIndex = sp * 8 + sb;
        int targetIndex = tp * 8 + tb;
        offset = targetIndex - startIndex;
        return offset >= 0;
    }

    public void SetSimulatedInput(int index, bool value)
    {
        if (index >= 0 && index < SimulatedInputs.Length)
        {
            SimulatedInputs[index] = value;
            _engines?.Io.SetBatch(InStart, SimulatedInputs.AsSpan());
            IoStateUpdated?.Invoke();
        }
    }

    public void PostTicket(DriverTicket ticket)
    {
        _ticketQueue.Writer.TryWrite(ticket);
    }

    public object? CreateDriverView()
    {
        return new MockIoView(this);
    }

    private async Task WorkerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // 1. 處理工作佇列中的指令票券
                while (_ticketQueue.Reader.TryRead(out var ticket))
                {
                    ExecuteCommand(ticket);
                }

                // 2. 雙向批次搬運：
                if (_engines != null)
                {
                    // 2a. 寫回 Input 到 IoEngine
                    _engines.Io.SetBatch(InStart, SimulatedInputs.AsSpan());

                    // 2b. 從 IoEngine 讀回 Output 狀態
                    var outputs = _engines.Io.GetBatch(OutStart, OutPorts);
                    Array.Copy(outputs, ReadbackOutputs, Math.Min(outputs.Length, ReadbackOutputs.Length));

                    IoStateUpdated?.Invoke();
                }

                await Task.Delay(IntervalMs, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 容錯防禦
            }
        }
    }

    private void ExecuteCommand(DriverTicket ticket)
    {
        string cmd = ticket.Command.ToUpperInvariant();
        if (cmd == "FORCE_UPDATE" || cmd == "UPDATE")
        {
            if (_engines != null)
            {
                _engines.Io.SetBatch(InStart, SimulatedInputs.AsSpan());
                var outputs = _engines.Io.GetBatch(OutStart, OutPorts);
                Array.Copy(outputs, ReadbackOutputs, Math.Min(outputs.Length, ReadbackOutputs.Length));
                IoStateUpdated?.Invoke();
            }
            ticket.SetCompleted();
        }
        else if (cmd == "SET_INPUT")
        {
            var args = ArgParaHelper.ParseKeyValueArgs(ticket.Parameter, out _);
            if (args.TryGetValue("Index", out var idxStr) && int.TryParse(idxStr, out int idx) &&
                args.TryGetValue("Value", out var valStr) && bool.TryParse(valStr, out bool val))
            {
                SetSimulatedInput(idx, val);
                ticket.SetCompleted();
            }
            else
            {
                ticket.SetFailed("INVALID_PARAM", "SET_INPUT requires Index and Value.");
            }
        }
        else
        {
            ticket.SetCompleted(DriverResponse.Ok());
        }
    }

    public void Shutdown()
    {
        _cts?.Cancel();
        try
        {
            _workerTask?.Wait(500);
        }
        catch { }
    }

    public void Dispose()
    {
        Shutdown();
        _cts?.Dispose();
    }
}
