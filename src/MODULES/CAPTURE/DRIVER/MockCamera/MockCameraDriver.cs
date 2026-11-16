using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;
using OpenCvSharp;
using Point = OpenCvSharp.Point;

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

    public async Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
    {
        _engines = engines;
        if (owner is not Avalonia.Controls.Window ownerWin) return currentParam;

        var root = new Grid { Margin = new Thickness(16), Background = Brush.Parse("#F8FAFC") };
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var hdrGrid = new Grid();
        hdrGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        hdrGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        hdrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        hdrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var lblSyntax = new TextBlock { Text = "Width=...,Height=...,Exposure=...,File=...", Foreground = Brush.Parse("#38BDF8"), FontSize = 11, FontWeight = FontWeight.SemiBold, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(lblSyntax, 1);
        var txtParamIn = new TextBlock { Text = string.IsNullOrWhiteSpace(currentParam) ? "ParameterIn: (空白)" : $"ParameterIn: {currentParam.Trim()}", Foreground = Brush.Parse("#94A3B8"), FontSize = 11, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetRow(txtParamIn, 1); Grid.SetColumnSpan(txtParamIn, 2);
        hdrGrid.Children.Add(new TextBlock { Text = "Capture / MockCamera - 驅動參數導引精靈", Foreground = Brush.Parse("#F8FAFC"), FontSize = 15, FontWeight = FontWeight.Bold });
        hdrGrid.Children.Add(lblSyntax);
        hdrGrid.Children.Add(txtParamIn);
        root.Children.Add(new Border { Background = Brush.Parse("#1E293B"), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 0, 10), Child = hdrGrid });

        var bodyGrid = new Grid();
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(112)));
        var fields = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };

        int imgSlots = engines?.Image?.GetAllNames().Count ?? 0;
        fields.Children.Add(new Border
        {
            Background = Brush.Parse("#F1F5F9"),
            BorderBrush = Brush.Parse("#CBD5E1"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 6),
            Child = new TextBlock { Text = $"目前 ImageEngine 緩衝區數: {imgSlots} | 實例: {(string.IsNullOrWhiteSpace(DeviceName) ? "Cam1" : DeviceName)}", Foreground = Brush.Parse("#475569"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap }
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
                Grid.SetColumn(metaLbl, 1); hdr.Children.Add(metaLbl);
            }
            var cmb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(8, 3, 34, 3), Foreground = Brushes.Transparent, FontSize = 13 };
            var txt = new TextBox { Margin = new Thickness(2, 2, 32, 2), BorderThickness = new Thickness(0), Background = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 3), FontSize = 13, FontWeight = required ? FontWeight.SemiBold : FontWeight.Normal, Foreground = Brush.Parse(required ? "#DC2626" : "#1E293B"), Watermark = watermark };
            return (new StackPanel { Spacing = 3, Children = { hdr, new Border { BorderBrush = Brush.Parse(accent), BorderThickness = new Thickness(required ? 1.8 : 1.5), CornerRadius = new CornerRadius(5), Background = Brushes.White, Child = new Grid { Children = { cmb, txt } } } } }, cmb, txt, metaLbl, hdr);
        }

        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); row1.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10))); row1.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var (wP, cmbW, txtW, txtResMeta, _) = MakeField("Width (影像寬度 px) *", "640 x 480", "640, 1280, 1920...", true);
        var (hP, cmbH, txtH, _, _) = MakeField("Height (影像高度 px) *", "", "480, 1024, 1080...", true);
        Grid.SetColumn(hP, 2); row1.Children.Add(wP); row1.Children.Add(hP); fields.Children.Add(row1);

        var (eP, cmbExp, txtExp, _, _) = MakeField("Exposure (曝光時間 ms) *", "Snap / Trigger 模擬延遲", "10, 20, 50, 100...", true);
        fields.Children.Add(eP);

        var (fP, cmbFile, txtFile, txtFileMeta, fHdr) = MakeField("File (預載模擬圖檔路徑，選填)", "動態生成測試圖", "選擇工作區影像檔、輸入路徑或留白動態生成...", false);
        var btnBrowse = new Button { Content = "瀏覽圖檔...", Background = Brush.Parse("#0E639C"), Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeight.SemiBold, Padding = new Thickness(10, 3), CornerRadius = new CornerRadius(4), Cursor = new Cursor(StandardCursorType.Hand) };
        Grid.SetColumn(btnBrowse, 2); fHdr.Children.Add(btnBrowse);
        fields.Children.Add(fP);

        var btnOk = new Button { Background = Brush.Parse("#10B981"), Foreground = Brushes.White, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(4, 12, 0, 4), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand), Content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = "✓", FontSize = 34, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center }, new TextBlock { Text = "套用參數", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center } } } };
        Grid.SetColumn(btnOk, 1); bodyGrid.Children.Add(fields); bodyGrid.Children.Add(btnOk);
        var bodyBorder = new Border { Background = Brushes.White, BorderBrush = Brush.Parse("#CBD5E1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14), Child = bodyGrid };
        Grid.SetRow(bodyBorder, 1); root.Children.Add(bodyBorder);

        var ftrGrid = new Grid();
        ftrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); ftrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var txtOut = new TextBox { IsReadOnly = true, Background = Brush.Parse("#F1F5F9"), Foreground = Brush.Parse("#0284C7"), FontWeight = FontWeight.Bold, FontFamily = new FontFamily("Consolas"), FontSize = 13, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
        var btnCancel = new Button { Content = "取消", Width = 86, Height = 36, VerticalAlignment = VerticalAlignment.Bottom, Background = Brush.Parse("#E2E8F0"), Foreground = Brush.Parse("#334155"), FontWeight = FontWeight.SemiBold, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(5), Cursor = new Cursor(StandardCursorType.Hand) };
        Grid.SetColumn(btnCancel, 1);
        ftrGrid.Children.Add(new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 12, 0), Children = { new TextBlock { Text = "ParameterOut (組合預覽):", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#475569") }, txtOut } });
        ftrGrid.Children.Add(btnCancel);
        var ftrBorder = new Border { Background = Brushes.White, BorderBrush = Brush.Parse("#CBD5E1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10), Margin = new Thickness(0, 10, 0, 0), Child = ftrGrid };
        Grid.SetRow(ftrBorder, 2); root.Children.Add(ftrBorder);

        var dlg = new Avalonia.Controls.Window { Title = "Capture@AddCam::MockCamera - Parameter Guidance", Width = 740, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#F8FAFC"), Content = root };

        var kv = ArgParaHelper.ParseKeyValueArgs(currentParam ?? "", out var posArgs);
        txtW.Text = kv.TryGetValue("Width", out var vw) && !string.IsNullOrWhiteSpace(vw) ? vw : Width.ToString();
        txtH.Text = kv.TryGetValue("Height", out var vh) && !string.IsNullOrWhiteSpace(vh) ? vh : Height.ToString();
        txtExp.Text = kv.TryGetValue("Exposure", out var ve) && !string.IsNullOrWhiteSpace(ve) ? ve : ExposureMs.ToString("G");
        txtFile.Text = kv.TryGetValue("File", out var vf) ? vf : (kv.TryGetValue("Path", out var vp) ? vp : (posArgs.Count > 0 ? posArgs[0] : (PreloadedImagePath ?? "")));

        var vars = engines?.Variable?.GetAllVariableNames().Select(v => $"${v}").ToList() ?? new List<string>();
        List<string> CollectSampleImages()
        {
            const string ClearFile = "(動態生成模擬測試圖 - 留白)";
            var items = new List<string> { ClearFile };
            try
            {
                string cwd = Directory.GetCurrentDirectory();
                foreach (var dir in new[] { Path.Combine(cwd, "PICTURE"), Path.Combine(cwd, "WORKSPACE"), cwd })
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir).Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)).Take(12))
                    {
                        string rel = Path.GetRelativePath(cwd, f);
                        if (!items.Contains(rel, StringComparer.OrdinalIgnoreCase)) items.Add(rel);
                    }
                }
            }
            catch { }
            items.AddRange(vars);
            return items;
        }

        bool syncing = false;
        void UpdatePreview()
        {
            string w = txtW.Text?.Trim() ?? "", h = txtH.Text?.Trim() ?? "", e = txtExp.Text?.Trim() ?? "", f = txtFile.Text?.Trim() ?? "";
            if (txtResMeta != null) txtResMeta.Text = $"{w} x {h}";
            if (txtFileMeta != null) txtFileMeta.Text = string.IsNullOrWhiteSpace(f) ? "動態生成測試圖" : (File.Exists(f) ? "✓ 檔案存在" : "路徑/變數");
            bool okAll = !string.IsNullOrWhiteSpace(w) && !string.IsNullOrWhiteSpace(h) && !string.IsNullOrWhiteSpace(e);
            btnOk.IsEnabled = okAll;
            if (!okAll) { txtOut.Text = string.Empty; return; }
            var segs = new List<string> { $"Width={w}", $"Height={h}", $"Exposure={e}" };
            if (!string.IsNullOrWhiteSpace(f)) segs.Add($"File={f}");
            txtOut.Text = string.Join(",", segs);
        }

        var wOpts = new List<string> { "640", "800", "1280", "1920", "2592" }; wOpts.AddRange(vars);
        var hOpts = new List<string> { "480", "600", "1024", "1080", "1944" }; hOpts.AddRange(vars);
        var expOpts = new List<string> { "10", "20", "50", "100", "500", "1000" }; expOpts.AddRange(vars);

        WireWizardCombo(cmbW, txtW, () => wOpts, "", () => syncing, v => syncing = v, UpdatePreview);
        WireWizardCombo(cmbH, txtH, () => hOpts, "", () => syncing, v => syncing = v, UpdatePreview);
        WireWizardCombo(cmbExp, txtExp, () => expOpts, "", () => syncing, v => syncing = v, UpdatePreview);
        WireWizardCombo(cmbFile, txtFile, CollectSampleImages, "(動態生成模擬測試圖 - 留白)", () => syncing, v => syncing = v, UpdatePreview);

        btnBrowse.Click += async (_, _) =>
        {
            try
            {
                var picked = await dlg.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "選擇 MockCamera 模擬載入圖檔",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("Image Files") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" } } }
                });
                if (picked != null && picked.Count > 0)
                {
                    txtFile.Text = picked[0].Path.LocalPath;
                    UpdatePreview();
                }
            }
            catch { }
        };

        void ConfirmClose() { var r = txtOut.Text?.Trim() ?? ""; if (!string.IsNullOrWhiteSpace(r)) dlg.Close(r); }
        btnCancel.Click += (_, _) => dlg.Close(null);
        btnOk.Click += (_, _) => ConfirmClose();
        dlg.KeyDown += (_, e) =>
        {
            if (cmbW.IsDropDownOpen || cmbH.IsDropDownOpen || cmbExp.IsDropDownOpen || cmbFile.IsDropDownOpen) return;
            if (e.Key == Key.Enter) ConfirmClose(); else if (e.Key == Key.Escape) dlg.Close(null);
        };
        UpdatePreview();
        return await dlg.ShowDialog<string?>(ownerWin);
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
            string sel = cmb.SelectedItem.ToString() ?? "";
            setSync(true);
            txt.Text = (!string.IsNullOrEmpty(clearSentinel) && sel == clearSentinel) ? "" : sel;
            cmb.SelectedIndex = -1;
            setSync(false);
            onUpdated();
        };
        txt.TextChanged += (_, _) => { if (!getSync()) onUpdated(); };
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
    }

    public void Dispose()
    {
        Shutdown();
        _cts?.Dispose();
    }
}
