using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;
using OpenCvSharp;
using Brush = Avalonia.Media.Brush;
using Brushes = Avalonia.Media.Brushes;
using FontFamily = Avalonia.Media.FontFamily;
using Point = OpenCvSharp.Point;
using Window = Avalonia.Controls.Window;

namespace ARCOS.Driver.DirectShowCamera;

/// <summary>
/// Windows DirectShow 原生 Webcam 視覺相機驅動 (DirectShowCameraDriver)。
/// 透過 OpenCvSharp VideoCaptureAPIs.DSHOW 與 Windows DirectShow COM 裝置列舉連動，
/// 支援筆記型電腦內建鏡頭與 USB UVC 相機之即時預覽、Snap 單張取像、Trigger 飛拍壓入、
/// 解析度 / FourCC (MJPG/YUY2) / 幀率 / 曝光與畫面翻轉線上組態。
/// </summary>
public sealed class DirectShowCameraDriver : IDriver
{
    private BasicEngines? _engines;
    private readonly Channel<DriverTicket> _ticketQueue = Channel.CreateUnbounded<DriverTicket>();
    private readonly object _frameLock = new();
    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private VideoCapture? _capture;
    private Mat? _latestFrame;

    private volatile bool _livePreviewEnabled;
    private volatile bool _autoOpenOnInit = true;
    private bool _disposed;

    public string DeviceName { get; private set; } = string.Empty;
    public string Category => "Capture";
    public string DriverType => "DirectShowCamera";

    public int CameraIndex { get; private set; } = 0;
    public string CameraFriendlyName { get; private set; } = "DirectShow Camera 0";
    public int Width { get; private set; } = 1280;
    public int Height { get; private set; } = 720;
    public double Fps { get; private set; } = 30.0;
    public string FourCC { get; private set; } = "MJPG";
    public double? Exposure { get; private set; } = null; // null = Auto Exposure
    public string FlipModeName { get; private set; } = "None";

    public int ActualWidth { get; private set; }
    public int ActualHeight { get; private set; }
    public double MeasuredFps { get; private set; }
    public bool IsConnected { get; private set; }
    public string LastError { get; private set; } = string.Empty;
    public int FrameCount { get; private set; }

    public bool LivePreviewEnabled
    {
        get => _livePreviewEnabled;
        set => _livePreviewEnabled = value;
    }

    public event Action? CameraStateUpdated;

