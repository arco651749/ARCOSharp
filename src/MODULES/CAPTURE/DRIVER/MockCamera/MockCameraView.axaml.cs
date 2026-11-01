using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ARCOS.Core.Drivers;
using OpenCvSharp;

namespace ARCOS.Driver.MockCamera;

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

        var txtPath = this.FindControl<TextBox>("TxtImagePath");
        if (txtPath != null && !string.IsNullOrEmpty(_driver.PreloadedImagePath))
        {
            txtPath.Text = _driver.PreloadedImagePath;
        }

        var btnSnap = this.FindControl<Button>("BtnSnap");
        if (btnSnap != null) btnSnap.Click += (s, e) => _driver.PostTicket(new DriverTicket("Snap"));

        var btnTrigger = this.FindControl<Button>("BtnTrigger");
        if (btnTrigger != null) btnTrigger.Click += (s, e) => _driver.PostTicket(new DriverTicket("Trigger"));

        var btnLoad = this.FindControl<Button>("BtnLoadImage");
        if (btnLoad != null)
        {
            btnLoad.Click += (s, e) =>
            {
                if (txtPath != null && !string.IsNullOrWhiteSpace(txtPath.Text))
                {
                    _driver.SetImageFile(txtPath.Text);
                    _driver.PostTicket(new DriverTicket("Snap"));
                }
            };
        }

        var btnBrowse = this.FindControl<Button>("BtnBrowseImage");
        if (btnBrowse != null)
        {
            btnBrowse.Click += async (s, e) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel != null)
                {
                    var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        Title = "選擇模擬相機載入之圖片 (Select Image for MockCamera)",
                        AllowMultiple = false,
                        FileTypeFilter = new[]
                        {
                            new FilePickerFileType("Image Files (*.jpg, *.png, *.bmp)")
                            {
                                Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.tiff" }
                            }
                        }
                    });

                    if (files.Count > 0)
                    {
                        string localPath = files[0].Path.LocalPath;
                        if (txtPath != null) txtPath.Text = localPath;
                        _driver.SetImageFile(localPath);
                        _driver.PostTicket(new DriverTicket("Snap"));
                    }
                }
            };
        }

        if (txtPath != null)
        {
            txtPath.LostFocus += (s, e) => _driver.SetImageFile(txtPath.Text);
            txtPath.KeyUp += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    _driver.SetImageFile(txtPath.Text);
                    _driver.PostTicket(new DriverTicket("Snap"));
                }
            };
        }

        _driver.CameraStateUpdated += OnCameraStateUpdated;
        DetachedFromVisualTree += (s, e) => _driver.CameraStateUpdated -= OnCameraStateUpdated;

        UpdateVisuals();
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

        var txtPath = this.FindControl<TextBox>("TxtImagePath");
        if (txtPath != null && !string.IsNullOrEmpty(_driver.PreloadedImagePath) && string.IsNullOrEmpty(txtPath.Text))
        {
            txtPath.Text = _driver.PreloadedImagePath;
        }

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
