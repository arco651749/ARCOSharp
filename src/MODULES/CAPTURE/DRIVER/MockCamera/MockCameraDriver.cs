using System.Threading.Channels;
using MVCON.Core.Common;
using MVCON.Core.Drivers;
using OpenCvSharp;

namespace MVCON.Driver.MockCamera;

/// <summary>
/// 模擬視覺相機驅動 (MockCameraDriver)
/// 對齊 Motion 的 Queue 模式，支援 Snap 單張取像、Trigger 飛拍壓入、
/// 本地圖檔虛擬取像，並自動連動 ImageEngine 與 $(Cam)#OK?
/// </summary>
public class MockCameraDriver : IDriver
{
    private BasicEngines? _engines;
    private readonly Channel<DriverTicket> _ticketQueue = Channel.CreateUnbounded<DriverTicket>();
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    public string DeviceName { get; private set; } = string.Empty;
    public string Category => "Capture";
    public string DriverType => "MockCamera";

    public int Width { get; private set; } = 640;
    public int Height { get; private set; } = 480;
    public double ExposureMs { get; private set; } = 20.0;
    public string? PreloadedImagePath { get; private set; }

    public int FrameCount { get; private set; } = 0;
    public event Action? CameraStateUpdated;

    public void Init(string deviceName, string param, BasicEngines engines)
    {
        DeviceName = deviceName;
        _engines = engines;

        // 解析參數：Width=640,Height=480,Exposure=20,File=sample.png
        var args = ArgParaHelper.ParseKeyValueArgs(param, out _);
        if (args.TryGetValue("Width", out var wStr) && int.TryParse(wStr, out int w)) Width = w;
        if (args.TryGetValue("Height", out var hStr) && int.TryParse(hStr, out int h)) Height = h;
        if (args.TryGetValue("Exposure", out var expStr) && double.TryParse(expStr, out double exp)) ExposureMs = exp;
        if (args.TryGetValue("File", out var fileStr)) PreloadedImagePath = fileStr;

        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
    }

    public void PostTicket(DriverTicket ticket)
    {
        _ticketQueue.Writer.TryWrite(ticket);
    }

    public object? CreateDriverView()
    {
        return new MockCameraView(this);
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
            catch
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
            case "Snap":
                // 模擬曝光時間
                if (ExposureMs > 0) await Task.Delay((int)ExposureMs, token);
                var snapMat = GenerateOrLoadImage();
                if (_engines != null)
                {
                    _engines.Image.SetSingleImage(DeviceName, snapMat);
                }
                snapMat.Dispose();
                FrameCount++;
                ticket.SetCompleted();
                break;

            case "Trigger":
                // 飛拍模式壓入陣列
                if (ExposureMs > 0) await Task.Delay((int)ExposureMs, token);
                var trigMat = GenerateOrLoadImage();
                string arrayName = string.IsNullOrEmpty(ticket.Parameter) ? DeviceName : ticket.Parameter;
                if (_engines != null)
                {
                    _engines.Image.AppendImage(arrayName, trigMat);
                }
                trigMat.Dispose();
                FrameCount++;
                ticket.SetCompleted();
                break;

            case "Config":
                var cfgArgs = ArgParaHelper.ParseKeyValueArgs(ticket.Parameter, out _);
                if (cfgArgs.TryGetValue("Exposure", out var exp) && double.TryParse(exp, out double dExp)) ExposureMs = dExp;
                ticket.SetCompleted();
                break;

            case "LoadFile":
                if (!string.IsNullOrEmpty(ticket.Parameter) && File.Exists(ticket.Parameter))
                {
                    PreloadedImagePath = ticket.Parameter;
                    _engines?.Image.LoadImage(DeviceName, ticket.Parameter);
                    FrameCount++;
                    ticket.SetCompleted();
                }
                else
                {
                    ticket.SetFailed("FILE_NOT_FOUND", $"Image file '{ticket.Parameter}' not found.");
                }
                break;

            case "Close":
                Shutdown();
                ticket.SetCompleted();
                break;

            default:
                ticket.SetCompleted(DriverResponse.Ok());
                break;
        }

        CameraStateUpdated?.Invoke();
    }

    public Mat GenerateOrLoadImage()
    {
        if (!string.IsNullOrEmpty(PreloadedImagePath) && File.Exists(PreloadedImagePath))
        {
            return Cv2.ImRead(PreloadedImagePath, ImreadModes.Color);
        }

        // 動態生成帶時間戳與十字線的測試圖檔
        var mat = new Mat(Height, Width, MatType.CV_8UC3, new Scalar(40, 40, 40));
        // 繪製中心十字與同心圓
        int cx = Width / 2;
        int cy = Height / 2;
        Cv2.Circle(mat, new Point(cx, cy), 50, new Scalar(0, 255, 0), 2);
        Cv2.Circle(mat, new Point(cx, cy), 100, new Scalar(0, 200, 255), 1);
        Cv2.Line(mat, new Point(cx - 80, cy), new Point(cx + 80, cy), new Scalar(0, 255, 0), 1);
        Cv2.Line(mat, new Point(cx, cy - 80), new Point(cx, cy + 80), new Scalar(0, 255, 0), 1);

        string text = $"{DeviceName} #{FrameCount + 1} {DateTime.Now:HH:mm:ss.fff}";
        Cv2.PutText(mat, text, new Point(20, 40), HersheyFonts.HersheySimplex, 0.7, new Scalar(255, 255, 255), 2);

        return mat;
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
