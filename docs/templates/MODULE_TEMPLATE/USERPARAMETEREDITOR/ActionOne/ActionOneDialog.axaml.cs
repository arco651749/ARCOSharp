using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;

namespace MVCON.Guidance.ActionOne;

public partial class ActionOneDialog : Window
{
    private readonly TextBlock? _txtParamIn;
    private readonly TextBox? _txtParamOut;
    private readonly ComboBox? _cmbTarget;
    private readonly ComboBox? _cmbDelay;
    private readonly Button? _btnApplyBig;
    private readonly Button? _btnOk;
    private readonly Button? _btnCancel;

    public ActionOneDialog()
    {
        InitializeComponent();
        _txtParamIn = this.FindControl<TextBlock>("TxtParamIn");
        _txtParamOut = this.FindControl<TextBox>("TxtParamOut");
        _cmbTarget = this.FindControl<ComboBox>("CmbTarget");
        _cmbDelay = this.FindControl<ComboBox>("CmbDelay");
        _btnApplyBig = this.FindControl<Button>("BtnApplyBig");
        _btnOk = this.FindControl<Button>("BtnOk");
        _btnCancel = this.FindControl<Button>("BtnCancel");

        if (_btnCancel != null) _btnCancel.Click += (_, _) => Close(null);
        if (_btnOk != null) _btnOk.Click += (_, _) => Close(_txtParamOut?.Text);
        if (_btnApplyBig != null) _btnApplyBig.Click += (_, _) => Close(_txtParamOut?.Text);

        if (_cmbTarget != null) _cmbTarget.SelectionChanged += (_, _) => UpdatePreview();
        if (_cmbDelay != null)
        {
            _cmbDelay.SelectionChanged += (_, _) => UpdatePreview();
            _cmbDelay.KeyUp += (_, _) => UpdatePreview();
        }

        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter) Close(_txtParamOut?.Text);
            else if (e.Key == Key.Escape) Close(null);
        };
    }

    public ActionOneDialog(string currentParam, IEnumerable<string> liveTargets) : this()
    {
        if (_txtParamIn != null) _txtParamIn.Text = $"ParameterIn: {currentParam}";

        if (_cmbTarget != null)
        {
            _cmbTarget.ItemsSource = liveTargets;
        }

        if (_cmbDelay != null)
        {
            _cmbDelay.ItemsSource = new[] { "0.0", "0.05", "0.1", "0.2", "0.5", "1.0", "2.0" };
            _cmbDelay.SelectedItem = "0.1";
        }

        // 解析傳入的原始參數 (如 "Target=Vacuum_1,Delay=0.2")
        ParseCurrentParam(currentParam);
        UpdatePreview();
    }

    private void ParseCurrentParam(string param)
    {
        if (string.IsNullOrWhiteSpace(param)) return;
        foreach (var seg in param.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = seg.Split('=', 2);
            if (pair.Length == 2)
            {
                var k = pair[0].Trim();
                var v = pair[1].Trim();
                if (k.Equals("Target", StringComparison.OrdinalIgnoreCase) && _cmbTarget != null)
                {
                    _cmbTarget.SelectedItem = v;
                }
                else if (k.Equals("Delay", StringComparison.OrdinalIgnoreCase) && _cmbDelay != null)
                {
                    _cmbDelay.SelectedItem = v;
                }
            }
        }
    }

    private void UpdatePreview()
    {
        string target = _cmbTarget?.SelectedItem?.ToString() ?? "Target1";
        string delay = _cmbDelay?.SelectedItem?.ToString() ?? "0.1";

        if (_txtParamOut != null)
        {
            _txtParamOut.Text = $"Target={target},Delay={delay}";
        }
    }
}
