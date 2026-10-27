using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using MVCON.Core.Guidance;

namespace MVCON.Module.IO.Guidance;

/// <summary>
/// 雙模獨立測試器進入點 (GuidanceRunner)：
/// 1. 若無傳入參數：啟動 GuidanceLauncherWindow，列出 14 個標準指令供工程師單獨測試 UI
/// 2. 若傳入指令 (如 SetPort 或 --cmd IO@WaitPortOn)：直接啟動該指令之專屬導引視窗
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        string? targetCommand = null;
        string inputParam = "";

        // 解析 CLI 參數
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--cmd" || args[i] == "-c") && i + 1 < args.Length)
            {
                targetCommand = args[i + 1];
            }
            else if ((args[i] == "--param" || args[i] == "-p") && i + 1 < args.Length)
            {
                inputParam = args[i + 1];
            }
            else if (!args[i].StartsWith('-') && targetCommand == null)
            {
                targetCommand = args[i];
            }
        }

        // 若帶入的指令不含 "IO@"，自動補上前綴
        if (!string.IsNullOrEmpty(targetCommand))
        {
            if (!targetCommand.Contains('@'))
            {
                targetCommand = $"IO@{targetCommand}";
            }
        }

        StandaloneApp.TargetCommandKey = targetCommand;
        StandaloneApp.InitialParam = inputParam;

        try
        {
            AppBuilder.Configure<StandaloneApp>()
                .UsePlatformDetect()
                .LogToTrace()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText("io_guidance_err.txt", ex.ToString());
        }
    }
}

public class StandaloneApp : Application
{
    public static string? TargetCommandKey { get; set; }
    public static string InitialParam { get; set; } = "";

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mockPorts = new[]
            {
                "Part In Feed Detector",
                "Reject Cylinder",
                "Vacuum Sensor",
                "Cylinder_Extend",
                "P0.0", "P0.1", "P1.0", "P2.0"
            };

