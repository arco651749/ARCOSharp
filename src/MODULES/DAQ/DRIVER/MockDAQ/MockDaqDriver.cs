using System.Threading.Channels;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;

namespace ARCOS.Driver.MockDAQ;

public enum MockWaveType
{
    TriangleCounter, // 循環三角波波型：最大值從 0 數到 1023 後再從 0 開始
    Sine,
    PressCurve,
    Square
}

/// <summary>
/// 模擬資料採集驅動 (MockDaqDriver)
/// 對齊資料擷取與波形分析規範，內部支援：
/// 1. 循環三角波波型：數值從 0 數到 1023 後自動歸 0 循環 (最大值 1023)
/// 2. AI 類：AnalogIn (單點讀取)、AnalogInWaveForm / Sample (連續採樣回寫 DaqEngine)
/// 3. AO 類：AnalogOut (單點類比輸出)、AnalogOutWaveForm (自 DaqEngine 取波形串流輸出)
/// 4. 控制類：Start, Stop, Config, Close
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
    public MockWaveType WaveType { get; private set; } = MockWaveType.TriangleCounter;

    private int _counter = 0;
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

        // 解析參數：Rate=1000,Count=500,Wave=Triangle
        var args = ArgParaHelper.ParseKeyValueArgs(param, out _);
        if (args.TryGetValue("Rate", out var rateStr) && double.TryParse(rateStr, out double r)) SampleRate = r;
        if (args.TryGetValue("Count", out var cntStr) && int.TryParse(cntStr, out int c)) SampleCount = c;
        if (args.TryGetValue("Wave", out var waveStr))
        {
            if (waveStr.Contains("Sine", StringComparison.OrdinalIgnoreCase)) WaveType = MockWaveType.Sine;
            else if (waveStr.Contains("Square", StringComparison.OrdinalIgnoreCase)) WaveType = MockWaveType.Square;
            else if (waveStr.Contains("Press", StringComparison.OrdinalIgnoreCase)) WaveType = MockWaveType.PressCurve;
            else WaveType = MockWaveType.TriangleCounter;
        }

        // 初始化時產生第一筆循環三角波樣本
        GenerateAndPushWaveform();

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
            case "Sample":
            case "AnalogInWaveForm":
                GenerateAndPushWaveform();
                ticket.SetCompleted();
                break;

            case "Start":
                IsSampling = true;
                ticket.SetCompleted();
                break;

            case "Stop":
                IsSampling = false;
                ticket.SetCompleted();
                break;

            case "AnalogIn":
                // 單點讀取：依照 0..1023 計數遞增回傳
                InstantInputVal = _counter;
                _counter = (_counter + 1) % 1024;
                ticket.SetCompleted(DriverResponse.Ok(InstantInputVal));
                break;

            case "AnalogOut":
                var argsAo = ArgParaHelper.ParseKeyValueArgs(ticket.Parameter, out var posArgsAo);
                if (argsAo.TryGetValue("Value", out var vStr) && double.TryParse(vStr, out double vVal))
                {
                    InstantOutputVal = vVal;
                }
                else if (posArgsAo.Count > 0 && double.TryParse(posArgsAo[^1], out double pVal))
                {
                    InstantOutputVal = pVal;
                }
                else
                {
                    var rawParts = ticket.Parameter.Split(',', StringSplitOptions.TrimEntries);
                    if (rawParts.Length > 0 && double.TryParse(rawParts[^1], out double rVal))
                    {
                        InstantOutputVal = rVal;
                    }
                }
                ticket.SetCompleted();
                break;

            case "AnalogOutWaveForm":
                if (_engines != null && !string.IsNullOrEmpty(ticket.Parameter))
                {
                    try
                    {
                        var entry = _engines.Daq.GetWaveform(ticket.Parameter);
                        if (entry.Channels.TryGetValue(0, out var chData) && chData.Y.Length > 0)
                        {
                            InstantOutputVal = chData.Y[^1];
                        }
                    }
                    catch { }
                }
                ticket.SetCompleted();
                break;

            case "Config":
                var args = ArgParaHelper.ParseKeyValueArgs(ticket.Parameter, out _);
                if (args.TryGetValue("Rate", out var r) && double.TryParse(r, out double dr)) SampleRate = dr;
                if (args.TryGetValue("Count", out var c) && int.TryParse(c, out int dc)) SampleCount = dc;
                if (args.TryGetValue("Wave", out var waveStr))
                {
                    if (waveStr.Contains("Sine", StringComparison.OrdinalIgnoreCase)) WaveType = MockWaveType.Sine;
                    else if (waveStr.Contains("Square", StringComparison.OrdinalIgnoreCase)) WaveType = MockWaveType.Square;
                    else if (waveStr.Contains("Press", StringComparison.OrdinalIgnoreCase)) WaveType = MockWaveType.PressCurve;
                    else WaveType = MockWaveType.TriangleCounter;
                }
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
            if (WaveType == MockWaveType.Sine)
            {
                double t = i * dt;
                yData[i] = 512.0 + 511.0 * Math.Sin(2 * Math.PI * 5 * t);
            }
            else if (WaveType == MockWaveType.Square)
            {
                yData[i] = (i % 50 < 25) ? 1023.0 : 0.0;
            }
            else if (WaveType == MockWaveType.PressCurve)
            {
                double progress = (double)i / SampleCount;
                if (progress < 0.3) yData[i] = progress / 0.3 * 800.0;
                else if (progress < 0.7) yData[i] = 800.0 + (progress - 0.3) / 0.4 * 223.0;
                else yData[i] = 1023.0 * (1.0 - (progress - 0.7) / 0.3);
            }
            else
            {
                // 循環三角波波型：最大值從 0 數到 1023 後再從 0 開始
                yData[i] = (_counter + i) % 1024;
            }
        }

        _counter = (_counter + SampleCount) % 1024;
        LatestWaveform = yData;
        InstantInputVal = yData[^1];

        if (_engines != null)
        {
            _engines.Daq.SetWaveform(DeviceName, DateTime.Now, dt, yData);
            _engines.Io.SetPort($"{DeviceName}#OK?", true);
        }

        DaqStateUpdated?.Invoke();
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
