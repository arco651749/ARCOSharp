using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;

namespace ARCOS.Driver.MockIO;

/// <summary>
/// MockIO 專屬驅動參數導引配置對話框 (MVCON Driver Parameter Wizard Window)
/// 當使用者於排程器配置 IO@AddIODriver 並點擊驅動精靈時彈出，
/// 提供 RS-232/TCP、點位起始位址/點數與輪詢週期之統一 MVCON 視覺配置。
/// </summary>
public class MockIoGuidanceDialog : Window
{
    public MockIoGuidanceDialog(MockIoDriver driver, string currentParam, BasicEngines? engines = null)
    {
        Title = "IO@AddIODriver::MockIO - Parameter Guidance";
        Width = 740;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#F8FAFC"));

        BuildMvconUi(driver, currentParam ?? string.Empty, engines);
    }

    private void BuildMvconUi(MockIoDriver driver, string currentParam, BasicEngines? engines)
    {
        var root = new Grid { Margin = new Thickness(16), Background = Brush.Parse("#F8FAFC") };
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // Header
        var hdrGrid = new Grid();
        hdrGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        hdrGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        hdrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        hdrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var lblTitle = new TextBlock { Text = "IO / MockIO - 驅動參數導引精靈", Foreground = Brush.Parse("#F8FAFC"), FontSize = 15, FontWeight = FontWeight.Bold };
        var lblSyntax = new TextBlock { Text = "Device=...,OutStart=...,OutPorts=...,InputStart=...,InPorts=...", Foreground = Brush.Parse("#38BDF8"), FontSize = 11, FontWeight = FontWeight.SemiBold, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(lblSyntax, 1);
        var txtParamIn = new TextBlock
        {
            Name = "TxtParamInInfo",
            Text = string.IsNullOrWhiteSpace(currentParam) ? "ParameterIn: (空白)" : $"ParameterIn: {currentParam.Trim()}",
            Foreground = Brush.Parse("#94A3B8"),
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(txtParamIn, 1);
        Grid.SetColumnSpan(txtParamIn, 2);
        hdrGrid.Children.Add(lblTitle);
        hdrGrid.Children.Add(lblSyntax);
        hdrGrid.Children.Add(txtParamIn);
        var hdrBorder = new Border { Background = Brush.Parse("#1E293B"), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 0, 10), Child = hdrGrid };
        root.Children.Add(hdrBorder);

        // Body
        var bodyGrid = new Grid();
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(112)));