            if (!string.IsNullOrEmpty(TargetCommandKey))
            {
                // 指定指令直開模式
                desktop.MainWindow = new IoGuidanceDialog(TargetCommandKey, InitialParam, mockPorts);
            }
            else
            {
                // 無參數雙擊：啟動 GuidanceLauncherWindow 總覽測試器
                desktop.MainWindow = new GuidanceLauncherWindow(mockPorts);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>
/// 輕量單獨測試器介面 (GuidanceRunner 主視窗)
/// </summary>
public class GuidanceLauncherWindow : Window
{
    private readonly ComboBox _cmbCommand;
    private readonly TextBox _txtParam;
    private readonly TextBlock _txtResult;

    public GuidanceLauncherWindow(string[] mockPorts)
    {
        Title = "⚙️ MVCON IO 參數導引測試器 (Guidance Runner)";
        Width = 620;
        Height = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#1E1E2E"));

        var commands = new[]
        {
            "SetPort", "ClrPort", "Flash(OnOff)", "Flash(OffOn)",
            "WaitPortOn", "WaitPortOff", "CheckLogicOn", "CheckLogicOff",
            "SetPortName", "AddVirtualIO", "AddIODriver", "InitIOPort",
            "ForceUpdate", "Shift"
        };

        var root = new StackPanel { Margin = new Thickness(24), Spacing = 14 };

        var header = new TextBlock
        {
            Text = "🎯 MVCON IO 指令參數導引視窗獨立測試器",
            FontSize = 17,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#89B4FA"))
        };
        root.Children.Add(header);

        var desc = new TextBlock
        {
            Text = "供工程師單獨測試各指令之導引視窗與 UI 互動 (支援載入自訂修改之 .Guidance.dll)",
            Foreground = new SolidColorBrush(Color.Parse("#A6ADC8")),
            FontSize = 12
        };
        root.Children.Add(desc);

        // Command selector
        root.Children.Add(new TextBlock { Text = "選擇測試目標指令 (Target Command):", FontWeight = FontWeight.SemiBold, Foreground = Brushes.White });
        _cmbCommand = new ComboBox
        {
            ItemsSource = commands,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        root.Children.Add(_cmbCommand);

        // Parameter input
        root.Children.Add(new TextBlock { Text = "初始傳入參數 (ParameterIn):", FontWeight = FontWeight.SemiBold, Foreground = Brushes.White });
        _txtParam = new TextBox
        {
            Text = "P0.0",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        root.Children.Add(_txtParam);

        // Result display
        _txtResult = new TextBlock
        {
            Text = "💡 提示：可點擊下方按鈕分別測試「UI 前面板導引 Method」或「C# 業務邏輯 Execute Method」",
            Foreground = new SolidColorBrush(Color.Parse("#A6E3A1")),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        var mockEngines = new MVCON.Core.Drivers.BasicEngines();

        var guidances = new System.Collections.Generic.Dictionary<string, IParameterGuidance>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["SetPort"] = new IoSetPortGuidance(),
            ["ClrPort"] = new IoClrPortGuidance(),
            ["WaitPortOn"] = new IoWaitPortOnGuidance(),
            ["WaitPortOff"] = new IoWaitPortOffGuidance(),
            ["Flash(OnOff)"] = new IoFlashOnOffGuidance(),
            ["Flash(OffOn)"] = new IoFlashOffOnGuidance(),
            ["CheckLogicOn"] = new IoCheckLogicOnGuidance(),
            ["CheckLogicOff"] = new IoCheckLogicOffGuidance(),
            ["SetPortName"] = new IoSetPortNameGuidance(),
            ["AddVirtualIO"] = new IoAddVirtualIoGuidance(),
            ["AddIODriver"] = new IoAddIoDriverGuidance(),
            ["InitIOPort"] = new IoInitIoPortGuidance(),
            ["ForceUpdate"] = new IoForceUpdateGuidance(),
            ["Shift"] = new IoShiftGuidance()
        };

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

        var btnLaunch = new Button
        {
            Content = "🖼️ 開啟 UI 面板 (Front Panel Method)",
            Width = 270,
            Height = 44,
            Background = new SolidColorBrush(Color.Parse("#313244")),
            Foreground = new SolidColorBrush(Color.Parse("#CBA6F7")),
            FontWeight = FontWeight.Bold
        };
        btnLaunch.Click += async (_, _) =>
        {
            string cmd = _cmbCommand.SelectedItem?.ToString() ?? "SetPort";
            if (guidances.TryGetValue(cmd, out var g))
            {
                var result = await g.ShowGuidanceDialogAsync(_txtParam.Text ?? "", mockEngines, this);
                if (result != null)
                {
                    _txtResult.Text = $"✅ [UI Method 回傳成功]\nParameterOut: {result}";
                    _txtParam.Text = result;
                }
                else
                {
                    _txtResult.Text = "ℹ️ 使用者取消或關閉導引視窗";
                }
            }
        };

        var btnExecute = new Button
        {
            Content = "⚡ 執行 C# 邏輯 (Execute Method)",
            Width = 270,
            Height = 44,
            Background = new SolidColorBrush(Color.Parse("#1E3A5F")),
            Foreground = new SolidColorBrush(Color.Parse("#38BDF8")),
            FontWeight = FontWeight.Bold
        };
        btnExecute.Click += (_, _) =>
        {
            string cmd = _cmbCommand.SelectedItem?.ToString() ?? "SetPort";
            if (guidances.TryGetValue(cmd, out var g))
            {
                bool ok = g.Execute(mockEngines, _txtParam.Text ?? "");
                string bitState = mockEngines.Io.GetPort("P0.0") ? "🟢 ON (True)" : "⚪ OFF (False)";
                _txtResult.Text = $"⚡ [C# Execute() Method 呼叫完成]\n目標指令: {g.CommandKey} | 傳入參數: '{_txtParam.Text}'\n底層引擎執行: {(ok ? "成功 (Success)" : "失敗 (Fail)")} | P0.0 即時電位狀態: {bitState}";
            }
        };

        btnPanel.Children.Add(btnLaunch);
        btnPanel.Children.Add(btnExecute);

        root.Children.Add(btnPanel);
        root.Children.Add(_txtResult);

        Content = root;
    }
}
