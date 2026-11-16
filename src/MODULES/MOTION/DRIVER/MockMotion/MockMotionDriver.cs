using System.Globalization;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;
using ARCOS.Core.Model;

namespace ARCOS.Driver.MockMotion;

/// <summary>
/// 模擬運動與機械手臂控制器驅動 (MockMotionDriver)
/// 支援：
/// 1. GetPos 同時寫入笛卡爾座標 ($motor) 與關節座標 ($motor#Joint)
/// 2. Driver 自主分析點位為笛卡爾座標或 #Joint 關節座標 (含 Path 內混用)，自動切換對應 Robot 指令
/// 3. GetState 自動寫入 $(motor)#Busy?、$(motor)#Alarm? 與控制器本身原生 ErrorCode (Motion#ErrorCode)
/// 4. SetTool 設定 Robot 工具座標系 ($motor, $toolpoint)
/// 5. ExtraCMD 執行各品牌 Robot 原生控制器指令
/// 6. SetDO / GetDI / GetDO API 供 IO Driver 跨驅動讀寫單一連線 Robot 上的自帶 I/O
/// </summary>
public class MockMotionDriver : IDriver
{
    private BasicEngines? _engines;
    private readonly Channel<DriverTicket> _ticketQueue = Channel.CreateUnbounded<DriverTicket>();
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    public string DeviceName { get; private set; } = string.Empty;
    public string Category => "Motion";
    public string DriverType => "MockMotion";