    public void Init(string deviceName, string param, BasicEngines engines)
    {
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Cam0" : deviceName.Trim();
        _engines = engines;

        ApplyParameterString(param, isInitialInit: true);

        _cts = new CancellationTokenSource();
        _workerTask = Task.Factory.StartNew(
            () => WorkerLoop(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void PostTicket(DriverTicket ticket)
    {
        if (_disposed)
        {
            ticket.SetFailed("DISPOSED", "DirectShowCameraDriver is disposed.");
            return;
        }
        _ticketQueue.Writer.TryWrite(ticket);
    }

    public object? CreateDriverView() => new DirectShowCameraView(this);

    /// <summary>
    /// 複製目前最新擷取到的影格供 UI 預覽顯示（呼叫端負責 Dispose 回傳的 Mat）。
    /// </summary>
    public bool TryCopyLatestFrame(out Mat? mat)
    {
        lock (_frameLock)
        {
            if (_latestFrame != null && !_latestFrame.Empty())
            {
                mat = _latestFrame.Clone();
                return true;
            }
            mat = null;
            return false;
        }
    }

    /// <summary>
    /// 產生帶有目前時間戳記 (TIMESTAMP) 的預設離線提示影像。
    /// </summary>
    public Mat CreateStatusPlaceholderMat()
    {
        int w = Width > 0 ? Width : 640;
        int h = Height > 0 ? Height : 480;
        var mat = new Mat(h, w, MatType.CV_8UC3, new Scalar(24, 28, 36));
        int cx = w / 2;
        int cy = h / 2;

        Cv2.Rectangle(mat, new OpenCvSharp.Rect(8, 8, Math.Max(1, w - 16), Math.Max(1, h - 16)), new Scalar(60, 80, 110), 2);
        Cv2.Line(mat, new Point(cx - 40, cy), new Point(cx + 40, cy), new Scalar(80, 140, 200), 1);
        Cv2.Line(mat, new Point(cx, cy - 40), new Point(cx, cy + 40), new Scalar(80, 140, 200), 1);
        Cv2.Circle(mat, new Point(cx, cy), 32, new Scalar(80, 140, 200), 1);

        string line1 = IsConnected
            ? $"{DeviceName} [{CameraIndex}: {CameraFriendlyName}]"
            : $"[OFFLINE] {DeviceName} (DirectShow Index={CameraIndex} Not Connected)";
        string timestampLine = $"TIMESTAMP: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | Frame #{FrameCount + 1}";
        string line3 = string.IsNullOrWhiteSpace(LastError)
            ? $"Target: {Width}x{Height} @ {Fps:0.#}fps ({FourCC})"
            : LastError;

        Cv2.PutText(mat, line1, new Point(24, 48), HersheyFonts.HersheySimplex, 0.72, new Scalar(100, 210, 255), 2);
        Cv2.PutText(mat, timestampLine, new Point(24, 92), HersheyFonts.HersheySimplex, 0.8, new Scalar(80, 240, 180), 2);
        Cv2.PutText(mat, line3, new Point(24, 132), HersheyFonts.HersheySimplex, 0.52, new Scalar(180, 190, 205), 1);
        return mat;
    }

    /// <summary>
    /// 解析組態字串並更新驅動設定；傳回 true 代表需重開相機（裝置索引、解析度、FPS 或 FourCC 變更）。
    /// </summary>
    public bool ApplyParameterString(string? param, bool isInitialInit)
    {
        int oldIndex = CameraIndex;
        int oldW = Width;
        int oldH = Height;
        double oldFps = Fps;
        string oldFourCC = FourCC;

        var args = ArgParaHelper.ParseKeyValueArgs(param ?? string.Empty, out var posArgs);
        var devices = DirectShowDeviceEnumerator.EnumerateVideoInputDevices();

        if (args.TryGetValue("AutoConnect", out var acStr) && bool.TryParse(acStr, out bool ac))
            _autoOpenOnInit = ac;

        if (args.TryGetValue("Index", out var idxSpec) && !string.IsNullOrWhiteSpace(idxSpec))
        {
            CameraIndex = DirectShowDeviceEnumerator.ResolveDeviceIndex(idxSpec, devices);
        }
        else if (args.TryGetValue("Device", out var devSpec) && !string.IsNullOrWhiteSpace(devSpec))
        {
            CameraIndex = DirectShowDeviceEnumerator.ResolveDeviceIndex(devSpec, devices);
        }
        else if (!isInitialInit && posArgs.Count > 0 && int.TryParse(posArgs[0], out int directIdx))
        {
            CameraIndex = Math.Max(0, directIdx);
        }

        var matched = devices.FirstOrDefault(d => d.Index == CameraIndex);
        CameraFriendlyName = matched?.FriendlyName ?? $"DirectShow Camera {CameraIndex}";

        if (args.TryGetValue("Width", out var wStr) && int.TryParse(wStr, out int w) && w > 0) Width = w;
        if (args.TryGetValue("Height", out var hStr) && int.TryParse(hStr, out int h) && h > 0) Height = h;
        if (args.TryGetValue("Fps", out var fpsStr) && double.TryParse(fpsStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double fps) && fps > 0) Fps = fps;

        if (args.TryGetValue("FourCC", out var fccStr))
            FourCC = NormalizeFourCC(fccStr);

        if (args.TryGetValue("Exposure", out var expStr))
            Exposure = ParseExposure(expStr);

        if (args.TryGetValue("Flip", out var flipStr))
            FlipModeName = NormalizeFlip(flipStr);

        return oldIndex != CameraIndex ||
               oldW != Width ||
               oldH != Height ||
               Math.Abs(oldFps - Fps) > 0.01 ||
               !string.Equals(oldFourCC, FourCC, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFourCC(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Auto";
        string u = raw.Trim().ToUpperInvariant();
        if (u == "AUTO" || u == "(AUTO)") return "Auto";
        return u.Length == 4 ? u : "Auto";
    }

    private static double? ParseExposure(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string t = raw.Trim();
        if (t.Equals("Auto", StringComparison.OrdinalIgnoreCase) || t.Equals("(Auto)", StringComparison.OrdinalIgnoreCase))
            return null;
        return double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out double val) ? val : null;
    }

    private static string NormalizeFlip(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "None";
        return raw.Trim().ToUpperInvariant() switch
        {
            "H" or "HORIZONTAL" or "X" => "H",
            "V" or "VERTICAL" or "Y" => "V",
            "HV" or "BOTH" or "180" or "XY" => "HV",
            _ => "None"
        };
    }

    private void WorkerLoop(CancellationToken token)
    {
        try
        {
            if (_autoOpenOnInit)
            {
                OpenCaptureOnWorker();
                CameraStateUpdated?.Invoke();
            }

            var fpsClock = Stopwatch.StartNew();
            int liveFramesWindow = 0;

            while (!token.IsCancellationRequested)
            {
                while (_ticketQueue.Reader.TryRead(out var ticket))
                {
                    ExecuteTicketOnWorker(ticket);
                }

                if (_livePreviewEnabled && IsConnected && _capture != null && _capture.IsOpened())
                {
                    using var raw = new Mat();
                    if (_capture.Read(raw) && !raw.Empty())
                    {
                        ApplyFlipInPlace(raw);
                        StoreLatestFrame(raw);

                        liveFramesWindow++;
                        double elapsedSec = fpsClock.Elapsed.TotalSeconds;
                        if (elapsedSec >= 0.5)
                        {
                            MeasuredFps = liveFramesWindow / elapsedSec;
                            liveFramesWindow = 0;
                            fpsClock.Restart();
                        }

                        CameraStateUpdated?.Invoke();
                    }
                    else
                    {
                        Thread.Sleep(10);
                    }
                }
                else
                {
                    Thread.Sleep(15);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsConnected = false;
            CameraStateUpdated?.Invoke();
        }
        finally
        {
            CloseCaptureOnWorker();
        }
    }

    private void ExecuteTicketOnWorker(DriverTicket ticket)
    {
        try
        {
            string cmd = ticket.Command.Trim();
            switch (cmd)
            {
                case "Open":
                case "Reconnect":
                {
                    if (!string.IsNullOrWhiteSpace(ticket.Parameter))
                        ApplyParameterString(ticket.Parameter, isInitialInit: false);

                    bool opened = OpenCaptureOnWorker();
                    if (opened) ticket.SetCompleted();
                    else ticket.SetFailed("DSHOW_OPEN_FAILED", LastError);
                    break;
                }

                case "Close":
                {
                    CloseCaptureOnWorker();
                    ticket.SetCompleted();
                    break;
                }

                case "Config":
                {
                    bool needReopen = ApplyParameterString(ticket.Parameter, isInitialInit: false);
                    if (needReopen || !IsConnected)
                    {
                        if (!OpenCaptureOnWorker())
                        {
                            ticket.SetFailed("DSHOW_CONFIG_FAILED", LastError);
                            break;
                        }
                    }
                    else
                    {
                        ApplyExposureSettingsOnWorker();
                    }
                    ticket.SetCompleted();
                    break;
                }

                case "Snap":
                case "Trigger":
                {
                    if (!IsConnected || _capture == null || !_capture.IsOpened())
                    {
                        OpenCaptureOnWorker();
                    }

                    Mat? captured = (IsConnected && _capture != null && _capture.IsOpened())
                        ? CaptureFreshFrameOnWorker()
                        : null;
                    bool isLiveFrame = captured != null && !captured.Empty();

                    using Mat frame = isLiveFrame ? captured! : CreateStatusPlaceholderMat();
                    if (!isLiveFrame)
                    {
                        captured?.Dispose();
                        if (string.IsNullOrWhiteSpace(LastError))
                            LastError = $"DirectShow camera #{CameraIndex} offline; returned timestamped fallback image.";
                    }
                    else
                    {
                        ApplyFlipInPlace(frame);
                    }

                    StoreLatestFrame(frame);

                    if (_engines != null)
                    {
                        var frameMeta = BuildCaptureMetadata(frame.Width, frame.Height, isLiveFrame);
                        ArgParaHelper.ParseKeyValueArgs(ticket.Parameter, out var posArgs);
                        bool hasTargetArrayArg = cmd.Equals("Trigger", StringComparison.OrdinalIgnoreCase) &&
                                                 posArgs.Count > 1 &&
                                                 !string.IsNullOrWhiteSpace(posArgs[1]);

                        if (cmd.Equals("Trigger", StringComparison.OrdinalIgnoreCase) && !hasTargetArrayArg)
                        {
                            _engines.Image.AppendImage(DeviceName, frame, frameMeta);
                        }
                        else
                        {
                            // Snap 或帶目標陣列參數的 Trigger (Capture@Trigger Cam, Arr) 寫入 Cam 單張槽位，再由 Trigger.csvi Append 至 Arr
                            _engines.Image.SetSingleImage(DeviceName, frame, frameMeta);
                        }
                        _engines.Io.SetPort($"{DeviceName}#OK?", isLiveFrame);
                    }

                    FrameCount++;
                    ticket.SetCompleted(isLiveFrame
                        ? DriverResponse.Ok(true)
                        : new DriverResponse { Success = true, ErrorCode = "OFFLINE_FALLBACK", Message = LastError, Payload = false });
                    break;
                }

                default:
                    ticket.SetCompleted(DriverResponse.Ok());
                    break;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ticket.SetFailed("DSHOW_ERROR", ex.Message);
        }
        finally
        {
            CameraStateUpdated?.Invoke();
        }
    }

    private bool OpenCaptureOnWorker()
    {
        CloseCaptureOnWorker();

        if (!OperatingSystem.IsWindows())
        {
            LastError = "DirectShowCamera driver requires Windows.";
            IsConnected = false;
            return false;
        }

        try
        {
            var cap = new VideoCapture(CameraIndex, VideoCaptureAPIs.DSHOW);
            if (!cap.IsOpened())
            {
                cap.Dispose();
                IsConnected = false;
                LastError = $"Cannot open Windows DirectShow camera index {CameraIndex} ({CameraFriendlyName}).";
                return false;
            }

            // On DirectShow UVC cameras, FourCC (especially MJPG) should be set before resolution for high FPS.
            if (FourCC.Length == 4 && !FourCC.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                int fourccCode = OpenCvSharp.FourCC.FromString(FourCC);
                TrySetProp(cap, VideoCaptureProperties.FourCC, fourccCode);
            }

            if (Width > 0) TrySetProp(cap, VideoCaptureProperties.FrameWidth, Width);
            if (Height > 0) TrySetProp(cap, VideoCaptureProperties.FrameHeight, Height);
            if (Fps > 0) TrySetProp(cap, VideoCaptureProperties.Fps, Fps);
            TrySetProp(cap, VideoCaptureProperties.BufferSize, 1);

            _capture = cap;
            ApplyExposureSettingsOnWorker();

            ActualWidth = (int)Math.Round(cap.Get(VideoCaptureProperties.FrameWidth));
            ActualHeight = (int)Math.Round(cap.Get(VideoCaptureProperties.FrameHeight));

            // Warm-up grab to flush initial black AEC frame on laptop webcams.
            using var warmup = new Mat();
            for (int i = 0; i < 3; i++)
            {
                cap.Grab();
            }
            if (cap.Retrieve(warmup) && !warmup.Empty())
            {
                ApplyFlipInPlace(warmup);
                StoreLatestFrame(warmup);
            }

            IsConnected = true;
            LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            LastError = $"DirectShow open exception: {ex.Message}";
            return false;
        }
    }

    private void ApplyExposureSettingsOnWorker()
    {
        if (_capture == null || !_capture.IsOpened()) return;
        if (Exposure.HasValue)
        {
            // In OpenCV cap_dshow: 0.25 switches AE off (manual), 0.75 switches AE on.
            TrySetProp(_capture, VideoCaptureProperties.AutoExposure, 0.25);
            TrySetProp(_capture, VideoCaptureProperties.Exposure, Exposure.Value);
        }
        else
        {
            TrySetProp(_capture, VideoCaptureProperties.AutoExposure, 0.75);
        }
    }

    private Mat? CaptureFreshFrameOnWorker()
    {
        if (_capture == null || !_capture.IsOpened()) return null;

        // If continuous live loop wasn't draining DirectShow ring buffer, flush 1 stale frame first.
        if (!_livePreviewEnabled)
        {
            _capture.Grab();
        }

        var mat = new Mat();
        if (_capture.Read(mat) && !mat.Empty())
            return mat;

        mat.Dispose();
        return null;
    }

    private void CloseCaptureOnWorker()
    {
        try
        {
            if (_capture != null)
            {
                _capture.Release();
                _capture.Dispose();
                _capture = null;
            }
        }
        catch
        {
            _capture = null;
        }
        IsConnected = false;
        MeasuredFps = 0;
    }

    private Dictionary<string, string> BuildCaptureMetadata(int imgWidth, int imgHeight, bool isLiveFrame)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Driver"] = DriverType,
            ["Camera"] = DeviceName,
            ["CameraIndex"] = CameraIndex.ToString(CultureInfo.InvariantCulture),
            ["CameraName"] = CameraFriendlyName,
            ["Width"] = imgWidth.ToString(CultureInfo.InvariantCulture),
            ["Height"] = imgHeight.ToString(CultureInfo.InvariantCulture),
            ["Fps"] = Fps.ToString("0.##", CultureInfo.InvariantCulture),
            ["FourCC"] = FourCC,
            ["Exposure"] = Exposure.HasValue ? Exposure.Value.ToString(CultureInfo.InvariantCulture) : "Auto",
            ["Flip"] = FlipModeName,
            ["ConnectionState"] = isLiveFrame ? "ONLINE" : "OFFLINE_FALLBACK",
            ["OK"] = isLiveFrame ? "True" : "False"
        };
    }

    private static void TrySetProp(VideoCapture cap, VideoCaptureProperties prop, double value)
    {
        try { cap.Set(prop, value); } catch { }
    }

    private void ApplyFlipInPlace(Mat mat)
    {
        switch (FlipModeName)
        {
            case "H": Cv2.Flip(mat, mat, FlipMode.Y); break;
            case "V": Cv2.Flip(mat, mat, FlipMode.X); break;
            case "HV": Cv2.Flip(mat, mat, FlipMode.XY); break;
        }
    }

    private void StoreLatestFrame(Mat src)
    {
        lock (_frameLock)
        {
            _latestFrame?.Dispose();
            _latestFrame = src.Clone();
        }
    }

    public async Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
    {
        _engines = engines;
        if (owner is not Window ownerWin) return currentParam;

        var root = new Grid { Margin = new Thickness(16), Background = Brush.Parse("#F8FAFC") };
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // MVCON Header
        var hdrGrid = new Grid();
        hdrGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        hdrGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        hdrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        hdrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var lblSyntax = new TextBlock
        {
            Text = "Index=...,Width=...,Height=...,FourCC=...",
            Foreground = Brush.Parse("#38BDF8"),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetColumn(lblSyntax, 1);

        var txtParamIn = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(currentParam) ? "ParameterIn: (空白)" : $"ParameterIn: {currentParam.Trim()}",
            Foreground = Brush.Parse("#94A3B8"),
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(txtParamIn, 1);
        Grid.SetColumnSpan(txtParamIn, 2);

        hdrGrid.Children.Add(new TextBlock { Text = "Capture / DirectShowCamera - 驅動參數導引精靈", Foreground = Brush.Parse("#F8FAFC"), FontSize = 15, FontWeight = FontWeight.Bold });
        hdrGrid.Children.Add(lblSyntax);
        hdrGrid.Children.Add(txtParamIn);
        root.Children.Add(new Border { Background = Brush.Parse("#1E293B"), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 0, 10), Child = hdrGrid });

        // MVCON Body
        var bodyGrid = new Grid();
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(112)));
        var fields = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };

        var detectedDevices = DirectShowDeviceEnumerator.EnumerateVideoInputDevices();
        int imgSlots = engines?.Image?.GetAllNames().Count ?? 0;
        var txtBanner = new TextBlock
        {
            Text = BuildDeviceBanner(detectedDevices, imgSlots),
            Foreground = Brush.Parse("#475569"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        };
        fields.Children.Add(new Border
        {
            Background = Brush.Parse("#F1F5F9"),
            BorderBrush = Brush.Parse("#CBD5E1"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 6),
            Child = txtBanner
        });

        static (StackPanel Panel, ComboBox Combo, TextBox Box, TextBlock? Meta, Grid HdrGrid) MakeField(string label, string meta, string watermark, bool required)
        {
            var accent = required ? "#DC2626" : "#2563EB";
            var hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            hdr.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            hdr.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            hdr.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse(accent), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            TextBlock? metaLbl = null;
            if (!string.IsNullOrEmpty(meta))
            {
                metaLbl = new TextBlock { Text = meta, Foreground = Brush.Parse(required ? "#64748B" : "#94A3B8"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                Grid.SetColumn(metaLbl, 1);
                hdr.Children.Add(metaLbl);
            }
            var cmb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(8, 3, 34, 3), Foreground = Brushes.Transparent, FontSize = 13 };
            var txt = new TextBox { Margin = new Thickness(2, 2, 32, 2), BorderThickness = new Thickness(0), Background = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 3), FontSize = 13, FontWeight = required ? FontWeight.SemiBold : FontWeight.Normal, Foreground = Brush.Parse(required ? "#DC2626" : "#1E293B"), Watermark = watermark };
            return (new StackPanel { Spacing = 3, Children = { hdr, new Border { BorderBrush = Brush.Parse(accent), BorderThickness = new Thickness(required ? 1.8 : 1.5), CornerRadius = new CornerRadius(5), Background = Brushes.White, Child = new Grid { Children = { cmb, txt } } } } }, cmb, txt, metaLbl, hdr);
        }

        // Row: DirectShow Device Index
        var (idxP, cmbIdx, txtIdx, txtIdxMeta, idxHdr) = MakeField("Index (DirectShow 相機索引/名稱) *", "已偵測装置", "0, 1 或從右側下拉選擇攝影機...", true);
        var btnRescan = new Button
        {
            Content = "↻ 重新掃描",
            Background = Brush.Parse("#0E639C"),
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 3),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        Grid.SetColumn(btnRescan, 2);
        idxHdr.Children.Add(btnRescan);
        fields.Children.Add(idxP);

        // Row: Width + Height
        var rowRes = new Grid();
        rowRes.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        rowRes.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10)));
        rowRes.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var (wP, cmbW, txtW, txtResMeta, _) = MakeField("Width (影像寬度 px) *", "1280 x 720", "640, 1280, 1920...", true);
        var (hP, cmbH, txtH, _, _) = MakeField("Height (影像高度 px) *", "", "480, 720, 1080...", true);
        Grid.SetColumn(hP, 2);
        rowRes.Children.Add(wP);
        rowRes.Children.Add(hP);
        fields.Children.Add(rowRes);

        // Row: Fps + FourCC (Optional)
        var rowStream = new Grid();
        rowStream.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        rowStream.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10)));
        rowStream.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var (fpsP, cmbFps, txtFps, _, _) = MakeField("Fps (幀率 fps，選填)", "預設 30", "15, 30, 60 (可留空)", false);
        var (fccP, cmbFcc, txtFcc, _, _) = MakeField("FourCC (影像編碼，選填)", "推薦 MJPG", "MJPG, YUY2 (可留空)", false);
        Grid.SetColumn(fccP, 2);
        rowStream.Children.Add(fpsP);
        rowStream.Children.Add(fccP);
        fields.Children.Add(rowStream);

        // Row: Exposure + Flip (Optional)
        var rowOpt = new Grid();
        rowOpt.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        rowOpt.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10)));
        rowOpt.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var (expP, cmbExp, txtExp, _, _) = MakeField("Exposure (曝光值，選填)", "Auto 或 -1~-13", "Auto, -4, -5, -6...", false);
        var (flipP, cmbFlip, txtFlip, _, _) = MakeField("Flip (畫面翻轉，選填)", "None / H / V / HV", "H, V, HV (可留空)", false);
        Grid.SetColumn(flipP, 2);
        rowOpt.Children.Add(expP);
        rowOpt.Children.Add(flipP);
        fields.Children.Add(rowOpt);

        // Apply button
        var btnOk = new Button
        {
            Background = Brush.Parse("#10B981"),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(4, 12, 0, 4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "✓", FontSize = 34, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "套用參數", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center }
                }
            }
        };
        Grid.SetColumn(btnOk, 1);
        bodyGrid.Children.Add(fields);
        bodyGrid.Children.Add(btnOk);

        var bodyBorder = new Border { Background = Brushes.White, BorderBrush = Brush.Parse("#CBD5E1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14), Child = bodyGrid };
        Grid.SetRow(bodyBorder, 1);
        root.Children.Add(bodyBorder);

        // MVCON Footer
        var ftrGrid = new Grid();
        ftrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        ftrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var txtOut = new TextBox { IsReadOnly = true, Background = Brush.Parse("#F1F5F9"), Foreground = Brush.Parse("#0284C7"), FontWeight = FontWeight.Bold, FontFamily = new FontFamily("Consolas"), FontSize = 13, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
        var btnCancel = new Button { Content = "取消", Width = 86, Height = 36, VerticalAlignment = VerticalAlignment.Bottom, Background = Brush.Parse("#E2E8F0"), Foreground = Brush.Parse("#334155"), FontWeight = FontWeight.SemiBold, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(5), Cursor = new Cursor(StandardCursorType.Hand) };
        Grid.SetColumn(btnCancel, 1);
        ftrGrid.Children.Add(new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 12, 0), Children = { new TextBlock { Text = "ParameterOut (組合預覽):", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#475569") }, txtOut } });
        ftrGrid.Children.Add(btnCancel);
        var ftrBorder = new Border { Background = Brushes.White, BorderBrush = Brush.Parse("#CBD5E1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10), Margin = new Thickness(0, 10, 0, 0), Child = ftrGrid };
        Grid.SetRow(ftrBorder, 2);
        root.Children.Add(ftrBorder);

        var dlg = new Window
        {
            Title = "Capture@AddCam::DirectShowCamera - Parameter Guidance",
            Width = 760,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush.Parse("#F8FAFC"),
            Content = root
        };

        // Initial values
        var kv = ArgParaHelper.ParseKeyValueArgs(currentParam ?? string.Empty, out _);
        txtIdx.Text = kv.TryGetValue("Index", out var vIdx) && !string.IsNullOrWhiteSpace(vIdx)
            ? vIdx
            : (kv.TryGetValue("Device", out var vDev) && !string.IsNullOrWhiteSpace(vDev) ? vDev : CameraIndex.ToString());
        txtW.Text = kv.TryGetValue("Width", out var vw) && !string.IsNullOrWhiteSpace(vw) ? vw : Width.ToString();
        txtH.Text = kv.TryGetValue("Height", out var vh) && !string.IsNullOrWhiteSpace(vh) ? vh : Height.ToString();
        txtFps.Text = kv.TryGetValue("Fps", out var vFps) ? vFps : Fps.ToString("0.#", CultureInfo.InvariantCulture);
        txtFcc.Text = kv.TryGetValue("FourCC", out var vFcc) ? vFcc : FourCC;
        txtExp.Text = kv.TryGetValue("Exposure", out var vExp) ? vExp : (Exposure.HasValue ? Exposure.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
        txtFlip.Text = kv.TryGetValue("Flip", out var vFlip) ? vFlip : (FlipModeName == "None" ? string.Empty : FlipModeName);

        var vars = engines?.Variable?.GetAllVariableNames().Select(v => $"${v}").ToList() ?? new List<string>();
        bool syncing = false;

        List<string> BuildDeviceOptions()
        {
            var list = detectedDevices.Select(d => d.DisplayName).ToList();
            if (list.Count == 0) list.Add("0");
            list.AddRange(vars);
            return list;
        }

        void UpdatePreview()
        {
            string rawIdx = txtIdx.Text?.Trim() ?? "";
            string normalizedIdx = NormalizeIndexToken(rawIdx, detectedDevices);
            string w = txtW.Text?.Trim() ?? "";
            string h = txtH.Text?.Trim() ?? "";
            string fps = txtFps.Text?.Trim() ?? "";
            string fcc = txtFcc.Text?.Trim() ?? "";
            string exp = txtExp.Text?.Trim() ?? "";
            string flip = txtFlip.Text?.Trim() ?? "";

            if (txtIdxMeta != null)
            {
                if (normalizedIdx.StartsWith("$"))
                {
                    txtIdxMeta.Text = "變數索引";
                }
                else
                {
                    int resolvedIdx = DirectShowDeviceEnumerator.ResolveDeviceIndex(normalizedIdx, detectedDevices);
                    var found = detectedDevices.FirstOrDefault(d => d.Index == resolvedIdx);
                    txtIdxMeta.Text = found != null ? found.FriendlyName : (detectedDevices.Count == 0 ? "未偵測到鏡頭" : $"Index {resolvedIdx}");
                }
            }
            if (txtResMeta != null) txtResMeta.Text = $"{w} x {h}";

            bool okAll = !string.IsNullOrWhiteSpace(normalizedIdx) && !string.IsNullOrWhiteSpace(w) && !string.IsNullOrWhiteSpace(h);
            btnOk.IsEnabled = okAll;
            if (!okAll) { txtOut.Text = string.Empty; return; }

            var segs = new List<string> { $"Index={normalizedIdx}", $"Width={w}", $"Height={h}" };
            if (!string.IsNullOrWhiteSpace(fps)) segs.Add($"Fps={fps}");
            if (!string.IsNullOrWhiteSpace(fcc)) segs.Add($"FourCC={fcc}");
            if (!string.IsNullOrWhiteSpace(exp)) segs.Add($"Exposure={exp}");
            if (!string.IsNullOrWhiteSpace(flip)) segs.Add($"Flip={flip}");
            txtOut.Text = string.Join(",", segs);
        }

        const string ClearVal = "(留白)";
        var wOpts = new List<string> { "640", "800", "1280", "1920" }; wOpts.AddRange(vars);
        var hOpts = new List<string> { "480", "600", "720", "1080" }; hOpts.AddRange(vars);
        var fpsOpts = new List<string> { ClearVal, "15", "30", "60" }; fpsOpts.AddRange(vars);
        var fccOpts = new List<string> { ClearVal, "MJPG", "YUY2", "Auto" }; fccOpts.AddRange(vars);
        var expOpts = new List<string> { ClearVal, "Auto", "-4", "-5", "-6", "-7", "-8", "-10" }; expOpts.AddRange(vars);
        var flipOpts = new List<string> { ClearVal, "H", "V", "HV" }; flipOpts.AddRange(vars);

        WireWizardCombo(cmbIdx, txtIdx, BuildDeviceOptions, "", () => syncing, v => syncing = v, () =>
        {
            // When user picks "0: Integrated Camera", normalize Index text cleanly to "0".
            syncing = true;
            txtIdx.Text = NormalizeIndexToken(txtIdx.Text, detectedDevices);
            syncing = false;
            UpdatePreview();
        });
        WireWizardCombo(cmbW, txtW, () => wOpts, "", () => syncing, v => syncing = v, UpdatePreview);
        WireWizardCombo(cmbH, txtH, () => hOpts, "", () => syncing, v => syncing = v, UpdatePreview);
        WireWizardCombo(cmbFps, txtFps, () => fpsOpts, ClearVal, () => syncing, v => syncing = v, UpdatePreview);
        WireWizardCombo(cmbFcc, txtFcc, () => fccOpts, ClearVal, () => syncing, v => syncing = v, UpdatePreview);
        WireWizardCombo(cmbExp, txtExp, () => expOpts, ClearVal, () => syncing, v => syncing = v, UpdatePreview);
        WireWizardCombo(cmbFlip, txtFlip, () => flipOpts, ClearVal, () => syncing, v => syncing = v, UpdatePreview);

        btnRescan.Click += (_, _) =>
        {
            detectedDevices = DirectShowDeviceEnumerator.EnumerateVideoInputDevices();
            txtBanner.Text = BuildDeviceBanner(detectedDevices, imgSlots);
            cmbIdx.ItemsSource = BuildDeviceOptions();
            UpdatePreview();
        };

        void ConfirmClose() { var r = txtOut.Text?.Trim() ?? string.Empty; if (!string.IsNullOrWhiteSpace(r)) dlg.Close(r); }
        btnCancel.Click += (_, _) => dlg.Close(null);
        btnOk.Click += (_, _) => ConfirmClose();
        dlg.KeyDown += (_, e) =>
        {
            if (cmbIdx.IsDropDownOpen || cmbW.IsDropDownOpen || cmbH.IsDropDownOpen ||
                cmbFps.IsDropDownOpen || cmbFcc.IsDropDownOpen || cmbExp.IsDropDownOpen || cmbFlip.IsDropDownOpen)
                return;
            if (e.Key == Key.Enter) ConfirmClose();
            else if (e.Key == Key.Escape) dlg.Close(null);
        };

        UpdatePreview();
        return await dlg.ShowDialog<string?>(ownerWin);
    }

    private string BuildDeviceBanner(IReadOnlyList<DirectShowDeviceInfo> devices, int imgSlots)
    {
        string devText = devices.Count > 0
            ? string.Join(" | ", devices.Select(d => d.DisplayName))
            : "(未偵測到 Windows DirectShow 視訊裝置)";
        return $"DirectShow 裝置 ({devices.Count}): {devText} | ImageEngine 緩衝區: {imgSlots} | 實例: {(string.IsNullOrWhiteSpace(DeviceName) ? "Cam0" : DeviceName)}";
    }

    private static string NormalizeIndexToken(string? raw, IReadOnlyList<DirectShowDeviceInfo> devices)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string trimmed = raw.Trim();
        if (trimmed.StartsWith('$')) return trimmed;
        int colon = trimmed.IndexOf(':');
        if (colon > 0 && int.TryParse(trimmed[..colon].Trim(), out int idx))
            return idx.ToString();
        if (int.TryParse(trimmed, out _)) return trimmed;
        var found = devices.FirstOrDefault(d => d.FriendlyName.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        return found != null ? found.Index.ToString() : trimmed;
    }

    private static void WireWizardCombo(ComboBox? cmb, TextBox? txt, Func<List<string>> getItems, string clearSentinel,
        Func<bool> getSync, Action<bool> setSync, Action onUpdated)
    {
        if (cmb == null || txt == null) return;
        cmb.ItemsSource = getItems();
        cmb.DropDownOpened += (_, _) => { cmb.ItemsSource = getItems(); setSync(true); cmb.SelectedIndex = -1; setSync(false); };
        cmb.SelectionChanged += (_, _) =>
        {
            if (getSync() || cmb.SelectedItem == null) return;
            string sel = cmb.SelectedItem.ToString() ?? string.Empty;
            setSync(true);
            txt.Text = (!string.IsNullOrEmpty(clearSentinel) && sel == clearSentinel) ? string.Empty : sel;
            cmb.SelectedIndex = -1;
            setSync(false);
            onUpdated();
        };
        txt.TextChanged += (_, _) => { if (!getSync()) onUpdated(); };
    }

    public void Shutdown()
    {
        _livePreviewEnabled = false;
        _cts?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
        try { _workerTask?.Wait(1500); } catch { }
        lock (_frameLock)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }
        _cts?.Dispose();
    }
}
