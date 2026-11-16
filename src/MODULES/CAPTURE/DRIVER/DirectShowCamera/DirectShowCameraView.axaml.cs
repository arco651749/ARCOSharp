using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ARCOS.Core.Drivers;
using OpenCvSharp;

namespace ARCOS.Driver.DirectShowCamera;

public partial class DirectShowCameraView : UserControl
{
    private readonly DirectShowCameraDriver _driver;
    private IReadOnlyList<DirectShowDeviceInfo> _devices = Array.Empty<DirectShowDeviceInfo>();
    private WriteableBitmap? _bitmap;
    private int _uiUpdatePending;

    public DirectShowCameraView()
    {
        AvaloniaXamlLoader.Load(this);
        _driver = null!;
    }

    public DirectShowCameraView(DirectShowCameraDriver driver)
    {
        AvaloniaXamlLoader.Load(this);
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));

        var cmbDev = this.FindControl<ComboBox>("CmbDevices");
        var btnRescan = this.FindControl<Button>("BtnRescan");
        var btnOpen = this.FindControl<Button>("BtnOpen");
        var btnClose = this.FindControl<Button>("BtnClose");
        var chkLive = this.FindControl<CheckBox>("ChkLive");

        var cmbRes = this.FindControl<ComboBox>("CmbResolution");
        var cmbFcc = this.FindControl<ComboBox>("CmbFourCC");
        var cmbFlip = this.FindControl<ComboBox>("CmbFlip");
        var cmbExp = this.FindControl<ComboBox>("CmbExposure");
        var btnApply = this.FindControl<Button>("BtnApplyConfig");
        var btnSnap = this.FindControl<Button>("BtnSnap");
        var btnTrigger = this.FindControl<Button>("BtnTrigger");

        PopulateDevices(cmbDev);

        if (cmbRes != null)
        {
            var resOpts = new List<string> { "640x480", "800x600", "1280x720", "1920x1080" };
            string curRes = $"{_driver.Width}x{_driver.Height}";
            if (!resOpts.Contains(curRes)) resOpts.Insert(0, curRes);
            cmbRes.ItemsSource = resOpts;
            cmbRes.SelectedItem = curRes;
        }

        if (cmbFcc != null)
        {
            cmbFcc.ItemsSource = new[] { "MJPG", "YUY2", "Auto" };
            cmbFcc.SelectedItem = _driver.FourCC;
        }

        if (cmbFlip != null)
        {
            cmbFlip.ItemsSource = new[] { "None", "H", "V", "HV" };
            cmbFlip.SelectedItem = _driver.FlipModeName;
        }

        if (cmbExp != null)
        {
            var expOpts = new List<string> { "Auto", "-3", "-4", "-5", "-6", "-7", "-8", "-9", "-10" };
            string curExp = _driver.Exposure.HasValue
                ? _driver.Exposure.Value.ToString(CultureInfo.InvariantCulture)
                : "Auto";
            if (!expOpts.Contains(curExp)) expOpts.Add(curExp);
            cmbExp.ItemsSource = expOpts;
            cmbExp.SelectedItem = curExp;
        }

        if (chkLive != null)
        {
            _driver.LivePreviewEnabled = chkLive.IsChecked == true;
            chkLive.IsCheckedChanged += (_, _) => _driver.LivePreviewEnabled = chkLive.IsChecked == true;
        }

        string BuildConfigParam()
        {
            int idx = _driver.CameraIndex;
            if (cmbDev?.SelectedItem is string devSel)
                idx = DirectShowDeviceEnumerator.ResolveDeviceIndex(devSel, _devices);

            string resStr = cmbRes?.SelectedItem as string ?? $"{_driver.Width}x{_driver.Height}";
            var resParts = resStr.Split('x');
            string w = resParts.Length == 2 ? resParts[0].Trim() : _driver.Width.ToString();
            string h = resParts.Length == 2 ? resParts[1].Trim() : _driver.Height.ToString();
            string fcc = cmbFcc?.SelectedItem as string ?? _driver.FourCC;
            string flip = cmbFlip?.SelectedItem as string ?? _driver.FlipModeName;
            string exp = cmbExp?.SelectedItem as string ?? "Auto";
            return $"Index={idx},Width={w},Height={h},FourCC={fcc},Flip={flip},Exposure={exp}";
        }

        if (btnRescan != null) btnRescan.Click += (_, _) => PopulateDevices(cmbDev);
        if (btnOpen != null) btnOpen.Click += (_, _) => _driver.PostTicket(new DriverTicket("Reconnect", BuildConfigParam()));
        if (btnClose != null) btnClose.Click += (_, _) => _driver.PostTicket(new DriverTicket("Close"));
        if (btnApply != null) btnApply.Click += (_, _) => _driver.PostTicket(new DriverTicket("Config", BuildConfigParam()));
        if (btnSnap != null) btnSnap.Click += (_, _) => _driver.PostTicket(new DriverTicket("Snap"));
        if (btnTrigger != null) btnTrigger.Click += (_, _) => _driver.PostTicket(new DriverTicket("Trigger"));

        _driver.CameraStateUpdated += OnCameraStateUpdated;
        AttachedToVisualTree += (_, _) =>
        {
            if (chkLive != null) _driver.LivePreviewEnabled = chkLive.IsChecked == true;
            ScheduleUiUpdate();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _driver.LivePreviewEnabled = false;
            _driver.CameraStateUpdated -= OnCameraStateUpdated;
        };

        UpdateVisuals();
    }

    private void PopulateDevices(ComboBox? cmbDev)
    {
        _devices = DirectShowDeviceEnumerator.EnumerateVideoInputDevices();
        if (cmbDev == null) return;

        var items = _devices.Count > 0
            ? _devices.Select(d => d.DisplayName).ToList()
            : new List<string> { $"{_driver.CameraIndex}: DirectShow Camera {_driver.CameraIndex}" };

        cmbDev.ItemsSource = items;
        var current = items.FirstOrDefault(x => x.StartsWith($"{_driver.CameraIndex}:", StringComparison.OrdinalIgnoreCase));
        cmbDev.SelectedItem = current ?? items.FirstOrDefault();
    }

    private void OnCameraStateUpdated()
    {
        ScheduleUiUpdate();
    }

    private void ScheduleUiUpdate()
    {
        if (Interlocked.Exchange(ref _uiUpdatePending, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _uiUpdatePending, 0);
                UpdateVisuals();
            }, DispatcherPriority.Render);
        }
    }

    private void UpdateVisuals()
    {
        var txtInfo = this.FindControl<TextBlock>("TxtDeviceInfo");
        if (txtInfo != null)
        {
            int showW = _driver.ActualWidth > 0 ? _driver.ActualWidth : _driver.Width;
            int showH = _driver.ActualHeight > 0 ? _driver.ActualHeight : _driver.Height;
            string fpsInfo = _driver.MeasuredFps > 0.1 ? $"{_driver.MeasuredFps:0.0} fps" : $"{_driver.Fps:0.#} fps";
            txtInfo.Text = $"{_driver.DeviceName} ({_driver.CameraFriendlyName}) | {showW}x{showH} | {fpsInfo} | {_driver.FourCC}";
        }

        var ledConn = this.FindControl<Ellipse>("LedConn");
        if (ledConn != null) ledConn.Fill = _driver.IsConnected ? Brushes.LimeGreen : Brushes.IndianRed;

        var txtConn = this.FindControl<TextBlock>("TxtConnState");
        if (txtConn != null) txtConn.Text = _driver.IsConnected ? "ONLINE" : "OFFLINE";

        var ledOk = this.FindControl<Ellipse>("LedOk");
        if (ledOk != null) ledOk.Fill = _driver.FrameCount > 0 ? Brushes.LimeGreen : Brushes.Gray;

        var txtCount = this.FindControl<TextBlock>("TxtFrameCount");
        if (txtCount != null) txtCount.Text = _driver.FrameCount.ToString();

        var txtErr = this.FindControl<TextBlock>("TxtErrorBanner");
        if (txtErr != null) txtErr.Text = _driver.LastError ?? string.Empty;

        var img = this.FindControl<Image>("ImgPreview");
        var txtPlace = this.FindControl<TextBlock>("TxtPlaceholder");
        if (img == null) return;

        if (_driver.TryCopyLatestFrame(out Mat? mat) && mat != null)
        {
            try
            {
                RenderMatToImage(img, mat);
                if (txtPlace != null) txtPlace.IsVisible = false;
            }
            finally
            {
                mat.Dispose();
            }
        }
        else
        {
            img.Source = null;
            if (txtPlace != null) txtPlace.IsVisible = true;
        }
    }

    private unsafe void RenderMatToImage(Image imgControl, Mat bgrMat)
    {
        using var bgra = new Mat();
        if (bgrMat.Channels() == 4)
            bgrMat.CopyTo(bgra);
        else if (bgrMat.Channels() == 1)
            Cv2.CvtColor(bgrMat, bgra, ColorConversionCodes.GRAY2BGRA);
        else
            Cv2.CvtColor(bgrMat, bgra, ColorConversionCodes.BGR2BGRA);

        if (!bgra.IsContinuous())
        {
            using var cont = bgra.Clone();
            CopyBgraToBitmap(imgControl, cont);
            return;
        }

        CopyBgraToBitmap(imgControl, bgra);
    }

    private unsafe void CopyBgraToBitmap(Image imgControl, Mat bgra)
    {
        int w = bgra.Width;
        int h = bgra.Height;
        if (w <= 0 || h <= 0) return;

        if (_bitmap == null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
        {
            _bitmap = new WriteableBitmap(
                new PixelSize(w, h),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);
        }

        using (var fb = _bitmap.Lock())
        {
            long bytesPerRow = Math.Min(fb.RowBytes, (long)w * 4);
            byte* srcBase = (byte*)bgra.DataPointer;
            byte* dstBase = (byte*)fb.Address;
            long srcStep = bgra.Step();

            for (int y = 0; y < h; y++)
            {
                Buffer.MemoryCopy(
                    srcBase + y * srcStep,
                    dstBase + y * fb.RowBytes,
                    fb.RowBytes,
                    bytesPerRow);
            }
        }

        // Update existing bitmap in-place via InvalidateVisual to avoid per-frame layout/flicker churn.
        if (!ReferenceEquals(imgControl.Source, _bitmap))
            imgControl.Source = _bitmap;
        else
            imgControl.InvalidateVisual();
    }
}
