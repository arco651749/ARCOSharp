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
using ARCOS.App.Dialogs;
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

    public BasicEngines Engines => _engines;
    public DriverManager DriverManager => _driverManager;
    public ArcosThreadVM VM => _vm;
    public ObservableCollection<IoChannelViewModel> IoChannels => _ioChannels;
    public SequencerEditorControl? Sequencer => this.FindControl<SequencerEditorControl>("SequencerEditor");

    public MainWindow()
    {
        InitializeComponent();

        // 0. 確保 ARCOSharp 標準目錄已建立 (SCRIPT, LOGS, PICTURE, WAVEFORM, POINT&PATH)
        ARCOS.Core.Common.ArcosPaths.EnsureDirectoriesExist();

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
        BindEngineInspectionTabs();
        BindThreadsTab();

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

        // 初始系統就緒記錄 (不預載腳本，靜候使用者載入)
        _vm.MainThreads[0].Log("[INFO] ARCOSharp System Ready. Awaiting script load via READ .PRO or SYSTEM@ProgramLoad.");

        // 啟動內嵌 MCP 與本地自動化控制介面
        Mcp.ArcosMcpServer.AttachMainWindow(this);

        // 預設切換至首頁 USER TAB 產線核心操作畫面
        this.Loaded += (_, _) =>
        {
            var tab = this.FindControl<TabItem>("TabUser");
            if (tab != null)
            {
                tab.IsSelected = true;
        this.KeyDown += MainWindow_KeyDown;
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
            _driverManager.AddDevice("DAQ", "Daq0", "MockDAQ", "Rate=1000,Count=500,Wave=Press", _engines);
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
        var btnOpenUserCsvi = this.FindControl<Button>("BtnOpenUserPanelCsvi");
        if (btnOpenUserCsvi != null)
        {
            btnOpenUserCsvi.Click += async (_, _) =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;
                var rootFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(System.IO.Path.GetFullPath(ARCOS.Core.Common.ArcosPaths.RootDirectory)));
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "選擇自訂 User Panel CSVI (Select User Panel CSVI)",
                    SuggestedStartLocation = rootFolder,
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("CSVI 3.0 Package (*.csvi)") { Patterns = new[] { "*.csvi" } }
                    }
                });
                if (files.Count > 0)
                {
                    LoadUserPanelFromCsvi(files[0].Path.LocalPath);
                }
            };
        }

        _vm.OnUserPanelOpenRequested += (csviPath) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                LoadUserPanelFromCsvi(csviPath);
            });
        };

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
                    _cachedIoFrontPanel = ARCOS.App.Guidance.ParameterGuidanceManager.Instance.GetFrontPanel("IO@SetPort")
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
                case 3: // System Master Dashboard (System.csvi Front Panel)
                    var sysPanel = ARCOS.App.Guidance.ParameterGuidanceManager.Instance.GetFrontPanel("System");
                    if (sysPanel != null)
                    {
                        container.Content = sysPanel;
                        if (sysPanel is Avalonia.Controls.Control ctrl && ctrl.DataContext != null)
                        {
                            var method = ctrl.DataContext.GetType().GetMethod("RefreshFromEngines");
                            method?.Invoke(ctrl.DataContext, new object[] { _engines });
                        }
                    }
                    break;
                case 4: // Thread Monitor (Thread.csvi Front Panel)
                    var threadPanel = ARCOS.App.Guidance.ParameterGuidanceManager.Instance.GetFrontPanel("Thread");
                    if (threadPanel != null) container.Content = threadPanel;
                    break;
                case 5: // Capture Viewfinder (Capture.csvi Front Panel)
                    var capPanel = ARCOS.App.Guidance.ParameterGuidanceManager.Instance.GetFrontPanel("Capture");
                    if (capPanel != null) container.Content = capPanel;
                    break;
                case 6: // MockCamera Driver (MockCamera.csvi Front Panel)
                    var camDriver = _driverManager.GetDevice("Capture", "CamTop");
                    var camView = camDriver?.CreateDriverView();
                    if (camView != null) container.Content = camView;
                    break;
                case 7: // DAQ Oscilloscope (DAQ.csvi Front Panel)
                    var daqPanel = ARCOS.App.Guidance.ParameterGuidanceManager.Instance.GetFrontPanel("DAQ");
                    if (daqPanel != null) container.Content = daqPanel;
                    break;
                case 8: // MockDAQ Driver (MockDAQ.csvi Front Panel)
                    var daqDriver = _driverManager.GetDevice("DAQ", "PressSensor") ?? _driverManager.GetDevice("DAQ", "Daq0");
                    var daqView = daqDriver?.CreateDriverView();
                    if (daqView != null) container.Content = daqView;
                    break;
                case 9: // Variable Watcher (Variable.csvi Front Panel)
                    var varPanel = ARCOS.App.Guidance.ParameterGuidanceManager.Instance.GetFrontPanel("Variable");
                    if (varPanel != null)
                    {
                        container.Content = varPanel;
                        if (varPanel is Avalonia.Controls.Control ctrl && ctrl.DataContext != null)
                        {
                            var method = ctrl.DataContext.GetType().GetMethod("RefreshFromEngines");
                            method?.Invoke(ctrl.DataContext, new object[] { _engines });
                        }
                    }
                    break;
                case 10: // Motion Controller (Motion.csvi Front Panel)
                    var motionPanel = ARCOS.App.Guidance.ParameterGuidanceManager.Instance.GetFrontPanel("Motion");
                    if (motionPanel != null)
                    {
                        container.Content = motionPanel;
                        if (motionPanel is Avalonia.Controls.Control ctrl && ctrl.DataContext != null)
                        {
                            var method = ctrl.DataContext.GetType().GetMethod("RefreshFromEngines");
                            method?.Invoke(ctrl.DataContext, new object[] { _engines });
                        }
                    }
                    break;
                case 11: // MockMotion Driver (MockMotion.csvi Front Panel)
                    var motDriver = _driverManager.GetDevice("Motion", "Station1") ?? _driverManager.GetDevice("Motion", "Motor1") ?? _driverManager.GetDevice("Motion", "Arm1");
                    var motView = motDriver?.CreateDriverView();
                    if (motView != null) container.Content = motView;
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

        var btnMountSystem = this.FindControl<Button>("BtnMountSystemPanel");
        if (btnMountSystem != null)
        {
            btnMountSystem.Click += (_, _) =>
            {
                if (cmbSubPanel != null) cmbSubPanel.SelectedIndex = 3;
                else switchSubPanel(3);
            };
        }

        var btnMountThread = this.FindControl<Button>("BtnMountThreadPanel");
        if (btnMountThread != null)
        {
            btnMountThread.Click += (_, _) =>
            {
                if (cmbSubPanel != null) cmbSubPanel.SelectedIndex = 4;
                else switchSubPanel(4);
            };
        }

        var btnMountCapture = this.FindControl<Button>("BtnMountCapturePanel");
        if (btnMountCapture != null)
        {
            btnMountCapture.Click += (_, _) =>
            {
                if (cmbSubPanel != null) cmbSubPanel.SelectedIndex = 5;
                else switchSubPanel(5);
            };
        }

        var btnMountCam = this.FindControl<Button>("BtnMountCamDriver");
        if (btnMountCam != null)
        {
            btnMountCam.Click += (_, _) =>
            {
                if (cmbSubPanel != null) cmbSubPanel.SelectedIndex = 6;
                else switchSubPanel(6);
            };
        }

        var btnMountDaq = this.FindControl<Button>("BtnMountDaqPanel");
        if (btnMountDaq != null)
        {
            btnMountDaq.Click += (_, _) =>
            {
                if (cmbSubPanel != null) cmbSubPanel.SelectedIndex = 7;
                else switchSubPanel(7);
            };
        }

        var btnMountDaqDriver = this.FindControl<Button>("BtnMountDaqDriver");
        if (btnMountDaqDriver != null)
        {
            btnMountDaqDriver.Click += (_, _) =>
            {
                if (cmbSubPanel != null) cmbSubPanel.SelectedIndex = 8;
                else switchSubPanel(8);
            };
        }

        var btnMountVar = this.FindControl<Button>("BtnMountVariablePanel");
        if (btnMountVar != null)
        {
            btnMountVar.Click += (_, _) =>
            {
                if (cmbSubPanel != null) cmbSubPanel.SelectedIndex = 9;
                else switchSubPanel(9);
            };
        }

        var btnMountMotion = this.FindControl<Button>("BtnMountMotionPanel");
        if (btnMountMotion != null)
        {
            btnMountMotion.Click += (_, _) =>
            {
                if (cmbSubPanel != null) cmbSubPanel.SelectedIndex = 10;
                else switchSubPanel(10);
            };
        }

        var btnMountMotionDriver = this.FindControl<Button>("BtnMountMotionDriver");
        if (btnMountMotionDriver != null)
        {
            btnMountMotionDriver.Click += (_, _) =>
            {
                if (cmbSubPanel != null) cmbSubPanel.SelectedIndex = 11;
                else switchSubPanel(11);
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

    // =========================================================
    // 動態載入自訂 User Panel CSVI (SYSTEM@UserPanelOpen 或按鍵)
    // =========================================================
    public bool LoadUserPanelFromCsvi(string csviNameOrPath)
    {
        try
        {
            string resolved = ARCOS.Core.Common.ArcosPaths.ResolveUserPanelCsviPath(csviNameOrPath);
            if (!File.Exists(resolved))
            {
                _vm.MainThreads[0].Log($"[UserPanelOpen] Error: CSVI file '{csviNameOrPath}' not found (resolved: '{resolved}')", isError: true);
                return false;
            }

            var container = this.FindControl<ContentControl>("UserFrontPanelContainer");
            if (container == null) return false;

            var pkg = ARCOS.Core.Csvi.CsviPackage.Load(resolved);
            object? panel = null;

            string cmdKey = !string.IsNullOrEmpty(pkg.Manifest.CommandKey) ? pkg.Manifest.CommandKey : System.IO.Path.GetFileNameWithoutExtension(resolved);

            if (pkg.Manifest.ComponentType == ARCOS.Core.Csvi.CsviComponentType.Driver)
            {
                var dev = _driverManager.GetDevice(pkg.Manifest.Module ?? "Custom", cmdKey);
                panel = dev?.CreateDriverView();
            }

            if (panel == null)
            {
                panel = ARCOS.App.Guidance.ParameterGuidanceManager.Instance.GetFrontPanel(cmdKey)
                     ?? ARCOS.App.Guidance.ParameterGuidanceManager.Instance.GetFrontPanel($"{pkg.Manifest.Module}@{cmdKey}");
            }

            if (panel == null && pkg.LoadedAssembly != null)
            {
                var ctrlType = pkg.LoadedAssembly.GetTypes().FirstOrDefault(t => typeof(Control).IsAssignableFrom(t) && !t.IsAbstract);
                if (ctrlType != null)
                {
                    panel = Activator.CreateInstance(ctrlType);
                }
            }

            if (panel != null)
            {
                container.Content = panel;
                if (panel is Control ctrl && ctrl.DataContext != null)
                {
                    var refreshM = ctrl.DataContext.GetType().GetMethod("RefreshFromEngines");
                    refreshM?.Invoke(ctrl.DataContext, new object[] { _engines });
                }
                _vm.MainThreads[0].Log($"[UserPanelOpen] Mounted User Panel: '{cmdKey}' ({System.IO.Path.GetFileName(resolved)})");
                return true;
            }
            else
            {
                _vm.MainThreads[0].Log($"[UserPanelOpen] Warning: CSVI '{cmdKey}' has no UI FrontPanel.", isError: true);
                return false;
            }
        }
        catch (Exception ex)
        {
            _vm.MainThreads[0].Log($"[UserPanelOpen] Exception loading User Panel: {ex.Message}", isError: true);
            return false;
        }
    }

    // =========================================================
    // Basic Engines 狀態檢視 TAB 綁定 (POINT, IMAQ, VARIABLE, WAVEFORM)
    // =========================================================
    private void BindEngineInspectionTabs()
    {
        BindImaqTab();
        BindPointTab();
        BindVariableTab();
        BindWaveformTab();
    }

    // =========================================================
    // 6. Threads 監控面板綁定 (Manager MainThread 常駐 + Workers 1~12 + SubThread 動態池)
    // =========================================================
    private void BindThreadsTab()
    {
        var mMain = this.FindControl<ThreadMonitorControl>("MonitorThreadMain");
        mMain?.BindContext(_vm.MainThreads[0], "Thread Main (0)");

        for (int i = 1; i <= 12; i++)
        {
            var mWorker = this.FindControl<ThreadMonitorControl>($"MonitorThread{i}");
            mWorker?.BindContext(_vm.MainThreads[i], $"Thread {i}");
        }

        _vm.OnProgramLoaded += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                mMain?.ReloadProgram();
                for (int i = 1; i <= 12; i++)
                {
                    var mw = this.FindControl<ThreadMonitorControl>($"MonitorThread{i}");
                    mw?.ReloadProgram();
                }
            });
        };

        // SubThread 工具列與清單綁定
        var btnGetSubs = this.FindControl<Button>("BtnGetSubThreadList");
        var lstSubs = this.FindControl<ListBox>("LstSubThreads");
        var txtSubCount = this.FindControl<TextBlock>("TxtSubThreadCount");
        var mSubDetail = this.FindControl<ThreadMonitorControl>("MonitorSubThreadDetail");
        var pnlEmpty = this.FindControl<Border>("PnlSubThreadEmptyNotice");
        var btnDemoSub = this.FindControl<Button>("BtnDemoSubThread");

        void RefreshSubThreadList()
        {
            var subs = _vm.SubThreads.Select(kv => new SubThreadItemViewModel(kv.Key, kv.Value)).ToList();
            if (lstSubs != null) lstSubs.ItemsSource = subs;
            if (txtSubCount != null)
            {
                int activeCount = subs.Count(s => s.Context.IsRunning);
                txtSubCount.Text = $"SubThreads: {activeCount} Active / {subs.Count} Total";
            }
        }

        if (btnGetSubs != null)
        {
            btnGetSubs.Click += (_, _) => RefreshSubThreadList();
        }

        if (lstSubs != null)
        {
            lstSubs.SelectionChanged += (_, _) =>
            {
                if (lstSubs.SelectedItem is SubThreadItemViewModel selected)
                {
                    if (mSubDetail != null)
                    {
                        mSubDetail.BindContext(selected.Context, selected.Name);
                    }
                    if (pnlEmpty != null) pnlEmpty.IsVisible = false;
                }
            };
        }

        if (btnDemoSub != null)
        {
            btnDemoSub.Click += (_, _) =>
            {
                int nextId = _vm.SubThreads.Count + 1;
                string subName = $"DemoSub_{nextId}";
                var demoProg = new ProgramTable { ProgramNumber = 90 + (nextId % 10), Name = subName };
                demoProg.Add(CommandItem.Parse("Timer@Delay\t1200", 0));
                demoProg.Add(CommandItem.Parse("IO@Set\tDO0,1", 1));
                demoProg.Add(CommandItem.Parse("Timer@Delay\t800", 2));
                demoProg.Add(CommandItem.Parse("IO@Set\tDO0,0", 3));
                demoProg.Add(CommandItem.Parse("Thread@Stop\t", 4));

                var ctx = _vm.StartSubThread(subName, demoProg, 1);
                RefreshSubThreadList();
                if (lstSubs != null)
                {
                    var matched = (lstSubs.ItemsSource as IEnumerable<SubThreadItemViewModel>)
                        ?.FirstOrDefault(x => x.Name == subName);
                    if (matched != null)
                    {
                        lstSubs.SelectedItem = matched;
                    }
                }
            };
        }
    }

    // =========================================================
    // Test Thread 專屬浮動視窗 (Ctrl+T 喚醒 / 最小化 / 頂層不奪焦)
    // =========================================================
    private TestThreadWindow? _testThreadWindow;
    public TestThreadWindow? TestThreadWindow => _testThreadWindow;

    public void ToggleTestThreadWindow()
    {
        if (_testThreadWindow == null)
        {
            _testThreadWindow = new TestThreadWindow(_vm, this);
            _testThreadWindow.ShowWithoutActivation(this);
        }
        else
        {
            if (_testThreadWindow.WindowState == WindowState.Minimized || !_testThreadWindow.IsVisible)
            {
                _testThreadWindow.ShowWithoutActivation(this);
            }
            else
            {
                _testThreadWindow.WindowState = WindowState.Minimized;
            }
        }
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.T)
        {
            ToggleTestThreadWindow();
            e.Handled = true;
        }
    }

    private void BindImaqTab()
    {
        var btnRefresh = this.FindControl<Button>("BtnRefreshImaq");
        var lst = this.FindControl<ListBox>("LstImaqImages");
        var txtSummary = this.FindControl<TextBlock>("TxtImaqSummary");
        var cmbFrame = this.FindControl<ComboBox>("CmbImaqFrameIndex");
        var ledOk = this.FindControl<Ellipse>("LedImaqOk");
        var txtStatus = this.FindControl<TextBlock>("TxtImaqStatus");
        var btnSave = this.FindControl<Button>("BtnSaveImaq");
        var imgPreview = this.FindControl<Image>("ImgImaqPreview");
        var txtPlace = this.FindControl<TextBlock>("TxtImaqPlaceholder");
        var txtKey = this.FindControl<TextBlock>("TxtImaqSelectedKey");
        var txtRes = this.FindControl<TextBlock>("TxtImaqResolution");
        var txtFrames = this.FindControl<TextBlock>("TxtImaqFramesOverlays");
        var txtTime = this.FindControl<TextBlock>("TxtImaqTimestamp");

        Action refreshList = () =>
        {
            var names = _engines.Imaq.GetAllNames();
            if (txtSummary != null) txtSummary.Text = $"Total Images: {names.Count}";
            if (lst != null)
            {
                string? prevSel = lst.SelectedItem as string;
                lst.ItemsSource = names;
                if (!string.IsNullOrEmpty(prevSel) && names.Contains(prevSel)) lst.SelectedItem = prevSel;
                else if (names.Count > 0) lst.SelectedIndex = 0;
            }
        };

        if (btnRefresh != null) btnRefresh.Click += (_, _) => refreshList();

        if (lst != null)
        {
            lst.SelectionChanged += (_, _) =>
            {
                string? key = lst.SelectedItem as string;
                if (string.IsNullOrEmpty(key)) return;

                try
                {
                    var frame = _engines.Imaq.GetFrame(key);
                    if (txtKey != null) txtKey.Text = key;
                    if (txtRes != null) txtRes.Text = $"{frame.Image.Width} x {frame.Image.Height} ({frame.Image.Type()})";
                    if (txtFrames != null) txtFrames.Text = $"Overlays: {frame.Overlays.Count}";
                    if (txtTime != null) txtTime.Text = frame.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");

                    bool isOk = _engines.Io.GetPort($"{key}#OK?");
                    if (ledOk != null) ledOk.Fill = isOk ? Brushes.LimeGreen : Brushes.LightGray;
                    if (txtStatus != null) txtStatus.Text = $"{key}#OK? = {isOk}";

                    if (imgPreview != null && !frame.Image.Empty())
                    {
                        using var canvas = frame.Image.Clone();
                        Cv2.ImEncode(".bmp", canvas, out byte[] buf);
                        using var ms = new MemoryStream(buf);
                        imgPreview.Source = new Bitmap(ms);
                        if (txtPlace != null) txtPlace.IsVisible = false;
                    }
                }
                catch
                {
                    if (txtPlace != null) txtPlace.IsVisible = true;
                }
            };
        }

        if (btnSave != null)
        {
            btnSave.Click += (_, _) =>
            {
                string? key = lst?.SelectedItem as string;
                if (string.IsNullOrEmpty(key)) return;
                try
                {
                    string camDir = ARCOS.Core.Common.ArcosPaths.GetCameraPictureDirectory(key);
                    string fileName = $"{key}_{DateTime.Now:yyyyMMdd_HHmmss}.bmp";
                    string fullPath = System.IO.Path.Combine(camDir, fileName);
                    _engines.Imaq.SaveImage(key, fullPath, true);
                    _vm.MainThreads[0].Log($"[IMAQ] Saved image '{key}' to '{fullPath}'");
                }
                catch (Exception ex)
                {
                    _vm.MainThreads[0].Log($"[IMAQ] Error saving image: {ex.Message}", isError: true);
                }
            };
        }

        _engines.Imaq.ImageUpdated += (key, idx) =>
        {
            Dispatcher.UIThread.Post(() => refreshList());
        };

        refreshList();
    }

    private void BindPointTab()
    {
        var btnRefresh = this.FindControl<Button>("BtnRefreshPoints");
        var lst = this.FindControl<ListBox>("LstPoints");
        var grid = this.FindControl<DataGrid>("GridPoints");
        var txtSummary = this.FindControl<TextBlock>("TxtPointSummary");
        var btnSave = this.FindControl<Button>("BtnSavePoints");
        var btnLoad = this.FindControl<Button>("BtnLoadPoints");

        var txtEditKey = this.FindControl<TextBox>("TxtPointEditKey");
        var txtX = this.FindControl<TextBox>("TxtPointX");
        var txtY = this.FindControl<TextBox>("TxtPointY");
        var txtZ = this.FindControl<TextBox>("TxtPointZ");
        var txtU = this.FindControl<TextBox>("TxtPointU");
        var txtV = this.FindControl<TextBox>("TxtPointV");
        var txtW = this.FindControl<TextBox>("TxtPointW");
        var btnApply = this.FindControl<Button>("BtnApplyPointEdit");

        Action refreshList = () =>
        {
            var names = _engines.Point.GetAllNames();
            if (txtSummary != null) txtSummary.Text = $"Total Points: {names.Count}";
            if (lst != null)
            {
                string? prevSel = lst.SelectedItem as string;
                lst.ItemsSource = names;
                if (!string.IsNullOrEmpty(prevSel) && names.Contains(prevSel)) lst.SelectedItem = prevSel;
                else if (names.Count > 0) lst.SelectedIndex = 0;
            }

            if (grid != null)
            {
                var rows = new List<PointRowViewModel>();
                foreach (var name in names)
                {
                    try
                    {
                        var pt = _engines.Point.GetPoint(name);
                        rows.Add(new PointRowViewModel
                        {
                            Name = name,
                            X = Math.Round(pt.X, 3),
                            Y = Math.Round(pt.Y, 3),
                            Z = Math.Round(pt.Z, 3),
                            U = Math.Round(pt.U, 3),
                            V = Math.Round(pt.V, 3),
                            W = Math.Round(pt.W, 3)
                        });
                    }
                    catch { }
                }
                grid.ItemsSource = rows;
            }
        };

        if (btnRefresh != null) btnRefresh.Click += (_, _) => refreshList();

        if (lst != null)
        {
            lst.SelectionChanged += (_, _) =>
            {
                string? key = lst.SelectedItem as string;
                if (string.IsNullOrEmpty(key)) return;
                try
                {
                    var pt = _engines.Point.GetPoint(key);
                    if (txtEditKey != null) txtEditKey.Text = key;
                    if (txtX != null) txtX.Text = pt.X.ToString("F3");
                    if (txtY != null) txtY.Text = pt.Y.ToString("F3");
                    if (txtZ != null) txtZ.Text = pt.Z.ToString("F3");
                    if (txtU != null) txtU.Text = pt.U.ToString("F3");
                    if (txtV != null) txtV.Text = pt.V.ToString("F3");
                    if (txtW != null) txtW.Text = pt.W.ToString("F3");
                }
                catch { }
            };
        }

        if (btnApply != null)
        {
            btnApply.Click += (_, _) =>
            {
                string key = txtEditKey?.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(key)) return;
                double.TryParse(txtX?.Text, out double x);
                double.TryParse(txtY?.Text, out double y);
                double.TryParse(txtZ?.Text, out double z);
                double.TryParse(txtU?.Text, out double u);
                double.TryParse(txtV?.Text, out double v);
                double.TryParse(txtW?.Text, out double w);

                _engines.Point.SetPoint(key, new MachinePoint(x, y, z, u, v, w));
                refreshList();
                _vm.MainThreads[0].Log($"[POINT] Updated point '{key}' = ({x}, {y}, {z}, {u}, {v}, {w})");
            };
        }

        if (btnSave != null)
        {
            btnSave.Click += (_, _) =>
            {
                _engines.Point.SaveToFile();
                _vm.MainThreads[0].Log("[POINT] Saved all points to POINT&PATH/Points.ini");
            };
        }

        if (btnLoad != null)
        {
            btnLoad.Click += (_, _) =>
            {
                _engines.Point.LoadFromFile();
                refreshList();
                _vm.MainThreads[0].Log("[POINT] Loaded points from POINT&PATH/Points.ini");
            };
        }

        refreshList();
    }

    private void BindVariableTab()
    {
        var btnRefresh = this.FindControl<Button>("BtnRefreshVariables");
        var lst = this.FindControl<ListBox>("LstVariables");
        var grid = this.FindControl<DataGrid>("GridVariables");
        var txtSummary = this.FindControl<TextBlock>("TxtVariableSummary");
        var cmbSec = this.FindControl<ComboBox>("CmbVariableSection");
        var btnSave = this.FindControl<Button>("BtnSaveVariables");
        var btnLoad = this.FindControl<Button>("BtnLoadVariables");

        var txtKey = this.FindControl<TextBox>("TxtVarEditKey");
        var txtSec = this.FindControl<TextBox>("TxtVarEditSection");
        var txtVal = this.FindControl<TextBox>("TxtVarEditValue");
        var btnApply = this.FindControl<Button>("BtnApplyVarEdit");
        var lstElements = this.FindControl<ListBox>("LstVarElements");

        Action refreshList = () =>
        {
            var names = _engines.Variable.GetAllVariableNames();
            if (txtSummary != null) txtSummary.Text = $"Total Variables: {names.Count}";
            if (lst != null)
            {
                string? prevSel = lst.SelectedItem as string;
                lst.ItemsSource = names;
                if (!string.IsNullOrEmpty(prevSel) && names.Contains(prevSel)) lst.SelectedItem = prevSel;
                else if (names.Count > 0) lst.SelectedIndex = 0;
            }

            if (grid != null)
            {
                var rows = new List<VariableRowViewModel>();
                foreach (var name in names)
                {
                    string val = _engines.Variable.GetRaw(name);
                    rows.Add(new VariableRowViewModel
                    {
                        Section = "Global",
                        Key = name,
                        Value = val
                    });
                }
                grid.ItemsSource = rows;
            }
        };

        if (btnRefresh != null) btnRefresh.Click += (_, _) => refreshList();

        if (lst != null)
        {
            lst.SelectionChanged += (_, _) =>
            {
                string? key = lst.SelectedItem as string;
                if (string.IsNullOrEmpty(key)) return;

                string val = _engines.Variable.GetRaw(key);
                if (txtKey != null) txtKey.Text = key;
                if (txtSec != null) txtSec.Text = "Global";
                if (txtVal != null) txtVal.Text = val;

                if (lstElements != null)
                {
                    var elements = val.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    lstElements.ItemsSource = elements.Select((e, i) => $"[{i}] {e}").ToList();
                }
            };
        }

        if (btnApply != null)
        {
            btnApply.Click += (_, _) =>
            {
                string key = txtKey?.Text?.Trim() ?? "";
                string sec = txtSec?.Text?.Trim() ?? "Global";
                string val = txtVal?.Text ?? "";
                if (!string.IsNullOrEmpty(key))
                {
                    _engines.Variable.Set(key, val, sec);
                    refreshList();
                    _vm.MainThreads[0].Log($"[VARIABLE] Set '{key}' = '{val}' (Section: {sec})");
                }
            };
        }

        if (btnSave != null)
        {
            btnSave.Click += (_, _) =>
            {
                string path = System.IO.Path.Combine(ARCOS.Core.Common.ArcosPaths.ScriptDirectory, "Variables.var");
                _engines.Variable.SaveToFile(path);
                _vm.MainThreads[0].Log($"[VARIABLE] Saved variables to '{path}'");
            };
        }

        if (btnLoad != null)
        {
            btnLoad.Click += (_, _) =>
            {
                string path = System.IO.Path.Combine(ARCOS.Core.Common.ArcosPaths.ScriptDirectory, "Variables.var");
                if (File.Exists(path))
                {
                    _engines.Variable.LoadFromFile(path);
                    refreshList();
                    _vm.MainThreads[0].Log($"[VARIABLE] Loaded variables from '{path}'");
                }
            };
        }

        refreshList();
    }

    private void BindWaveformTab()
    {
        var btnRefresh = this.FindControl<Button>("BtnRefreshWaveforms");
        var lst = this.FindControl<ListBox>("LstWaveforms");
        var txtSummary = this.FindControl<TextBlock>("TxtWaveformSummary");
        var cmbChannel = this.FindControl<ComboBox>("CmbWaveformChannel");
        var btnSave = this.FindControl<Button>("BtnSaveWaveform");
        var daqPlot = this.FindControl<AvaPlot>("DaqPlot");

        var txtCount = this.FindControl<TextBlock>("TxtWfStatsCount");
        var txtRate = this.FindControl<TextBlock>("TxtWfStatsRate");
        var txtMinMax = this.FindControl<TextBlock>("TxtWfStatsMinMax");
        var txtVpp = this.FindControl<TextBlock>("TxtWfStatsVpp");

        Action updatePlot = () =>
        {
            string? key = lst?.SelectedItem as string;
            if (string.IsNullOrEmpty(key) || daqPlot == null) return;

            int ch = cmbChannel?.SelectedIndex >= 0 ? cmbChannel.SelectedIndex : 0;
            try
            {
                var entry = _engines.Daq.GetWaveform(key);
                var chData = entry.GetOrCreateChannel(ch);
                if (chData.Y == null || chData.Y.Length == 0) return;

                int n = chData.Y.Length;
                double min = chData.Y.Min();
                double max = chData.Y.Max();
                double vpp = max - min;
                double rate = chData.Dt > 0 ? (1.0 / chData.Dt) : 0;

                if (txtCount != null) txtCount.Text = $"Samples: {n}";
                if (txtRate != null) txtRate.Text = $"Sample Rate: {rate:F1} Hz (dt={chData.Dt})";
                if (txtMinMax != null) txtMinMax.Text = $"Min / Max: {min:F3} / {max:F3} V";
                if (txtVpp != null) txtVpp.Text = $"Vpp: {vpp:F3} V";

                daqPlot.Plot.Clear();
                var sig = daqPlot.Plot.Add.Signal(chData.Y, chData.Dt > 0 ? chData.Dt : 1.0);
                sig.LineWidth = 2.0f;
                sig.Color = ScottPlot.Colors.DodgerBlue;
                daqPlot.Plot.Title($"DAQ Waveform: {key} (Ch {ch})");
                daqPlot.Plot.XLabel("Time (seconds)");
                daqPlot.Plot.YLabel("Voltage / Amplitude (V)");
                daqPlot.Plot.Axes.AutoScale();
                daqPlot.Refresh();
            }
            catch (Exception ex)
            {
                _vm.MainThreads[0].Log($"[Waveform] Error rendering plot: {ex.Message}", isError: true);
            }
        };

        Action refreshList = () =>
        {
            var names = _engines.Daq.GetAllNames();
            if (txtSummary != null) txtSummary.Text = $"Total Waveforms: {names.Count}";
            if (lst != null)
            {
                string? prevSel = lst.SelectedItem as string;
                lst.ItemsSource = names;
                if (!string.IsNullOrEmpty(prevSel) && names.Contains(prevSel)) lst.SelectedItem = prevSel;
                else if (names.Count > 0) lst.SelectedIndex = 0;
            }
            updatePlot();
        };

        if (btnRefresh != null) btnRefresh.Click += (_, _) => refreshList();
        if (lst != null) lst.SelectionChanged += (_, _) => updatePlot();
        if (cmbChannel != null) cmbChannel.SelectionChanged += (_, _) => updatePlot();

        if (btnSave != null)
        {
            btnSave.Click += (_, _) =>
            {
                string? key = lst?.SelectedItem as string;
                if (string.IsNullOrEmpty(key)) return;
                int ch = cmbChannel?.SelectedIndex >= 0 ? cmbChannel.SelectedIndex : 0;
                try
                {
                    _engines.Daq.SaveWaveform(key, channel: ch);
                    _vm.MainThreads[0].Log($"[WAVEFORM] Saved waveform '{key}' Ch{ch} to WAVEFORM/ directory");
                }
                catch (Exception ex)
                {
                    _vm.MainThreads[0].Log($"[WAVEFORM] Error saving waveform: {ex.Message}", isError: true);
                }
            };
        }

        refreshList();
    }
}

public class PointRowViewModel
{
    public string Name { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double U { get; set; }
    public double V { get; set; }
    public double W { get; set; }
}

public class VariableRowViewModel
{
    public string Section { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

