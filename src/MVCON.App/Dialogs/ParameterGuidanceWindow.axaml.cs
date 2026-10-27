using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MVCON.Core.Drivers;

namespace MVCON.App.Dialogs;

public partial class ParameterGuidanceWindow : Window
{
    public string CommandKey { get; }
    public string InitialParameter { get; }
    public string? ResultParameter { get; private set; }
    public bool IsIoCommand { get; }

    public ParameterGuidanceWindow() : this("System@Log Message", "", null)
    {
    }

    public ParameterGuidanceWindow(string commandKey, string currentParam) : this(commandKey, currentParam, null)
    {
    }

    public ParameterGuidanceWindow(string commandKey, string currentParam, BasicEngines? engines)
    {
        CommandKey = string.IsNullOrWhiteSpace(commandKey) ? "System@Nop" : commandKey.Trim();
        InitialParameter = currentParam ?? "";
        IsIoCommand = CommandKey.StartsWith("IO@", StringComparison.OrdinalIgnoreCase);

        InitializeComponent();

        Title = $"{CommandKey}__Parameter.vi";

        var txtSubtitle = this.FindControl<TextBlock>("TxtWindowSubtitle");
        if (txtSubtitle != null) txtSubtitle.Text = $"{CommandKey}__Parameter.vi";

        var lblSig = this.FindControl<TextBlock>("LblCommandSignature");
        if (lblSig != null)
        {
            lblSig.Text = FormatCommandSignature(CommandKey);
        }

        var txtIn = this.FindControl<TextBox>("TxtParameterIn");
        if (txtIn != null) txtIn.Text = InitialParameter;

        var txtOut = this.FindControl<TextBox>("TxtParameterOut");
        if (txtOut != null) txtOut.Text = InitialParameter;

        var pnlIo = this.FindControl<Grid>("PnlIoGuidance");
        var pnlGeneric = this.FindControl<Grid>("PnlGenericGuidance");

        if (IsIoCommand)
        {
            if (pnlIo != null) pnlIo.IsVisible = true;
            if (pnlGeneric != null) pnlGeneric.IsVisible = false;

            SetupIoGuidance(engines);
        }
        else
        {
            if (pnlIo != null) pnlIo.IsVisible = false;
            if (pnlGeneric != null) pnlGeneric.IsVisible = true;

            var txtEdit = this.FindControl<TextBox>("TxtParameterEdit");
            if (txtEdit != null)
            {
                txtEdit.Text = InitialParameter;
                txtEdit.KeyUp += (s, e) =>
                {
                    if (txtOut != null) txtOut.Text = txtEdit.Text;
                    if (e.Key == Key.Enter)
                    {
                        OnConfirmClick(s, e);
                    }
                };
                Dispatcher.UIThread.Post(() =>
                {
                    txtEdit.Focus();
                    txtEdit.SelectAll();
                });
            }
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void SetupIoGuidance(BasicEngines? engines)
    {
        var cboIoName = this.FindControl<ComboBox>("CboIoName");
        var cboIoDelay = this.FindControl<ComboBox>("CboIoDelay");
        var cboIoForce = this.FindControl<ComboBox>("CboIoForceUpdate");
        var txtOut = this.FindControl<TextBox>("TxtParameterOut");

        // 1. 動態從系統 IoEngine 獲取所有已登記之別名與實體點位
        var portList = new List<string>();
        if (engines?.Io != null)
        {
            portList.AddRange(engines.Io.GetAllPortNames());
        }

        // 若系統尚未初始化或為兜底模式，自動填充前 64 點實體點 (P0.0 ~ P7.7)
        if (portList.Count == 0)
        {
            for (int p = 0; p < 8; p++)
            {
                for (int b = 0; b < 8; b++)
                {
                    portList.Add($"P{p}.{b}");
                }
            }
        }

        // 2. 解析既有參數 (例如: P0.0,0.2 或 Cylinder_Extend,0.5,ForceUpdate)
        string initialPort = "";
        string initialDelay = "";
        string initialForce = "";

        if (!string.IsNullOrWhiteSpace(InitialParameter))
        {
            var parts = InitialParameter.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length > 0) initialPort = parts[0];
            if (parts.Length > 1) initialDelay = parts[1];
            if (parts.Length > 2) initialForce = parts[2];
        }

        // 確保初始點位若不在清單中則置頂補入
        if (!string.IsNullOrEmpty(initialPort) && !portList.Contains(initialPort, StringComparer.OrdinalIgnoreCase))
        {
            portList.Insert(0, initialPort);
        }

        if (cboIoName != null)
        {
            cboIoName.ItemsSource = portList;
            if (!string.IsNullOrEmpty(initialPort))
            {
                cboIoName.SelectedItem = portList.FirstOrDefault(p => p.Equals(initialPort, StringComparison.OrdinalIgnoreCase));
            }
            else if (portList.Count > 0)
            {
                cboIoName.SelectedIndex = 0;
            }
        }

        // 3. 配置延遲選項 (支援常用秒數與輸入)
        var delayPresets = new List<string> { "0.0", "0.05", "0.1", "0.2", "0.5", "1.0", "2.0" };
        if (!string.IsNullOrEmpty(initialDelay) && !delayPresets.Contains(initialDelay))
        {
            delayPresets.Insert(0, initialDelay);
        }

        if (cboIoDelay != null)
        {
            cboIoDelay.ItemsSource = delayPresets;
            if (!string.IsNullOrEmpty(initialDelay))
            {
                cboIoDelay.SelectedItem = delayPresets.FirstOrDefault(d => d.Equals(initialDelay, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                cboIoDelay.SelectedIndex = 0;
            }
        }

        // 4. 配置 ForceUpdate 強制刷新選項
        var forcePresets = new List<string> { "(無)", "ForceUpdate" };
        if (cboIoForce != null)
        {
            cboIoForce.ItemsSource = forcePresets;
            if (!string.IsNullOrEmpty(initialForce) && initialForce.Contains("Force", StringComparison.OrdinalIgnoreCase))
            {
                cboIoForce.SelectedIndex = 1;
            }
            else
            {
                cboIoForce.SelectedIndex = 0;
            }
        }

        // 5. 綁定變更事件，即時在 ParameterOut 組合輸出
        void UpdatePreview()
        {
            string port = cboIoName?.SelectedItem?.ToString() ?? "";
            string delay = cboIoDelay?.SelectedItem?.ToString() ?? "";
            string force = cboIoForce?.SelectedItem?.ToString() ?? "";
            if (force == "(無)") force = "";

            if (string.IsNullOrEmpty(port))
            {
                if (txtOut != null) txtOut.Text = "";
                return;
            }

            var res = new List<string> { port };
            if (!string.IsNullOrEmpty(delay) && delay != "0.0" && delay != "0")
            {
                res.Add(delay);
                if (!string.IsNullOrEmpty(force))
                {
                    res.Add(force);
                }
            }
            else if (!string.IsNullOrEmpty(force))
            {
                res.Add("0.0");
                res.Add(force);
            }

            if (txtOut != null)
            {
                txtOut.Text = string.Join(",", res);
            }
        }

        if (cboIoName != null) cboIoName.SelectionChanged += (_, _) => UpdatePreview();
        if (cboIoDelay != null) cboIoDelay.SelectionChanged += (_, _) => UpdatePreview();
        if (cboIoForce != null) cboIoForce.SelectionChanged += (_, _) => UpdatePreview();

        UpdatePreview();
    }

    private static string FormatCommandSignature(string cmd)
    {
        if (cmd.StartsWith("IO@", StringComparison.OrdinalIgnoreCase))
            return "IO@SetPort/ClrPort $IOName(,$Delay,ForceUpdate)";
        if (cmd.Equals("System@Log Message", StringComparison.OrdinalIgnoreCase))
            return "System@Log Message $Message";
        if (cmd.Equals("System@Delay", StringComparison.OrdinalIgnoreCase))
            return "System@Delay $Seconds";
        if (cmd.Equals("System@Record", StringComparison.OrdinalIgnoreCase))
            return "System@Record $RecordName,$Message";
        if (cmd.Equals("System@LogRecordCycleTime", StringComparison.OrdinalIgnoreCase))
            return "System@LogRecordCycleTime $RecordName,$Unit";
        if (cmd.Equals("System@#Comment----->>", StringComparison.OrdinalIgnoreCase))
            return "System@#Comment----->> $CommentText";
        if (cmd.Equals("Motion@GoPoint", StringComparison.OrdinalIgnoreCase))
            return "Motion@GoPoint $MotorName,$Speed,$PointName";
        if (cmd.Equals("Motion@WaitMotor", StringComparison.OrdinalIgnoreCase))
            return "Motion@WaitMotor $MotorName,TimeOut=$Sec";
        if (cmd.Equals("Capture@Snap", StringComparison.OrdinalIgnoreCase))
            return "Capture@Snap $CamName";
        if (cmd.Equals("Capture@PatternMatchOne", StringComparison.OrdinalIgnoreCase))
            return "Capture@PatternMatchOne $CamName,$ROIName,$TemplateFile,$MinScore";
        if (cmd.Equals("Variable@Set", StringComparison.OrdinalIgnoreCase))
            return "Variable@Set $VarName,$Value";
        if (cmd.Equals("Variable@Inc", StringComparison.OrdinalIgnoreCase))
            return "Variable@Inc $VarName";
        if (cmd.Equals("Point&Path@Calculate", StringComparison.OrdinalIgnoreCase))
            return "Point&Path@Calculate $Expression";
        if (cmd.Equals("Thread@Label", StringComparison.OrdinalIgnoreCase))
            return "Thread@Label $LabelName";
        if (cmd.Equals("Thread@Jump", StringComparison.OrdinalIgnoreCase))
            return "Thread@Jump $TargetLabel,$Condition";

        return $"{cmd} $Parameters";
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        var txtOut = this.FindControl<TextBox>("TxtParameterOut");
        var txtEdit = this.FindControl<TextBox>("TxtParameterEdit");

        if (IsIoCommand)
        {
            ResultParameter = txtOut?.Text?.Trim() ?? "";
        }
        else
        {
            ResultParameter = txtEdit?.Text?.Trim() ?? txtOut?.Text?.Trim() ?? "";
        }
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        ResultParameter = null;
        Close(false);
    }

    public static async Task<string?> ShowGuidanceAsync(Window owner, string commandKey, string currentParam, BasicEngines? engines = null)
    {
        var win = new ParameterGuidanceWindow(commandKey, currentParam, engines);
        bool? ok = await win.ShowDialog<bool?>(owner);
        return (ok == true) ? win.ResultParameter : null;
    }
}