    /// <summary>
    /// 當前笛卡爾座標 (Cartesian: X, Y, Z, U, V, W)
    /// </summary>
    public MachinePoint CurrentPos { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>
    /// 當前關節座標 (Joint: J1~J6 儲存於 X~W，且 H["Coord"] = "Joint")
    /// </summary>
    public MachinePoint CurrentJointPos { get; } = new(0, 0, 0, 0, 0, 0, "Coord=Joint");

    public MachinePoint ToGoPos { get; } = new(0, 0, 0, 0, 0, 0);
    public List<MachinePoint> PathQueue { get; } = new();

    public bool IsBusy { get; private set; } = false;
    public bool IsHomed { get; private set; } = false;
    public bool IsAlarm { get; set; } = false;

    /// <summary>
    /// 控制器本身的原生錯誤碼 (0 代表正常，非 0 為控制器原生 Error Code)
    /// </summary>
    public int ControllerErrorCode { get; set; } = 0;

    /// <summary>
    /// 當前啟用的工具座標名稱與偏置 (由 Motion@SetTool 設定)
    /// </summary>
    public string CurrentToolName { get; private set; } = "Tool0";
    public MachinePoint CurrentToolOffset { get; private set; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>
    /// 最後一次執行的 Robot 運動指令型態 ("LINEAR_MOVE" 或 "JOINT_MOVE")
    /// </summary>
    public string LastRobotMoveMode { get; private set; } = "LINEAR_MOVE";

    /// <summary>
    /// 最後一次透過 ExtraCMD 下達至 Robot 控制器的原生指令字串
    /// </summary>
    public string LastExtraCommand { get; private set; } = string.Empty;

    /// <summary>
    /// 透過 Config 指令設定之控制器內部組態參數表
    /// </summary>
    public Dictionary<string, string> ConfigParameters { get; } = new(StringComparer.OrdinalIgnoreCase);

    // 手臂控制器自帶之專屬虛擬 DI / DO (供 IO Driver 透過 SetDO/GetDI/GetDO API 存取)
    private readonly Dictionary<int, bool> _digitalInputs = new();
    private readonly Dictionary<int, bool> _digitalOutputs = new();

    public event Action? MotionStateUpdated;

    public void Init(string deviceName, string param, BasicEngines engines)
    {
        DeviceName = deviceName;
        _engines = engines;
        CurrentJointPos.H["Coord"] = "Joint";

        // 初始化狀態回寫至 IoEngine 與 PointEngine (同時建立 $motor 與 $motor#Joint)
        UpdateEngines();

        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
    }

    public void PostTicket(DriverTicket ticket)
    {
        _ticketQueue.Writer.TryWrite(ticket);
    }

    public object? CreateDriverView()
    {
        return new MockMotionView(this);
    }

    public async Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
    {
        _engines = engines;
        if (owner is not Window ownerWin) return currentParam;

        var root = new Grid { Margin = new Thickness(16), Background = Brush.Parse("#F8FAFC") };
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var hdrGrid = new Grid();
        hdrGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        hdrGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        hdrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        hdrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var lblSyntax = new TextBlock { Text = "Axes=...,Speed=...,Acc=...,Dec=...", Foreground = Brush.Parse("#38BDF8"), FontSize = 11, FontWeight = FontWeight.SemiBold, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(lblSyntax, 1);
        var txtParamIn = new TextBlock { Text = string.IsNullOrWhiteSpace(currentParam) ? "ParameterIn: (空白)" : $"ParameterIn: {currentParam.Trim()}", Foreground = Brush.Parse("#94A3B8"), FontSize = 11, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetRow(txtParamIn, 1); Grid.SetColumnSpan(txtParamIn, 2);
        hdrGrid.Children.Add(new TextBlock { Text = "Motion / MockMotion - 驅動參數導引精靈", Foreground = Brush.Parse("#F8FAFC"), FontSize = 15, FontWeight = FontWeight.Bold });
        hdrGrid.Children.Add(lblSyntax);
        hdrGrid.Children.Add(txtParamIn);
        root.Children.Add(new Border { Background = Brush.Parse("#1E293B"), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 0, 10), Child = hdrGrid });

        var bodyGrid = new Grid();
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(112)));
        var fields = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };

        var motors = engines?.Io?.GetAllPortNames().Where(p => p.EndsWith("#Busy?", StringComparison.OrdinalIgnoreCase)).Select(p => p.Split('#')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        int ptCount = engines?.Point?.GetAllNames().Count ?? 0;
        fields.Children.Add(new Border
        {
            Background = Brush.Parse("#F1F5F9"),
            BorderBrush = Brush.Parse("#CBD5E1"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 6),
            Child = new TextBlock { Text = $"目前系統軸控: {(motors.Count > 0 ? string.Join(", ", motors) : "(尚未註冊)")} | 點位數: {ptCount} | 實例: {(string.IsNullOrWhiteSpace(DeviceName) ? "Motor1" : DeviceName)}", Foreground = Brush.Parse("#475569"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap }
        });

        static (StackPanel Panel, ComboBox Combo, TextBox Box, TextBlock? Meta) MakeField(string label, string meta, string watermark, bool required)
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
                Grid.SetColumn(metaLbl, 1); hdr.Children.Add(metaLbl);
            }
            var cmb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(8, 3, 34, 3), Foreground = Brushes.Transparent, FontSize = 13 };
            var txt = new TextBox { Margin = new Thickness(2, 2, 32, 2), BorderThickness = new Thickness(0), Background = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 3), FontSize = 13, FontWeight = required ? FontWeight.SemiBold : FontWeight.Normal, Foreground = Brush.Parse(required ? "#DC2626" : "#1E293B"), Watermark = watermark };
            return (new StackPanel { Spacing = 3, Children = { hdr, new Border { BorderBrush = Brush.Parse(accent), BorderThickness = new Thickness(required ? 1.8 : 1.5), CornerRadius = new CornerRadius(5), Background = Brushes.White, Child = new Grid { Children = { cmb, txt } } } } }, cmb, txt, metaLbl);
        }

        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); row1.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10))); row1.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var (axP, cmbAxes, txtAxes, txtAxesMeta) = MakeField("Axes (控制軸數) *", "4-Axis SCARA", "1, 3, 4, 6...", true);
        var (spP, cmbSpeed, txtSpeed, _) = MakeField("Speed (預設速度 mm/s) *", "", "50, 100, 200...", true);
        Grid.SetColumn(spP, 2); row1.Children.Add(axP); row1.Children.Add(spP); fields.Children.Add(row1);

        var row2 = new Grid();
        row2.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); row2.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10))); row2.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var (acP, cmbAcc, txtAcc, _) = MakeField("Acc (加速度 mm/s^2，選填)", "", "1000", false);
        var (dcP, cmbDec, txtDec, _) = MakeField("Dec (減速度 mm/s^2，選填)", "", "1000", false);
        Grid.SetColumn(dcP, 2); row2.Children.Add(acP); row2.Children.Add(dcP); fields.Children.Add(row2);

        var (exP, cmbExtra, txtExtra, _) = MakeField("Extra Key=Value (額外控制器參數，選填)", "如 HomeOnInit=true 或 Port=COM1", "選填擴充參數，如 Coord=Joint 或留白...", false);
        fields.Children.Add(exP);

        var btnOk = new Button { Background = Brush.Parse("#10B981"), Foreground = Brushes.White, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(4, 12, 0, 4), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand), Content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = "✓", FontSize = 34, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center }, new TextBlock { Text = "套用參數", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center } } } };
        Grid.SetColumn(btnOk, 1); bodyGrid.Children.Add(fields); bodyGrid.Children.Add(btnOk);
        var bodyBorder = new Border { Background = Brushes.White, BorderBrush = Brush.Parse("#CBD5E1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14), Child = bodyGrid };
        Grid.SetRow(bodyBorder, 1); root.Children.Add(bodyBorder);

        var ftrGrid = new Grid();
        ftrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); ftrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var txtOut = new TextBox { IsReadOnly = true, Background = Brush.Parse("#F1F5F9"), Foreground = Brush.Parse("#0284C7"), FontWeight = FontWeight.Bold, FontFamily = new FontFamily("Consolas"), FontSize = 13, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
        var btnCancel = new Button { Content = "取消", Width = 86, Height = 36, VerticalAlignment = VerticalAlignment.Bottom, Background = Brush.Parse("#E2E8F0"), Foreground = Brush.Parse("#334155"), FontWeight = FontWeight.SemiBold, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(5), Cursor = new Cursor(StandardCursorType.Hand) };
        Grid.SetColumn(btnCancel, 1);
        ftrGrid.Children.Add(new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 12, 0), Children = { new TextBlock { Text = "ParameterOut (組合預覽):", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#475569") }, txtOut } });
        ftrGrid.Children.Add(btnCancel);
        var ftrBorder = new Border { Background = Brushes.White, BorderBrush = Brush.Parse("#CBD5E1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10), Margin = new Thickness(0, 10, 0, 0), Child = ftrGrid };
        Grid.SetRow(ftrBorder, 2); root.Children.Add(ftrBorder);

        var dlg = new Window { Title = "Motion@AddMotion::MockMotion - Parameter Guidance", Width = 740, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#F8FAFC"), Content = root };

        var kv = ArgParaHelper.ParseKeyValueArgs(currentParam ?? "", out _);
        txtAxes.Text = kv.TryGetValue("Axes", out var vAxes) && !string.IsNullOrWhiteSpace(vAxes) ? vAxes : "4";
        txtSpeed.Text = kv.TryGetValue("Speed", out var vSpd) && !string.IsNullOrWhiteSpace(vSpd) ? vSpd : "100";
        txtAcc.Text = kv.TryGetValue("Acc", out var vAcc) ? vAcc : "1000";
        txtDec.Text = kv.TryGetValue("Dec", out var vDec) ? vDec : "1000";
        txtExtra.Text = string.Join(", ", kv.Where(p => !p.Key.Equals("Axes", StringComparison.OrdinalIgnoreCase) && !p.Key.Equals("Speed", StringComparison.OrdinalIgnoreCase) && !p.Key.Equals("Acc", StringComparison.OrdinalIgnoreCase) && !p.Key.Equals("Dec", StringComparison.OrdinalIgnoreCase)).Select(p => $"{p.Key}={p.Value}"));

        var vars = engines?.Variable?.GetAllVariableNames().Select(v => $"${v}").ToList() ?? new List<string>();
        bool syncing = false;
        void UpdatePreview()
        {
            string ax = txtAxes.Text?.Trim() ?? "", sp = txtSpeed.Text?.Trim() ?? "", ac = txtAcc.Text?.Trim() ?? "", dc = txtDec.Text?.Trim() ?? "", ex = txtExtra.Text?.Trim() ?? "";
            if (txtAxesMeta != null) txtAxesMeta.Text = ax == "6" ? "6-Axis Robot" : (ax == "4" ? "4-Axis SCARA" : $"{ax} Axis");
            bool okAll = !string.IsNullOrWhiteSpace(ax) && !string.IsNullOrWhiteSpace(sp);
            btnOk.IsEnabled = okAll;
            if (!okAll) { txtOut.Text = string.Empty; return; }
            var segs = new List<string> { $"Axes={ax}", $"Speed={sp}" };
            if (!string.IsNullOrWhiteSpace(ac)) segs.Add($"Acc={ac}");
            if (!string.IsNullOrWhiteSpace(dc)) segs.Add($"Dec={dc}");
            if (!string.IsNullOrWhiteSpace(ex)) segs.Add(ex);
            txtOut.Text = string.Join(", ", segs);
        }

        var axesOpts = new List<string> { "1", "3", "4", "6", "8" }; axesOpts.AddRange(vars);
        var speedOpts = new List<string> { "50", "100", "200", "500", "1000" }; speedOpts.AddRange(vars);
        const string ClearVal = "(留白)";
        var accOpts = new List<string> { ClearVal, "500", "1000", "2000", "5000" }; accOpts.AddRange(vars);
        var extraOpts = new List<string> { ClearVal, "HomeOnInit=true", "Coord=Joint", "Port=COM1,Baud=115200" }; extraOpts.AddRange(vars);

        WireWizardCombo(cmbAxes, txtAxes, () => axesOpts, "", () => syncing, v => syncing = v, UpdatePreview);
        WireWizardCombo(cmbSpeed, txtSpeed, () => speedOpts, "", () => syncing, v => syncing = v, UpdatePreview);
        WireWizardCombo(cmbAcc, txtAcc, () => accOpts, ClearVal, () => syncing, v => syncing = v, UpdatePreview);
        WireWizardCombo(cmbDec, txtDec, () => accOpts, ClearVal, () => syncing, v => syncing = v, UpdatePreview);
        WireWizardCombo(cmbExtra, txtExtra, () => extraOpts, ClearVal, () => syncing, v => syncing = v, UpdatePreview);

        void ConfirmClose() { var r = txtOut.Text?.Trim() ?? ""; if (!string.IsNullOrWhiteSpace(r)) dlg.Close(r); }
        btnCancel.Click += (_, _) => dlg.Close(null);
        btnOk.Click += (_, _) => ConfirmClose();
        dlg.KeyDown += (_, e) =>
        {
            if (cmbAxes.IsDropDownOpen || cmbSpeed.IsDropDownOpen || cmbAcc.IsDropDownOpen || cmbDec.IsDropDownOpen || cmbExtra.IsDropDownOpen) return;
            if (e.Key == Key.Enter) ConfirmClose(); else if (e.Key == Key.Escape) dlg.Close(null);
        };
        UpdatePreview();
        return await dlg.ShowDialog<string?>(ownerWin);
    }

    private static void WireWizardCombo(ComboBox? cmb, TextBox? txt, Func<List<string>> getItems, string clearSentinel,
        Func<bool> getSync, Action<bool> setSync, Action onUpdated)
    {
        if (cmb == null || txt == null) return;
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

    private async Task WorkerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var ticket = await _ticketQueue.Reader.ReadAsync(token);
                await ExecuteCaseAsync(ticket, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // 容錯防禦
            }
        }
    }

    private async Task ExecuteCaseAsync(DriverTicket ticket, CancellationToken token)
    {
        string cmd = ticket.Command.Trim();

        switch (cmd)
        {
            case "Home":
                SetBusy(true);
                await Task.Delay(60, token);
                CurrentPos.X = 0; CurrentPos.Y = 0; CurrentPos.Z = 0;
                CurrentPos.U = 0; CurrentPos.V = 0; CurrentPos.W = 0;
                SyncJointFromCartesian();
                IsHomed = true;
                IsAlarm = false;
                ControllerErrorCode = 0;
                SetBusy(false);
                ticket.SetCompleted();
                break;

            case "Go":
            case "Go(SingleAxis)":
                SetBusy(true);
                try
                {
                    var targetPt = ParseGoCommandParam(ticket.Parameter);
                    LastRobotMoveMode = "LINEAR_MOVE";
                    RecordMoveMode(LastRobotMoveMode, "Go");
                    ApplyInfAxisMask(targetPt, isJoint: false);
                    await SimulateInterpolationAsync(isJoint: false, token);
                }
                finally
                {
                    SetBusy(false);
                    ticket.SetCompleted();
                }
                break;

            case "GoPoint":
            case "GoPoint(SingleAxis)":
                SetBusy(true);
                try
                {
                    var pt = ResolvePointFromTicketParam(ticket.Parameter, out bool isJoint, out string ptLabel);
                    LastRobotMoveMode = isJoint ? "JOINT_MOVE" : "LINEAR_MOVE";
                    RecordMoveMode(LastRobotMoveMode, ptLabel);
                    ApplyInfAxisMask(pt, isJoint);
                    await SimulateInterpolationAsync(isJoint, token);
                }
                finally
                {
                    SetBusy(false);
                    ticket.SetCompleted();
                }
                break;

            case "ClearPath":
                PathQueue.Clear();
                ticket.SetCompleted();
                break;

            case "AddPath":
                {
                    var pt = ResolvePointFromTicketParam(ticket.Parameter, out bool isJoint, out _);
                    var cloned = pt.Clone();
                    if (isJoint)
                    {
                        cloned.H["Coord"] = "Joint";
                    }
                    PathQueue.Add(cloned);
                    ticket.SetCompleted();
                }
                break;

            case "RunPath":
                SetBusy(true);
                try
                {
                    foreach (var pt in PathQueue.ToList())
                    {
                        if (token.IsCancellationRequested) break;
                        bool isJoint = IsJointPoint(pt, null);
                        LastRobotMoveMode = isJoint ? "JOINT_MOVE" : "LINEAR_MOVE";
                        RecordMoveMode(LastRobotMoveMode, isJoint ? "Path#Joint" : "Path#Cartesian");
                        ApplyInfAxisMask(pt, isJoint);
                        await SimulateInterpolationAsync(isJoint, token);
                    }
                    PathQueue.Clear();
                }
                finally
                {
                    SetBusy(false);
                    ticket.SetCompleted();
                }
                break;

            case "Stop":
                SetBusy(false);
                ticket.SetCompleted();
                break;

            case "GetPos":
                ExecuteGetPosCommand(ticket);
                break;

            case "GetState":
                ExecuteGetStateCommand(ticket);
                break;

            case "Config":
                ExecuteConfigCommand(ticket.Parameter, ticket);
                break;

            case "SetTool":
                ExecuteSetToolCommand(ticket.Parameter, ticket);
                break;

            case "ExtraCMD":
                await ExecuteExtraCommandAsync(ticket.Parameter, ticket, token);
                break;

            case "SetDO":
                ExecuteSetDOCommand(ticket.Parameter, ticket);
                break;

            case "GetDI":
                ExecuteGetDICommand(ticket.Parameter, ticket);
                break;

            case "GetDO":
                ExecuteGetDOCommand(ticket.Parameter, ticket);
                break;

            case "Close":
                Shutdown();
                ticket.SetCompleted();
                break;

            case "UpdateInfo":
            default:
                UpdateEngines();
                ticket.SetCompleted();
                break;
        }

        MotionStateUpdated?.Invoke();
    }

    /// <summary>
    /// GetPos：同時寫入笛卡爾座標 ($motor) 與關節座標 ($motor#Joint)
    /// </summary>
    public virtual void ExecuteGetPosCommand(DriverTicket ticket)
    {
        UpdateEngines();
        if (_engines != null && !string.IsNullOrEmpty(DeviceName))
        {
            var cartClone = CurrentPos.Clone();
            var jointClone = CurrentJointPos.Clone();
            jointClone.H["Coord"] = "Joint";

            _engines.Point.SetPoint(DeviceName, cartClone);
            _engines.Point.SetPoint($"{DeviceName}#Joint", jointClone);
        }
        ticket.SetCompleted(DriverResponse.Ok(CurrentPos.Clone()));
    }

    /// <summary>
    /// GetState：寫入 IO 的 $(motor)#Busy? 與 $(motor)#Alarm?，並回報控制器本身的 ErrorCode
    /// </summary>
    public virtual void ExecuteGetStateCommand(DriverTicket ticket)
    {
        if (_engines != null && !string.IsNullOrEmpty(DeviceName))
        {
            _engines.Io.SetPort($"{DeviceName}#Busy?", IsBusy);
            _engines.Io.SetPort($"{DeviceName}#Done?", !IsBusy);
            _engines.Io.SetPort($"{DeviceName}#Alarm?", IsAlarm);
            _engines.Variable.Set("Motion#ErrorCode", ControllerErrorCode.ToString(CultureInfo.InvariantCulture), "Global");
        }

        var stateDict = new Dictionary<string, object>
        {
            ["IsBusy"] = IsBusy,
            ["IsHomed"] = IsHomed,
            ["IsAlarm"] = IsAlarm,
            ["ErrorCode"] = ControllerErrorCode
        };

        if (IsAlarm || ControllerErrorCode != 0)
        {
            int errCode = ControllerErrorCode != 0 ? ControllerErrorCode : -1;
            ticket.SetFailed(errCode.ToString(CultureInfo.InvariantCulture), $"Controller Error Code: {errCode}");
            return;
        }

        ticket.SetCompleted(DriverResponse.Ok(stateDict));
    }

    /// <summary>
    /// SetTool：設定 Robot 工具座標系 ($motion, $toolpoint)
    /// </summary>
    public virtual void ExecuteSetToolCommand(string rawParameter, DriverTicket ticket)
    {
        ArgParaHelper.ParseKeyValueArgs(rawParameter, out var posArgs);
        string toolExpr = string.Empty;
        if (posArgs.Count >= 2 && string.Equals(posArgs[0], DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            toolExpr = posArgs[1];
        }
        else if (posArgs.Count >= 1)
        {
            toolExpr = posArgs[^1];
        }

        if (string.IsNullOrWhiteSpace(toolExpr))
        {
            ticket.SetFailed("INVALID_TOOL", "SetTool requires a tool point name or coordinate.");
            return;
        }

        CurrentToolName = toolExpr.Trim();
        if (_engines != null)
        {
            try
            {
                if (CurrentToolName.StartsWith('(') && CurrentToolName.EndsWith(')'))
                {
                    CurrentToolOffset = MachinePoint.ParseCoords(CurrentToolName);
                }
                else
                {
                    CurrentToolOffset = _engines.Point.GetPoint(CurrentToolName).Clone();
                }
            }
            catch
            {
                CurrentToolOffset = new MachinePoint(0, 0, 0, 0, 0, 0);
            }

            _engines.Variable.Set($"{DeviceName}#Tool", CurrentToolName, "Global");
            _engines.Point.SetPoint($"{DeviceName}#Tool", CurrentToolOffset.Clone());
            _engines.Record.Record("SYSTEMLOG", $"[MockMotion:{DeviceName}] [SetTool] Active tool set to '{CurrentToolName}' ({CurrentToolOffset.X},{CurrentToolOffset.Y},{CurrentToolOffset.Z})");
        }

        ticket.SetCompleted(DriverResponse.Ok(CurrentToolName));
    }

    /// <summary>
    /// 專供 Motion@ExtraCMD 調用之 Robot 原生控制器指令處理函式。
    /// 支援 MockMotion 原生指令：
    /// - PWR ON / PWR OFF
    /// - SPEED &lt;val&gt;
    /// - ACCEL &lt;val&gt;
    /// - DOUT &lt;idx&gt;,&lt;0|1&gt;
    /// - ERR &lt;code&gt; / RST
    /// - 其他任意原生字串透傳
    /// </summary>
    public virtual async Task ExecuteExtraCommandAsync(string rawParameter, DriverTicket ticket, CancellationToken token)
    {
        string nativeCmd = ExtractNativePayload(rawParameter);
        LastExtraCommand = nativeCmd;

        // 解析 MockMotion 原生控制器測試指令
        if (nativeCmd.StartsWith("ERR ", StringComparison.OrdinalIgnoreCase))
        {
            string codeStr = nativeCmd.Substring(4).Trim();
            if (int.TryParse(codeStr, out int errCode))
            {
                ControllerErrorCode = errCode;
                IsAlarm = errCode != 0;
            }
        }
        else if (nativeCmd.Equals("RST", StringComparison.OrdinalIgnoreCase) ||
                 nativeCmd.Equals("RESET", StringComparison.OrdinalIgnoreCase))
        {
            ControllerErrorCode = 0;
            IsAlarm = false;
        }
        else if (nativeCmd.StartsWith("SPEED ", StringComparison.OrdinalIgnoreCase))
        {
            ConfigParameters["Speed"] = nativeCmd.Substring(6).Trim();
        }
        else if (nativeCmd.StartsWith("ACCEL ", StringComparison.OrdinalIgnoreCase))
        {
            ConfigParameters["Accel"] = nativeCmd.Substring(6).Trim();
        }
        else if (nativeCmd.StartsWith("DOUT ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = nativeCmd.Substring(5).Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out int dIdx) && int.TryParse(parts[1], out int dVal))
            {
                SetDigitalOutput(dIdx, dVal != 0);
            }
        }

        UpdateEngines();

        if (_engines != null)
        {
            _engines.Variable.Set($"{DeviceName}#LastExtraCMD", nativeCmd, "Global");
            _engines.Record.Record("SYSTEMLOG", $"[MockMotion:{DeviceName}] [ExtraCMD] Native robot command executed: '{nativeCmd}'");
        }

        await Task.Delay(20, token);
        ticket.SetCompleted(DriverResponse.Ok(nativeCmd));
    }

    /// <summary>
    /// 專供 Motion@Config 調用之控制器參數組態設定函式 ($motor, $parameter, $value)
    /// </summary>
    public virtual void ExecuteConfigCommand(string rawParameter, DriverTicket ticket)
    {
        var kv = ArgParaHelper.ParseKeyValueArgs(rawParameter, out var posArgs);
        foreach (var pair in kv)
        {
            if (!pair.Key.StartsWith('_'))
            {
                ConfigParameters[pair.Key] = pair.Value;
            }
        }

        if (posArgs.Count >= 3)
        {
            ConfigParameters[posArgs[1]] = posArgs[2];
        }
        else if (posArgs.Count == 2 && !string.Equals(posArgs[0], DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            ConfigParameters[posArgs[0]] = posArgs[1];
        }

        ticket.SetCompleted(DriverResponse.Ok());
    }

    #region Robot 內部 DO / DI API (供 IO Driver 跨驅動調用)

    public void SetDigitalOutput(int index, bool value)
    {
        _digitalOutputs[index] = value;
        _engines?.Io.SetPort($"{DeviceName}#DO{index}", value);
    }

    public bool GetDigitalOutput(int index)
    {
        return _digitalOutputs.TryGetValue(index, out bool val) && val;
    }

    public void SetDigitalInput(int index, bool value)
    {
        _digitalInputs[index] = value;
        _engines?.Io.SetPort($"{DeviceName}#DI{index}", value);
    }

    public bool GetDigitalInput(int index)
    {
        return _digitalInputs.TryGetValue(index, out bool val) && val;
    }

    private void ExecuteSetDOCommand(string parameter, DriverTicket ticket)
    {
        var doArgs = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        if (doArgs.TryGetValue("Index", out var doIdx) && int.TryParse(doIdx, out int dIdx) &&
            doArgs.TryGetValue("Value", out var doVal))
        {
            bool bVal = doVal.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                        doVal.Equals("On", StringComparison.OrdinalIgnoreCase) ||
                        (int.TryParse(doVal, out int iVal) && iVal != 0);
            SetDigitalOutput(dIdx, bVal);
            ticket.SetCompleted(DriverResponse.Ok(bVal));
        }
        else if (posArgs.Count >= 2 && int.TryParse(posArgs[^2], out int pIdx))
        {
            string vStr = posArgs[^1];
            bool bVal = vStr.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                        vStr.Equals("On", StringComparison.OrdinalIgnoreCase) ||
                        (int.TryParse(vStr, out int iVal) && iVal != 0);
            SetDigitalOutput(pIdx, bVal);
            ticket.SetCompleted(DriverResponse.Ok(bVal));
        }
        else
        {
            ticket.SetFailed("INVALID_PARAM", "SetDO requires Index and Value.");
        }
    }

    private void ExecuteGetDICommand(string parameter, DriverTicket ticket)
    {
        var diArgs = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        if (diArgs.TryGetValue("Index", out var diIdx) && int.TryParse(diIdx, out int iIdx))
        {
            bool val = GetDigitalInput(iIdx);
            ticket.SetCompleted(DriverResponse.Ok(val));
        }
        else if (posArgs.Count >= 1 && int.TryParse(posArgs[^1], out int pIdx))
        {
            bool val = GetDigitalInput(pIdx);
            ticket.SetCompleted(DriverResponse.Ok(val));
        }
        else
        {
            ticket.SetFailed("INVALID_PARAM", "GetDI requires Index.");
        }
    }

    private void ExecuteGetDOCommand(string parameter, DriverTicket ticket)
    {
        var gDoArgs = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        if (gDoArgs.TryGetValue("Index", out var gIdx) && int.TryParse(gIdx, out int gdIdx))
        {
            bool val = GetDigitalOutput(gdIdx);
            ticket.SetCompleted(DriverResponse.Ok(val));
        }
        else if (posArgs.Count >= 1 && int.TryParse(posArgs[^1], out int pIdx))
        {
            bool val = GetDigitalOutput(pIdx);
            ticket.SetCompleted(DriverResponse.Ok(val));
        }
        else
        {
            ticket.SetFailed("INVALID_PARAM", "GetDO requires Index.");
        }
    }

    #endregion

    /// <summary>
    /// Driver 自主解析點位與判定是否為 #Joint 關節座標：
    /// 支援：
    /// 1. 原始腳本參數："$motor, $speed, $point" 或 "$motor, $speed, ($X,$Y,$Z,$U,$V,$W)"
    /// 2. 直接點位名稱："P1"、"P1#Joint"、"Tray[2]"
    /// 3. Key=Value 座標："Speed=100,X=10,Y=20..." (可含 Coord=Joint)
    /// </summary>
    private MachinePoint ResolvePointFromTicketParam(string rawParam, out bool isJoint, out string resolvedLabel)
    {
        isJoint = false;
        resolvedLabel = rawParam?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawParam)) return MachinePoint.AllInf();

        string trimmed = rawParam.Trim();

        // 情況 A：Key=Value 格式 (如 GoSafe 第 1 階段 "Speed=100,Z=0" 或直接指定 X=...,Y=...)
        if (trimmed.Contains("X=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Y=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Z=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("U=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("V=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("W=", StringComparison.OrdinalIgnoreCase))
        {
            var pt = ParsePointFromParam(trimmed, out bool kvJoint);
            isJoint = kvJoint;
            return pt;
        }

        // 情況 B：檢查是否含括號座標 "$motor, $speed, (x,y,z,...)"
        int openParen = trimmed.IndexOf('(');
        int closeParen = trimmed.LastIndexOf(')');
        if (openParen >= 0 && closeParen > openParen)
        {
            string prefix = trimmed.Substring(0, openParen).Trim();
            // 若是 P1@Z(50) 這種軸覆寫語法，不是直接括號座標
            if (prefix.EndsWith('@') || (prefix.Contains('@') && !prefix.EndsWith(',')))
            {
                // 交給下方點位名稱解析
            }
            else
            {
                string coordStr = trimmed.Substring(openParen, closeParen - openParen + 1);
                var pt = MachinePoint.ParseCoords(coordStr);
                isJoint = IsJointPoint(pt, coordStr);
                resolvedLabel = coordStr;
                return pt;
            }
        }

        // 情況 C：拆解位置參數 (例如 "$motor, $speed, $point" 或 "$point")
        ArgParaHelper.ParseKeyValueArgs(trimmed, out var posArgs);
        string pointExpr = trimmed;
        if (posArgs.Count >= 3 && string.Equals(posArgs[0], DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            pointExpr = posArgs[2];
        }
        else if (posArgs.Count == 2 && string.Equals(posArgs[0], DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            pointExpr = posArgs[1];
        }
        else if (posArgs.Count >= 1)
        {
            pointExpr = posArgs[^1];
        }

        resolvedLabel = pointExpr.Trim();
        if (_engines != null && !string.IsNullOrEmpty(resolvedLabel))
        {
            var pt = _engines.Point.GetPoint(resolvedLabel);
            isJoint = IsJointPoint(pt, resolvedLabel);
            if (isJoint)
            {
                pt.H["Coord"] = "Joint";
            }
            return pt;
        }

        isJoint = resolvedLabel.Contains("#Joint", StringComparison.OrdinalIgnoreCase);
        return MachinePoint.AllInf();
    }

    private static bool IsJointPoint(MachinePoint pt, string? pointName)
    {
        if (!string.IsNullOrWhiteSpace(pointName) &&
            pointName.Contains("#Joint", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string coordTag = pt.GetHString("Coord");
        if (coordTag.Equals("Joint", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string valTag = pt.GetHString("Value");
        if (valTag.Equals("Joint", StringComparison.OrdinalIgnoreCase) ||
            valTag.Equals("#Joint", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private void RecordMoveMode(string moveMode, string targetLabel)
    {
        if (_engines != null && !string.IsNullOrEmpty(DeviceName))
        {
            _engines.Variable.Set($"{DeviceName}#LastMoveMode", moveMode == "JOINT_MOVE" ? "Joint" : "Cartesian", "Global");
            _engines.Record.Record("SYSTEMLOG", $"[MockMotion:{DeviceName}] [{moveMode}] Target='{targetLabel}' Tool='{CurrentToolName}'");
        }
    }

    private MachinePoint ParseGoCommandParam(string rawParam)
    {
        if (rawParam.Contains("X=", StringComparison.OrdinalIgnoreCase) ||
            rawParam.Contains("Y=", StringComparison.OrdinalIgnoreCase) ||
            rawParam.Contains("Z=", StringComparison.OrdinalIgnoreCase))
        {
            return ParsePointFromParam(rawParam, out _);
        }

        // 支援原始 "$motor, $axis, $speed, $X(,$Y,$Z,...)"
        ArgParaHelper.ParseKeyValueArgs(rawParam, out var posArgs);
        var pt = MachinePoint.AllInf();
        if (posArgs.Count >= 4)
        {
            string axis = posArgs[1].ToUpperInvariant();
            int valIdx = 3;
            char[] axisOrder = { 'X', 'Y', 'Z', 'U', 'V', 'W' };
            foreach (char ax in axisOrder)
            {
                if (axis.Contains(ax) && valIdx < posArgs.Count &&
                    double.TryParse(posArgs[valIdx], NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                {
                    switch (ax)
                    {
                        case 'X': pt.X = v; break;
                        case 'Y': pt.Y = v; break;
                        case 'Z': pt.Z = v; break;
                        case 'U': pt.U = v; break;
                        case 'V': pt.V = v; break;
                        case 'W': pt.W = v; break;
                    }
                    valIdx++;
                }
            }
        }
        return pt;
    }

    private string ExtractNativePayload(string rawParameter)
    {
        if (string.IsNullOrWhiteSpace(rawParameter)) return string.Empty;
        string trimmed = rawParameter.Trim();
        int commaIdx = trimmed.IndexOf(',');
        if (commaIdx > 0)
        {
            string firstToken = trimmed.Substring(0, commaIdx).Trim();
            if (string.Equals(firstToken, DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Substring(commaIdx + 1).Trim();
            }
        }
        return trimmed;
    }

    /// <summary>
    /// Inf 軸屏蔽比對：支援笛卡爾基準或 Joint 關節基準
    /// </summary>
    private void ApplyInfAxisMask(MachinePoint target, bool isJoint)
    {
        var basePos = isJoint ? CurrentJointPos : CurrentPos;
        ToGoPos.X = !double.IsInfinity(target.X) ? target.X : basePos.X;
        ToGoPos.Y = !double.IsInfinity(target.Y) ? target.Y : basePos.Y;
        ToGoPos.Z = !double.IsInfinity(target.Z) ? target.Z : basePos.Z;
        ToGoPos.U = !double.IsInfinity(target.U) ? target.U : basePos.U;
        ToGoPos.V = !double.IsInfinity(target.V) ? target.V : basePos.V;
        ToGoPos.W = !double.IsInfinity(target.W) ? target.W : basePos.W;

        _engines?.Point.SetPoint($"{DeviceName}_ToGo", ToGoPos);
    }

    /// <summary>
    /// 模擬平滑插補運動 (依據 isJoint 決定更新關節座標或笛卡爾座標，並同步另一座標系)
    /// </summary>
    private async Task SimulateInterpolationAsync(bool isJoint, CancellationToken token)
    {
        int steps = 5;
        var activePos = isJoint ? CurrentJointPos : CurrentPos;

        double dx = (ToGoPos.X - activePos.X) / steps;
        double dy = (ToGoPos.Y - activePos.Y) / steps;
        double dz = (ToGoPos.Z - activePos.Z) / steps;
        double du = (ToGoPos.U - activePos.U) / steps;
        double dv = (ToGoPos.V - activePos.V) / steps;
        double dw = (ToGoPos.W - activePos.W) / steps;

        for (int i = 0; i < steps; i++)
        {
            if (token.IsCancellationRequested) break;
            activePos.X += dx;
            activePos.Y += dy;
            activePos.Z += dz;
            activePos.U += du;
            activePos.V += dv;
            activePos.W += dw;

            if (isJoint) SyncCartesianFromJoint();
            else SyncJointFromCartesian();

            UpdateEngines();
            MotionStateUpdated?.Invoke();
            await Task.Delay(15, token);
        }

        activePos.X = ToGoPos.X;
        activePos.Y = ToGoPos.Y;
        activePos.Z = ToGoPos.Z;
        activePos.U = ToGoPos.U;
        activePos.V = ToGoPos.V;
        activePos.W = ToGoPos.W;

        if (isJoint) SyncCartesianFromJoint();
        else SyncJointFromCartesian();

        UpdateEngines();
    }

    private void SyncJointFromCartesian()
    {
        CurrentJointPos.X = CurrentPos.X;
        CurrentJointPos.Y = CurrentPos.Y;
        CurrentJointPos.Z = CurrentPos.Z;
        CurrentJointPos.U = CurrentPos.U;
        CurrentJointPos.V = CurrentPos.V;
        CurrentJointPos.W = CurrentPos.W;
        CurrentJointPos.H["Coord"] = "Joint";
    }

    private void SyncCartesianFromJoint()
    {
        CurrentPos.X = CurrentJointPos.X;
        CurrentPos.Y = CurrentJointPos.Y;
        CurrentPos.Z = CurrentJointPos.Z;
        CurrentPos.U = CurrentJointPos.U;
        CurrentPos.V = CurrentJointPos.V;
        CurrentPos.W = CurrentJointPos.W;
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        UpdateEngines();
    }

    private void UpdateEngines()
    {
        if (_engines != null && !string.IsNullOrEmpty(DeviceName))
        {
            _engines.Io.SetPort($"{DeviceName}#Busy?", IsBusy);
            _engines.Io.SetPort($"{DeviceName}#Done?", !IsBusy);
            _engines.Io.SetPort($"{DeviceName}#Homed?", IsHomed);
            _engines.Io.SetPort($"{DeviceName}#Alarm?", IsAlarm);

            var cartClone = CurrentPos.Clone();
            var jointClone = CurrentJointPos.Clone();
            jointClone.H["Coord"] = "Joint";

            _engines.Point.SetPoint($"{DeviceName}_CurPos", cartClone);
            _engines.Point.SetPoint($"{DeviceName}_CurJoint", jointClone);
            _engines.Point.SetPoint(DeviceName, cartClone);
            _engines.Point.SetPoint($"{DeviceName}#Joint", jointClone);
        }
    }

    private static MachinePoint ParsePointFromParam(string param, out bool isJoint)
    {
        var pt = MachinePoint.AllInf();
        var args = ArgParaHelper.ParseKeyValueArgs(param, out _);
        if (args.TryGetValue("X", out var x) && double.TryParse(x, NumberStyles.Any, CultureInfo.InvariantCulture, out double dx)) pt.X = dx;
        if (args.TryGetValue("Y", out var y) && double.TryParse(y, NumberStyles.Any, CultureInfo.InvariantCulture, out double dy)) pt.Y = dy;
        if (args.TryGetValue("Z", out var z) && double.TryParse(z, NumberStyles.Any, CultureInfo.InvariantCulture, out double dz)) pt.Z = dz;
        if (args.TryGetValue("U", out var u) && double.TryParse(u, NumberStyles.Any, CultureInfo.InvariantCulture, out double du)) pt.U = du;
        if (args.TryGetValue("V", out var v) && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out double dv)) pt.V = dv;
        if (args.TryGetValue("W", out var w) && double.TryParse(w, NumberStyles.Any, CultureInfo.InvariantCulture, out double dw)) pt.W = dw;

        isJoint = args.TryGetValue("Coord", out var coordVal) &&
                  coordVal.Equals("Joint", StringComparison.OrdinalIgnoreCase);
        if (isJoint)
        {
            pt.H["Coord"] = "Joint";
        }
        return pt;
    }

    public void Shutdown()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        Shutdown();
        _cts?.Dispose();
    }
}
