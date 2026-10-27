using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace MVCON.Module.IO.Guidance.Views;

public partial class IoFrontPanelView : UserControl
{
    public IoFrontPanelView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnBitToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string port)
        {
            if (DataContext is IoFrontPanelViewModel vm)
            {
                vm.ToggleBit(port);
            }
        }
    }

    private void OnRefreshAllClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is IoFrontPanelViewModel vm && vm.AttachedEngines != null)
        {
            vm.RefreshFromEngines(vm.AttachedEngines);
        }
    }
}
