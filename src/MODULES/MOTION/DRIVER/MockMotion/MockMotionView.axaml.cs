using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using MVCON.Core.Drivers;

namespace MVCON.Driver.MockMotion;

public partial class MockMotionView : UserControl
{
    private readonly MockMotionDriver _driver;

    public MockMotionView()
    {
        InitializeComponent();
        _driver = null!;
    }

    public MockMotionView(MockMotionDriver driver)
    {
        InitializeComponent();
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));

        var txtInfo = this.FindControl<TextBlock>("TxtDeviceInfo");
        if (txtInfo != null)
        {
            txtInfo.Text = $"{_driver.DeviceName} ({_driver.DriverType})";
        }

        BindEvents();
        UpdateVisuals();

        _driver.MotionStateUpdated += OnMotionStateUpdated;
        DetachedFromVisualTree += (s, e) => _driver.MotionStateUpdated -= OnMotionStateUpdated;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void BindEvents()
    {
        var btnHome = this.FindControl<Button>("BtnHome");
        if (btnHome != null) btnHome.Click += (s, e) => _driver.PostTicket(new DriverTicket("Home"));

        var btnStop = this.FindControl<Button>("BtnStop");
        if (btnStop != null) btnStop.Click += (s, e) => _driver.PostTicket(new DriverTicket("Stop"));

        var btnXPlus = this.FindControl<Button>("BtnXPlus");
        if (btnXPlus != null) btnXPlus.Click += (s, e) => DoJog("X", GetJogStep());

        var btnXMinus = this.FindControl<Button>("BtnXMinus");
        if (btnXMinus != null) btnXMinus.Click += (s, e) => DoJog("X", -GetJogStep());

        var btnYPlus = this.FindControl<Button>("BtnYPlus");
        if (btnYPlus != null) btnYPlus.Click += (s, e) => DoJog("Y", GetJogStep());

        var btnYMinus = this.FindControl<Button>("BtnYMinus");
        if (btnYMinus != null) btnYMinus.Click += (s, e) => DoJog("Y", -GetJogStep());

        var btnZPlus = this.FindControl<Button>("BtnZPlus");
        if (btnZPlus != null) btnZPlus.Click += (s, e) => DoJog("Z", GetJogStep());

        var btnZMinus = this.FindControl<Button>("BtnZMinus");
        if (btnZMinus != null) btnZMinus.Click += (s, e) => DoJog("Z", -GetJogStep());
    }

    private double GetJogStep()
    {
        var txtStep = this.FindControl<TextBox>("TxtJogStep");
        if (txtStep != null && double.TryParse(txtStep.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double step))
        {
            return step;
        }
        return 10.0;
    }

    private void DoJog(string axis, double delta)
    {
        double targetVal = axis switch
        {
            "X" => _driver.CurrentPos.X + delta,
            "Y" => _driver.CurrentPos.Y + delta,
            "Z" => _driver.CurrentPos.Z + delta,
            _ => 0
        };
        _driver.PostTicket(new DriverTicket("Go", $"{axis}={targetVal.ToString(CultureInfo.InvariantCulture)}"));
    }

    private void OnMotionStateUpdated()
    {
        Dispatcher.UIThread.Post(UpdateVisuals);
    }

    private void UpdateVisuals()
    {
        var txtX = this.FindControl<TextBlock>("TxtX");
        if (txtX != null) txtX.Text = _driver.CurrentPos.X.ToString("F3", CultureInfo.InvariantCulture);

        var txtY = this.FindControl<TextBlock>("TxtY");
        if (txtY != null) txtY.Text = _driver.CurrentPos.Y.ToString("F3", CultureInfo.InvariantCulture);

        var txtZ = this.FindControl<TextBlock>("TxtZ");
        if (txtZ != null) txtZ.Text = _driver.CurrentPos.Z.ToString("F3", CultureInfo.InvariantCulture);

        var txtU = this.FindControl<TextBlock>("TxtU");
        if (txtU != null) txtU.Text = _driver.CurrentPos.U.ToString("F3", CultureInfo.InvariantCulture);

        var txtV = this.FindControl<TextBlock>("TxtV");
        if (txtV != null) txtV.Text = _driver.CurrentPos.V.ToString("F3", CultureInfo.InvariantCulture);

        var txtW = this.FindControl<TextBlock>("TxtW");
        if (txtW != null) txtW.Text = _driver.CurrentPos.W.ToString("F3", CultureInfo.InvariantCulture);

        var ledHomed = this.FindControl<Ellipse>("LedHomed");
        if (ledHomed != null) ledHomed.Fill = _driver.IsHomed ? Brushes.LimeGreen : Brushes.Gray;

        var ledBusy = this.FindControl<Ellipse>("LedBusy");
        if (ledBusy != null) ledBusy.Fill = _driver.IsBusy ? Brushes.Gold : Brushes.Gray;

        var ledDone = this.FindControl<Ellipse>("LedDone");
        if (ledDone != null) ledDone.Fill = (!_driver.IsBusy && _driver.IsHomed) ? Brushes.LimeGreen : Brushes.Gray;
    }
}
