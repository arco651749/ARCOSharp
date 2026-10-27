using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using MVCON.Core.Drivers;

namespace MVCON.Module.IO.Guidance;

public partial class IoGuidanceDialog : Window
{
    private readonly string _commandKey = "IO@SetPort";
    private readonly List<string> _portList = new();
    private readonly BasicEngines? _engines;

    // 頂部與底部控制項
    private TextBlock? _txtCommandTitle;
    private TextBlock? _txtSignature;
    private TextBlock? _txtParamIn;
    private TextBox? _txtParamOut;
    private Button? _btnApplyBig;
    private Button? _btnOk;
    private Button? _btnCancel;

    // 各面板容器
    private StackPanel? _pnlSetClrPort;
    private StackPanel? _pnlFlash;
    private StackPanel? _pnlWaitPort;
    private StackPanel? _pnlCheckLogic;
    private StackPanel? _pnlSetPortName;
    private StackPanel? _pnlAddVirtualIO;
    private StackPanel? _pnlAddIODriver;
    private StackPanel? _pnlDeviceOnly;
    private StackPanel? _pnlShift;

    // 1. SetPort / ClrPort 控制項
    private ComboBox? _cmbSetPortName;
    private ComboBox? _cmbSetPortDelay;
    private ComboBox? _cmbSetPortForce;

    // 2. Flash 控制項
    private ComboBox? _cmbFlashPort;
    private ComboBox? _cmbFlashT1;
    private ComboBox? _cmbFlashT2;
    private TextBlock? _lblFlashT1;
    private TextBlock? _lblFlashT2;

    // 3. WaitPort 控制項
    private ComboBox? _cmbWaitPort;
    private ComboBox? _cmbWaitTimeout;
    private ComboBox? _cmbWaitLabel;
    private TextBox? _txtWaitMessage;

    // 4. CheckLogic 控制項
    private TextBox? _txtLogicExpr;
    private ComboBox? _cmbLogicTimeout;
    private ComboBox? _cmbLogicLabel;
    private TextBox? _txtLogicMessage;

    // 5. SetPortName 控制項
    private ComboBox? _cmbRenamePort;
    private TextBox? _txtNewPortName;

    // 6. AddVirtualIO 控制項
    private TextBox? _txtVirtualPortName;
    private ComboBox? _cmbVirtualState;

    // 7. AddIODriver 控制項
    private TextBox? _txtDriverDeviceName;
    private ComboBox? _cmbDriverType;
    private TextBox? _txtDriverParams;

    // 8. DeviceOnly 控制項
    private ComboBox? _cmbDeviceName;

    // 9. Shift 控制項
    private TextBox? _txtShiftPorts;
    private ComboBox? _cmbShiftAmount;

    public IoGuidanceDialog()
    {
        InitializeComponent();
        BindControls();
    }

    public IoGuidanceDialog(string commandKey, string currentParam, IEnumerable<string> livePorts, BasicEngines? engines = null) : this()
    {
        _commandKey = string.IsNullOrWhiteSpace(commandKey) ? "IO@SetPort" : commandKey.Trim();
        _engines = engines;
        _portList.AddRange(livePorts);

        if (_portList.Count == 0)
        {
            for (int p = 0; p < 8; p++)
                for (int b = 0; b < 8; b++)
                    _portList.Add($"P{p}.{b}");
        }

        Title = $"{_commandKey}__Parameter.vi";
        if (_txtCommandTitle != null) _txtCommandTitle.Text = $"⚙️ PARAMETER GUIDANCE : {_commandKey}";
        if (_txtParamIn != null) _txtParamIn.Text = $"ParameterIn (原始輸入值): {currentParam}";

        ConfigureActivePanel();
        PopulateDefaultPresets();
        ParseInitialParam(currentParam);
        UpdatePreview();
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }

