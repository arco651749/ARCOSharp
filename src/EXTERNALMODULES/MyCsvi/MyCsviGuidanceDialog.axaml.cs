using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ARCOS.Core.Drivers;

namespace ExternalModules.MyCsvi;

public partial class MyCsviGuidanceDialog : Window
{
    private RadioButton _rbHistogram;
    private RadioButton _rbMath;
    private StackPanel _pnlHistogram;
    private StackPanel _pnlMath;
    private TextBox _txtCamera;
    private TextBox _txtMinThresh;
    private TextBox _txtMaxThresh;
    private TextBox _txtVal1;
    private TextBox _txtVal2;
    private TextBox _txtReturnVar;
    private TextBox _txtPreview;
    private Button _btnConfirm;
    private Button _btnCancel;

    public MyCsviGuidanceDialog()
    {
        InitializeComponent();
    }

    public MyCsviGuidanceDialog(string currentParam, BasicEngines? engines) : this()
    {
        ParseInitialParam(currentParam);
        UpdatePreview();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _rbHistogram = this.FindControl<RadioButton>("RbHistogram")!;
        _rbMath = this.FindControl<RadioButton>("RbMath")!;
        _pnlHistogram = this.FindControl<StackPanel>("PnlHistogram")!;
        _pnlMath = this.FindControl<StackPanel>("PnlMath")!;
        _txtCamera = this.FindControl<TextBox>("TxtCamera")!;
        _txtMinThresh = this.FindControl<TextBox>("TxtMinThresh")!;
        _txtMaxThresh = this.FindControl<TextBox>("TxtMaxThresh")!;
        _txtVal1 = this.FindControl<TextBox>("TxtVal1")!;
        _txtVal2 = this.FindControl<TextBox>("TxtVal2")!;
        _txtReturnVar = this.FindControl<TextBox>("TxtReturnVar")!;
        _txtPreview = this.FindControl<TextBox>("TxtPreview")!;
        _btnConfirm = this.FindControl<Button>("BtnConfirm")!;
        _btnCancel = this.FindControl<Button>("BtnCancel")!;

        _rbHistogram.IsCheckedChanged += (_, _) =>
        {
            bool isHist = _rbHistogram.IsChecked == true;
            _pnlHistogram.IsVisible = isHist;
            _pnlMath.IsVisible = !isHist;
            UpdatePreview();
        };

        _rbMath.IsCheckedChanged += (_, _) =>
        {
            bool isMath = _rbMath.IsChecked == true;
            _pnlMath.IsVisible = isMath;
            _pnlHistogram.IsVisible = !isMath;
            UpdatePreview();
        };

        _txtCamera.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdatePreview(); };
        _txtMinThresh.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdatePreview(); };
        _txtMaxThresh.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdatePreview(); };
        _txtVal1.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdatePreview(); };
        _txtVal2.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdatePreview(); };
        _txtReturnVar.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdatePreview(); };

        _btnConfirm.Click += (_, _) =>
        {
            Close(BuildParamString());
        };

        _btnCancel.Click += (_, _) =>
        {
            Close(null);
        };
    }

    private void ParseInitialParam(string currentParam)
    {
        if (string.IsNullOrWhiteSpace(currentParam)) return;

        var tokens = currentParam.Trim().TrimEnd(';').Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length >= 2 && double.TryParse(tokens[0], out _) && double.TryParse(tokens[1], out _))
        {
            _rbMath.IsChecked = true;
            _pnlMath.IsVisible = true;
            _pnlHistogram.IsVisible = false;
            _txtVal1.Text = tokens[0];
            _txtVal2.Text = tokens[1];
            if (tokens.Length > 2) _txtReturnVar.Text = tokens[2].TrimStart('$');
        }
        else
        {
            _rbHistogram.IsChecked = true;
            _pnlHistogram.IsVisible = true;
            _pnlMath.IsVisible = false;
            if (tokens.Length > 0) _txtCamera.Text = tokens[0];
            if (tokens.Length > 1) _txtMinThresh.Text = tokens[1];
            if (tokens.Length > 2) _txtMaxThresh.Text = tokens[2];
            if (tokens.Length > 3) _txtReturnVar.Text = tokens[3].TrimStart('$');
        }
    }

    private string BuildParamString()
    {
        string retVar = string.IsNullOrWhiteSpace(_txtReturnVar.Text) ? "Result" : _txtReturnVar.Text.Trim().TrimStart('$');
        if (_rbMath.IsChecked == true)
        {
            string v1 = string.IsNullOrWhiteSpace(_txtVal1.Text) ? "0" : _txtVal1.Text.Trim();
            string v2 = string.IsNullOrWhiteSpace(_txtVal2.Text) ? "0" : _txtVal2.Text.Trim();
            return $"{v1},{v2},{retVar}";
        }
        else
        {
            string cam = string.IsNullOrWhiteSpace(_txtCamera.Text) ? "Cam1" : _txtCamera.Text.Trim();
            string min = string.IsNullOrWhiteSpace(_txtMinThresh.Text) ? "0" : _txtMinThresh.Text.Trim();
            string max = string.IsNullOrWhiteSpace(_txtMaxThresh.Text) ? "255" : _txtMaxThresh.Text.Trim();
            return $"{cam},{min},{max},{retVar}";
        }
    }

    private void UpdatePreview()
    {
        if (_txtPreview != null)
        {
            _txtPreview.Text = BuildParamString();
        }
    }
}
