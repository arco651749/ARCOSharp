using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;

namespace ARCOS.Driver.MockIO;

public partial class MockIoView : UserControl
{
    private MockIoDriver _driver;
    private readonly List<ToggleButtonState> _inputButtons = new();
    private readonly List<Ellipse> _outputLeds = new();
    private bool _heartbeatState = false;

    private class ToggleButtonState
    {
        public Button Button = null!;
        public Border Indicator = null!;
        public int Index;
    }

    public MockIoView()
    {
        InitializeComponent();
        _driver = null!;
    }

    public MockIoView(MockIoDriver driver)
    {
        InitializeComponent();
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));

        UpdateHeaderInfo();
        BuildGroupedInputControls();
        BuildGroupedOutputControls();
        InitWizardEvents();

        _driver.IoStateUpdated += OnIoStateUpdated;
        DetachedFromVisualTree += (s, e) => _driver.IoStateUpdated -= OnIoStateUpdated;

        // 綁定快速控制
        var btnClearAll = this.FindControl<Button>("BtnClearAllIn");
        if (btnClearAll != null) btnClearAll.Click += (_, _) => SetAllInputs(false);

        var btnSetAll = this.FindControl<Button>("BtnSetAllIn");
        if (btnSetAll != null) btnSetAll.Click += (_, _) => SetAllInputs(true);

        var btnForce = this.FindControl<Button>("BtnForceUpdate");
        if (btnForce != null) btnForce.Click += (_, _) => _driver.PostTicket(new DriverTicket("FORCE_UPDATE"));
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void UpdateHeaderInfo()
    {
        var txtDev = this.FindControl<TextBlock>("TxtHeaderDevice");
        if (txtDev != null) txtDev.Text = $"{_driver.DeviceName} ({_driver.DriverType})";

        var txtBus = this.FindControl<TextBlock>("TxtHeaderBus");
        if (txtBus != null) txtBus.Text = $"Bus: {_driver.Device}";

        string outEnd = MockIoDriver.GetPortNameAtOffset(_driver.OutStart, Math.Max(0, _driver.OutPorts - 1));
        string inEnd = MockIoDriver.GetPortNameAtOffset(_driver.InStart, Math.Max(0, _driver.InPorts - 1));

        var txtSummaryOut = this.FindControl<TextBlock>("TxtSummaryOut");
        if (txtSummaryOut != null) txtSummaryOut.Text = $"DO: {_driver.OutStart} ~ {outEnd} ({_driver.OutPorts}點)";

        var txtSummaryIn = this.FindControl<TextBlock>("TxtSummaryIn");
        if (txtSummaryIn != null) txtSummaryIn.Text = $"DI: {_driver.InStart} ~ {inEnd} ({_driver.InPorts}點)";

        var txtInTotal = this.FindControl<TextBlock>("TxtInTotalBadge");
        if (txtInTotal != null) txtInTotal.Text = $"[{_driver.InPorts} 點]";

        var txtOutTotal = this.FindControl<TextBlock>("TxtOutTotalBadge");
        if (txtOutTotal != null) txtOutTotal.Text = $"[{_driver.OutPorts} 點]";

        var txtRate = this.FindControl<TextBlock>("TxtScanRate");
        if (txtRate != null) txtRate.Text = $"{_driver.IntervalMs}ms ({1000 / Math.Max(1, _driver.IntervalMs)}Hz)";
    }

    private void BuildGroupedInputControls()
    {
        var container = this.FindControl<StackPanel>("InputsContainer");
        if (container == null) return;

        container.Children.Clear();
        _inputButtons.Clear();

        int totalPorts = _driver.InPorts;
        int bytesCount = (totalPorts + 7) / 8;

        for (int b = 0; b < bytesCount; b++)
        {
            int startBit = b * 8;
            int endBit = Math.Min(totalPorts - 1, startBit + 7);
            string startPort = _driver.GetInputPortName(startBit);
            string endPort = _driver.GetInputPortName(endBit);

            var groupBorder = new Border
            {
                Background = Brush.Parse("#232734"),
                BorderBrush = Brush.Parse("#2F3547"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 6),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var groupLayout = new StackPanel { Spacing = 6 };
            var header = new TextBlock
            {
                Text = $"Byte {b}: {startPort} ~ {endPort} (Bit {startBit % 8}..{endBit % 8})",
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = Brush.Parse("#93C5FD")
            };
            groupLayout.Children.Add(header);

            var bitsGrid = new UniformGrid { Columns = 8 };

            for (int i = startBit; i <= endBit; i++)
            {
                int idx = i;
                string portName = _driver.GetInputPortName(idx);
                bool isChecked = _driver.SimulatedInputs.Length > idx && _driver.SimulatedInputs[idx];

                var bitCard = new Border
                {
                    Background = Brush.Parse(isChecked ? "#1E3A8A" : "#1A1D26"),
                    BorderBrush = Brush.Parse(isChecked ? "#3B82F6" : "#2D3344"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(2, 4),
                    Margin = new Thickness(2),
                    // Cursor Hand
                };

                var bitStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2 };
                var lblPort = new TextBlock
                {
                    Text = portName,
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brush.Parse("#F1F5F9"),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                var indicator = new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(5),
                    Background = Brush.Parse(isChecked ? "#60A5FA" : "#475569"),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                var lblBit = new TextBlock
                {
                    Text = $"b{idx % 8}",
                    FontSize = 9,
                    Foreground = Brush.Parse("#94A3B8"),
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                bitStack.Children.Add(lblPort);
                bitStack.Children.Add(indicator);
                bitStack.Children.Add(lblBit);
                bitCard.Child = bitStack;

                // 點擊卡片直接切換狀態
                bitCard.PointerPressed += (s, e) =>
                {
                    bool current = _driver.SimulatedInputs.Length > idx && _driver.SimulatedInputs[idx];
                    bool newVal = !current;
                    _driver.SetSimulatedInput(idx, newVal);
                    indicator.Background = Brush.Parse(newVal ? "#60A5FA" : "#475569");
                    bitCard.Background = Brush.Parse(newVal ? "#1E3A8A" : "#1A1D26");
                    bitCard.BorderBrush = Brush.Parse(newVal ? "#3B82F6" : "#2D3344");
                };

                bitsGrid.Children.Add(bitCard);
            }

            groupLayout.Children.Add(bitsGrid);
            groupBorder.Child = groupLayout;
            container.Children.Add(groupBorder);
        }
    }

    private void BuildGroupedOutputControls()
    {
        var container = this.FindControl<StackPanel>("OutputsContainer");
        if (container == null) return;

        container.Children.Clear();
        _outputLeds.Clear();

        int totalPorts = _driver.OutPorts;
        int bytesCount = (totalPorts + 7) / 8;

        for (int b = 0; b < bytesCount; b++)
        {
            int startBit = b * 8;
            int endBit = Math.Min(totalPorts - 1, startBit + 7);
            string startPort = _driver.GetOutputPortName(startBit);
            string endPort = _driver.GetOutputPortName(endBit);

            var groupBorder = new Border
            {
                Background = Brush.Parse("#232734"),
                BorderBrush = Brush.Parse("#2F3547"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 6),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var groupLayout = new StackPanel { Spacing = 6 };
            var header = new TextBlock
            {
                Text = $"Byte {b}: {startPort} ~ {endPort} (Bit {startBit % 8}..{endBit % 8})",
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = Brush.Parse("#34D399")
            };
            groupLayout.Children.Add(header);

            var bitsGrid = new UniformGrid { Columns = 8 };

            for (int i = startBit; i <= endBit; i++)
            {
                int idx = i;
                string portName = _driver.GetOutputPortName(idx);

                var bitCard = new Border
                {
                    Background = Brush.Parse("#1A1D26"),
                    BorderBrush = Brush.Parse("#2D3344"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(2, 4),
                    Margin = new Thickness(2)
                };

                var bitStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2 };
                var lblPort = new TextBlock
                {
                    Text = portName,
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brush.Parse("#F1F5F9"),
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var led = new Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = Brush.Parse("#334155"),
                    Stroke = Brush.Parse("#475569"),
                    StrokeThickness = 1,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var lblBit = new TextBlock
                {
                    Text = $"b{idx % 8}",
                    FontSize = 9,
                    Foreground = Brush.Parse("#94A3B8"),
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                bitStack.Children.Add(lblPort);
                bitStack.Children.Add(led);
                bitStack.Children.Add(lblBit);
                bitCard.Child = bitStack;

                _outputLeds.Add(led);
                bitsGrid.Children.Add(bitCard);
            }

            groupLayout.Children.Add(bitsGrid);
            groupBorder.Child = groupLayout;
            container.Children.Add(groupBorder);
        }
    }

    private void SetAllInputs(bool value)
    {
        for (int i = 0; i < _driver.InPorts; i++)
        {
            _driver.SetSimulatedInput(i, value);
        }
        BuildGroupedInputControls();
    }

    private void OnIoStateUpdated()
    {
        Dispatcher.UIThread.Post(UpdateVisuals);
    }

    private void UpdateVisuals()
    {
        // 閃爍心跳指示燈
        _heartbeatState = !_heartbeatState;
        var ledHb = this.FindControl<Ellipse>("LedHeartbeat");
        if (ledHb != null)
        {
            ledHb.Fill = Brush.Parse(_heartbeatState ? "#10B981" : "#065F46");
        }

        // 更新各個 Output 燈號
        for (int i = 0; i < _outputLeds.Count; i++)
        {
            if (i < _driver.ReadbackOutputs.Length)
            {
                bool isOn = _driver.ReadbackOutputs[i];
                _outputLeds[i].Fill = Brush.Parse(isOn ? "#10B981" : "#334155");
                _outputLeds[i].Stroke = Brush.Parse(isOn ? "#6EE7B7" : "#475569");
            }
        }
    }

    // ──────────────── Wizard 頁面互動事件 ────────────────
    private void InitWizardEvents()
    {
        var radSerial = this.FindControl<RadioButton>("RadSerial");
        var radTcp = this.FindControl<RadioButton>("RadTcp");
        var pnlSerial = this.FindControl<StackPanel>("PnlSerialConfig");
        var pnlTcp = this.FindControl<StackPanel>("PnlTcpConfig");

        if (radSerial != null && radTcp != null && pnlSerial != null && pnlTcp != null)
        {
            radSerial.IsCheckedChanged += (_, _) =>
            {
                pnlSerial.IsVisible = radSerial.IsChecked == true;
                pnlTcp.IsVisible = radTcp.IsChecked == true;
                RecalculateWizardParam();
            };
            radTcp.IsCheckedChanged += (_, _) =>
            {
                pnlSerial.IsVisible = radSerial.IsChecked == true;
                pnlTcp.IsVisible = radTcp.IsChecked == true;
                RecalculateWizardParam();
            };
        }

        var txtDev = this.FindControl<TextBox>("TxtWizDeviceName");
        var txtOutStart = this.FindControl<TextBox>("TxtWizOutStart");
        var cmbOutPorts = this.FindControl<ComboBox>("CmbWizOutPorts");
        var txtInStart = this.FindControl<TextBox>("TxtWizInStart");
        var cmbInPorts = this.FindControl<ComboBox>("CmbWizInPorts");
        var txtInterval = this.FindControl<TextBox>("TxtWizInterval");
        var cmbCom = this.FindControl<ComboBox>("CmbComPort");
        var txtIp = this.FindControl<TextBox>("TxtTcpIp");
        var txtPort = this.FindControl<TextBox>("TxtTcpPort");

        if (txtDev != null) txtDev.TextChanged += (_, _) => RecalculateWizardParam();
        if (txtOutStart != null) txtOutStart.TextChanged += (_, _) => RecalculateWizardParam();
        if (cmbOutPorts != null) cmbOutPorts.SelectionChanged += (_, _) => RecalculateWizardParam();
        if (txtInStart != null) txtInStart.TextChanged += (_, _) => RecalculateWizardParam();
        if (cmbInPorts != null) cmbInPorts.SelectionChanged += (_, _) => RecalculateWizardParam();
        if (txtInterval != null) txtInterval.TextChanged += (_, _) => RecalculateWizardParam();
        if (cmbCom != null) cmbCom.SelectionChanged += (_, _) => RecalculateWizardParam();
        if (txtIp != null) txtIp.TextChanged += (_, _) => RecalculateWizardParam();
        if (txtPort != null) txtPort.TextChanged += (_, _) => RecalculateWizardParam();

        var btnApply = this.FindControl<Button>("BtnApplyWiz");
        if (btnApply != null)
        {
            btnApply.Click += (_, _) =>
            {
                var txtParam = this.FindControl<TextBox>("TxtGeneratedParam");
                if (txtParam != null && !string.IsNullOrWhiteSpace(txtParam.Text))
                {
                    // 重新將參數套用回目前的 Driver
                    string paramStr = txtParam.Text.Trim();
                    string devName = "IO1";
                    int firstComma = paramStr.IndexOf(',');
                    if (firstComma > 0)
                    {
                        devName = paramStr.Substring(0, firstComma).Trim();
                        paramStr = paramStr.Substring(firstComma + 1).Trim();
                    }

                    // 取得現有引擎引用重新 Init
                    var enginesField = typeof(MockIoDriver).GetField("_engines", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (enginesField?.GetValue(_driver) is ARCOS.Core.Drivers.BasicEngines engines)
                    {
                        _driver.Init(devName, paramStr, engines);
                        UpdateHeaderInfo();
                        BuildGroupedInputControls();
                        BuildGroupedOutputControls();
                    }
                }
            };
        }

        RecalculateWizardParam();
    }

    private void RecalculateWizardParam()
    {
        var txtDev = this.FindControl<TextBox>("TxtWizDeviceName");
        var radSerial = this.FindControl<RadioButton>("RadSerial");
        var cmbCom = this.FindControl<ComboBox>("CmbComPort");
        var txtIp = this.FindControl<TextBox>("TxtTcpIp");
        var txtPort = this.FindControl<TextBox>("TxtTcpPort");
        var txtOutStart = this.FindControl<TextBox>("TxtWizOutStart");
        var cmbOutPorts = this.FindControl<ComboBox>("CmbWizOutPorts");
        var txtInStart = this.FindControl<TextBox>("TxtWizInStart");
        var cmbInPorts = this.FindControl<ComboBox>("CmbWizInPorts");
        var txtGenerated = this.FindControl<TextBox>("TxtGeneratedParam");

        string devName = txtDev?.Text?.Trim() ?? "IO1";
        if (string.IsNullOrEmpty(devName)) devName = "IO1";

        string deviceParam = "Device=com1";
        if (radSerial?.IsChecked == true)
        {
            string com = (cmbCom?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "com1";
            deviceParam = $"Device={com}";
        }
        else
        {
            string ip = txtIp?.Text?.Trim() ?? "192.168.0.1";
            string port = txtPort?.Text?.Trim() ?? "5000";
            deviceParam = $"Device={ip}:{port}";
        }

        string outStart = txtOutStart?.Text?.Trim() ?? "p0.0";
        int outPorts = 32;
        if (int.TryParse((cmbOutPorts?.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int op)) outPorts = op;

        string inStart = txtInStart?.Text?.Trim() ?? "p4.0";
        int inPorts = 32;
        if (int.TryParse((cmbInPorts?.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int ipVal)) inPorts = ipVal;

        // 計算區間徽章
        string outEnd = MockIoDriver.GetPortNameAtOffset(outStart, Math.Max(0, outPorts - 1));
        var txtWizOutBadge = this.FindControl<TextBlock>("TxtWizOutBadge");
        if (txtWizOutBadge != null) txtWizOutBadge.Text = $"👉 區間: {outStart} ~ {outEnd} ({outPorts} 點)";

        string inEnd = MockIoDriver.GetPortNameAtOffset(inStart, Math.Max(0, inPorts - 1));
        var txtWizInBadge = this.FindControl<TextBlock>("TxtWizInBadge");
        if (txtWizInBadge != null) txtWizInBadge.Text = $"👉 區間: {inStart} ~ {inEnd} ({inPorts} 點)";

        // 生成標準參數
        if (txtGenerated != null)
        {
            txtGenerated.Text = $"{devName},{deviceParam},OutStart={outStart},OutPorts={outPorts},InputStart={inStart},InPorts={inPorts}";
        }
    }
}
