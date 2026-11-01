using System.Threading.Channels;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;
using OpenCvSharp;

namespace ARCOS.Driver.MockCamera;

/// <summary>
/// 模擬視覺相機驅動 (MockCameraDriver)
/// 對齊視覺檢測流程，支援：
/// 1. Snap 單張取像、Trigger 飛拍壓入
/// 2. 支援指定/瀏覽載入本機圖檔 (支援 .jpg, .png, .bmp)
/// 3. 自動連動 ImageEngine 與 $(Cam)#OK? 旗標
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
    public string? PreloadedImagePath { get; set; }

    public int FrameCount { get; private set; } = 0;
    public event Action? CameraStateUpdated;

    public void Init(string deviceName, string param, BasicEngines engines)
    {
        DeviceName = deviceName;
        _engines = engines;

        // 解析參數：Width=640,Height=480,Exposure=20,File=sample.png
        var args = ArgParaHelper.ParseKeyValueArgs(param, out var posArgs);
        if (args.TryGetValue("Width", out var wStr) && int.TryParse(wStr, out int w)) Width = w;
        if (args.TryGetValue("Height", out var hStr) && int.TryParse(hStr, out int h)) Height = h;
        if (args.TryGetValue("Exposure", out var expStr) && double.TryParse(expStr, out double exp)) ExposureMs = exp;
        if (args.TryGetValue("File", out var fileStr)) PreloadedImagePath = fileStr;
        else if (args.TryGetValue("Path", out var pathStr)) PreloadedImagePath = pathStr;
        else if (posArgs.Count > 0 && File.Exists(posArgs[0])) PreloadedImagePath = posArgs[0];

        // 若未指定但當前目錄有 test_cap.jpg，作為預設預載圖檔
        if (string.IsNullOrEmpty(PreloadedImagePath))
        {
            string defaultImg = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "test_cap.jpg");
            if (File.Exists(defaultImg)) PreloadedImagePath = defaultImg;
        }

        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
    }

    public void SetImageFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            PreloadedImagePath = path.Trim();
            CameraStateUpdated?.Invoke();
        }
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

        // 檢查指令參數中是否帶有自訂圖檔路徑 (例如 Snap::Cam0, D:\test.jpg 或 File=test.jpg)
        string? customFile = ExtractFilePathFromParam(ticket.Parameter);
        if (!string.IsNullOrEmpty(customFile) && File.Exists(customFile))
        {
            PreloadedImagePath = customFile;
        }

        switch (cmd)
        {
            case "Snap":
                if (ExposureMs > 0) await Task.Delay((int)ExposureMs, token);
                var snapMat = GenerateOrLoadImage();
                if (_engines != null)
                {
                    _engines.Image.SetSingleImage(DeviceName, snapMat);
                    _engines.Io.SetPort($"{DeviceName}#OK?", true);
                }
                snapMat.Dispose();
                FrameCount++;
                ticket.SetCompleted();
                break;

            case "Trigger":
                if (ExposureMs > 0) await Task.Delay((int)ExposureMs, token);
                var trigMat = GenerateOrLoadImage();
                string arrayName = string.IsNullOrEmpty(ticket.Parameter) || ticket.Parameter == customFile
                    ? DeviceName : ticket.Parameter;
                if (_engines != null)
                {
                    _engines.Image.AppendImage(arrayName, trigMat);
                    _engines.Io.SetPort($"{DeviceName}#OK?", true);
                }
                trigMat.Dispose();
                FrameCount++;
                ticket.SetCompleted();
                break;

            case "Config":
                var cfgArgs = ArgParaHelper.ParseKeyValueArgs(ticket.Parameter, out _);
                if (cfgArgs.TryGetValue("Exposure", out var exp) && double.TryParse(exp, out double dExp)) ExposureMs = dExp;
                if (cfgArgs.TryGetValue("File", out var f)) PreloadedImagePath = f;
                ticket.SetCompleted();
                break;

            case "LoadFile":
                if (!string.IsNullOrEmpty(ticket.Parameter) && File.Exists(ticket.Parameter))
                {
                    PreloadedImagePath = ticket.Parameter;
                    _engines?.Image.LoadImage(DeviceName, ticket.Parameter);
                    _engines?.Io.SetPort($"{DeviceName}#OK?", true);
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

    private string? ExtractFilePathFromParam(string param)
    {
        if (string.IsNullOrWhiteSpace(param)) return null;
        var args = ArgParaHelper.ParseKeyValueArgs(param, out var posArgs);
        if (args.TryGetValue("File", out var f) && !string.IsNullOrWhiteSpace(f)) return f;
        if (args.TryGetValue("Path", out var p) && !string.IsNullOrWhiteSpace(p)) return p;
        foreach (var arg in posArgs)
        {
            if (File.Exists(arg)) return arg;
            if (arg.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                arg.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                arg.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            {
                return arg;
            }
        }
        return null;
    }

    public Mat GenerateOrLoadImage()
    {
        if (!string.IsNullOrEmpty(PreloadedImagePath) && File.Exists(PreloadedImagePath))
        {
            var loaded = Cv2.ImRead(PreloadedImagePath, ImreadModes.Color);
            if (!loaded.Empty()) return loaded;
        }

        // 若無指定圖檔，檢查根目錄是否有 test_cap.jpg 作為預設載入圖片
        string defaultCap = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "test_cap.jpg");
        if (File.Exists(defaultCap))
        {
            var loaded = Cv2.ImRead(defaultCap, ImreadModes.Color);
            if (!loaded.Empty()) return loaded;
        }

        // 動態生成帶時間戳與十字線的測試圖檔
        var mat = new Mat(Height, Width, MatType.CV_8UC3, new Scalar(40, 40, 40));
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
