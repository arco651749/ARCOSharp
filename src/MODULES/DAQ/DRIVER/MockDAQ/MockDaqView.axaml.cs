using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using MVCON.Core.Drivers;

namespace MVCON.Driver.MockDAQ;

public partial class MockDaqView : UserControl
{
    private readonly MockDaqDriver _driver;

    public MockDaqView()
    {
        InitializeComponent();
        _driver = null!;
    }

    public MockDaqView(MockDaqDriver driver)
    {
        InitializeComponent();
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));

        var txtInfo = this.FindControl<TextBlock>("TxtDeviceInfo");
        if (txtInfo != null)
        {
            txtInfo.Text = $"{_driver.DeviceName} ({_driver.DriverType}) | 採樣率: {_driver.SampleRate}Hz | 點數: {_driver.SampleCount}";
        }

        var btnSample = this.FindControl<Button>("BtnSample");
        if (btnSample != null) btnSample.Click += (s, e) => _driver.PostTicket(new DriverTicket("Sample"));

        var btnStart = this.FindControl<Button>("BtnStart");
        if (btnStart != null) btnStart.Click += (s, e) => _driver.PostTicket(new DriverTicket("Start"));

        var btnStop = this.FindControl<Button>("BtnStop");
        if (btnStop != null) btnStop.Click += (s, e) => _driver.PostTicket(new DriverTicket("Stop"));

        _driver.DaqStateUpdated += OnDaqStateUpdated;
        DetachedFromVisualTree += (s, e) => _driver.DaqStateUpdated -= OnDaqStateUpdated;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDaqStateUpdated()
    {
        Dispatcher.UIThread.Post(UpdateVisuals);
    }

    private void UpdateVisuals()
    {
        var txtInstant = this.FindControl<TextBlock>("TxtInstant");
        if (txtInstant != null) txtInstant.Text = _driver.InstantValue.ToString("F2", CultureInfo.InvariantCulture);

        var ledSampling = this.FindControl<Ellipse>("LedSampling");
        if (ledSampling != null) ledSampling.Fill = _driver.IsSampling ? Brushes.LimeGreen : Brushes.Gray;

        var ledOk = this.FindControl<Ellipse>("LedOk");
        if (ledOk != null) ledOk.Fill = _driver.LatestWaveform.Length > 0 ? Brushes.DeepSkyBlue : Brushes.Gray;

        var txtPlace = this.FindControl<TextBlock>("TxtChartPlaceholder");
        if (txtPlace != null) txtPlace.IsVisible = _driver.LatestWaveform.Length == 0;

        DrawWaveform();
    }

    private void DrawWaveform()
    {
        var canvas = this.FindControl<Canvas>("WaveCanvas");
        if (canvas == null) return;

        canvas.Children.Clear();
        var yData = _driver.LatestWaveform;
        if (yData.Length < 2) return;

        double width = canvas.Bounds.Width;
        double height = canvas.Bounds.Height;
        if (width <= 0 || height <= 0) return;

        double max = yData.Max();
        double min = yData.Min();
        double span = max - min;
        if (span <= 0) span = 1;

        double stepX = width / (yData.Length - 1);

        for (int i = 0; i < yData.Length - 1; i++)
        {
            double x1 = i * stepX;
            double y1 = height - ((yData[i] - min) / span * (height - 20) + 10);
            double x2 = (i + 1) * stepX;
            double y2 = height - ((yData[i + 1] - min) / span * (height - 20) + 10);

            var line = new Line
            {
                StartPoint = new Point(x1, y1),
                EndPoint = new Point(x2, y2),
                Stroke = Brushes.DeepSkyBlue,
                StrokeThickness = 1.5
            };
            canvas.Children.Add(line);
        }
    }
}
