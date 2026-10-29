using System.Threading.Channels;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;

namespace ARCOS.Driver.MockDAQ;

public enum MockWaveType
{
    Sine,
    PressCurve,
    Square
}

/// <summary>
/// 模擬資料採集驅動 (MockDaqDriver)
/// 對齊 TestDAQAICMD.vi 與 TestDAQAOCMD.vi，完整支援：
/// 1. AI 類：AnalogIn (單點讀取)、AnalogInWaveForm / Sample (連續採樣回寫 DaqEngine)
/// 2. AO 類：AnalogOut (單點類比輸出)、AnalogOutWaveForm (自 DaqEngine 取波形串流輸出)
/// 3. 控制類：Start, Stop, Config, Close
/// </summary>
public class MockDaqDriver : IDriver
{
    private BasicEngines? _engines;
    private readonly Channel<DriverTicket> _ticketQueue = Channel.CreateUnbounded<DriverTicket>();
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    public string DeviceName { get; private set; } = string.Empty;
    public string Category => "DAQ";
    public string DriverType => "MockDAQ";

    public double SampleRate { get; private set; } = 1000.0; // 1kHz
    public int SampleCount { get; private set; } = 500;
    public MockWaveType WaveType { get; private set; } = MockWaveType.PressCurve;

    public bool IsSampling { get; private set; } = false;
    public double InstantInputVal { get; private set; } = 0.0;
    public double InstantValue => InstantInputVal;
    public double InstantOutputVal { get; private set; } = 0.0;
    public double[] LatestWaveform { get; private set; } = Array.Empty<double>();

    public event Action? DaqStateUpdated;

    public void Init(string deviceName, string param, BasicEngines engines)
    {
        DeviceName = deviceName;
        _engines = engines;

        // 解析參數：Rate=1000,Count=500,Wave=Press
        var args = ArgParaHelper.ParseKeyValueArgs(param, out _);
        if (args.TryGetValue("Rate", out var rateStr) && double.TryParse(rateStr, out double r)) SampleRate = r;
        if (args.TryGetValue("Count", out var cntStr) && int.TryParse(cntStr, out int c)) SampleCount = c;
        if (args.TryGetValue("Wave", out var waveStr))
        {
            if (waveStr.Contains("Sine", StringComparison.OrdinalIgnoreCase)) WaveType = MockWaveType.Sine;
            else if (waveStr.Contains("Square", StringComparison.OrdinalIgnoreCase)) WaveType = MockWaveType.Square;
            else WaveType = MockWaveType.PressCurve;
        }

        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
    }

    public void PostTicket(DriverTicket ticket)
    {
        _ticketQueue.Writer.TryWrite(ticket);
    }

    public object? CreateDriverView()
    {
        return new MockDaqView(this);
    }

    private async Task WorkerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // 1. 處理主程式發來的 Queue 指令
                while (_ticketQueue.Reader.TryRead(out var ticket))
                {
                    ExecuteCase(ticket);
                }

                // 2. 若處於連續採樣狀態，定時產生波形數據並寫回 DaqEngine
                if (IsSampling)
                {
                    GenerateAndPushWaveform();
                    await Task.Delay((int)(SampleCount / SampleRate * 1000), token);
                }
                else
                {
                    // 空閒時阻塞等待下一個 Ticket
                    var ticket = await _ticketQueue.Reader.ReadAsync(token);
                    ExecuteCase(ticket);
                }
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

