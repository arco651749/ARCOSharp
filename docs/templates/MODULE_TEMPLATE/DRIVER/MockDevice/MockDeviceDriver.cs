using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ARCOS.Core.Drivers;

namespace ARCOS.Driver.MockDevice;

/// <summary>
/// 雙模硬體驅動範本 (實作 IDriver，支援同進程 DLL 掛載與桌面 EXE 雙擊調試)
/// </summary>
public class MockDeviceDriver : IDriver
{
    public string DeviceName { get; private set; } = "Device1";
    public string Category => "Custom";
    public string DriverType => "MockDevice";

    private BasicEngines? _engines;
    private readonly ConcurrentQueue<DriverTicket> _queue = new();
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    public bool IsConnected { get; private set; }
    public int ProcessedCount { get; private set; }

    public void Init(string deviceName, string param, BasicEngines engines)
    {
        DeviceName = deviceName;
        _engines = engines;
        _cts = new CancellationTokenSource();
        IsConnected = true;

        _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
    }

    public void PostTicket(DriverTicket ticket)
    {
        _queue.Enqueue(ticket);
    }

    private async Task WorkerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_queue.TryDequeue(out var ticket))
            {
                ProcessedCount++;
                // 模擬底層硬體通訊
                await Task.Delay(50, token);
                _engines?.Io.SetPort("Template_Done_Sensor", true);
                ticket.SetCompleted(DriverResponse.Ok());
            }
            await Task.Delay(10, token);
        }
    }

    public object? CreateDriverView()
    {
        return new MockDeviceView(this);
    }

    public void Shutdown()
    {
        _cts?.Cancel();
        IsConnected = false;
    }

    public void Dispose() => Shutdown();
}
