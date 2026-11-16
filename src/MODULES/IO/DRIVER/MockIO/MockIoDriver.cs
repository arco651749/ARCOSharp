using System.Threading.Channels;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;

namespace ARCOS.Driver.MockIO;

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
    public string Device { get; private set; } = "Virtual";

    public string OutStart { get; private set; } = "P0.0";
    public int OutPorts { get; private set; } = 16;
    public string InStart { get; private set; } = "P1.0";
    public int InPorts { get; private set; } = 16;
    public int IntervalMs { get; private set; } = 25;

    public bool[] SimulatedInputs { get; private set; } = Array.Empty<bool>();
    public bool[] ReadbackOutputs { get; private set; } = Array.Empty<bool>();
    /// <summary>模擬硬體端實體 DO 鎖存暫存器（用以驗證 Init 僅回讀一次 DO，之後僅週期性寫 DO / 讀 DI）</summary>
    public bool[] HardwareOutputs { get; private set; } = Array.Empty<bool>();

    /// <summary>Init 期間讀取實體硬體 DO 初值的次數（應嚴格為 1，進入背景掃描後不再增加）</summary>
    public int InitialDoReadCount { get; private set; }
    /// <summary>背景掃描／指令觸發寫出 DO 至硬體的累計次數</summary>
    public int PeriodicDoWriteCount { get; private set; }
    /// <summary>背景掃描／指令觸發自硬體讀取 DI 的累計次數</summary>
    public int PeriodicDiReadCount { get; private set; }

    public event Action? IoStateUpdated;
    public event Action? PortNamesRefreshed;

    public void Init(string deviceName, string param, BasicEngines engines)
    {
        _cts?.Cancel();
        DeviceName = deviceName;
        _engines = engines;

        // 解析參數：支援 Device=COM1 或 Device=IP:Port，OutStart/OutSart、InputStart/InStart 容錯解析，
        // 以及 InitDO / InitDoMask 模擬設備上電時原本已開啟的實體 DO 狀態。
        var args = ArgParaHelper.ParseKeyValueArgs(param, out var posArgs);
        if (args.TryGetValue("Device", out var dev)) Device = dev;
        if (args.TryGetValue("OutStart", out var outStart) || args.TryGetValue("OutSart", out outStart)) OutStart = outStart;
        if (args.TryGetValue("OutPorts", out var outPorts) && int.TryParse(outPorts, out int op)) OutPorts = op;
        if (args.TryGetValue("InputStart", out var inStart) || args.TryGetValue("InStart", out inStart)) InStart = inStart;
        if (args.TryGetValue("InPorts", out var inPorts) && int.TryParse(inPorts, out int ip)) InPorts = ip;
        if (args.TryGetValue("Interval", out var interval) && int.TryParse(interval, out int iv) && iv > 0) IntervalMs = iv;

        SimulatedInputs = new bool[InPorts];
        ReadbackOutputs = new bool[OutPorts];
        HardwareOutputs = new bool[OutPorts];
        InitialDoReadCount = 0;
        PeriodicDoWriteCount = 0;
        PeriodicDiReadCount = 0;

        // 【真實硬體 IO 範本步驟 1】：僅於初始化執行一次實體硬體 DO 回讀並設定為 IoEngine DO 初值，
        // 避免軟體剛啟動時把機台原本開啟中的真空閥 / 夾持汽缸瞬間洗成 false。
        SeedInitialOutputsFromHardwareOnce(args);
        SyncInputsFromHardware();

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

        // 監聽點位改名事件：當腳本執行 IO@SetPortName 時同步通知前面板刷新名稱
        _engines.Io.PortNameChanged += (_, _) =>
        {
            PortNamesRefreshed?.Invoke();
        };

        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
    }

    private void SeedInitialOutputsFromHardwareOnce(IReadOnlyDictionary<string, string> args)
    {
        bool[] hwDo = ReadHardwareOutputsInitialOnce(args);
        InitialDoReadCount++;

        Array.Copy(hwDo, HardwareOutputs, Math.Min(hwDo.Length, HardwareOutputs.Length));
        Array.Copy(hwDo, ReadbackOutputs, Math.Min(hwDo.Length, ReadbackOutputs.Length));

        if (_engines != null && hwDo.Length > 0)
        {
            _engines.Io.SetBatch(OutStart, hwDo.AsSpan());
        }
    }

    private bool[] ReadHardwareOutputsInitialOnce(IReadOnlyDictionary<string, string> args)
    {
        bool[] hwDo = _engines != null ? _engines.Io.GetBatch(OutStart, OutPorts) : new bool[OutPorts];

        if (args.TryGetValue("InitDoMask", out var maskStr) && !string.IsNullOrWhiteSpace(maskStr))
        {
            string s = maskStr.Trim();
            ulong mask = 0;
            bool parsed = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out mask)
                : ulong.TryParse(s, out mask);
            if (parsed)
            {
                for (int i = 0; i < hwDo.Length && i < 64; i++)
                {
                    if ((mask & (1UL << i)) != 0) hwDo[i] = true;
                }
            }
        }

        if (args.TryGetValue("InitDO", out var initDoStr) && !string.IsNullOrWhiteSpace(initDoStr))
        {
            foreach (var token in initDoStr.Split(new[] { ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = token.Trim();
                if (TryGetPortOffset(OutStart, t, out int portOffset) && portOffset >= 0 && portOffset < hwDo.Length)
                {
                    hwDo[portOffset] = true;
                }
                else if (int.TryParse(t, out int idx) && idx >= 0 && idx < hwDo.Length)
                {
                    hwDo[idx] = true;
                }
            }
        }

        return hwDo;
    }

    private void SyncInputsFromHardware()
    {
        if (_engines == null) return;
        _engines.Io.SetBatch(InStart, SimulatedInputs.AsSpan());
        PeriodicDiReadCount++;
    }

    private void FlushOutputsToHardware()
    {
        if (_engines == null) return;
        var outputs = _engines.Io.GetBatch(OutStart, OutPorts);
        int count = Math.Min(outputs.Length, HardwareOutputs.Length);
        Array.Copy(outputs, HardwareOutputs, count);
        Array.Copy(outputs, ReadbackOutputs, Math.Min(outputs.Length, ReadbackOutputs.Length));
        PeriodicDoWriteCount++;
    }

    public string GetInputPortName(int index) => GetPortNameAtOffset(InStart, index);
    public string GetOutputPortName(int index) => GetPortNameAtOffset(OutStart, index);

    public string GetInputPortAlias(int index) => _engines?.Io.GetPortAlias(GetInputPortName(index)) ?? string.Empty;
    public string GetOutputPortAlias(int index) => _engines?.Io.GetPortAlias(GetOutputPortName(index)) ?? string.Empty;

    public static string GetPortNameAtOffset(string startPort, int offset)
    {
        if (ARCOS.Core.Engines.IoEngine.TryParsePhysicalPort(startPort, out int sp, out int sb))
        {
            int total = sp * 8 + sb + offset;
            int port = total / 8;
            int bit = total % 8;
            return $"P{port}.{bit}";
        }
        return $"{startPort}+{offset}";
    }

    private static bool TryGetPortOffset(string startPort, string targetPort, out int offset)
    {
        offset = -1;
        if (!ARCOS.Core.Engines.IoEngine.TryParsePhysicalPort(startPort, out int sp, out int sb)) return false;
        if (!ARCOS.Core.Engines.IoEngine.TryParsePhysicalPort(targetPort, out int tp, out int tb)) return false;

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

    public async Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
    {
        _engines = engines;
        var winOwner = owner as Avalonia.Controls.Window;
        var dlg = new MockIoGuidanceDialog(this, currentParam, engines);
        if (winOwner != null)
        {
            return await dlg.ShowDialog<string?>(winOwner);
        }
        dlg.Show();
        return null;
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

                // 2. 進入運行迴圈後：僅單向「寫 DO 到硬體」與「從硬體讀 DI」（不再回讀硬體 DO）
                if (_engines != null)
                {
                    SyncInputsFromHardware();
                    FlushOutputsToHardware();

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
        if (cmd == "INITIOPORT" || cmd == "INITIO" || cmd == "INIT")
        {
            if (!string.IsNullOrWhiteSpace(ticket.Parameter))
            {
                var args = ArgParaHelper.ParseKeyValueArgs(ticket.Parameter, out var posArgs);
                if (args.TryGetValue("Interval", out var ivStr) && int.TryParse(ivStr, out int iv1) && iv1 > 0)
                {
                    IntervalMs = iv1;
                }
                else if (posArgs.Count > 0 && int.TryParse(posArgs[^1], out int iv2) && iv2 > 0)
                {
                    IntervalMs = iv2;
                }
            }

            if (_engines != null)
            {
                SyncInputsFromHardware();
                FlushOutputsToHardware();
            }
            PortNamesRefreshed?.Invoke();
            IoStateUpdated?.Invoke();
            ticket.SetCompleted();
        }
        else if (cmd == "FORCEUPDATE" || cmd == "FORCE_UPDATE" || cmd == "FORCEUPDATEIO" || cmd == "UPDATE")
        {
            if (_engines != null)
            {
                SyncInputsFromHardware();
                FlushOutputsToHardware();
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
    }

    public void Dispose()
    {
        Shutdown();
        _cts?.Dispose();
    }
}