        var fields = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };

        var regDrivers = engines?.Io?.GetRegisteredDrivers().ToList() ?? new List<string>();
        string regText = regDrivers.Count > 0 ? string.Join(", ", regDrivers) : "(尚未註冊)";
        var txtSummary = new TextBlock
        {
            Text = $"目前系統 IO 驅動: {regText} | 實例: {(string.IsNullOrWhiteSpace(driver.DeviceName) ? "NewIO" : driver.DeviceName)}",
            Foreground = Brush.Parse("#475569"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        };
        fields.Children.Add(new Border { Background = Brush.Parse("#F1F5F9"), BorderBrush = Brush.Parse("#CBD5E1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 6), Child = txtSummary });

        var (devPanel, cmbDev, txtDev, _) = MakeField("Device (通訊埠 / IP 位址) *", "Virtual / COM / TCP", "Virtual, com1, 192.168.0.1:5000...", true);
        fields.Children.Add(devPanel);

        var rowOut = new Grid();
        rowOut.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        rowOut.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10)));
        rowOut.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var (osPanel, cmbOs, txtOs, txtOsMeta) = MakeField("OutStart (DO 起始) *", "DO Range", "P0.0", true);
        var (opPanel, cmbOp, txtOp, _) = MakeField("OutPorts (DO 點數) *", "", "8, 16, 32...", true);
        Grid.SetColumn(opPanel, 2);
        rowOut.Children.Add(osPanel);
        rowOut.Children.Add(opPanel);
        fields.Children.Add(rowOut);

        var rowIn = new Grid();
        rowIn.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        rowIn.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10)));
        rowIn.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var (isPanel, cmbIs, txtIs, txtIsMeta) = MakeField("InputStart (DI 起始) *", "DI Range", "P1.0, P4.0...", true);
        var (ipPanel, cmbIp, txtIp, _) = MakeField("InPorts (DI 點數) *", "", "8, 16, 32...", true);
        Grid.SetColumn(ipPanel, 2);
        rowIn.Children.Add(isPanel);
        rowIn.Children.Add(ipPanel);
        fields.Children.Add(rowIn);

        var (ivPanel, cmbIv, txtIv, _) = MakeField("Interval (IO 輪詢週期 ms，選填)", "預設 25 ms", "例如 25 或 50 (可留空)", false);
        fields.Children.Add(ivPanel);

        var btnOk = new Button
        {
            Name = "BtnOk",
            Background = Brush.Parse("#10B981"),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(4, 12, 0, 4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "✓", FontSize = 34, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "套用參數", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center }
                }
            }
        };
        Grid.SetColumn(btnOk, 1);
        bodyGrid.Children.Add(fields);
        bodyGrid.Children.Add(btnOk);

        var bodyBorder = new Border { Background = Brushes.White, BorderBrush = Brush.Parse("#CBD5E1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14), Child = bodyGrid };
        Grid.SetRow(bodyBorder, 1);
        root.Children.Add(bodyBorder);

        // Footer
        var ftrGrid = new Grid();
        ftrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        ftrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var txtOut = new TextBox { Name = "TxtParamOut", IsReadOnly = true, Background = Brush.Parse("#F1F5F9"), Foreground = Brush.Parse("#0284C7"), FontWeight = FontWeight.Bold, FontFamily = new FontFamily("Consolas"), FontSize = 13, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
        var ftrStack = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 12, 0), Children = { new TextBlock { Text = "ParameterOut (組合預覽):", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#475569") }, txtOut } };
        var btnCancel = new Button { Name = "BtnCancel", Content = "取消", Width = 86, Height = 36, VerticalAlignment = VerticalAlignment.Bottom, Background = Brush.Parse("#E2E8F0"), Foreground = Brush.Parse("#334155"), FontWeight = FontWeight.SemiBold, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(5), Cursor = new Cursor(StandardCursorType.Hand) };
        Grid.SetColumn(btnCancel, 1);
        ftrGrid.Children.Add(ftrStack);
        ftrGrid.Children.Add(btnCancel);
        var ftrBorder = new Border { Background = Brushes.White, BorderBrush = Brush.Parse("#CBD5E1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10), Margin = new Thickness(0, 10, 0, 0), Child = ftrGrid };
        Grid.SetRow(ftrBorder, 2);
        root.Children.Add(ftrBorder);

        Content = root;

        var kv = ArgParaHelper.ParseKeyValueArgs(currentParam, out _);
        txtDev.Text = kv.TryGetValue("Device", out var vDev) && !string.IsNullOrWhiteSpace(vDev) ? vDev : (string.IsNullOrWhiteSpace(driver.Device) ? "com1" : driver.Device);
        txtOs.Text = kv.TryGetValue("OutStart", out var vOs) && !string.IsNullOrWhiteSpace(vOs) ? vOs : (string.IsNullOrWhiteSpace(driver.OutStart) ? "P0.0" : driver.OutStart);
        txtOp.Text = kv.TryGetValue("OutPorts", out var vOp) && !string.IsNullOrWhiteSpace(vOp) ? vOp : (driver.OutPorts > 0 ? driver.OutPorts.ToString() : "16");
        txtIs.Text = (kv.TryGetValue("InputStart", out var vIs) && !string.IsNullOrWhiteSpace(vIs)) ? vIs
            : (kv.TryGetValue("InStart", out var vIs2) && !string.IsNullOrWhiteSpace(vIs2) ? vIs2 : (string.IsNullOrWhiteSpace(driver.InStart) ? "P1.0" : driver.InStart));
        txtIp.Text = kv.TryGetValue("InPorts", out var vIp) && !string.IsNullOrWhiteSpace(vIp) ? vIp : (driver.InPorts > 0 ? driver.InPorts.ToString() : "16");
        txtIv.Text = kv.TryGetValue("Interval", out var vIv) ? vIv : string.Empty;

        var vars = engines?.Variable?.GetAllVariableNames().Select(v => $"${v}").ToList() ?? new List<string>();
        bool syncing = false;
        void UpdatePreview()
        {
            string dev = txtDev.Text?.Trim() ?? "";
            string os = txtOs.Text?.Trim() ?? "";
            string op = txtOp.Text?.Trim() ?? "";
            string ins = txtIs.Text?.Trim() ?? "";
            string inp = txtIp.Text?.Trim() ?? "";
            string iv = txtIv.Text?.Trim() ?? "";

            if (txtOsMeta != null)
                txtOsMeta.Text = int.TryParse(op, out int oCnt) && oCnt > 0 ? $"{os} ~ {MockIoDriver.GetPortNameAtOffset(os, oCnt - 1)}" : "DO Range";
            if (txtIsMeta != null)
                txtIsMeta.Text = int.TryParse(inp, out int iCnt) && iCnt > 0 ? $"{ins} ~ {MockIoDriver.GetPortNameAtOffset(ins, iCnt - 1)}" : "DI Range";

            bool okAll = !string.IsNullOrWhiteSpace(dev) && !string.IsNullOrWhiteSpace(os) &&
                         !string.IsNullOrWhiteSpace(op) && !string.IsNullOrWhiteSpace(ins) && !string.IsNullOrWhiteSpace(inp);
            btnOk.IsEnabled = okAll;
            if (!okAll) { txtOut.Text = string.Empty; return; }
            var segs = new List<string> { $"Device={dev}", $"OutStart={os}", $"OutPorts={op}", $"InputStart={ins}", $"InPorts={inp}" };
            if (!string.IsNullOrWhiteSpace(iv)) segs.Add($"Interval={iv}");
            txtOut.Text = string.Join(",", segs);
        }

        var devOpts = new List<string> { "Virtual", "com1", "com2", "com3", "com4", "192.168.0.1:5000" }; devOpts.AddRange(vars);
        var portStartOpts = new List<string> { "P0.0", "P1.0", "P2.0", "P4.0", "P8.0" }; portStartOpts.AddRange(vars);
        var portCountOpts = new List<string> { "8", "16", "32", "64" }; portCountOpts.AddRange(vars);
        const string ClearInterval = "(留白 - 預設 25ms)";
        var ivOpts = new List<string> { ClearInterval, "10", "25", "50", "100", "200" }; ivOpts.AddRange(vars);

        WireEditableCombo(cmbDev, txtDev, () => devOpts, "", () => syncing, v => syncing = v, UpdatePreview);
        WireEditableCombo(cmbOs, txtOs, () => portStartOpts, "", () => syncing, v => syncing = v, UpdatePreview);
        WireEditableCombo(cmbOp, txtOp, () => portCountOpts, "", () => syncing, v => syncing = v, UpdatePreview);
        WireEditableCombo(cmbIs, txtIs, () => portStartOpts, "", () => syncing, v => syncing = v, UpdatePreview);
        WireEditableCombo(cmbIp, txtIp, () => portCountOpts, "", () => syncing, v => syncing = v, UpdatePreview);
        WireEditableCombo(cmbIv, txtIv, () => ivOpts, ClearInterval, () => syncing, v => syncing = v, UpdatePreview);

        void ConfirmClose() { var r = txtOut.Text?.Trim() ?? string.Empty; if (!string.IsNullOrWhiteSpace(r)) Close(r); }
        btnCancel.Click += (_, _) => Close(null);
        btnOk.Click += (_, _) => ConfirmClose();
        KeyDown += (_, e) =>
        {
            if (cmbDev.IsDropDownOpen || cmbOs.IsDropDownOpen || cmbOp.IsDropDownOpen ||
                cmbIs.IsDropDownOpen || cmbIp.IsDropDownOpen || cmbIv.IsDropDownOpen) return;
            if (e.Key == Key.Enter) ConfirmClose();
            else if (e.Key == Key.Escape) Close(null);
        };
        UpdatePreview();
    }

    private static (StackPanel Panel, ComboBox Combo, TextBox Box, TextBlock? Meta) MakeField(string label, string meta, string watermark, bool required)
    {
        var accent = required ? "#DC2626" : "#2563EB";
        var hdr = new Grid();
        hdr.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        hdr.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        hdr.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse(accent), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        TextBlock? metaLbl = null;
        if (!string.IsNullOrEmpty(meta))
        {
            metaLbl = new TextBlock { Text = meta, Foreground = Brush.Parse(required ? "#64748B" : "#94A3B8"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(metaLbl, 1);
            hdr.Children.Add(metaLbl);
        }

        var cmb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(8, 3, 34, 3), Foreground = Brushes.Transparent, FontSize = 13 };
        var txt = new TextBox { Margin = new Thickness(2, 2, 32, 2), BorderThickness = new Thickness(0), Background = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 3), FontSize = 13, FontWeight = required ? FontWeight.SemiBold : FontWeight.Normal, Foreground = Brush.Parse(required ? "#DC2626" : "#1E293B"), Watermark = watermark };
        var boxGrid = new Grid { Children = { cmb, txt } };
        var border = new Border { BorderBrush = Brush.Parse(accent), BorderThickness = new Thickness(required ? 1.8 : 1.5), CornerRadius = new CornerRadius(5), Background = Brushes.White, Child = boxGrid };
        var sp = new StackPanel { Spacing = 3, Children = { hdr, border } };
        return (sp, cmb, txt, metaLbl);
    }

    private static void WireEditableCombo(ComboBox cmb, TextBox txt, Func<List<string>> getItems, string clearSentinel,
        Func<bool> getSync, Action<bool> setSync, Action onUpdated)
    {
        cmb.ItemsSource = getItems();
        cmb.DropDownOpened += (_, _) => { cmb.ItemsSource = getItems(); setSync(true); cmb.SelectedIndex = -1; setSync(false); };
        cmb.SelectionChanged += (_, _) =>
        {
            if (getSync() || cmb.SelectedItem == null) return;
            string sel = cmb.SelectedItem.ToString() ?? "";
            setSync(true);
            txt.Text = (!string.IsNullOrEmpty(clearSentinel) && sel == clearSentinel) ? "" : sel;
            cmb.SelectedIndex = -1;
            setSync(false);
            onUpdated();
        };
        txt.TextChanged += (_, _) => { if (!getSync()) onUpdated(); };
    }
}
