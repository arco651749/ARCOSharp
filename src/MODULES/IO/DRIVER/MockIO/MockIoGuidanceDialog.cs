using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ARCOS.Core.Drivers;

namespace ARCOS.Driver.MockIO;

/// <summary>
/// MockIO 專屬驅動參數導引配置對話框 (Driver Parameter Wizard Window)
/// 當使用者於排程器配置 IO@AddIODriver 並選取 MockIO 時彈出，
/// 提供 RS-232/TCP、點位區間與掃描頻率之全圖形化視覺配置！
/// </summary>
public class MockIoGuidanceDialog : Window
{
    private readonly MockIoView _view;
    private string? _result;

    public MockIoGuidanceDialog(MockIoDriver driver, string currentParam)
    {
        Title = "⚙️ MockIO 驅動參數導引配置精靈 (Driver Wizard)";
        Width = 720;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#181A20"));

        _view = new MockIoView(driver);

        // 切換至 Tab 2: ⚙️ 參數導引與映射 (Driver Wizard)
        var tabControl = (_view.Content as Grid)?.Children.OfType<TabControl>().FirstOrDefault();
        if (tabControl != null)
        {
            tabControl.SelectedIndex = 1;
        }

        var rootGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto")
        };
        rootGrid.Children.Add(_view);

        var bottomBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#222530")),
            Padding = new Thickness(12, 10),
            BorderBrush = new SolidColorBrush(Color.Parse("#2D3242")),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        Grid.SetRow(bottomBar, 1);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 12
        };

        var btnCancel = new Button
        {
            Content = "取消 (Cancel)",
            Padding = new Thickness(16, 7),
            Background = new SolidColorBrush(Color.Parse("#334155")),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(4),
            FontSize = 12
        };
        btnCancel.Click += (_, _) => Close(null);

        var btnOk = new Button
        {
            Content = "✔ 確認套用參數 (Apply to Command)",
            Padding = new Thickness(20, 7),
            Background = new SolidColorBrush(Color.Parse("#10B981")),
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            CornerRadius = new CornerRadius(4),
            FontSize = 12
        };
        btnOk.Click += (_, _) =>
        {
            var txtGenerated = _view.FindControl<TextBox>("TxtGeneratedParam");
            _result = txtGenerated?.Text?.Trim();
            Close(_result);
        };

        btnPanel.Children.Add(btnCancel);
        btnPanel.Children.Add(btnOk);
        bottomBar.Child = btnPanel;
        rootGrid.Children.Add(bottomBar);

        Content = rootGrid;
    }
}
