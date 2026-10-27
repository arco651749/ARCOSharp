using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace MVCON.Driver.MockIO;

public partial class MockIoView : UserControl
{
    private readonly MockIoDriver _driver;
    private readonly List<CheckBox> _inputCheckBoxes = new();
    private readonly List<Ellipse> _outputLeds = new();

    public MockIoView()
    {
        InitializeComponent();
        _driver = null!;
    }

    public MockIoView(MockIoDriver driver)
    {
        InitializeComponent();
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));

        var txtInfo = this.FindControl<TextBlock>("TxtDeviceInfo");
        if (txtInfo != null)
        {
            txtInfo.Text = $"{_driver.DeviceName} ({_driver.DriverType}) | Out: {_driver.OutStart}[{_driver.OutPorts}] | In: {_driver.InStart}[{_driver.InPorts}] | 內部週期: {_driver.IntervalMs}ms";
        }

        BuildInputControls();
        BuildOutputControls();

        _driver.IoStateUpdated += OnIoStateUpdated;
        DetachedFromVisualTree += (s, e) => _driver.IoStateUpdated -= OnIoStateUpdated;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void BuildInputControls()
    {
        var panel = this.FindControl<WrapPanel>("InputsPanel");
        if (panel == null) return;

        panel.Children.Clear();
        _inputCheckBoxes.Clear();

        for (int i = 0; i < _driver.InPorts; i++)
        {
            int idx = i;
            var cb = new CheckBox
            {
                Content = $"In {idx}",
                Foreground = Brushes.White,
                Margin = new Thickness(6),
                IsChecked = _driver.SimulatedInputs.Length > idx && _driver.SimulatedInputs[idx]
            };

            cb.IsCheckedChanged += (s, e) => _driver.SetSimulatedInput(idx, cb.IsChecked == true);

            _inputCheckBoxes.Add(cb);
            panel.Children.Add(cb);
        }
    }

    private void BuildOutputControls()
    {
        var panel = this.FindControl<WrapPanel>("OutputsPanel");
        if (panel == null) return;

        panel.Children.Clear();
        _outputLeds.Clear();

        for (int i = 0; i < _driver.OutPorts; i++)
        {
            int idx = i;
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 6, 8, 6)
            };

            var led = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = Brushes.Gray,
                Stroke = Brushes.WhiteSmoke,
                StrokeThickness = 1,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var lbl = new TextBlock
            {
                Text = $"Out {idx}",
                Foreground = Brushes.LightGray,
                VerticalAlignment = VerticalAlignment.Center
            };

            sp.Children.Add(led);
            sp.Children.Add(lbl);

            _outputLeds.Add(led);
            panel.Children.Add(sp);
        }
    }

    private void OnIoStateUpdated()
    {
        Dispatcher.UIThread.Post(UpdateVisuals);
    }

    private void UpdateVisuals()
    {
        for (int i = 0; i < _outputLeds.Count; i++)
        {
            if (i < _driver.ReadbackOutputs.Length)
            {
                bool isOn = _driver.ReadbackOutputs[i];
                _outputLeds[i].Fill = isOn ? Brushes.LimeGreen : Brushes.Gray;
            }
        }
    }
}
