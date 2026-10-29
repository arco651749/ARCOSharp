using Avalonia.Controls;
using ARCOS.Core.Drivers;

namespace ARCOS.Driver.MockDevice;

public partial class MockDeviceView : UserControl
{
    private readonly MockDeviceDriver? _driver;

    public MockDeviceView()
    {
        InitializeComponent();
    }

    public MockDeviceView(MockDeviceDriver driver) : this()
    {
        _driver = driver;
        var btn = this.FindControl<Button>("BtnTestTicket");
        var txtCount = this.FindControl<TextBlock>("TxtCount");

        if (btn != null)
        {
            btn.Click += (_, _) =>
            {
                _driver?.PostTicket(new DriverTicket("TestSignal", "123"));
                if (txtCount != null && _driver != null)
                {
                    txtCount.Text = $"{_driver.ProcessedCount} tickets";
                }
            };
        }
    }
}
