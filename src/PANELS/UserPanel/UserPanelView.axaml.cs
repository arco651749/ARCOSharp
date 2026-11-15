using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        if (sender is ToggleButton tb && tb.IsChecked.HasValue)
        {
            _vm.SetVacuum(tb.IsChecked.Value);
        }
        else
        {
            _vm.ToggleVacuum();
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        _vm.ConfirmOperatorPrompt();
    }

    private void OnApplyVarsClick(object? sender, RoutedEventArgs e)
    {
        _vm.ApplyVariablesToEngine();
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

    private void OnFilterElementsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string roleFilter)
        {
            _vm.SetRoleFilter(roleFilter);
        }
    }

    private void OnRefreshElementsClick(object? sender, RoutedEventArgs e)
    {
        _vm.RefreshPanelElements();
    }
}