    private void ExecuteCase(DriverTicket ticket)
    {
        string cmd = ticket.Command.Trim();

        switch (cmd)
        {
            // === AI 類比輸入指令 ===
            case "AnalogIn":
            case "Read":
                InstantInputVal = GenerateInstantSample();
                ticket.SetCompleted(DriverResponse.Ok(InstantInputVal));
                break;

            case "Sample":
            case "AnalogInWaveForm":
                GenerateAndPushWaveform();
                ticket.SetCompleted();
                break;

            // === AO 類比輸出指令 (對齊 TestDAQAOCMD.vi) ===
            case "AnalogOut":
                // 格式：Value=5.0 或單數值 5.0
                var aoArgs = ArgParaHelper.ParseKeyValueArgs(ticket.Parameter, out var posArgs);
                if (aoArgs.TryGetValue("Value", out var vStr) && double.TryParse(vStr, out double v))
                {
                    InstantOutputVal = v;
                    ticket.SetCompleted();
                }
                else if (posArgs.Count > 0 && double.TryParse(posArgs[0], out double pVal))
                {
                    InstantOutputVal = pVal;
                    ticket.SetCompleted();
                }
                else
                {
                    ticket.SetFailed("INVALID_PARAM", "AnalogOut requires Value parameter.");
                }
                break;

            case "AnalogOutWaveForm":
                // 自 DaqEngine 取出指定波形進行類比硬體輸出
                // 參數：WaveFormName(,CONTINUE)
                var wArgs = ArgParaHelper.ParseKeyValueArgs(ticket.Parameter, out var wPos);
                string waveName = wPos.Count > 0 ? wPos[0] : (wArgs.TryGetValue("Name", out var nm) ? nm : "");

                if (_engines != null && !string.IsNullOrEmpty(waveName))
                {
                    try
                    {
                        var entry = _engines.Daq.GetWaveform(waveName);
                        var chData = entry.GetOrCreateChannel(0);
                        LatestWaveform = chData.Y;
                        if (chData.Y.Length > 0)
                        {
                            InstantOutputVal = chData.Y[^1];
                        }
                        ticket.SetCompleted();
                    }
                    catch (Exception ex)
                    {
                        ticket.SetFailed("WAVEFORM_NOT_FOUND", ex.Message);
                    }
                }
                else
                {
                    ticket.SetFailed("INVALID_PARAM", "AnalogOutWaveForm requires valid WaveFormName.");
                }
                break;

            // === 控制類指令 ===
            case "Start":
                IsSampling = true;
                ticket.SetCompleted();
                break;

            case "Stop":
                IsSampling = false;
                ticket.SetCompleted();
                break;

            case "Config":
                var args = ArgParaHelper.ParseKeyValueArgs(ticket.Parameter, out _);
                if (args.TryGetValue("Rate", out var r) && double.TryParse(r, out double dr)) SampleRate = dr;
                if (args.TryGetValue("Count", out var c) && int.TryParse(c, out int dc)) SampleCount = dc;
                ticket.SetCompleted();
                break;

            case "Close":
                Shutdown();
                ticket.SetCompleted();
                break;

            default:
                ticket.SetCompleted(DriverResponse.Ok());
                break;
        }

        DaqStateUpdated?.Invoke();
    }

    private void GenerateAndPushWaveform()
    {
        var yData = new double[SampleCount];
        double dt = 1.0 / SampleRate;

        for (int i = 0; i < SampleCount; i++)
        {
            double t = i * dt;
            if (WaveType == MockWaveType.Sine)
            {
                yData[i] = 10.0 * Math.Sin(2 * Math.PI * 5 * t) + (Random.Shared.NextDouble() - 0.5);
            }
            else if (WaveType == MockWaveType.Square)
            {
                yData[i] = (Math.Sin(2 * Math.PI * 5 * t) >= 0 ? 10.0 : -10.0);
            }
            else
            {
                // 典型壓力機壓裝曲線 (上升 -> 峰值保壓 -> 下降)
                double progress = (double)i / SampleCount;
                if (progress < 0.3) yData[i] = progress / 0.3 * 80.0;
                else if (progress < 0.7) yData[i] = 80.0 + (progress - 0.3) / 0.4 * 20.0;
                else yData[i] = 100.0 * (1.0 - (progress - 0.7) / 0.3);

                yData[i] += (Random.Shared.NextDouble() - 0.5) * 2.0;
            }
        }

        LatestWaveform = yData;
        InstantInputVal = yData[^1];

        if (_engines != null)
        {
            _engines.Daq.SetWaveform(DeviceName, DateTime.Now, dt, yData);
            _engines.Io.SetPort($"{DeviceName}#OK?", true);
        }

        DaqStateUpdated?.Invoke();
    }

    private double GenerateInstantSample()
    {
        return 50.0 + (Random.Shared.NextDouble() - 0.5) * 5.0;
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
