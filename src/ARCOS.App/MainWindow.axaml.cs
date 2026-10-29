using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Window = Avalonia.Controls.Window;
using ARCOS.App.Controls;
using ARCOS.App.ViewModels;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using ARCOS.Core.Execution;
using ARCOS.Core.Model;
using OpenCvSharp;
using ScottPlot.Avalonia;

namespace ARCOS.App;

public partial class MainWindow : Window
{
    private readonly BasicEngines _engines;
    private readonly DriverManager _driverManager;
    private readonly ArcosThreadVM _vm;
    private readonly DispatcherTimer _uiTimer;
    private readonly ObservableCollection<IoChannelViewModel> _ioChannels = new();
    private object? _defaultSubPanelContent;
    private object? _cachedIoFrontPanel;

    public MainWindow()
    {
        InitializeComponent();

        // 1. 初始化底層 8 大非重入 Headless 引擎
        var io = new IoEngine();
        var varEng = new VariableEngine();
        var pt = new PointEngine(io);
        var img = new ImageEngine(io);
        var daq = new DaqEngine(io);
        var timer = new TimerEngine(io);

        _engines = new BasicEngines(io, varEng, pt, img, daq, timer);
        _driverManager = new DriverManager(_engines);

        // 2. 註冊預設 4 大 Mock 硬體驅動
        RegisterDefaultMockDrivers();

        // 3. 建立多執行緒排程虛擬機
        _vm = new ArcosThreadVM(_engines, _driverManager);

        // 4. 掛載 Sequencer Editor
        var seqEditor = this.FindControl<SequencerEditorControl>("SequencerEditor");
        seqEditor?.AttachVM(_vm);

        // 5. 初始化 Capture 頁面的無限點位虛擬化 IO 列表 (512 實體點 + 恆常虛擬 IO)
        InitializeIoChannelList();

        _engines.Io.IoChanged += (port, alias, val) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var item = _ioChannels.FirstOrDefault(x => string.Equals(x.Port, port, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    item.State = val;
                    if (!string.IsNullOrEmpty(alias) && item.Name == item.Port)
                    {
                        item.Name = alias;
                    }
                }
            });
        };

        // 6. 綁定 USER TAB 與 CAPTURE 交互事件與 SubPanel 動態調度
        BindUserTabControls();
        BindCaptureTabControls();
        BindIoModulesTab();

        // 7. UI 定時刷新計時器 (50ms，驅動即時狀態更新)
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        // 8. 監聽執行緒即時日誌流，推送到 USER TAB 的 Log 視窗
        _vm.MainThreads[0].OnLog += (entry, isErr) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var txtLogs = this.FindControl<TextBox>("TxtUserLogs");
                if (txtLogs != null)
                {
                    txtLogs.Text += entry + Environment.NewLine;
                    txtLogs.CaretIndex = txtLogs.Text.Length;
                }
            });
        };

        // 初始系統就緒記錄
        _vm.MainThreads[0].Log("[INFO] ARCOS System Ready. Program Table Loaded.");

        // 預設切換至首頁 USER TAB 產線核心操作畫面
        this.Loaded += (_, _) =>
        {
            var tab = this.FindControl<TabItem>("TabUser");
            if (tab != null)
            {
                tab.IsSelected = true;
            }
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() =>
        {
            var mainTab = this.FindControl<TabControl>("MainTabControl");
            if (mainTab != null)
            {
                mainTab.SelectedIndex = 0;
            }
        });
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void RegisterDefaultMockDrivers()
    {
        try
        {
            _driverManager.AddDevice("IO", "StationIO", "MockIO", "StartPort=P0.0,BitCount=64,Interval=25", _engines);
        }
        catch (Exception ex)
        {
            _vm.MainThreads[0].Log($"[WARN] Failed to load dynamic MockIO: {ex.Message}", isError: true);
        }

        try
        {
            _driverManager.AddDevice("Motion", "RobotGantry", "MockMotion", "Mechanism=Gantry6Axis", _engines);
        }
        catch (Exception ex)
        {
            _vm.MainThreads[0].Log($"[WARN] Failed to load dynamic MockMotion: {ex.Message}", isError: true);
        }

        try
        {
            _driverManager.AddDevice("Capture", "CamTop", "MockCamera", "CamIndex=0,Res=640x480", _engines);
        }
        catch (Exception ex)
        {
            _vm.MainThreads[0].Log($"[WARN] Failed to load dynamic MockCamera: {ex.Message}", isError: true);
        }

        try
        {
            _driverManager.AddDevice("DAQ", "PressSensor", "MockDAQ", "Rate=1000,Channels=1", _engines);
        }
        catch (Exception ex)
        {
            _vm.MainThreads[0].Log($"[WARN] Failed to load dynamic MockDAQ: {ex.Message}", isError: true);
        }


        // 設定預設教導點位
        _engines.Point.SetPoint("P_PICK", new MachinePoint(100.0, 200.0, -50.0, 0, 0, 0));
        _engines.Point.SetPoint("P_PLACE", new MachinePoint(350.0, 200.0, -50.0, 0, 0, 0));
        _engines.Point.SetPoint("P_STANDBY", new MachinePoint(0.0, 0.0, 0.0, 0, 0, 0));
        _engines.Point.SetPoint("CURRENT", new MachinePoint(12.350, 45.680, -10.200, 0, 0, 0));

        // 設定相機預設 2 點校正座標 (原點 P0, 尺度 P1: 0.05 mm/pixel)
        _engines.Image.SetCoord("CamTop", new[]
        {
            new MachinePoint(0, 0, 0, 0, 0, 0),
            new MachinePoint(100, 100, 0.05, 0, 0, 0)
        });

        // 填入 Capture 清單
        var lstCam = this.FindControl<ListBox>("LstCaptureCameras");
        lstCam?.Items.Add("CAM0 (CamTop)");

        var lstMot = this.FindControl<ListBox>("LstCaptureMotors");
        lstMot?.Items.Add("RobotGantry");

        var cmbPts = this.FindControl<ComboBox>("CmbMotionPoints");
        if (cmbPts != null)
        {
            cmbPts.ItemsSource = new[] { "P_PICK", "P_PLACE", "P_STANDBY" };
            cmbPts.SelectedIndex = 0;
        }
    }

    private void InitializeIoChannelList()
    {
        var lst = this.FindControl<ListBox>("LstIoChannels");
        _ioChannels.Clear();

        // 1. 常駐虛擬 IO (TRUE 永真 / FALSE 永假，不可覆寫之硬體/邏輯常數)
        _ioChannels.Add(new IoChannelViewModel
        {
            Port = "TRUE",
            Name = "Virtual Logic HIGH (1)",
            State = true,
            IsVisible = true
        });

        _ioChannels.Add(new IoChannelViewModel
        {
            Port = "FALSE",
            Name = "Virtual Logic LOW (0)",
            State = false,
            IsVisible = true
        });

        // 2. 預設初始化 512 個實體 IO 點位 (P0.0 ~ P63.7，共 64 Ports x 8 Bits)
        for (int port = 0; port < 64; port++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                string portName = $"P{port}.{bit}";
                string alias = portName switch
                {
                    "P0.0" => "Safety Door Switch",
                    "P0.1" => "Air Pressure Sensor",
                    "P0.2" => "Part In Feed Detector",
                    "P0.3" => "Align Cylinder Fwd",
                    "P0.4" => "Align Cylinder Back",
                    "P0.5" => "Vacuum Sensor 1",
                    "P0.6" => "Vacuum Sensor 2",
                    "P0.7" => "Camera Trigger Out",
                    "P1.0" => "Feeder Motor Ready",
                    "P1.1" => "Discharge Sensor",
                    "P1.2" => "Reject Cylinder",
                    "P1.3" => "Tower Lamp Green",
                    "P1.4" => "Tower Lamp Yellow",
                    "P1.5" => "Tower Lamp Red",
                    "P1.6" => "Buzzer Alarm",
                    "P1.7" => "Ionizer Blower",
                    "P2.0" => "Conveyor Motor Run",
                    "P2.1" => "Gripper Open Sensor",
                    "P2.2" => "Gripper Close Sensor",
                    "P2.3" => "Station Index Proximity",
                    "P2.4" => "Light Source Ch1",
                    "P2.5" => "Light Source Ch2",
                    "P2.6" => "Calibration Target",
                    "P2.7" => "Laser Pointer Out",
                    _ => portName
                };

                if (alias != portName)
                {
                    _engines.Io.SetPortName(portName, alias);
                }

                _ioChannels.Add(new IoChannelViewModel
                {
                    Port = portName,
                    Name = alias,
                    State = _engines.Io.GetPort(portName),
                    IsVisible = true
                });
            }
        }

        if (lst != null)
        {
            lst.ItemsSource = _ioChannels;
        }
    }

    private void BindUserTabControls()
    {
        // 1. PAUSE 按鈕 (Prog 95)
        var btnPause = this.FindControl<Button>("BtnUserPause");
        if (btnPause != null)
        {
            btnPause.Click += (_, _) =>
            {
                bool isPaused = _engines.Timer.IsSystemPaused;
                _engines.Timer.SetSystemPause(!isPaused);
                _vm.TriggerSystemHook(!isPaused ? 95 : 94);
            };
        }

        // 2. !!STOP!! 緊急停機按鈕 (Prog 99)
        var btnStop = this.FindControl<Button>("BtnUserStop");
        if (btnStop != null)
        {
            btnStop.Click += (_, _) =>
            {
                _vm.StopAll();
                _vm.TriggerSystemHook(99);
                _vm.MainThreads[0].Log("[EMG] Emergency Stop triggered by User Button!!", isError: true);
            };
        }

        // 3. SubPanel CSVI 虛擬儀表動態掛載調度
        var container = this.FindControl<ContentControl>("UserFrontPanelContainer");
        if (container != null && _defaultSubPanelContent == null)
        {
            _defaultSubPanelContent = container.Content;
        }

        var cmbSubPanel = this.FindControl<ComboBox>("CmbSubPanelSelect");
        var btnMountIo = this.FindControl<Button>("BtnMountIoPanel");
        var btnMountDriver = this.FindControl<Button>("BtnMountDriverPanel");
        var btnReset = this.FindControl<Button>("BtnResetSubPanel");

        Action<int> switchSubPanel = (idx) =>
        {
            if (container == null) return;
            switch (idx)
            {
                case 0: // Default Station HMI
                    container.Content = _defaultSubPanelContent;
                    break;
                case 1: // IO Dashboard (IO.csvi Front Panel)
                    _cachedIoFrontPanel ??= ARCOS.App.Guidance.ParameterGuidanceManager.Instance.GetFrontPanel("IO@SetPort")
                                          ?? ARCOS.App.Guidance.ParameterGuidanceManager.Instance.GetFrontPanel("IO");
                    if (_cachedIoFrontPanel != null)
                    {
                        container.Content = _cachedIoFrontPanel;
                        if (_cachedIoFrontPanel is Avalonia.Controls.Control ctrl && ctrl.DataContext != null)
                        {
                            var method = ctrl.DataContext.GetType().GetMethod("RefreshFromEngines");
                            method?.Invoke(ctrl.DataContext, new object[] { _engines });
                        }
                    }
                    break;
                case 2: // MockIO Driver Panel (MockIO.csvi)
                    var driver = _driverManager.GetDevice("IO", "StationIO");
                    var driverView = driver?.CreateDriverView();
                    if (driverView != null)
                    {
                        container.Content = driverView;
                    }
                    break;
            }
        };

        if (cmbSubPanel != null)
        {
            cmbSubPanel.SelectionChanged += (_, _) =>
            {
                switchSubPanel(cmbSubPanel.SelectedIndex);
            };
        }

        if (btnMountIo != null)
        {
            btnMountIo.Click += (_, _) =>
            {
                if (cmbSubPanel != null) cmbSubPanel.SelectedIndex = 1;
                else switchSubPanel(1);
            };
        }

        if (btnMountDriver != null)
        {
            btnMountDriver.Click += (_, _) =>
            {
                if (cmbSubPanel != null) cmbSubPanel.SelectedIndex = 2;
                else switchSubPanel(2);
            };
        }

        if (btnReset != null)
        {
            btnReset.Click += (_, _) =>
            {
                if (cmbSubPanel != null) cmbSubPanel.SelectedIndex = 0;
                else switchSubPanel(0);
            };
        }
    }

    private void BindIoModulesTab()
    {
        var lstDrivers = this.FindControl<ListBox>("LstIoDrivers");
        var driverContainer = this.FindControl<ContentControl>("DriverViewContainer");
        if (lstDrivers == null) return;

        var available = _driverManager.Loader.GetAvailableDrivers("IO");
        lstDrivers.ItemsSource = available.Count > 0 ? available : new List<string> { "MockIO" };

        lstDrivers.SelectionChanged += (_, _) =>
        {
            if (lstDrivers.SelectedItem is string driverName && driverContainer != null)
            {
                var driver = _driverManager.GetDevice("IO", "StationIO");
                if (driver != null && driver.DriverType.Equals(driverName, StringComparison.OrdinalIgnoreCase))
                {
                    driverContainer.Content = driver.CreateDriverView();
                }
                else
                {
                    var newDriver = _driverManager.Loader.LoadDriver("IO", driverName);
                    if (newDriver != null)
                    {
                        newDriver.Init(driverName, "", _engines);
                        driverContainer.Content = newDriver.CreateDriverView();
                    }
                }
            }
        };

        if (lstDrivers.Items.Count > 0 || available.Count > 0)
        {
            lstDrivers.SelectedIndex = 0;
        }
    }

    private void BindCaptureTabControls()
    {
        // 1. Snap 拍照按鈕
        var btnSnap = this.FindControl<Button>("BtnCaptureSnap");
        if (btnSnap != null)
        {
            btnSnap.Click += (_, _) => PerformSnap();
        }

        // 2. 寸動控制 (Jog Minus / Plus)
        var btnMinus = this.FindControl<Button>("BtnJogMinus");
        if (btnMinus != null)
        {
            btnMinus.Click += (_, _) =>
            {
                var cmbAxis = this.FindControl<ComboBox>("CmbJogAxis");
                var cmbStep = this.FindControl<ComboBox>("CmbJogStep");
                string axis = (cmbAxis?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "X";
                string step = (cmbStep?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "1.00";
                var driver = _driverManager.GetDriver("Motion", "RobotGantry");
                driver?.PostTicket(new DriverTicket("Jog", $"Axis={axis},Step=-{step}"));
                _vm.MainThreads[0].Log($"[Motion] JOG <<-- Axis={axis}, Step=-{step}");
            };
        }

        var btnPlus = this.FindControl<Button>("BtnJogPlus");
        if (btnPlus != null)
        {
            btnPlus.Click += (_, _) =>
            {
                var cmbAxis = this.FindControl<ComboBox>("CmbJogAxis");
                var cmbStep = this.FindControl<ComboBox>("CmbJogStep");
                string axis = (cmbAxis?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "X";
                string step = (cmbStep?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "1.00";
                var driver = _driverManager.GetDriver("Motion", "RobotGantry");
                driver?.PostTicket(new DriverTicket("Jog", $"Axis={axis},Step=+{step}"));
                _vm.MainThreads[0].Log($"[Motion] JOG ++>> Axis={axis}, Step=+{step}");
            };
        }

        // 3. GetPos 讀取座標並回填目標格
        var btnGetPos = this.FindControl<Button>("BtnGetPos");
        if (btnGetPos != null)
        {
            btnGetPos.Click += (_, _) =>
            {
                var pt = _engines.Point.GetPoint("CURRENT");
                var txtTarget = this.FindControl<TextBox>("TxtMotionTarget");
                if (txtTarget != null)
                {
                    txtTarget.Text = $"{pt.X:F2}, {pt.Y:F2}, {pt.Z:F2}";
                }
                _vm.MainThreads[0].Log($"[Motion] GetPos -> ({pt.X:F3}, {pt.Y:F3}, {pt.Z:F3})");
            };
        }

        // 4. GO 目標定位運動
        var btnMotionGo = this.FindControl<Button>("BtnMotionGo");
        if (btnMotionGo != null)
        {
            btnMotionGo.Click += (_, _) =>
            {
                var txtTarget = this.FindControl<TextBox>("TxtMotionTarget");
                var txtSpeed = this.FindControl<TextBox>("TxtMotionSpeed");
                string targetStr = txtTarget?.Text ?? "0.0, 0.0, 0.0";
                string speedStr = txtSpeed?.Text ?? "20.0";
                var driver = _driverManager.GetDriver("Motion", "RobotGantry");
                driver?.PostTicket(new DriverTicket("AbsMove", $"Pos={targetStr},Speed={speedStr}"));
                _vm.MainThreads[0].Log($"[Motion] GO AbsMove -> Pos={targetStr}, Speed={speedStr}");
            };
        }

        // 5. Motion Stop 煞車停止
        var btnMotionStop = this.FindControl<Button>("BtnMotionStop");
        if (btnMotionStop != null)
        {
            btnMotionStop.Click += (_, _) =>
            {
                var driver = _driverManager.GetDriver("Motion", "RobotGantry");
                driver?.PostTicket(new DriverTicket("Stop"));
                _vm.MainThreads[0].Log("[Motion] Motion STOP command executed.");
            };
        }

        // 6. Go Point 點位跳轉
        var btnGoPoint = this.FindControl<Button>("BtnGoPoint");
        if (btnGoPoint != null)
        {
            btnGoPoint.Click += (_, _) =>
            {
                var cmb = this.FindControl<ComboBox>("CmbMotionPoints");
                if (cmb?.SelectedItem is string ptName)
                {
                    var driver = _driverManager.GetDriver("Motion", "RobotGantry");
                    driver?.PostTicket(new DriverTicket("GoPoint", $"Point={ptName},Speed=20.0"));
                    _vm.MainThreads[0].Log($"[Motion] GoPoint dispatched -> {ptName}");
                }
            };
        }

        // 7. Go Pallet 料盤跳轉
        var btnGoPallet = this.FindControl<Button>("BtnGoPallet");
        if (btnGoPallet != null)
        {
            btnGoPallet.Click += (_, _) =>
            {
                var driver = _driverManager.GetDriver("Motion", "RobotGantry");
                driver?.PostTicket(new DriverTicket("GoPallet", "Tray=0,Index=1,Speed=20.0"));
                _vm.MainThreads[0].Log("[Motion] Go Pallet dispatched -> Tray 0, Index 1");
            };
        }

        // 觸發初始單幀預覽
        PerformSnap();
    }

    private void PerformSnap()
    {
        var driver = _driverManager.GetDriver("Capture", "CamTop");
        if (driver != null)
        {
            var ticket = new DriverTicket("Snap");
            driver.PostTicket(ticket);
            ticket.Wait(500);

            var frame = _engines.Image.GetFrame("CamTop");
            if (frame != null && !frame.Image.IsDisposed)
            {
                var imgView = this.FindControl<Image>("ImgLivePreview");
                if (imgView != null)
                {
                    Cv2.ImEncode(".bmp", frame.Image, out byte[] buf);
                    using var ms = new MemoryStream(buf);
                    imgView.Source = new Bitmap(ms);
                }
            }
        }
    }

    // =========================================================
    // Capture 交互事件處理常式
    // =========================================================

    public void OnIoFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cmb && cmb.SelectedItem is ComboBoxItem item)
        {
            string content = item.Content?.ToString() ?? "";

            Func<string, bool> predicate = content switch
            {
                var s when s.Contains("Virtual IOs") => port => port == "TRUE" || port == "FALSE" || !port.StartsWith("P", StringComparison.OrdinalIgnoreCase),
                var s when s.StartsWith("P0.* (") => port => port.StartsWith("P0.", StringComparison.OrdinalIgnoreCase),
                var s when s.StartsWith("P1.* (") => port => port.StartsWith("P1.", StringComparison.OrdinalIgnoreCase),
                var s when s.StartsWith("P2.* (") => port => port.StartsWith("P2.", StringComparison.OrdinalIgnoreCase),
                var s when s.StartsWith("P3.* (") => port => port.StartsWith("P3.", StringComparison.OrdinalIgnoreCase),
                var s when s.Contains("Bank 0") => port => IsInPortRange(port, 0, 7),
                var s when s.Contains("Bank 1") => port => IsInPortRange(port, 8, 15),
                var s when s.Contains("Bank 2") => port => IsInPortRange(port, 16, 31),
                var s when s.Contains("Bank 3") => port => IsInPortRange(port, 32, 63),
                _ => _ => true // All IO (512 Points + Virtual)
            };

            foreach (var ch in _ioChannels)
            {
                ch.IsVisible = predicate(ch.Port);
            }
        }
    }

    private static bool IsInPortRange(string portStr, int minPort, int maxPort)
    {
        if (!portStr.StartsWith("P", StringComparison.OrdinalIgnoreCase)) return false;
        int dotIdx = portStr.IndexOf('.');
        if (dotIdx > 1 && int.TryParse(portStr.AsSpan(1, dotIdx - 1), out int p))
        {
            return p >= minPort && p <= maxPort;
        }
        return false;
    }

    public void OnIoBitToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string port)
        {
            if (string.Equals(port, "TRUE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(port, "FALSE", StringComparison.OrdinalIgnoreCase))
            {
                _vm.MainThreads[0].Log($"[IO] Constant Virtual IO '{port}' is immutable.", isError: false);
                return;
            }

            bool current = _engines.Io.GetPort(port);
            _engines.Io.SetPort(port, !current);
            var ch = _ioChannels.FirstOrDefault(x => x.Port == port);
            if (ch != null) ch.State = !current;
            _vm.MainThreads[0].Log($"[IO] Bit toggle {port} -> {(!current ? "1 (ON)" : "0 (OFF)")}");
        }
    }

    public void OnImagePointerMoved(object? sender, PointerEventArgs e)
    {
        var img = sender as Image;
        if (img == null) return;

        var pos = e.GetPosition(img);
        var txtPos = this.FindControl<TextBlock>("TxtImaqPosition");
        if (txtPos == null) return;

        double scaleX = img.Bounds.Width > 0 ? 640.0 / img.Bounds.Width : 1.0;
        double scaleY = img.Bounds.Height > 0 ? 480.0 / img.Bounds.Height : 1.0;

        double px = Math.Clamp(pos.X * scaleX, 0, 640);
        double py = Math.Clamp(pos.Y * scaleY, 0, 480);

        var (wx, wy, isCalib) = _engines.Image.PixelToWorld("CamTop", px, py);
        if (isCalib)
        {
            txtPos.Text = $"IMAQPosition: ({wx:F3} mm, {wy:F3} mm) [Calibrated]";
        }
        else
        {
            txtPos.Text = $"IMAQPosition: ({px:F0} px, {py:F0} px) [Uncalibrated px]";
        }
    }

    public void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var img = sender as Image;
        if (img == null) return;

        var pos = e.GetPosition(img);
        double scaleX = img.Bounds.Width > 0 ? 640.0 / img.Bounds.Width : 1.0;
        double scaleY = img.Bounds.Height > 0 ? 480.0 / img.Bounds.Height : 1.0;

        double px = Math.Clamp(pos.X * scaleX, 0, 640);
        double py = Math.Clamp(pos.Y * scaleY, 0, 480);

        var (wx, wy, isCalib) = _engines.Image.PixelToWorld("CamTop", px, py);
        var txtSelect = this.FindControl<TextBox>("TxtImaqSelectPoint");
        if (txtSelect != null)
        {
            txtSelect.Text = isCalib ? $"({wx:F3} mm, {wy:F3} mm)" : $"({px:F0} px, {py:F0} px)";
        }

        // 記錄至系統點位與日誌
        _engines.Point.SetPoint("IMAQSelectPoint", new MachinePoint(wx, wy, 0, 0, 0, 0));
        _vm.MainThreads[0].Log($"[Capture] Target Point Selected: {(isCalib ? $"{wx:F3}mm, {wy:F3}mm" : $"{px:F0}px, {py:F0}px")}");
    }

    public void OnToolSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tool)
        {
            var txtRoi = this.FindControl<TextBox>("TxtRoiString");
            if (txtRoi == null) return;

            string dsl = tool switch
            {
                "Line" => "Line(100, 150, 500, 150)",
                "Rect" => "Rect(120, 80, 400, 300)",
                "RotatedRect" => "RotatedRect(320, 240, 250, 120, 35.0)",
                "Circle" => "Circle(320, 240, 85)",
                "Annulus" => "Annulus(320, 240, 50, 95)",
                "Crosshair" => "Crosshair(320, 240, 25)",
                _ => txtRoi.Text ?? string.Empty
            };

            txtRoi.Text = dsl;
            _engines.Image.SetRoi("CamTop", dsl);
            _vm.MainThreads[0].Log($"[ROI VDM] Tool '{tool}' applied -> {dsl}");
        }
    }

    public void OnRoiStringKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var txtRoi = this.FindControl<TextBox>("TxtRoiString");
            if (txtRoi != null && !string.IsNullOrWhiteSpace(txtRoi.Text))
            {
                _engines.Image.SetRoi("CamTop", txtRoi.Text ?? string.Empty);
                _vm.MainThreads[0].Log($"[ROI VDM] ROI String updated -> {txtRoi.Text}");
            }
        }
    }

    public void OnMotionPointSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cmb && cmb.SelectedItem is string ptName)
        {
            var pt = _engines.Point.GetPoint(ptName);
            var txtTarget = this.FindControl<TextBlock>("TxtTargetPointCoords");
            if (txtTarget != null)
            {
                txtTarget.Text = $"Target: X {pt.X:F1}  Y {pt.Y:F1}  Z {pt.Z:F1}  U {pt.U:F1}";
            }
            var txtPos = this.FindControl<TextBox>("TxtMotionTarget");
            if (txtPos != null)
            {
                txtPos.Text = $"{pt.X:F1}, {pt.Y:F1}, {pt.Z:F1}";
            }
        }
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        // 1. 刷新 USER TAB 的 OK 狀態燈
        var ledOk = this.FindControl<Ellipse>("LedUserOk");
        if (ledOk != null)
        {
            bool isPaused = _engines.Timer.IsSystemPaused;
            bool isRunning = _vm.MainThreads[0].IsRunning;
            if (isPaused)
            {
                ledOk.Fill = new SolidColorBrush(Color.FromRgb(220, 140, 0)); // 橘色暫停
            }
            else if (isRunning)
            {
                ledOk.Fill = new SolidColorBrush(Color.FromRgb(0, 204, 68));  // 綠色運行中
            }
            else
            {
                ledOk.Fill = new SolidColorBrush(Color.FromRgb(120, 120, 120)); // 灰色待機
            }
        }

        // 2. 刷新無限點位 IO 狀態 (支援虛擬化綁定)
        for (int i = 0; i < _ioChannels.Count; i++)
        {
            var ch = _ioChannels[i];
            ch.State = _engines.Io.GetPort(ch.Port);
        }

        // 3. 刷新 7 軸即時坐標數位讀表 (DRO, Digital Readout)
        var curPt = _engines.Point.GetPoint("CURRENT");
        var txtDroX = this.FindControl<TextBlock>("TxtDroX");
        if (txtDroX != null) txtDroX.Text = $"X: {curPt.X:F3}";
        var txtDroY = this.FindControl<TextBlock>("TxtDroY");
        if (txtDroY != null) txtDroY.Text = $"Y: {curPt.Y:F3}";
        var txtDroZ = this.FindControl<TextBlock>("TxtDroZ");
        if (txtDroZ != null) txtDroZ.Text = $"Z: {curPt.Z:F3}";
        var txtDroU = this.FindControl<TextBlock>("TxtDroU");
        if (txtDroU != null) txtDroU.Text = $"U: {curPt.U:F3}";
        var txtDroV = this.FindControl<TextBlock>("TxtDroV");
        if (txtDroV != null) txtDroV.Text = $"V: {curPt.V:F3}";
        var txtDroW = this.FindControl<TextBlock>("TxtDroW");
        if (txtDroW != null) txtDroW.Text = $"W: {curPt.W:F3}";
        var txtDroH = this.FindControl<TextBlock>("TxtDroH");
        if (txtDroH != null) txtDroH.Text = $"H: {(int)curPt.H}";
    }
}
