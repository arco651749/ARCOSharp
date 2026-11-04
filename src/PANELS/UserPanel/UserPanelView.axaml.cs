using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ARCOS.Core.Drivers;

namespace ARCOS.Panels.UserPanel;

public partial class UserPanelView : UserControl
{
    private readonly UserPanelViewModel _vm;

    public UserPanelView()
    {
        _vm = new UserPanelViewModel();
        DataContext = _vm;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void Init(BasicEngines engines)
    {
        _vm.Init(engines);
    }

    public void RefreshFromEngines(BasicEngines engines)
    {
        _vm.RefreshFromEngines(engines);
    }

    private void OnSimulatePartFeedClick(object? sender, RoutedEventArgs e)
    {
        _vm.SimulatePartFeed();
    }

    private void OnTriggerInspectionClick(object? sender, RoutedEventArgs e)
    {
        _vm.TriggerInspection();
    }

    private void OnRejectPartClick(object? sender, RoutedEventArgs e)
    {
        _vm.RejectPart();
    }

    private void OnToggleVacuumClick(object? sender, RoutedEventArgs e)
    {
        _vm.ToggleVacuum();
    }

    private void OnResetStatsClick(object? sender, RoutedEventArgs e)
    {
        _vm.ResetStats();
    }

    private void OnIoToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string port)
        {
            _vm.ToggleIoBit(port);
        }
    }
}
