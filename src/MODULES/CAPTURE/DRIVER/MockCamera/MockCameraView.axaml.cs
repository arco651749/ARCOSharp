using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MVCON.Core.Drivers;
using OpenCvSharp;

namespace MVCON.Driver.MockCamera;

public partial class MockCameraView : UserControl
{
    private readonly MockCameraDriver _driver;

    public MockCameraView()
    {
        InitializeComponent();
        _driver = null!;
    }

    public MockCameraView(MockCameraDriver driver)
    {
        InitializeComponent();
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));

        var txtInfo = this.FindControl<TextBlock>("TxtDeviceInfo");
        if (txtInfo != null)
        {
            txtInfo.Text = $"{_driver.DeviceName} ({_driver.DriverType}) | {_driver.Width}x{_driver.Height} | 曝光: {_driver.ExposureMs}ms";
        }

        var btnSnap = this.FindControl<Button>("BtnSnap");
        if (btnSnap != null) btnSnap.Click += (s, e) => _driver.PostTicket(new DriverTicket("Snap"));

        _driver.CameraStateUpdated += OnCameraStateUpdated;
        DetachedFromVisualTree += (s, e) => _driver.CameraStateUpdated -= OnCameraStateUpdated;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnCameraStateUpdated()
    {
        Dispatcher.UIThread.Post(UpdateVisuals);
    }

    private void UpdateVisuals()
    {
        var txtCount = this.FindControl<TextBlock>("TxtFrameCount");
        if (txtCount != null) txtCount.Text = _driver.FrameCount.ToString();

        var ledOk = this.FindControl<Ellipse>("LedOk");
        if (ledOk != null) ledOk.Fill = _driver.FrameCount > 0 ? Brushes.LimeGreen : Brushes.Gray;

        var txtPlace = this.FindControl<TextBlock>("TxtPlaceholder");
        if (txtPlace != null) txtPlace.IsVisible = _driver.FrameCount == 0;

        var img = this.FindControl<Image>("ImgPreview");
        if (img != null)
        {
            try
            {
                using var mat = _driver.GenerateOrLoadImage();
                Cv2.ImEncode(".bmp", mat, out byte[] buf);
                using var ms = new MemoryStream(buf);
                img.Source = new Bitmap(ms);
            }
            catch { }
        }
    }
}