    private void BindControls()
    {
        _txtCommandTitle = this.FindControl<TextBlock>("TxtCommandTitle");
        _txtSignature = this.FindControl<TextBlock>("TxtSignature");
        _txtParamIn = this.FindControl<TextBlock>("TxtParamIn");
        _txtParamOut = this.FindControl<TextBox>("TxtParamOut");
        _btnApplyBig = this.FindControl<Button>("BtnApplyBig");
        _btnOk = this.FindControl<Button>("BtnOk");
        _btnCancel = this.FindControl<Button>("BtnCancel");

        _pnlSetClrPort = this.FindControl<StackPanel>("PnlSetClrPort");
        _pnlFlash = this.FindControl<StackPanel>("PnlFlash");
        _pnlWaitPort = this.FindControl<StackPanel>("PnlWaitPort");
        _pnlCheckLogic = this.FindControl<StackPanel>("PnlCheckLogic");
        _pnlSetPortName = this.FindControl<StackPanel>("PnlSetPortName");
        _pnlAddVirtualIO = this.FindControl<StackPanel>("PnlAddVirtualIO");
        _pnlAddIODriver = this.FindControl<StackPanel>("PnlAddIODriver");
        _pnlDeviceOnly = this.FindControl<StackPanel>("PnlDeviceOnly");
        _pnlShift = this.FindControl<StackPanel>("PnlShift");

        _cmbSetPortName = this.FindControl<ComboBox>("CmbSetPortName");
        _cmbSetPortDelay = this.FindControl<ComboBox>("CmbSetPortDelay");
        _cmbSetPortForce = this.FindControl<ComboBox>("CmbSetPortForce");

        _cmbFlashPort = this.FindControl<ComboBox>("CmbFlashPort");
        _cmbFlashT1 = this.FindControl<ComboBox>("CmbFlashT1");
        _cmbFlashT2 = this.FindControl<ComboBox>("CmbFlashT2");
        _lblFlashT1 = this.FindControl<TextBlock>("LblFlashT1");
        _lblFlashT2 = this.FindControl<TextBlock>("LblFlashT2");

        _cmbWaitPort = this.FindControl<ComboBox>("CmbWaitPort");
        _cmbWaitTimeout = this.FindControl<ComboBox>("CmbWaitTimeout");
        _cmbWaitLabel = this.FindControl<ComboBox>("CmbWaitLabel");
        _txtWaitMessage = this.FindControl<TextBox>("TxtWaitMessage");

        _txtLogicExpr = this.FindControl<TextBox>("TxtLogicExpr");
        _cmbLogicTimeout = this.FindControl<ComboBox>("CmbLogicTimeout");
        _cmbLogicLabel = this.FindControl<ComboBox>("CmbLogicLabel");
        _txtLogicMessage = this.FindControl<TextBox>("TxtLogicMessage");

        _cmbRenamePort = this.FindControl<ComboBox>("CmbRenamePort");
        _txtNewPortName = this.FindControl<TextBox>("TxtNewPortName");

        _txtVirtualPortName = this.FindControl<TextBox>("TxtVirtualPortName");
        _cmbVirtualState = this.FindControl<ComboBox>("CmbVirtualState");

        _txtDriverDeviceName = this.FindControl<TextBox>("TxtDriverDeviceName");
        _cmbDriverType = this.FindControl<ComboBox>("CmbDriverType");
        _txtDriverParams = this.FindControl<TextBox>("TxtDriverParams");

        _cmbDeviceName = this.FindControl<ComboBox>("CmbDeviceName");

        _txtShiftPorts = this.FindControl<TextBox>("TxtShiftPorts");
        _cmbShiftAmount = this.FindControl<ComboBox>("CmbShiftAmount");

        if (_btnCancel != null) _btnCancel.Click += (_, _) => Close(null);
        if (_btnOk != null) _btnOk.Click += (_, _) => Close(_txtParamOut?.Text);
        if (_btnApplyBig != null) _btnApplyBig.Click += (_, _) => Close(_txtParamOut?.Text);

        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter) Close(_txtParamOut?.Text);
            else if (e.Key == Key.Escape) Close(null);
        };

        // 綁定所有控制項變更即時觸發預覽
        Action ev = () => UpdatePreview();
        WireEvent(_cmbSetPortName, ev); WireEvent(_cmbSetPortDelay, ev); WireEvent(_cmbSetPortForce, ev);
        WireEvent(_cmbFlashPort, ev); WireEvent(_cmbFlashT1, ev); WireEvent(_cmbFlashT2, ev);
        WireEvent(_cmbWaitPort, ev); WireEvent(_cmbWaitTimeout, ev); WireEvent(_cmbWaitLabel, ev); WireEvent(_txtWaitMessage, ev);
        WireEvent(_txtLogicExpr, ev); WireEvent(_cmbLogicTimeout, ev); WireEvent(_cmbLogicLabel, ev); WireEvent(_txtLogicMessage, ev);
        WireEvent(_cmbRenamePort, ev); WireEvent(_txtNewPortName, ev);
        WireEvent(_txtVirtualPortName, ev); WireEvent(_cmbVirtualState, ev);
        WireEvent(_txtDriverDeviceName, ev); WireEvent(_cmbDriverType, ev); WireEvent(_txtDriverParams, ev);
        WireEvent(_cmbDeviceName, ev);
        WireEvent(_txtShiftPorts, ev); WireEvent(_cmbShiftAmount, ev);
    }

    private static void WireEvent(Control? c, Action act)
    {
        if (c is ComboBox cb) cb.SelectionChanged += (_, _) => act();
        else if (c is TextBox tb) tb.KeyUp += (_, _) => act();
    }

    private void ConfigureActivePanel()
    {
        HideAllPanels();
        string action = _commandKey.Contains('@') ? _commandKey.Split('@', 2)[1] : _commandKey;

        if (action.Equals("SetPort", StringComparison.OrdinalIgnoreCase) || action.Equals("ClrPort", StringComparison.OrdinalIgnoreCase))
        {
            ShowPanel(_pnlSetClrPort);
            SetSig($"IO@{action}::$port(,$second,ForceUpdate)");
        }
        else if (action.StartsWith("Flash", StringComparison.OrdinalIgnoreCase))
        {
            ShowPanel(_pnlFlash);
            bool isOnOff = action.Equals("Flash(OnOff)", StringComparison.OrdinalIgnoreCase);
            if (_lblFlashT1 != null) _lblFlashT1.Text = isOnOff ? "🔵 $onSecond (ON 秒數):" : "🔵 $offSecond (OFF 秒數):";
            if (_lblFlashT2 != null) _lblFlashT2.Text = isOnOff ? "🔵 $offSecond (OFF 秒數):" : "🔵 $onSecond (ON 秒數):";
            SetSig($"IO@{action}::$port,$firstSec,$secondSec");
        }
        else if (action.StartsWith("WaitPort", StringComparison.OrdinalIgnoreCase))
        {
            ShowPanel(_pnlWaitPort);
            SetSig($"IO@{action}::$port,$timeout,[[$label]]$message");
        }
        else if (action.StartsWith("CheckLogic", StringComparison.OrdinalIgnoreCase))
        {
            ShowPanel(_pnlCheckLogic);
            SetSig($"IO@{action}::$logic,$timeout,[[$label]]$message");
        }
        else if (action.Equals("SetPortName", StringComparison.OrdinalIgnoreCase))
        {
            ShowPanel(_pnlSetPortName);
            SetSig("IO@SetPortName::$port,$newname (Input帶?, Output不帶?)");
        }
        else if (action.Equals("AddVirtualIO", StringComparison.OrdinalIgnoreCase))
        {
            ShowPanel(_pnlAddVirtualIO);
            SetSig("IO@AddVirtualIO::$port,{ON|OFF}");
        }
        else if (action.Equals("AddIODriver", StringComparison.OrdinalIgnoreCase))
        {
            ShowPanel(_pnlAddIODriver);
            SetSig("IO@AddIODriver::$io,$driver,[parameters]");
        }
        else if (action.Equals("InitIOPort", StringComparison.OrdinalIgnoreCase) || action.Equals("ForceUpdate", StringComparison.OrdinalIgnoreCase))
        {
            ShowPanel(_pnlDeviceOnly);
            SetSig($"IO@{action}::$io");
        }
        else if (action.Equals("Shift", StringComparison.OrdinalIgnoreCase))
        {
            ShowPanel(_pnlShift);
            SetSig("IO@Shift::$io1,$io2,$io3...,Shift=$num");
        }
        else
        {
            ShowPanel(_pnlSetClrPort);
            SetSig($"IO@{action}::$port");
        }
    }

    private void SetSig(string sig)
    {
        if (_txtSignature != null) _txtSignature.Text = $"Signature: {sig}";
    }

    private void HideAllPanels()
    {
        if (_pnlSetClrPort != null) _pnlSetClrPort.IsVisible = false;
        if (_pnlFlash != null) _pnlFlash.IsVisible = false;
        if (_pnlWaitPort != null) _pnlWaitPort.IsVisible = false;
        if (_pnlCheckLogic != null) _pnlCheckLogic.IsVisible = false;
        if (_pnlSetPortName != null) _pnlSetPortName.IsVisible = false;
        if (_pnlAddVirtualIO != null) _pnlAddVirtualIO.IsVisible = false;
        if (_pnlAddIODriver != null) _pnlAddIODriver.IsVisible = false;
        if (_pnlDeviceOnly != null) _pnlDeviceOnly.IsVisible = false;
        if (_pnlShift != null) _pnlShift.IsVisible = false;
    }

    private static void ShowPanel(StackPanel? p)
    {
        if (p != null) p.IsVisible = true;
    }

    private void PopulateDefaultPresets()
    {
        var delays = new[] { "0.0", "0.05", "0.1", "0.2", "0.5", "1.0", "2.0" };
        var timeouts = new[] { "1.0", "2.0", "3.0", "5.0", "10.0" };
        var labels = new[] { "(無)", "[[Err1]]", "[[TimeoutErr]]", "[[RetryScan]]", "[[AbortLoop]]" };

        if (_cmbSetPortName != null) _cmbSetPortName.ItemsSource = _portList;
        if (_cmbSetPortDelay != null) { _cmbSetPortDelay.ItemsSource = delays; _cmbSetPortDelay.SelectedIndex = 0; }
        if (_cmbSetPortForce != null) { _cmbSetPortForce.ItemsSource = new[] { "(無)", "ForceUpdate" }; _cmbSetPortForce.SelectedIndex = 0; }

        if (_cmbFlashPort != null) _cmbFlashPort.ItemsSource = _portList;
        if (_cmbFlashT1 != null) { _cmbFlashT1.ItemsSource = delays.Where(d => d != "0.0").ToList(); _cmbFlashT1.SelectedIndex = 1; }
        if (_cmbFlashT2 != null) { _cmbFlashT2.ItemsSource = delays.Where(d => d != "0.0").ToList(); _cmbFlashT2.SelectedIndex = 2; }

        if (_cmbWaitPort != null) _cmbWaitPort.ItemsSource = _portList;
        if (_cmbWaitTimeout != null) { _cmbWaitTimeout.ItemsSource = timeouts; _cmbWaitTimeout.SelectedIndex = 2; }
        if (_cmbWaitLabel != null) { _cmbWaitLabel.ItemsSource = labels; _cmbWaitLabel.SelectedIndex = 0; }

        if (_cmbLogicTimeout != null) { _cmbLogicTimeout.ItemsSource = timeouts; _cmbLogicTimeout.SelectedIndex = 2; }
        if (_cmbLogicLabel != null) { _cmbLogicLabel.ItemsSource = labels; _cmbLogicLabel.SelectedIndex = 0; }

        var physicalOnly = _portList.Where(p => p.StartsWith("P", StringComparison.OrdinalIgnoreCase)).ToList();
        if (_cmbRenamePort != null) _cmbRenamePort.ItemsSource = physicalOnly.Count > 0 ? physicalOnly : _portList;

        if (_cmbVirtualState != null) { _cmbVirtualState.ItemsSource = new[] { "ON", "OFF" }; _cmbVirtualState.SelectedIndex = 0; }

        if (_cmbDriverType != null) { _cmbDriverType.ItemsSource = new[] { "MockIO", "AdvantechIO", "ADLINK8164" }; _cmbDriverType.SelectedIndex = 0; }

        var devNames = new[] { "IO1", "IO2", "StationIO", "MockIO" };
        if (_cmbDeviceName != null) { _cmbDeviceName.ItemsSource = devNames; _cmbDeviceName.SelectedIndex = 0; }

        if (_cmbShiftAmount != null) { _cmbShiftAmount.ItemsSource = new[] { "1", "-1", "2", "-2" }; _cmbShiftAmount.SelectedIndex = 0; }
    }

    private void ParseInitialParam(string currentParam)
    {
        if (string.IsNullOrWhiteSpace(currentParam)) return;
        var parts = currentParam.Split(',', StringSplitOptions.TrimEntries);
        string action = _commandKey.Contains('@') ? _commandKey.Split('@', 2)[1] : _commandKey;

        if (action.Equals("SetPort", StringComparison.OrdinalIgnoreCase) || action.Equals("ClrPort", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length > 0 && _cmbSetPortName != null) _cmbSetPortName.SelectedItem = parts[0];
            if (parts.Length > 1 && _cmbSetPortDelay != null) _cmbSetPortDelay.SelectedItem = parts[1];
            if (parts.Length > 2 && _cmbSetPortForce != null) _cmbSetPortForce.SelectedItem = parts[2];
        }
        else if (action.StartsWith("Flash", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length > 0 && _cmbFlashPort != null) _cmbFlashPort.SelectedItem = parts[0];
            if (parts.Length > 1 && _cmbFlashT1 != null) _cmbFlashT1.SelectedItem = parts[1];
            if (parts.Length > 2 && _cmbFlashT2 != null) _cmbFlashT2.SelectedItem = parts[2];
        }
        else if (action.StartsWith("WaitPort", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length > 0 && _cmbWaitPort != null) _cmbWaitPort.SelectedItem = parts[0];
            if (parts.Length > 1 && _cmbWaitTimeout != null) _cmbWaitTimeout.SelectedItem = parts[1];
            if (parts.Length > 2) ParseLabelAndMessage(parts[2], _cmbWaitLabel, _txtWaitMessage);
        }
        else if (action.StartsWith("CheckLogic", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length > 0 && _txtLogicExpr != null) _txtLogicExpr.Text = parts[0];
            if (parts.Length > 1 && _cmbLogicTimeout != null) _cmbLogicTimeout.SelectedItem = parts[1];
            if (parts.Length > 2) ParseLabelAndMessage(parts[2], _cmbLogicLabel, _txtLogicMessage);
        }
        else if (action.Equals("SetPortName", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length > 0 && _cmbRenamePort != null) _cmbRenamePort.SelectedItem = parts[0];
            if (parts.Length > 1 && _txtNewPortName != null) _txtNewPortName.Text = parts[1];
        }
        else if (action.Equals("AddVirtualIO", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length > 0 && _txtVirtualPortName != null) _txtVirtualPortName.Text = parts[0];
            if (parts.Length > 1 && _cmbVirtualState != null) _cmbVirtualState.SelectedItem = parts[1].ToUpperInvariant();
        }
        else if (action.Equals("AddIODriver", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length > 0 && _txtDriverDeviceName != null) _txtDriverDeviceName.Text = parts[0];
            if (parts.Length > 1 && _cmbDriverType != null) _cmbDriverType.SelectedItem = parts[1];
            if (parts.Length > 2 && _txtDriverParams != null) _txtDriverParams.Text = string.Join(",", parts.Skip(2));
        }
        else if (action.Equals("InitIOPort", StringComparison.OrdinalIgnoreCase) || action.Equals("ForceUpdate", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length > 0 && _cmbDeviceName != null) _cmbDeviceName.SelectedItem = parts[0];
        }
        else if (action.Equals("Shift", StringComparison.OrdinalIgnoreCase))
        {
            var pList = new List<string>();
            foreach (var p in parts)
            {
                if (p.StartsWith("Shift=", StringComparison.OrdinalIgnoreCase) && _cmbShiftAmount != null)
                {
                    _cmbShiftAmount.SelectedItem = p.Substring(6).Trim();
                }
                else
                {
                    pList.Add(p);
                }
            }
            if (_txtShiftPorts != null) _txtShiftPorts.Text = string.Join(",", pList);
        }
    }

    private static void ParseLabelAndMessage(string rawMsg, ComboBox? cbLabel, TextBox? txtMsg)
    {
        if (string.IsNullOrWhiteSpace(rawMsg)) return;
        if (rawMsg.StartsWith("[[") && rawMsg.Contains("]]"))
        {
            int closeIdx = rawMsg.IndexOf("]]", StringComparison.Ordinal);
            string labelTag = rawMsg.Substring(0, closeIdx + 2);
            string remainder = rawMsg.Substring(closeIdx + 2);

            if (cbLabel != null) cbLabel.SelectedItem = labelTag;
            if (txtMsg != null) txtMsg.Text = remainder;
        }
        else
        {
            if (txtMsg != null) txtMsg.Text = rawMsg;
        }
    }

    private void UpdatePreview()
    {
        if (_txtParamOut == null) return;
        string action = _commandKey.Contains('@') ? _commandKey.Split('@', 2)[1] : _commandKey;

        if (action.Equals("SetPort", StringComparison.OrdinalIgnoreCase) || action.Equals("ClrPort", StringComparison.OrdinalIgnoreCase))
        {
            string port = _cmbSetPortName?.SelectedItem?.ToString() ?? "P0.0";
            string delay = _cmbSetPortDelay?.SelectedItem?.ToString() ?? "0.0";
            string force = _cmbSetPortForce?.SelectedItem?.ToString() ?? "";
            if (force == "(無)") force = "";

            var segs = new List<string> { port };
            if (!string.IsNullOrEmpty(delay) && delay != "0.0" && delay != "0")
            {
                segs.Add(delay);
                if (!string.IsNullOrEmpty(force)) segs.Add(force);
            }
            else if (!string.IsNullOrEmpty(force))
            {
                segs.Add("0.0");
                segs.Add(force);
            }
            _txtParamOut.Text = string.Join(",", segs);
        }
        else if (action.StartsWith("Flash", StringComparison.OrdinalIgnoreCase))
        {
            string port = _cmbFlashPort?.SelectedItem?.ToString() ?? "P0.0";
            string t1 = _cmbFlashT1?.SelectedItem?.ToString() ?? "0.2";
            string t2 = _cmbFlashT2?.SelectedItem?.ToString() ?? "0.5";
            _txtParamOut.Text = $"{port},{t1},{t2}";
        }
        else if (action.StartsWith("WaitPort", StringComparison.OrdinalIgnoreCase))
        {
            string port = _cmbWaitPort?.SelectedItem?.ToString() ?? "P0.0";
            string timeout = _cmbWaitTimeout?.SelectedItem?.ToString() ?? "3.0";
            string label = _cmbWaitLabel?.SelectedItem?.ToString() ?? "";
            if (label == "(無)") label = "";
            string msg = _txtWaitMessage?.Text?.Trim() ?? "";

            string tail = $"{label}{msg}";
            _txtParamOut.Text = string.IsNullOrEmpty(tail) ? $"{port},{timeout}" : $"{port},{timeout},{tail}";
        }
        else if (action.StartsWith("CheckLogic", StringComparison.OrdinalIgnoreCase))
        {
            string logic = _txtLogicExpr?.Text?.Trim() ?? "P0.0|P0.1";
            string timeout = _cmbLogicTimeout?.SelectedItem?.ToString() ?? "3.0";
            string label = _cmbLogicLabel?.SelectedItem?.ToString() ?? "";
            if (label == "(無)") label = "";
            string msg = _txtLogicMessage?.Text?.Trim() ?? "";

            string tail = $"{label}{msg}";
            _txtParamOut.Text = string.IsNullOrEmpty(tail) ? $"{logic},{timeout}" : $"{logic},{timeout},{tail}";
        }
        else if (action.Equals("SetPortName", StringComparison.OrdinalIgnoreCase))
        {
            string port = _cmbRenamePort?.SelectedItem?.ToString() ?? "P0.0";
            string newName = _txtNewPortName?.Text?.Trim() ?? "Sensor1?";
            _txtParamOut.Text = $"{port},{newName}";
        }
        else if (action.Equals("AddVirtualIO", StringComparison.OrdinalIgnoreCase))
        {
            string port = _txtVirtualPortName?.Text?.Trim() ?? "VPort1";
            string val = _cmbVirtualState?.SelectedItem?.ToString() ?? "ON";
            _txtParamOut.Text = $"{port},{val}";
        }
        else if (action.Equals("AddIODriver", StringComparison.OrdinalIgnoreCase))
        {
            string io = _txtDriverDeviceName?.Text?.Trim() ?? "IO1";
            string drv = _cmbDriverType?.SelectedItem?.ToString() ?? "MockIO";
            string prms = _txtDriverParams?.Text?.Trim() ?? "";
            _txtParamOut.Text = string.IsNullOrEmpty(prms) ? $"{io},{drv}" : $"{io},{drv},{prms}";
        }
        else if (action.Equals("InitIOPort", StringComparison.OrdinalIgnoreCase) || action.Equals("ForceUpdate", StringComparison.OrdinalIgnoreCase))
        {
            string io = _cmbDeviceName?.SelectedItem?.ToString() ?? "IO1";
            _txtParamOut.Text = io;
        }
        else if (action.Equals("Shift", StringComparison.OrdinalIgnoreCase))
        {
            string ports = _txtShiftPorts?.Text?.Trim() ?? "P0.0,P0.1,P0.2";
            string shift = _cmbShiftAmount?.SelectedItem?.ToString() ?? "1";
            _txtParamOut.Text = $"{ports},Shift={shift}";
        }
    }
}
