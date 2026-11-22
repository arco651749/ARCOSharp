using System;

using System.Collections.Generic;

using System.Collections.ObjectModel;

using System.IO;

using System.Linq;

using Avalonia;

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



public class ModuleListItem

{

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string BadgeText { get; set; } = "CORE";

    public IBrush BadgeBackground { get; set; } = Brushes.SlateGray;

}



public partial class MainWindow : Window

{

    private readonly BasicEngines _engines;

    private readonly DriverManager _driverManager;

    private readonly ArcosThreadVM _vm;

    private readonly DispatcherTimer _uiTimer;

    private readonly ObservableCollection<IoChannelViewModel> _ioChannels = new();

    private readonly ObservableCollection<ModuleListItem> _loadedModules = new();


    private sealed class LoadedUserPanelSlot
    {
        public string PanelName { get; set; } = "";
        public string FilePath { get; set; } = "";
        public ARCOS.Core.Csvi.CsviPackage? Package { get; set; }
        public Control RootControl { get; set; } = null!;
        public List<string> ImageControlNames { get; set; } = new();
        public HashSet<string> BlinkControls { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> BlinkOriginalOpacity { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private object? _defaultSubPanelContent;

    private readonly Dictionary<string, LoadedUserPanelSlot> _loadedUserPanels = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _loadedUserPanelOrder = new();

    private string _activeUserPanelName = ARCOS.Core.Engines.UserPanelEngine.DefaultPanelName;

    private bool _isSyncingActivePanelFromEngine;

    private object? _cachedIoFrontPanel;

    private string _selectedCameraName = "CamTop";

    private DispatcherTimer? _snapLiveTimer;

    private bool _isContinuousLive = false;

    private DateTime _lastSnapClickTime = DateTime.MinValue;

    private readonly HashSet<Control> _suppressedUiControls = new();

    private DispatcherTimer? _userBlinkTimer;

    private bool _blinkPhase;



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



        // 2. 驅動程式按需動態掛載 (On-Demand Loading，僅於使用者下達指令時動態載入)

        WireDriverDynamicEvents();



        // 3. 建立多執行緒排程虛擬機 (啟用每個 Thread 獨立平行背景迴圈)

        _vm = new ArcosThreadVM(_engines, _driverManager) { AutoSpawnBackgroundLoop = true };



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



        // 即時監聽別名與虛擬點位綁定事件，確保 SETPORTNAME 時 CAPTURE 與主視窗 PORT LIST 100% 同步即時更新

        _engines.Io.PortNameChanged += (port, alias) =>

        {

            Dispatcher.UIThread.Post(() =>

            {

                var item = _ioChannels.FirstOrDefault(x => string.Equals(x.Port, port, StringComparison.OrdinalIgnoreCase));

                if (item != null)

                {

                    item.Name = alias;

                }

            });

        };



        _engines.Io.VirtualIoAdded += (name, val) =>

        {

            Dispatcher.UIThread.Post(() =>

            {

                var existing = _ioChannels.FirstOrDefault(x => string.Equals(x.Port, name, StringComparison.OrdinalIgnoreCase));

                if (existing == null)

                {

                    _ioChannels.Add(new IoChannelViewModel

                    {

                        Port = name,

                        Name = name,

                        State = val,

                        IsVisible = true

                    });

                }

            });

        };



        // 6. 綁定 USER TAB 與 CAPTURE 交互事件與 SubPanel 動態調度

        BindUserTabControls();

        BindCaptureTabControls();

        BindModulesInfoTab();

        BindEngineInspectionTabs();

        BindThreadsTab();



        // 7. UI 定時刷新計時器 (50ms，驅動即時狀態更新)

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };

        _uiTimer.Tick += UiTimer_Tick;

        _uiTimer.Start();



        // 8. 監聽執行緒即時日誌流，推送到 USER TAB 與 SYSTEM 模組的 Log 視窗

        _engines.Record.Recorded += (recName, ts, msg) =>
        {
            if (!string.Equals(recName, "SYSTEMLOG", StringComparison.OrdinalIgnoreCase)) return;
            Dispatcher.UIThread.Post(() =>
            {
                var txtLogs = this.FindControl<TextBox>("TxtUserLogs");
                if (txtLogs != null)
                {
                    txtLogs.Text += $"[{ts}] {msg}" + Environment.NewLine;
                    txtLogs.CaretIndex = txtLogs.Text.Length;
                }
            }, DispatcherPriority.Background);
        };

        _engines.Record.Cleared += recName =>
        {
            if (string.Equals(recName, "SYSTEMLOG", StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var txtLogs = this.FindControl<TextBox>("TxtUserLogs");
                    if (txtLogs != null)
                    {
                        txtLogs.Text = string.Empty;
                        txtLogs.CaretIndex = 0;
                    }
                }, DispatcherPriority.Background);
            }
            else if (string.Equals(recName, "SYSTEMLOG_ERROR", StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var txtLogs = this.FindControl<TextBox>("TxtUserLogs");
                    if (txtLogs != null && !string.IsNullOrEmpty(txtLogs.Text))
                    {
                        var remainingLines = txtLogs.Text
                            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(line => !line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase));
                        var joined = string.Join(Environment.NewLine, remainingLines);
                        txtLogs.Text = string.IsNullOrEmpty(joined) ? string.Empty : joined + Environment.NewLine;
                        txtLogs.CaretIndex = txtLogs.Text.Length;
                    }
                }, DispatcherPriority.Background);
            }
        };

        _vm.OnMessageBoxRequested = ShowSystemMessageBoxAsync;
        _vm.OnWarningBoxRequested = ShowSystemWarningBoxAsync;
        _vm.OnEmgStateChanged += isEmg => Dispatcher.UIThread.Post(UpdateUserStatusBadge);



        // 初始系統就緒記錄 (不預載腳本，靜候使用者載入)

        _vm.MainThreads[0].Log("[INFO] ARCOSharp System Ready. Awaiting script load via READ .PRO or SYSTEM@ProgramLoad.");



        // 註冊全域穿透式鍵盤快速鍵監聽 (Tunneling 策略：確保任何子控制項焦點下 Ctrl+T 均能全局喚醒 Test Thread Editor)

        this.AddHandler(InputElement.KeyDownEvent, MainWindow_KeyDown, RoutingStrategies.Tunnel);



        // 啟動內嵌 MCP 與本地自動化控制介面

        Mcp.ArcosMcpServer.AttachMainWindow(this);



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



            var studio = this.FindControl<ARCOS.CsviStudio.Controls.CsviStudioControl>("StudioControl");

            var tabStudio = this.FindControl<TabItem>("TabStudio");

            if (studio != null && tabStudio != null)

            {

                tabStudio.Header = $"CSVI STUDIO: MCP {studio.McpPort}";

            }

        });

    }



    private void InitializeComponent()

    {

        AvaloniaXamlLoader.Load(this);

    }



    private void WireDriverDynamicEvents()

    {

        _driverManager.DeviceAdded += (category, deviceName, driver) =>

        {

            Dispatcher.UIThread.Post(() =>

            {

                if (category.Equals("IO", StringComparison.OrdinalIgnoreCase))

                {

                    RefreshIoTab();

                }

                else if (category.Equals("Capture", StringComparison.OrdinalIgnoreCase))

                {

                    RefreshCaptureTab();

                    var lstCam = this.FindControl<ListBox>("LstCaptureCameras");

                    if (lstCam != null && !lstCam.Items.Contains(deviceName))

                    {

                        lstCam.Items.Add(deviceName);

                    }

                }

                else if (category.Equals("Motion", StringComparison.OrdinalIgnoreCase))

                {

                    RefreshMotionTab();

                    var lstMot = this.FindControl<ListBox>("LstCaptureMotors");

                    if (lstMot != null && !lstMot.Items.Contains(deviceName))

                    {

                        lstMot.Items.Add(deviceName);

                    }

                }

                else if (category.Equals("DAQ", StringComparison.OrdinalIgnoreCase))

                {

                    RefreshDaqTab();

                }

                _vm.MainThreads[0].Log($"[DriverManager] Dynamic Device Attached: [{category}] {deviceName}");

            });

        };



        _driverManager.DeviceRemoved += (category, deviceName) =>

        {

            Dispatcher.UIThread.Post(() =>

            {

                if (category.Equals("IO", StringComparison.OrdinalIgnoreCase))

                {

                    RefreshIoTab();

                }

                else if (category.Equals("Capture", StringComparison.OrdinalIgnoreCase))

                {

                    RefreshCaptureTab();

                    var lstCam = this.FindControl<ListBox>("LstCaptureCameras");

                    lstCam?.Items.Remove(deviceName);

                }

                else if (category.Equals("Motion", StringComparison.OrdinalIgnoreCase))

                {

                    RefreshMotionTab();

                    var lstMot = this.FindControl<ListBox>("LstCaptureMotors");

                    lstMot?.Items.Remove(deviceName);

                }

                else if (category.Equals("DAQ", StringComparison.OrdinalIgnoreCase))

                {

                    RefreshDaqTab();

                }

                _vm.MainThreads[0].Log($"[DriverManager] Dynamic Device Detached: [{category}] {deviceName}");

            });

        };

    }



    private static Control CreateDriverNotMountedPlaceholder(string category, string exampleCmd)

    {

        return new Border

        {

            Background = new SolidColorBrush(Color.Parse("#0F172A")),

            BorderBrush = new SolidColorBrush(Color.Parse("#334155")),

            BorderThickness = new Thickness(1),

            CornerRadius = new CornerRadius(8),

            Padding = new Thickness(24),

            Margin = new Thickness(16),

            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,

            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,

            Child = new StackPanel

            {

                Spacing = 12,

                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,

                Children =

                {

                    new TextBlock

                    {

                        Text = $"⚠️ [ 尚未載入 {category} 驅動設備 ]",

                        FontWeight = FontWeight.Bold,

                        FontSize = 15,

                        Foreground = new SolidColorBrush(Color.Parse("#F59E0B")),

                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center

                    },

                    new TextBlock

                    {

                        Text = $"請在腳本中執行指令以掛載硬體驅動，範例：",

                        FontSize = 13,

                        Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),

                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center

                    },

                    new Border

                    {

                        Background = new SolidColorBrush(Color.Parse("#1E293B")),

                        CornerRadius = new CornerRadius(4),

                        Padding = new Thickness(12, 6),

                        Child = new TextBlock

                        {

                            Text = exampleCmd,

                            FontFamily = new FontFamily("Consolas, Courier New, monospace"),

                            FontSize = 13,

                            FontWeight = FontWeight.SemiBold,

                            Foreground = new SolidColorBrush(Color.Parse("#38BDF8")),

                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center

                        }

                    }

                }

            }

        };

    }



    private void RegisterDefaultMockDrivers()

    {

        // 遵循微核心宿主與模組解耦規範：開機啟動階段不預載入硬體 Mock 驅動，等待指令（如 IO@AddIODriver, Capture@AddCam 等）動態掛載



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



        // Capture 裝置清單初始為空，待動態掛載時由 DriverManager 事件自動填入



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

        // 保持純淨出廠狀態：預設無任何 Demo 別名，僅依使用者腳本或配方中的 IO@SetPortName 動態命名

        for (int port = 0; port < 64; port++)

        {

            for (int bit = 0; bit < 8; bit++)

            {

                string portName = $"P{port}.{bit}";

                _ioChannels.Add(new IoChannelViewModel

                {

                    Port = portName,

                    Name = portName,

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

        // 1. PAUSE 按鈕 (對應 95: Pause On 與 94: Pause Off，觸發之 ThreadName 尾端帶 "&")

        var btnPause = this.FindControl<Button>("BtnUserPause");

        if (btnPause != null)

        {

            btnPause.Click += (_, _) =>

            {

                bool isPaused = _engines.Timer.IsSystemPaused;

                int hookProg = isPaused ? 94 : 95; // 94: Pause Off (System@Resume), 95: Pause On (System@Pause)

                var hookCtx = _vm.TriggerSystemHook(hookProg);

                if (hookCtx == null)

                {

                    // 若程式區 94/95 被清空，直接切換 Global Pause

                    _vm.SetGlobalPause(!isPaused, triggerHook: false);

                }

            };

        }



        // 2. !!STOP!! 緊急停機按鈕 (對應 99: EMG Stop，預設 Motion@Stop 停止所有軸)

        var btnStop = this.FindControl<Button>("BtnUserStop");

        if (btnStop != null)

        {

            btnStop.Click += (_, _) =>

            {

                _vm.MainThreads[0].Log("[EMG] Emergency Stop triggered by User Button!!", isError: true);

                _vm.TriggerEmgStop("User STOP button");

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

        _vm.OnUserPanelCloseRequested += (panelName) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                UnloadUserPanelSlot(panelName);
            });
        };

        _engines.Image.UserDisplayRequested += OnUserDisplayRequested;
        _engines.Daq.UserDisplayRequested += OnDaqUserDisplayRequested;
        _engines.UserPanel.CommandRequested += OnUserPanelCommandRequested;
        _engines.UserPanel.LiveReadHandler = ReadUserPanelLiveControlValue;



        var btnMountIo = this.FindControl<Button>("BtnMountIoPanel");

        var btnMountDriver = this.FindControl<Button>("BtnMountDriverPanel");

        var btnReset = this.FindControl<Button>("BtnResetSubPanel");



        Action<int> switchSubPanel = (idx) =>

        {

            if (container == null) return;

            UnloadAllUserPanels();

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

                    var driver = _driverManager.GetDevice("IO", "StationIO") ?? _driverManager.GetDevicesByCategory("IO").FirstOrDefault();

                    container.Content = driver?.CreateDriverView() ?? CreateDriverNotMountedPlaceholder("IO", "IO@AddDriver StationIO, MockIO");

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

                    var camDriver = _driverManager.GetDevice("Capture", "CamTop") ?? _driverManager.GetDevicesByCategory("Capture").FirstOrDefault();

                    container.Content = camDriver?.CreateDriverView() ?? CreateDriverNotMountedPlaceholder("Capture", "Capture@AddCam CamTop, MockCamera");

                    break;

                case 7: // DAQ Oscilloscope (DAQ.csvi Front Panel)

                    var daqPanel = ARCOS.App.Guidance.ParameterGuidanceManager.Instance.GetFrontPanel("DAQ");

                    if (daqPanel != null) container.Content = daqPanel;

                    break;

                case 8: // MockDAQ Driver (MockDAQ.csvi Front Panel)

                    var daqDriver = _driverManager.GetDevice("DAQ", "PressSensor") ?? _driverManager.GetDevice("DAQ", "Daq0") ?? _driverManager.GetDevicesByCategory("DAQ").FirstOrDefault();

                    container.Content = daqDriver?.CreateDriverView() ?? CreateDriverNotMountedPlaceholder("DAQ", "DAQ@AddDAQ Daq0, MockDAQ");

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

                    var motDriver = _driverManager.GetDevice("Motion", "Station1") ?? _driverManager.GetDevice("Motion", "RobotGantry") ?? _driverManager.GetDevicesByCategory("Motion").FirstOrDefault();

                    container.Content = motDriver?.CreateDriverView() ?? CreateDriverNotMountedPlaceholder("Motion", "Motion@AddMotion RobotGantry, MockMotion");

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



    private void BindModulesInfoTab()

    {

        var lstModules = this.FindControl<ListBox>("LstLoadedModules");

        if (lstModules == null) return;



        _loadedModules.Clear();

        _loadedModules.Add(new ModuleListItem { Name = "SYSTEM", Description = "System Logs & Performance", BadgeText = "CORE", BadgeBackground = new SolidColorBrush(Color.Parse("#0284C7")) });

        _loadedModules.Add(new ModuleListItem { Name = "THREAD", Description = "Thread Monitor Matrix", BadgeText = "ENGINE", BadgeBackground = new SolidColorBrush(Color.Parse("#7C3AED")) });

        _loadedModules.Add(new ModuleListItem { Name = "VARIABLE", Description = "Variables & Parameters", BadgeText = "DATA", BadgeBackground = new SolidColorBrush(Color.Parse("#059669")) });

        _loadedModules.Add(new ModuleListItem { Name = "POINT&PATH", Description = "Point Table & Teaching", BadgeText = "MOTION", BadgeBackground = new SolidColorBrush(Color.Parse("#EA580C")) });

        _loadedModules.Add(new ModuleListItem { Name = "CALL", Description = "External CSVI Manager", BadgeText = "PLUGIN", BadgeBackground = new SolidColorBrush(Color.Parse("#D97706")) });

        _loadedModules.Add(new ModuleListItem { Name = "USER", Description = "User Controls & Dialog Panels", BadgeText = "UI", BadgeBackground = new SolidColorBrush(Color.Parse("#0891B2")) });

        _loadedModules.Add(new ModuleListItem { Name = "IO", Description = "IO Devices & SubPanels", BadgeText = "HARDWARE", BadgeBackground = new SolidColorBrush(Color.Parse("#2563EB")) });

        _loadedModules.Add(new ModuleListItem { Name = "CAPTURE", Description = "Cameras & Image Buffers", BadgeText = "HARDWARE", BadgeBackground = new SolidColorBrush(Color.Parse("#0284C7")) });

        _loadedModules.Add(new ModuleListItem { Name = "MOTION", Description = "Motion Controllers & Jog", BadgeText = "HARDWARE", BadgeBackground = new SolidColorBrush(Color.Parse("#EA580C")) });

        _loadedModules.Add(new ModuleListItem { Name = "DAQ", Description = "Data Acquisition & Plots", BadgeText = "HARDWARE", BadgeBackground = new SolidColorBrush(Color.Parse("#9333EA")) });



        lstModules.ItemsSource = _loadedModules;



        // 監聽 ModuleRegistry 動態載入事件 (由 System@IncludeModule 觸發)

        if (_vm.Executor.Registry != null)

        {

            _vm.Executor.Registry.ModuleRegistered += (modName) =>

            {

                Dispatcher.UIThread.Post(() =>

                {

                    if (!_loadedModules.Any(m => string.Equals(m.Name, modName, StringComparison.OrdinalIgnoreCase)))

                    {

                        _loadedModules.Add(new ModuleListItem

                        {

                            Name = modName.ToUpperInvariant(),

                            Description = $"Dynamic Included Module ({modName})",

                            BadgeText = "DYNAMIC",

                            BadgeBackground = new SolidColorBrush(Color.Parse("#4F46E5"))

                        });

                    }

                });

            };

        }



        var pnlSys = this.FindControl<Grid>("PnlModuleSystem");

        var pnlThr = this.FindControl<Grid>("PnlModuleThread");

        var pnlVar = this.FindControl<Grid>("PnlModuleVariable");

        var pnlPt = this.FindControl<Grid>("PnlModulePointPath");

        var pnlCall = this.FindControl<Grid>("PnlModuleCall");

        var pnlIo = this.FindControl<Grid>("PnlModuleIo");

        var pnlCapture = this.FindControl<Grid>("PnlModuleCapture");

        var pnlMotion = this.FindControl<Grid>("PnlModuleMotion");

        var pnlDaq = this.FindControl<Grid>("PnlModuleDaq");

        var pnlDynamic = this.FindControl<ContentControl>("PnlModuleDynamicHost");



        lstModules.SelectionChanged += (_, _) =>

        {

            if (lstModules.SelectedItem is ModuleListItem selected)

            {

                bool isBuiltIn = selected.Name is "SYSTEM" or "THREAD" or "VARIABLE" or "POINT&PATH" or "CALL" or "IO" or "CAPTURE" or "IMAQ" or "MOTION" or "DAQ";

                if (pnlSys != null) pnlSys.IsVisible = selected.Name == "SYSTEM";

                if (pnlThr != null) pnlThr.IsVisible = selected.Name == "THREAD";

                if (pnlVar != null) pnlVar.IsVisible = selected.Name == "VARIABLE";

                if (pnlPt != null) pnlPt.IsVisible = selected.Name == "POINT&PATH";

                if (pnlCall != null) pnlCall.IsVisible = selected.Name == "CALL";

                if (pnlIo != null) pnlIo.IsVisible = selected.Name == "IO";

                if (pnlCapture != null) pnlCapture.IsVisible = selected.Name == "CAPTURE" || selected.Name == "IMAQ";

                if (pnlMotion != null) pnlMotion.IsVisible = selected.Name == "MOTION";

                if (pnlDaq != null) pnlDaq.IsVisible = selected.Name == "DAQ";

                if (pnlDynamic != null) pnlDynamic.IsVisible = !isBuiltIn;



                if (selected.Name == "IO")

                {

                    RefreshIoTab();

                }

                else if (selected.Name == "CALL")

                {

                    RefreshCallModuleFiles();

                }

                else if (selected.Name == "CAPTURE" || selected.Name == "IMAQ")

                {

                    RefreshCaptureTab();

                }

                else if (selected.Name == "MOTION")

                {

                    RefreshMotionTab();

                }

                else if (selected.Name == "DAQ")

                {

                    RefreshDaqTab();

                }

                else if (!isBuiltIn && pnlDynamic != null)

                {

                    var dynPanel = ARCOS.App.Guidance.ParameterGuidanceManager.Instance.GetFrontPanel(selected.Name);

                    if (dynPanel is Control ctrl)

                    {

                        pnlDynamic.Content = ctrl;

                        var m1 = ctrl.GetType().GetMethod("RefreshFromEngines") ?? ctrl.GetType().GetMethod("Init");

                        if (m1 != null)

                        {

                            m1.Invoke(ctrl, new object[] { _engines });

                        }

                        else if (ctrl.DataContext != null)

                        {

                            var m2 = ctrl.DataContext.GetType().GetMethod("RefreshFromEngines") ?? ctrl.DataContext.GetType().GetMethod("Init");

                            m2?.Invoke(ctrl.DataContext, new object[] { _engines });

                        }

                    }

                }

            }

        };



        BindSystemModule();

        BindCallModule();

        BindIoModulesTab();

        BindCaptureModule();

        BindMotionModule();

        BindDaqModule();



        lstModules.SelectedIndex = 0;

    }



    private void BindSystemModule()

    {

        var btnGetRecords = this.FindControl<Button>("BtnGetRecords");

        var lstChannels = this.FindControl<ListBox>("LstRecordChannels");

        var lstEntries = this.FindControl<ListBox>("LstRecordEntries");

        var txtIndex = this.FindControl<TextBlock>("TxtRecordIndex");

        var txtCap = this.FindControl<TextBlock>("TxtRecordCapability");

        var btnClear = this.FindControl<Button>("BtnClearSystemLog");



        bool suppressPull = false;

        Action refreshChannels = () =>

        {

            suppressPull = true;
            try
            {
            var names = _engines.Record.GetAllRecordNames();

            string? prev = lstChannels?.SelectedItem as string;

            if (lstChannels != null)

            {

                lstChannels.ItemsSource = names;

                if (!string.IsNullOrEmpty(prev) && names.Contains(prev))

                    lstChannels.SelectedItem = prev;

                else if (names.Count > 0)

                    lstChannels.SelectedIndex = 0;

            }
            }
            finally { suppressPull = false; }
        };



        Action updateSelectedChannel = () =>
        {
            string? chName = lstChannels?.SelectedItem as string ?? "SYSTEMLOG";
            var ch = _engines.Record.GetOrCreateChannel(chName);
            if (lstEntries != null)
            {
                // 最高架構準則：僅在使用者點擊 [Get RecordS] 或選取清單項目時，才建立快照送進 UI
                // 執行期間內部資料極速累積，UI 完全不監聽、不重排，保障最大吞吐量
                var snapshot = new ARCOS.Core.Model.RecordEntry[ch.Capacity];
                for (int i = 0; i < ch.Capacity; i++)
                {
                    snapshot[i] = new ARCOS.Core.Model.RecordEntry(ch.Entries[i].Timestamp, ch.Entries[i].Message);
                }
                lstEntries.ItemsSource = snapshot;
            }
            if (txtIndex != null) txtIndex.Text = ch.Index.ToString();
            if (txtCap != null) txtCap.Text = ch.Capacity.ToString();
        };



        if (btnGetRecords != null) btnGetRecords.Click += (_, _) => refreshChannels();

        if (lstChannels != null)
        {
            // 選取變更（含 Get RecordS 重建清單）不拉取；僅使用者點選清單項目才拉取該 Record 內容
            lstChannels.SelectionChanged += (_, _) => { if (!suppressPull) updateSelectedChannel(); };
            lstChannels.Tapped += (_, _) => { if (lstChannels.SelectedItem != null) updateSelectedChannel(); };
        }



        if (btnClear != null)

        {

            btnClear.Click += (_, _) =>

            {

                string? chName = lstChannels?.SelectedItem as string ?? "SYSTEMLOG";

                var ch = _engines.Record.GetOrCreateChannel(chName);

                ch.Clear();

                updateSelectedChannel();

            };

        }



        refreshChannels();

    }



    private static readonly System.Text.RegularExpressions.Regex XClassRegex = new(@"\bx:Class\s*=\s*""[^""]*""", System.Text.RegularExpressions.RegexOptions.Compiled);



    private static void CollectTextBoxValues(Control root, List<string> values)

    {

        if (root is TextBox tb && !tb.IsReadOnly && root.IsVisible && !string.IsNullOrWhiteSpace(tb.Text))

        {

            values.Add(tb.Text.Trim());

        }

        else if (root is Panel p && p.IsVisible)

        {

            foreach (var child in p.Children)

            {

                CollectTextBoxValues(child, values);

            }

        }

        else if (root is ContentControl cc && cc.IsVisible && cc.Content is Control childCtrl)

        {

            CollectTextBoxValues(childCtrl, values);

        }

        else if (root is Border b && b.IsVisible && b.Child is Control bc)

        {

            CollectTextBoxValues(bc, values);

        }

        else if (root is ScrollViewer sv && sv.IsVisible && sv.Content is Control svc)

        {

            CollectTextBoxValues(svc, values);

        }

    }



    private void UpdateCallModulePreview(string csviFileName)

    {

        var pnlFrontPanelHost = this.FindControl<Border>("PnlCallFrontPanelHost");

        var pnlWizardHost = this.FindControl<Border>("PnlCallWizardHost");

        if (pnlFrontPanelHost == null && pnlWizardHost == null) return;



        var candidateDirs = new[]

        {

            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExternalModules"),

            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "dist", "ARCOS_Runtime", "ExternalModules"),

            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "ExternalModules")

        };



        string? fullPath = null;

        foreach (var dir in candidateDirs)

        {

            string candidate = System.IO.Path.Combine(dir, csviFileName);

            if (System.IO.File.Exists(candidate)) { fullPath = candidate; break; }

        }



        if (fullPath == null)

        {

            if (pnlFrontPanelHost != null)

                pnlFrontPanelHost.Child = new TextBlock { Text = $"找不到檔案：{csviFileName}", Foreground = Brushes.Gray, Margin = new Thickness(20), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };

            if (pnlWizardHost != null)

                pnlWizardHost.Child = new TextBlock { Text = $"找不到檔案：{csviFileName}", Foreground = Brushes.Gray, Margin = new Thickness(20), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };

            return;

        }



        try

        {

            byte[] bytes = System.IO.File.ReadAllBytes(fullPath);

            var pkg = ARCOS.Core.Csvi.CsviPackage.LoadFromBytes(bytes, unpackSources: false);

            if (pkg.HasSourceBundleResource && pkg.Manifest.PackagingMode == ARCOS.Core.Csvi.CsviPackagingMode.PortableOpen)

            {

                try { pkg = ARCOS.Core.Csvi.CsviPackage.LoadFromBytes(bytes, unpackSources: true); } catch { }

            }



            // 1. 取得 Panel XAML

            string? panelXaml = null;

            if (pkg.EmbeddedSourceFiles.TryGetValue("Panel.axaml", out var px)) panelXaml = px;

            else if (pkg.EmbeddedRuntimeXaml.TryGetValue("CSVI.RuntimeXaml.Panel.axaml", out px)) panelXaml = px;



            // 2. 取得 Wizard XAML

            string? wizardXaml = null;

            if (pkg.EmbeddedSourceFiles.TryGetValue("Wizard.axaml", out var wx)) wizardXaml = wx;

            else if (pkg.EmbeddedRuntimeXaml.TryGetValue("CSVI.RuntimeXaml.Wizard.axaml", out wx)) wizardXaml = wx;



            // 渲染 FrontPanel

            if (pnlFrontPanelHost != null)

            {

                if (!string.IsNullOrWhiteSpace(panelXaml))

                {

                    try

                    {

                        string cleanXaml = XClassRegex.Replace(panelXaml, "");

                        var ctrl = Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader.Parse<Control>(cleanXaml);

                        pnlFrontPanelHost.Child = ctrl;

                    }

                    catch (Exception ex)

                    {

                        pnlFrontPanelHost.Child = new TextBlock { Text = $"FrontPanel XAML 解析錯誤：\n{ex.Message}", Foreground = Brushes.Crimson, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };

                    }

                }

                else

                {

                    var liveCtrl = ARCOS.CsviStudio.Controls.CsviStudioControl.TryInstantiateLiveControlFromAssembly(bytes, pkg.Manifest.CommandKey);

                    if (liveCtrl != null)

                    {

                        pnlFrontPanelHost.Child = liveCtrl;

                    }

                    else

                    {

                        pnlFrontPanelHost.Child = new TextBlock { Text = "此 CSVI 組件未內嵌 FrontPanel 前面板。", Foreground = Brushes.Gray, Margin = new Thickness(20), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };

                    }

                }

            }



            // 渲染 Wizard

            if (pnlWizardHost != null)

            {

                if (!string.IsNullOrWhiteSpace(wizardXaml))

                {

                    try

                    {

                        string cleanXaml = XClassRegex.Replace(wizardXaml, "");

                        var ctrl = Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader.Parse<Control>(cleanXaml);

                        pnlWizardHost.Child = ctrl;

                    }

                    catch (Exception ex)

                    {

                        pnlWizardHost.Child = new TextBlock { Text = $"Wizard XAML 解析錯誤：\n{ex.Message}", Foreground = Brushes.Crimson, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };

                    }

                }

                else

                {

                    pnlWizardHost.Child = new TextBlock { Text = "此 CSVI 組件未內嵌 Wizard 參數導引精靈。", Foreground = Brushes.Gray, Margin = new Thickness(20), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };

                }

            }

        }

        catch (Exception ex)

        {

            if (pnlFrontPanelHost != null)

                pnlFrontPanelHost.Child = new TextBlock { Text = $"讀取 CSVI 失敗：{ex.Message}", Foreground = Brushes.Crimson, Margin = new Thickness(12) };

            if (pnlWizardHost != null)

                pnlWizardHost.Child = new TextBlock { Text = $"讀取 CSVI 失敗：{ex.Message}", Foreground = Brushes.Crimson, Margin = new Thickness(12) };

        }

    }



    private void BindCallModule()

    {

        var btnRefresh = this.FindControl<Button>("BtnRefreshCallFiles");

        var lstFiles = this.FindControl<ListBox>("LstCallFiles");

        var txtSelected = this.FindControl<TextBlock>("TxtCallSelectedFile");

        var txtDuration = this.FindControl<TextBlock>("TxtCallDuration");

        var txtStatus = this.FindControl<TextBlock>("TxtCallStatus");

        var txtArgs = this.FindControl<TextBox>("TxtCallTestArgs");

        var cmbMode = this.FindControl<ComboBox>("CmbCallTestMode");

        var btnApplyWizard = this.FindControl<Button>("BtnCallApplyFromWizard");

        var btnRun = this.FindControl<Button>("BtnCallRunTest");

        var txtOutput = this.FindControl<TextBox>("TxtCallTestOutput");

        var btnStudio = this.FindControl<Button>("BtnOpenCsviStudio");



        if (btnRefresh != null) btnRefresh.Click += (_, _) => RefreshCallModuleFiles();



        if (lstFiles != null)

        {

            lstFiles.SelectionChanged += (_, _) =>

            {

                if (lstFiles.SelectedItem is string f && txtSelected != null)

                {

                    txtSelected.Text = f;

                    UpdateCallModulePreview(f);

                    var tabViews = this.FindControl<TabControl>("TabCallViews");

                    if (tabViews != null) tabViews.SelectedIndex = 0;

                }

            };

        }



        if (btnApplyWizard != null)

        {

            btnApplyWizard.Click += (_, _) =>

            {

                var pnlWiz = this.FindControl<Border>("PnlCallWizardHost");

                if (pnlWiz?.Child is Control ctrl)

                {

                    var values = new List<string>();

                    CollectTextBoxValues(ctrl, values);

                    if (values.Count > 0)

                    {

                        string combined = string.Join(", ", values);

                        if (txtArgs != null) txtArgs.Text = combined;

                        if (txtOutput != null) txtOutput.Text = $"[Wizard] 已成功從參數導引精靈帶入測試參數: {combined}";

                    }

                    else

                    {

                        if (txtOutput != null) txtOutput.Text = "[Wizard] 導引精靈中無可提取的 TextBox 輸入欄位。";

                    }

                }

            };

        }



        if (btnRun != null)

        {

            btnRun.Click += (_, _) =>

            {

                string target = txtSelected?.Text ?? "";

                if (string.IsNullOrEmpty(target) || target == "None")

                {

                    if (txtOutput != null) txtOutput.Text = "請先由左側選取要測試的 CSVI 小程式！";

                    return;

                }

                string modCmdName = System.IO.Path.GetFileNameWithoutExtension(target);

                string args = txtArgs?.Text?.Trim() ?? "";

                int modeIdx = cmbMode?.SelectedIndex ?? 0;

                string suffix = modeIdx switch { 1 => "&", 2 => "&&", _ => "" };

                string fullCmd = $"Call@{modCmdName}{suffix}\t{args}";



                var sw = System.Diagnostics.Stopwatch.StartNew();

                try

                {

                    var tabViews = this.FindControl<TabControl>("TabCallViews");

                    if (tabViews != null) tabViews.SelectedIndex = 1;

                    var result = _vm.Executor.ExecuteCommandLine(_vm.MainThreads[0], fullCmd);

                    sw.Stop();

                    if (txtDuration != null) txtDuration.Text = $"{sw.ElapsedMilliseconds} ms";

                    if (txtStatus != null) txtStatus.Text = result.Status == CommandStatus.Completed ? "SUCCESS" : result.Status.ToString();

                    if (txtOutput != null) txtOutput.Text = $"Command: {fullCmd}\nStatus: {result.Status}\nTimestamp: {DateTime.Now:HH:mm:ss.fff}";

                }

                catch (Exception ex)

                {

                    sw.Stop();

                    if (txtDuration != null) txtDuration.Text = $"{sw.ElapsedMilliseconds} ms";

                    if (txtStatus != null) txtStatus.Text = "ERROR";

                    if (txtOutput != null) txtOutput.Text = $"Error executing Call command:\n{ex.Message}";

                }

            };

        }



        if (btnStudio != null)

        {

            btnStudio.Click += (_, _) =>

            {

                string target = txtSelected?.Text ?? "";

                if (!string.IsNullOrEmpty(target) && target != "None")

                {

                    string resolvedPath = ARCOS.Core.Common.ArcosPaths.ResolveExternalModuleCsviPath(target);

                    var studioCtrl = this.FindControl<ARCOS.CsviStudio.Controls.CsviStudioControl>("StudioControl");

                    if (studioCtrl != null && System.IO.File.Exists(resolvedPath))

                    {

                        studioCtrl.LoadFile(resolvedPath);

                    }

                }

                var mainTab = this.FindControl<TabControl>("MainTabControl");

                if (mainTab != null)

                {

                    mainTab.SelectedIndex = 5;

                }

            };

        }

    }



    private void RefreshCallModuleFiles()

    {

        var lstFiles = this.FindControl<ListBox>("LstCallFiles");

        if (lstFiles == null) return;

        var files = new List<string>();



        var candidateDirs = new[]

        {

            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExternalModules"),

            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "dist", "ARCOS_Runtime", "ExternalModules"),

            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "ExternalModules")

        };



        foreach (var dir in candidateDirs)

        {

            if (System.IO.Directory.Exists(dir))

            {

                foreach (var f in System.IO.Directory.GetFiles(dir, "*.csvi"))

                {

                    string fn = System.IO.Path.GetFileName(f);

                    if (!files.Contains(fn)) files.Add(fn);

                }

            }

        }



        if (files.Count == 0)

        {

            files.Add("MyCalc.csvi");

        }



        lstFiles.ItemsSource = files;

        if (files.Count > 0 && lstFiles.SelectedIndex < 0)

        {

            lstFiles.SelectedIndex = 0;

        }

    }



    private void RefreshIoTab()

    {

        var lstDrivers = this.FindControl<ListBox>("LstIoDrivers");

        var driverContainer = this.FindControl<ContentControl>("DriverViewContainer");

        var devices = _driverManager.GetDevicesByCategory("IO");



        if (devices.Count == 0)

        {

            if (lstDrivers != null) lstDrivers.ItemsSource = new List<string>();

            if (driverContainer != null)

            {

                driverContainer.Content = CreateDriverNotMountedPlaceholder("IO", "IO@AddIODriver StationIO, MockIO");

            }

            return;

        }



        if (lstDrivers != null)

        {

            var driverList = devices.Select(dev => $"{dev.DeviceName} ({dev.DriverType})").ToList();

            lstDrivers.ItemsSource = driverList;

            if (lstDrivers.SelectedIndex < 0 && driverList.Count > 0)

            {

                lstDrivers.SelectedIndex = 0;

            }

        }

    }



    private void BindCaptureModule()

    {

        var lstDrivers = this.FindControl<ListBox>("LstCaptureDrivers");

        var host = this.FindControl<ContentControl>("PnlCaptureDriverSubPanelHost");

        var rbDriver = this.FindControl<RadioButton>("RbCaptureDriverView");

        var rbImaq = this.FindControl<RadioButton>("RbCaptureImaqView");

        var pnlImaq = this.FindControl<Grid>("PnlCaptureImaqInspector");

        var btnRefresh = this.FindControl<Button>("BtnRefreshImaq");



        if (rbDriver != null && rbImaq != null)

        {

            rbDriver.IsCheckedChanged += (_, _) =>

            {

                if (host != null) host.IsVisible = rbDriver.IsChecked == true;

                if (pnlImaq != null) pnlImaq.IsVisible = rbImaq.IsChecked == true;

            };

            rbImaq.IsCheckedChanged += (_, _) =>

            {

                if (host != null) host.IsVisible = rbDriver.IsChecked == true;

                if (pnlImaq != null) pnlImaq.IsVisible = rbImaq.IsChecked == true;

            };

        }



        if (lstDrivers != null)

        {

            lstDrivers.SelectionChanged += (_, _) =>

            {

                if (lstDrivers.SelectedItem is string rawName && host != null)

                {

                    string driverName = rawName.Split(' ')[0];

                    var driver = _driverManager.GetDevice("Capture", driverName);

                    if (driver != null)

                    {

                        host.Content = driver.CreateDriverView();

                    }

                    else

                    {

                        host.Content = CreateDriverNotMountedPlaceholder("Capture", "Capture@AddCam CamTop, MockCamera");

                    }

                }

            };

        }

    }



    private void RefreshCaptureTab()

    {

        var lstDrivers = this.FindControl<ListBox>("LstCaptureDrivers");

        var host = this.FindControl<ContentControl>("PnlCaptureDriverSubPanelHost");

        var devices = _driverManager.GetDevicesByCategory("Capture");



        if (devices.Count == 0)

        {

            if (lstDrivers != null) lstDrivers.ItemsSource = new List<string>();

            if (host != null)

            {

                host.Content = CreateDriverNotMountedPlaceholder("Capture", "Capture@AddCam CamTop, MockCamera");

            }

        }

        else

        {

            if (lstDrivers != null)

            {

                var driverList = devices.Select(dev => $"{dev.DeviceName} ({dev.DriverType})").ToList();

                lstDrivers.ItemsSource = driverList;

                if (lstDrivers.SelectedIndex < 0 && driverList.Count > 0)

                {

                    lstDrivers.SelectedIndex = 0;

                }

            }

        }



        // 觸發刷新 IMAQ 清單

        var lstImaq = this.FindControl<ListBox>("LstImaqImages");

        var txtSummary = this.FindControl<TextBlock>("TxtImaqSummary");

        if (lstImaq != null)

        {

            var names = _engines.Imaq.GetAllNames();

            if (txtSummary != null) txtSummary.Text = $"Total Images: {names.Count}";

            string? prevSel = lstImaq.SelectedItem as string;

            lstImaq.ItemsSource = names;

            if (!string.IsNullOrEmpty(prevSel) && names.Contains(prevSel)) lstImaq.SelectedItem = prevSel;

            else if (names.Count > 0) lstImaq.SelectedIndex = 0;

        }

    }



    private void BindMotionModule()

    {

        var lstDrivers = this.FindControl<ListBox>("LstMotionDrivers");

        var host = this.FindControl<ContentControl>("PnlMotionDriverSubPanelHost");

        var btnRefresh = this.FindControl<Button>("BtnRefreshMotionDrivers");



        if (btnRefresh != null)

        {

            btnRefresh.Click += (_, _) => RefreshMotionTab();

        }



        if (lstDrivers != null)

        {

            lstDrivers.SelectionChanged += (_, _) =>

            {

                if (lstDrivers.SelectedItem is string rawName && host != null)

                {

                    string driverName = rawName.Split(' ')[0];

                    var driver = _driverManager.GetDevice("Motion", driverName);

                    if (driver != null)

                    {

                        host.Content = driver.CreateDriverView();

                    }

                    else

                    {

                        host.Content = CreateDriverNotMountedPlaceholder("Motion", "Motion@AddMotion RobotGantry, MockMotion");

                    }

                }

            };

        }

    }



    private void RefreshMotionTab()

    {

        var lstDrivers = this.FindControl<ListBox>("LstMotionDrivers");

        var host = this.FindControl<ContentControl>("PnlMotionDriverSubPanelHost");

        var devices = _driverManager.GetDevicesByCategory("Motion");



        if (devices.Count == 0)

        {

            if (lstDrivers != null) lstDrivers.ItemsSource = new List<string>();

            if (host != null)

            {

                host.Content = CreateDriverNotMountedPlaceholder("Motion", "Motion@AddMotion RobotGantry, MockMotion");

            }

        }

        else

        {

            if (lstDrivers != null)

            {

                var driverList = devices.Select(dev => $"{dev.DeviceName} ({dev.DriverType})").ToList();

                lstDrivers.ItemsSource = driverList;

                if (lstDrivers.SelectedIndex < 0 && driverList.Count > 0)

                {

                    lstDrivers.SelectedIndex = 0;

                }

            }

        }

    }



    private void BindDaqModule()

    {

        var lstDrivers = this.FindControl<ListBox>("LstDaqDrivers");

        var host = this.FindControl<ContentControl>("PnlDaqDriverSubPanelHost");

        var rbDriver = this.FindControl<RadioButton>("RbDaqDriverView");

        var rbWave = this.FindControl<RadioButton>("RbDaqWaveformView");

        var pnlWave = this.FindControl<Border>("PnlDaqWaveformHost");



        if (rbDriver != null && rbWave != null)

        {

            rbDriver.IsCheckedChanged += (_, _) =>

            {

                if (host != null) host.IsVisible = rbDriver.IsChecked == true;

                if (pnlWave != null) pnlWave.IsVisible = rbWave.IsChecked == true;

            };

            rbWave.IsCheckedChanged += (_, _) =>

            {

                if (host != null) host.IsVisible = rbDriver.IsChecked == true;

                if (pnlWave != null) pnlWave.IsVisible = rbWave.IsChecked == true;

            };

        }



        if (lstDrivers != null)

        {

            lstDrivers.SelectionChanged += (_, _) =>

            {

                if (lstDrivers.SelectedItem is string rawName && host != null)

                {

                    string driverName = rawName.Split(' ')[0];

                    var driver = _driverManager.GetDevice("DAQ", driverName);

                    if (driver != null)

                    {

                        host.Content = driver.CreateDriverView();

                    }

                    else

                    {

                        host.Content = CreateDriverNotMountedPlaceholder("DAQ", "DAQ@AddDAQ Daq0, MockDAQ");

                    }

                }

            };

        }

    }



    private void RefreshDaqTab()

    {

        var lstDrivers = this.FindControl<ListBox>("LstDaqDrivers");

        var host = this.FindControl<ContentControl>("PnlDaqDriverSubPanelHost");

        var devices = _driverManager.GetDevicesByCategory("DAQ");



        if (devices.Count == 0)

        {

            if (lstDrivers != null) lstDrivers.ItemsSource = new List<string>();

            if (host != null)

            {

                host.Content = CreateDriverNotMountedPlaceholder("DAQ", "DAQ@AddDAQ Daq0, MockDAQ");

            }

        }

        else

        {

            if (lstDrivers != null)

            {

                var driverList = devices.Select(dev => $"{dev.DeviceName} ({dev.DriverType})").ToList();

                lstDrivers.ItemsSource = driverList;

                if (lstDrivers.SelectedIndex < 0 && driverList.Count > 0)

                {

                    lstDrivers.SelectedIndex = 0;

                }

            }

        }



        // 刷新波形清單

        var lstWaves = this.FindControl<ListBox>("LstWaveforms");

        var txtSummary = this.FindControl<TextBlock>("TxtWaveformSummary");

        if (lstWaves != null)

        {

            var names = _engines.Daq.GetAllNames();

            if (txtSummary != null) txtSummary.Text = $"Total Waveforms: {names.Count}";

            string? prevSel = lstWaves.SelectedItem as string;

            lstWaves.ItemsSource = names;

            if (!string.IsNullOrEmpty(prevSel) && names.Contains(prevSel)) lstWaves.SelectedItem = prevSel;

            else if (names.Count > 0) lstWaves.SelectedIndex = 0;

        }

    }



    private void BindIoModulesTab()

    {

        var lstDrivers = this.FindControl<ListBox>("LstIoDrivers");

        var driverContainer = this.FindControl<ContentControl>("DriverViewContainer");

        if (lstDrivers == null) return;



        RefreshIoTab();



        lstDrivers.SelectionChanged += (_, _) =>

        {

            if (lstDrivers.SelectedItem is string rawName && driverContainer != null)

            {

                string driverName = rawName.Split(' ')[0];

                var driver = _driverManager.GetDevice("IO", driverName);

                if (driver != null)

                {

                    driverContainer.Content = driver.CreateDriverView();

                }

                else

                {

                    driverContainer.Content = CreateDriverNotMountedPlaceholder("IO", "IO@AddIODriver StationIO, MockIO");

                }

            }

        };

    }





    private void BindCaptureTabControls()

    {

        // 0. CamList 相機切換 (藍框需求：點選相機清單立即切換並預覽該相機)

        var lstCam = this.FindControl<ListBox>("LstCaptureCameras");

        if (lstCam != null)

        {

            lstCam.SelectionChanged += (_, _) =>

            {

                if (lstCam.SelectedItem is string raw)

                {

                    string camName = raw.Contains("(") && raw.Contains(")")

                        ? raw.Substring(raw.IndexOf('(') + 1).TrimEnd(')').Trim()

                        : raw.Trim();

                    _selectedCameraName = camName;

                    PerformSnap(_selectedCameraName);

                }

            };

        }



        // 1. Snap 拍照按鈕 (需求：單擊拍單張，Double Click 切換 5fps 連續擷取，再單擊退回單張)

        var btnSnap = this.FindControl<Button>("BtnCaptureSnap");

        if (btnSnap != null)

        {

            Action triggerLive = () =>

            {

                _isContinuousLive = true;

                btnSnap.Content = "● LIVE (5fps)";

                btnSnap.Background = new SolidColorBrush(Color.Parse("#10B981"));

                btnSnap.Foreground = Brushes.White;



                if (_snapLiveTimer == null)

                {

                    _snapLiveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };

                    _snapLiveTimer.Tick += (_, _) =>

                    {

                        if (_isContinuousLive)

                        {

                            PerformSnap(_selectedCameraName);

                        }

                    };

                }

                _snapLiveTimer.Start();

            };



            Action stopLive = () =>

            {

                _isContinuousLive = false;

                _snapLiveTimer?.Stop();

                btnSnap.Content = "Snap";

                btnSnap.Background = new SolidColorBrush(Color.Parse("#0284C7"));

                btnSnap.Foreground = Brushes.White;

            };



            btnSnap.DoubleTapped += (_, e) =>

            {

                e.Handled = true;

                triggerLive();

            };



            btnSnap.Click += (_, _) =>

            {

                var now = DateTime.Now;

                if ((now - _lastSnapClickTime).TotalMilliseconds < 500)

                {

                    _lastSnapClickTime = DateTime.MinValue;

                    triggerLive();

                    return;

                }

                _lastSnapClickTime = now;



                if (_isContinuousLive)

                {

                    stopLive();

                }

                else

                {

                    PerformSnap(_selectedCameraName);

                }

            };

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



        // 若已有相機驅動掛載，觸發初始單幀預覽

        if (_driverManager.GetDevicesByCategory("Capture").Count > 0)

        {

            PerformSnap();

        }

    }



    private async void PerformSnap(string? camName = null)

    {

        string targetCam = !string.IsNullOrEmpty(camName) ? camName : _selectedCameraName;

        var driver = _driverManager.GetDriver("Capture", targetCam) ?? _driverManager.GetDevicesByCategory("Capture").FirstOrDefault();

        if (driver == null) return;



        try

        {

            var ticket = new DriverTicket("Snap");

            driver.PostTicket(ticket);

            var res = await ticket.Task;

            if (res.Success)

            {

                var frame = _engines.Image.GetFrame(targetCam);

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

        catch (Exception ex)

        {

            _vm.MainThreads[0].Log($"[Capture] Snap warning: {ex.Message}");

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



    private void UpdateUserStatusBadge()
    {
        var border = this.FindControl<Border>("BorderUserStatus");
        var ledOk = this.FindControl<Ellipse>("LedUserOk");
        var lbl = this.FindControl<TextBlock>("TxtUserStatusLabel");
        if (ledOk == null) return;

        if (_vm.IsEmgStopped)
        {
            ledOk.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            if (border != null)
            {
                border.Background = new SolidColorBrush(Color.Parse("#FEF2F2"));
                border.BorderBrush = new SolidColorBrush(Color.Parse("#FCA5A5"));
            }
            if (lbl != null)
            {
                lbl.Text = "EMG STOP";
                lbl.Foreground = new SolidColorBrush(Color.Parse("#B91C1C"));
            }
            return;
        }

        bool isPaused = _engines.Timer.IsSystemPaused;
        bool isRunning = _vm.MainThreads[0].IsRunning;
        if (isPaused)
        {
            ledOk.Fill = new SolidColorBrush(Color.FromRgb(220, 140, 0));
            if (border != null)
            {
                border.Background = new SolidColorBrush(Color.Parse("#FFFBEB"));
                border.BorderBrush = new SolidColorBrush(Color.Parse("#FCD34D"));
            }
            if (lbl != null)
            {
                lbl.Text = "PAUSE";
                lbl.Foreground = new SolidColorBrush(Color.Parse("#92400E"));
            }
        }
        else if (isRunning)
        {
            ledOk.Fill = new SolidColorBrush(Color.FromRgb(0, 204, 68));
            if (border != null)
            {
                border.Background = new SolidColorBrush(Color.Parse("#F0FDF4"));
                border.BorderBrush = new SolidColorBrush(Color.Parse("#86EFAC"));
            }
            if (lbl != null)
            {
                lbl.Text = "RUNNING";
                lbl.Foreground = new SolidColorBrush(Color.Parse("#065F46"));
            }
        }
        else
        {
            ledOk.Fill = new SolidColorBrush(Color.FromRgb(120, 120, 120));
            if (border != null)
            {
                border.Background = new SolidColorBrush(Color.Parse("#F8FAFC"));
                border.BorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1"));
            }
            if (lbl != null)
            {
                lbl.Text = "READY";
                lbl.Foreground = new SolidColorBrush(Color.Parse("#334155"));
            }
        }
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        // 核心準則：程式執行速度優先，非 Active 的 UI 絕對禁止更新 (Zero-Cost Inactive UI)
        if (!this.IsVisible || this.WindowState == WindowState.Minimized) return;

        var mainTab = this.FindControl<TabControl>("MainTabControl");
        int activeTabIdx = mainTab?.SelectedIndex ?? 0;

        // 1. 若處於 User Tab (Index 0)：僅刷新必要之機台運轉/暫停/EMG LED 狀態燈
        if (activeTabIdx == 0)
        {
            UpdateUserStatusBadge();
            return;
        }

        // 2. 僅當處於 Capture Tab (Index 1) 時，才刷新無限點位 IO 狀態
        if (activeTabIdx == 1)
        {
            for (int i = 0; i < _ioChannels.Count; i++)
            {
                var ch = _ioChannels[i];
                ch.State = _engines.Io.GetPort(ch.Port);
            }
            return;
        }

        // 3. 僅當處於 Modules Info Tab (Index 2) 時，依據當前可見的子面板靶向局部刷新
        if (activeTabIdx == 2)
        {
            var pnlSystem = this.FindControl<Grid>("PnlModuleSystem");
            if (pnlSystem != null && pnlSystem.IsVisible)
            {
                // RecordIndex 與表格遵循 On-Demand Pull 原則，僅在點擊 Get RecordS 或選單時更新
                return;
            }

            var pnlMotion = this.FindControl<Grid>("PnlModuleMotion");
            if (pnlMotion != null && pnlMotion.IsVisible)
            {
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
                if (txtDroH != null) txtDroH.Text = $"H: {curPt.GetHString()}";
                return;
            }
        }
    }

    // =========================================================
    // 動態載入、多面板駐留與切換 (SYSTEM@UserPanelOpen / USER@SwitchUserPanel)
    // =========================================================

    private LoadedUserPanelSlot? ResolveUserPanelSlot(string? panelName)
    {
        if (_loadedUserPanels.Count == 0) return null;
        string key = string.IsNullOrWhiteSpace(panelName) ? _activeUserPanelName : panelName.Trim();
        if (_loadedUserPanels.TryGetValue(key, out var exact)) return exact;

        string noExt = System.IO.Path.GetFileNameWithoutExtension(key);
        foreach (var slot in _loadedUserPanels.Values)
        {
            if (string.Equals(slot.PanelName, noExt, StringComparison.OrdinalIgnoreCase))
                return slot;

            if (!string.IsNullOrWhiteSpace(slot.FilePath))
            {
                string fName = System.IO.Path.GetFileName(slot.FilePath);
                string fStem = System.IO.Path.GetFileNameWithoutExtension(slot.FilePath);
                if (string.Equals(fName, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fStem, noExt, StringComparison.OrdinalIgnoreCase))
                {
                    return slot;
                }
            }
        }
        return null;
    }

    public bool LoadUserPanelFromCsvi(string csviNameOrPath)
    {
        try
        {
            var (csviSpec, panelAlias, activate) = ARCOS.Core.Engines.UserPanelEngine.ParseOpenSpec(csviNameOrPath);
            string resolved = ARCOS.Core.Common.ArcosPaths.ResolveUserPanelCsviPath(csviSpec);
            if (!File.Exists(resolved))
            {
                _vm.MainThreads[0].Log($"[UserPanelOpen] Error: CSVI file '{csviSpec}' not found (resolved: '{resolved}')", isError: true);
                return false;
            }

            var container = this.FindControl<ContentControl>("UserFrontPanelContainer");
            if (container == null) return false;

            // 若同名面板已載入，僅卸載該舊插槽以利熱重載，保留其他已開啟面板
            if (_loadedUserPanels.TryGetValue(panelAlias, out var existingSlot))
            {
                UnloadSlotResources(existingSlot);
                _loadedUserPanels.Remove(panelAlias);
                _loadedUserPanelOrder.RemoveAll(n => string.Equals(n, panelAlias, StringComparison.OrdinalIgnoreCase));
            }

            var pkg = ARCOS.Core.Csvi.CsviPackage.Load(resolved);
            object? panel = null;
            string cmdKey = !string.IsNullOrEmpty(pkg.Manifest.CommandKey)
                ? pkg.Manifest.CommandKey
                : System.IO.Path.GetFileNameWithoutExtension(resolved);

            if (pkg.Manifest.ComponentType == ARCOS.Core.Csvi.CsviComponentType.Driver)
            {
                var dev = _driverManager.GetDevice(pkg.Manifest.Module ?? "Custom", cmdKey);
                panel = dev?.CreateDriverView();
            }

            // 優先直接從目標 CSVI 組件本身實例化 FrontPanel（避免誤落回 ParameterGuidanceManager 的模組預設 System.csvi）
            if (panel == null && pkg.LoadedAssembly != null)
            {
                var guidanceType = pkg.LoadedAssembly.GetTypes().FirstOrDefault(t =>
                    typeof(ARCOS.Core.Guidance.IParameterGuidance).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                if (guidanceType != null && Activator.CreateInstance(guidanceType) is ARCOS.Core.Guidance.IParameterGuidance g)
                {
                    panel = g.GetFrontPanelControl();
                }

                if (panel == null)
                {
                    var ctrlType = pkg.LoadedAssembly.GetTypes().FirstOrDefault(t => typeof(Control).IsAssignableFrom(t) && !t.IsAbstract);
                    if (ctrlType != null)
                    {
                        panel = Activator.CreateInstance(ctrlType);
                    }
                }
            }

            if (panel == null)
            {
                panel = ARCOS.App.Guidance.ParameterGuidanceManager.Instance.GetFrontPanel(cmdKey)
                     ?? ARCOS.App.Guidance.ParameterGuidanceManager.Instance.GetFrontPanel($"{pkg.Manifest.Module}@{cmdKey}");
            }

            if (panel is not Control ctrl)
            {
                try { pkg.Unload(); } catch { }
                _vm.MainThreads[0].Log($"[UserPanelOpen] Warning: CSVI '{cmdKey}' has no UI FrontPanel.", isError: true);
                return false;
            }

            var m1 = ctrl.GetType().GetMethod("Init") ?? ctrl.GetType().GetMethod("RefreshFromEngines");
            if (m1 != null)
            {
                m1.Invoke(ctrl, new object[] { _engines });
            }
            else if (ctrl.DataContext != null)
            {
                var m2 = ctrl.DataContext.GetType().GetMethod("Init") ?? ctrl.DataContext.GetType().GetMethod("RefreshFromEngines");
                m2?.Invoke(ctrl.DataContext, new object[] { _engines });
            }

            var imgNames = DiscoverAndRegisterUserPanelControls(panelAlias, ctrl, resolved, activate);
            var newSlot = new LoadedUserPanelSlot
            {
                PanelName = panelAlias,
                FilePath = resolved,
                Package = pkg,
                RootControl = ctrl,
                ImageControlNames = imgNames
            };
            _loadedUserPanels[panelAlias] = newSlot;
            if (!_loadedUserPanelOrder.Any(n => string.Equals(n, panelAlias, StringComparison.OrdinalIgnoreCase)))
            {
                _loadedUserPanelOrder.Add(panelAlias);
            }

            ApplyEngineSnapshotToSlot(newSlot);

            if (activate || _loadedUserPanels.Count == 1)
            {
                ActivateUserPanelSlot(panelAlias);
            }
            else
            {
                RefreshUserPanelSwitcherBar();
            }

            _vm.MainThreads[0].Log($"[UserPanelOpen] Mounted User Panel '{panelAlias}' ({System.IO.Path.GetFileName(resolved)}) [Total={_loadedUserPanels.Count}, Active={_activeUserPanelName}]");
            return true;
        }
        catch (Exception ex)
        {
            var baseEx = ex.GetBaseException();
            _vm.MainThreads[0].Log($"[UserPanelOpen] Exception loading User Panel: {baseEx.Message}", isError: true);
            return false;
        }
    }

    public bool ActivateUserPanelSlot(string panelName)
    {
        var slot = ResolveUserPanelSlot(panelName);
        if (slot == null) return false;

        var container = this.FindControl<ContentControl>("UserFrontPanelContainer");
        if (container == null) return false;

        if (!ReferenceEquals(container.Content, slot.RootControl))
        {
            container.Content = slot.RootControl;
        }

        _activeUserPanelName = slot.PanelName;
        _engines.Image.SetUserDisplayControls(slot.ImageControlNames);

        if (!_isSyncingActivePanelFromEngine &&
            !string.Equals(_engines.UserPanel.ActivePanelName, slot.PanelName, StringComparison.OrdinalIgnoreCase))
        {
            _isSyncingActivePanelFromEngine = true;
            try { _engines.UserPanel.SwitchPanel(slot.PanelName); }
            finally { _isSyncingActivePanelFromEngine = false; }
        }

        RefreshUserPanelSwitcherBar();
        return true;
    }

    /// <summary>
    /// 在主視窗樣式與排版環境下對任意已載入（含背景未顯示）的 UserPanel 進行無干擾擷圖，
    /// 擷取結束後立即無縫恢復原本容器內容，不改動使用者的前景作用中面板與切換列。
    /// </summary>
    public bool TryRenderUserPanelSlot(string? panelName, Func<Control, int, int, bool> renderCallback, out string resolvedPanelName, out string? error)
    {
        resolvedPanelName = string.Empty;
        error = null;

        var container = this.FindControl<ContentControl>("UserFrontPanelContainer");
        if (container == null)
        {
            error = "UserFrontPanelContainer not found.";
            return false;
        }

        Control? targetCtrl = null;
        if (!string.IsNullOrWhiteSpace(panelName))
        {
            var slot = ResolveUserPanelSlot(panelName);
            if (slot == null)
            {
                error = $"Loaded UserPanel '{panelName}' not found. Loaded panels: {string.Join(", ", _loadedUserPanelOrder)}";
                return false;
            }
            resolvedPanelName = slot.PanelName;
            targetCtrl = slot.RootControl;
        }
        else
        {
            resolvedPanelName = _activeUserPanelName;
            targetCtrl = (container.Content as Control) ?? ResolveUserPanelSlot(_activeUserPanelName)?.RootControl;
        }

        if (targetCtrl == null)
        {
            error = "No active or loaded UserPanel to capture.";
            return false;
        }

        int w = (int)Math.Round(container.Bounds.Width > 50 ? container.Bounds.Width : 760);
        int h = (int)Math.Round(container.Bounds.Height > 50 ? container.Bounds.Height : 520);

        var prevContent = container.Content;
        bool needTempSwap = !ReferenceEquals(prevContent, targetCtrl);
        if (needTempSwap)
        {
            container.Content = targetCtrl;
            container.UpdateLayout();
        }
        try
        {
            return renderCallback(targetCtrl, w, h);
        }
        finally
        {
            if (needTempSwap)
            {
                container.Content = prevContent;
                container.UpdateLayout();
            }
        }
    }

    public bool UnloadUserPanelSlot(string panelName)
    {
        var slot = ResolveUserPanelSlot(panelName);
        if (slot == null) return false;

        bool wasActive = string.Equals(_activeUserPanelName, slot.PanelName, StringComparison.OrdinalIgnoreCase);
        UnloadSlotResources(slot);
        _loadedUserPanels.Remove(slot.PanelName);
        _loadedUserPanelOrder.RemoveAll(n => string.Equals(n, slot.PanelName, StringComparison.OrdinalIgnoreCase));

        if (!string.Equals(slot.PanelName, "", StringComparison.Ordinal))
        {
            _engines.UserPanel.ClosePanel(slot.PanelName);
        }

        var container = this.FindControl<ContentControl>("UserFrontPanelContainer");
        if (_loadedUserPanels.Count == 0)
        {
            _activeUserPanelName = ARCOS.Core.Engines.UserPanelEngine.DefaultPanelName;
            _engines.Image.SetUserDisplayControls(Array.Empty<string>());
            if (container != null) container.Content = _defaultSubPanelContent;
        }
        else if (wasActive)
        {
            string fallback = _loadedUserPanelOrder[^1];
            ActivateUserPanelSlot(fallback);
            return true;
        }

        RefreshUserPanelSwitcherBar();
        return true;
    }

    private void UnloadSlotResources(LoadedUserPanelSlot slot)
    {
        foreach (var ctrlName in slot.BlinkControls.ToList())
        {
            var ctrl = FindUserPanelNamedControl(slot.RootControl, ctrlName);
            if (ctrl != null && slot.BlinkOriginalOpacity.TryGetValue(ctrlName, out double orig))
            {
                ctrl.Opacity = orig;
            }
        }
        slot.BlinkControls.Clear();
        slot.BlinkOriginalOpacity.Clear();

        var container = this.FindControl<ContentControl>("UserFrontPanelContainer");
        if (container != null && ReferenceEquals(container.Content, slot.RootControl))
        {
            container.Content = null;
        }

        try
        {
            if (slot.RootControl is IDisposable d) d.Dispose();
            slot.Package?.Unload();
        }
        catch { }
    }

    private void UnloadAllUserPanels()
    {
        ClearUserPanelBindingsAndBlink();
        foreach (var slot in _loadedUserPanels.Values.ToList())
        {
            UnloadSlotResources(slot);
        }
        _loadedUserPanels.Clear();
        _loadedUserPanelOrder.Clear();
        _activeUserPanelName = ARCOS.Core.Engines.UserPanelEngine.DefaultPanelName;
        RefreshUserPanelSwitcherBar();
    }

    private void RefreshUserPanelSwitcherBar()
    {
        var bar = this.FindControl<Border>("UserPanelSwitcherBar");
        var stack = this.FindControl<StackPanel>("UserPanelSwitcherButtons");
        var lblCount = this.FindControl<TextBlock>("TxtLoadedPanelsCount");
        if (bar == null || stack == null) return;

        stack.Children.Clear();
        bar.IsVisible = _loadedUserPanels.Count > 0;
        if (lblCount != null)
        {
            lblCount.Text = $"{_loadedUserPanels.Count} Panel{(_loadedUserPanels.Count == 1 ? "" : "s")}";
        }

        foreach (var pName in _loadedUserPanelOrder)
        {
            if (!_loadedUserPanels.TryGetValue(pName, out var slot)) continue;
            bool isActive = string.Equals(slot.PanelName, _activeUserPanelName, StringComparison.OrdinalIgnoreCase);

            var pillGrid = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            var btnSelect = new Button
            {
                Content = slot.PanelName,
                Padding = new Thickness(10, 3),
                FontSize = 11,
                FontWeight = isActive ? FontWeight.Bold : FontWeight.SemiBold,
                Background = new SolidColorBrush(Color.Parse(isActive ? "#0284C7" : "#FFFFFF")),
                Foreground = new SolidColorBrush(Color.Parse(isActive ? "#FFFFFF" : "#334155")),
                BorderBrush = new SolidColorBrush(Color.Parse(isActive ? "#0284C7" : "#CBD5E1")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4, 0, 0, 4),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            string captureName = slot.PanelName;
            btnSelect.Click += (_, _) => ActivateUserPanelSlot(captureName);

            var btnClose = new Button
            {
                Content = "×",
                Padding = new Thickness(6, 3),
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Background = new SolidColorBrush(Color.Parse(isActive ? "#0369A1" : "#F8FAFC")),
                Foreground = new SolidColorBrush(Color.Parse(isActive ? "#E0F2FE" : "#64748B")),
                BorderBrush = new SolidColorBrush(Color.Parse(isActive ? "#0284C7" : "#CBD5E1")),
                BorderThickness = new Thickness(0, 1, 1, 1),
                CornerRadius = new CornerRadius(0, 4, 4, 0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            btnClose.Click += (_, _) => UnloadUserPanelSlot(captureName);

            Grid.SetColumn(btnSelect, 0);
            Grid.SetColumn(btnClose, 1);
            pillGrid.Children.Add(btnSelect);
            pillGrid.Children.Add(btnClose);
            stack.Children.Add(pillGrid);
        }
    }

    private void ApplyEngineSnapshotToSlot(LoadedUserPanelSlot slot)
    {
        var items = _engines.UserPanel.QueryControlsInPanel(slot.PanelName);
        foreach (var item in items)
        {
            var ctrl = FindUserPanelNamedControl(slot.RootControl, item.Name);
            if (ctrl == null) continue;
            ctrl.IsEnabled = item.IsEnabled;
            ctrl.IsVisible = item.IsVisible;
            if (item.IsBlinking)
            {
                if (!slot.BlinkOriginalOpacity.ContainsKey(item.Name))
                    slot.BlinkOriginalOpacity[item.Name] = ctrl.Opacity;
                slot.BlinkControls.Add(item.Name);
                EnsureUserBlinkTimer();
            }
        }
    }

    private List<string> DiscoverAndRegisterUserPanelControls(string panelName, Control root, string filePath, bool activate)
    {
        var imgNames = new List<string>();
        var discovered = new List<ARCOS.Core.Engines.UserPanelControlInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string GetElementId(Control c)
        {
            return !string.IsNullOrWhiteSpace(c.Name)
                ? c.Name.Trim()
                : (Avalonia.Automation.AutomationProperties.GetAutomationId(c)?.Trim() ?? "");
        }

        static string ExtractButtonText(ContentControl cc)
        {
            if (cc.Content is string s) return s;
            if (cc.Content is TextBlock tb) return tb.Text ?? "";
            if (cc.Content is Panel panel)
            {
                var innerTb = panel.Children.OfType<TextBlock>().LastOrDefault();
                if (innerTb != null) return innerTb.Text ?? "";
            }
            return cc.Content?.ToString() ?? "";
        }

        void Traverse(object? node)
        {
            if (node is Control ctrl)
            {
                string id = GetElementId(ctrl);
                string tag = ctrl.Tag?.ToString() ?? "";

                if (ctrl is Image && !string.IsNullOrWhiteSpace(id))
                {
                    imgNames.Add(id);
                }

                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                {
                    var kind = ARCOS.Core.Engines.UserPanelControlKind.Unknown;
                    var role = ARCOS.Core.Engines.UserPanelElementRole.Indicator;
                    string val = "";
                    string text = "";
                    string options = "";

                    switch (ctrl)
                    {
                        case CheckBox cb:
                            kind = ARCOS.Core.Engines.UserPanelControlKind.CheckBox;
                            role = ARCOS.Core.Engines.UserPanelElementRole.Control;
                            val = cb.IsChecked == true ? "ON" : "OFF";
                            text = cb.Content?.ToString() ?? "";
                            cb.IsCheckedChanged += (_, _) =>
                            {
                                if (_suppressedUiControls.Contains(cb)) return;
                                _engines.UserPanel.NotifyUiValueChangedInPanel(panelName, id, cb.IsChecked == true ? "ON" : "OFF");
                            };
                            break;
                        case ToggleButton tog:
                            kind = ARCOS.Core.Engines.UserPanelControlKind.ToggleButton;
                            role = ARCOS.Core.Engines.UserPanelElementRole.Control;
                            val = tog.IsChecked == true ? "ON" : "OFF";
                            text = ExtractButtonText(tog);
                            tog.IsCheckedChanged += (_, _) =>
                            {
                                if (_suppressedUiControls.Contains(tog)) return;
                                _engines.UserPanel.NotifyUiValueChangedInPanel(panelName, id, tog.IsChecked == true ? "ON" : "OFF", ExtractButtonText(tog));
                            };
                            break;
                        case Button btn:
                            kind = ARCOS.Core.Engines.UserPanelControlKind.Button;
                            role = ARCOS.Core.Engines.UserPanelElementRole.Control;
                            val = "OFF";
                            text = ExtractButtonText(btn);
                            break;
                        case TextBox tb:
                            kind = ARCOS.Core.Engines.UserPanelControlKind.TextBox;
                            role = tb.IsReadOnly ? ARCOS.Core.Engines.UserPanelElementRole.Indicator : ARCOS.Core.Engines.UserPanelElementRole.Control;
                            val = tb.Text ?? "";
                            tb.TextChanged += (_, _) =>
                            {
                                if (_suppressedUiControls.Contains(tb)) return;
                                _engines.UserPanel.NotifyUiValueChangedInPanel(panelName, id, tb.Text ?? "");
                            };
                            break;
                        case ComboBox cmb:
                            kind = ARCOS.Core.Engines.UserPanelControlKind.ComboBox;
                            role = ARCOS.Core.Engines.UserPanelElementRole.Control;
                            val = cmb.SelectedItem?.ToString() ?? "";
                            options = string.Join("|", cmb.Items.Cast<object?>().Select(i => i?.ToString() ?? ""));
                            cmb.SelectionChanged += (_, _) =>
                            {
                                if (_suppressedUiControls.Contains(cmb)) return;
                                _engines.UserPanel.NotifyUiValueChangedInPanel(panelName, id, cmb.SelectedItem?.ToString() ?? "");
                            };
                            break;
                        case TabControl tab:
                            kind = ARCOS.Core.Engines.UserPanelControlKind.TabControl;
                            role = ARCOS.Core.Engines.UserPanelElementRole.Control;
                            val = tab.SelectedIndex.ToString();
                            options = string.Join("|", tab.Items.OfType<TabItem>().Select(t => !string.IsNullOrWhiteSpace(t.Name) ? t.Name! : (t.Header?.ToString() ?? "")));
                            tab.SelectionChanged += (_, _) =>
                            {
                                if (_suppressedUiControls.Contains(tab)) return;
                                _engines.UserPanel.NotifyUiValueChangedInPanel(panelName, id, tab.SelectedIndex.ToString());
                            };
                            break;
                        case Image:
                            kind = ARCOS.Core.Engines.UserPanelControlKind.Image;
                            role = ARCOS.Core.Engines.UserPanelElementRole.Indicator;
                            break;
                        case ScottPlot.Avalonia.AvaPlot:
                            kind = ARCOS.Core.Engines.UserPanelControlKind.Plot;
                            role = ARCOS.Core.Engines.UserPanelElementRole.Indicator;
                            break;
                        case Border when tag.StartsWith("DAQ:", StringComparison.OrdinalIgnoreCase) || id.StartsWith("Plot", StringComparison.OrdinalIgnoreCase):
                            kind = ARCOS.Core.Engines.UserPanelControlKind.Plot;
                            role = ARCOS.Core.Engines.UserPanelElementRole.Indicator;
                            break;
                        case Ellipse:
                            kind = ARCOS.Core.Engines.UserPanelControlKind.Led;
                            role = ARCOS.Core.Engines.UserPanelElementRole.Indicator;
                            break;
                        case TextBlock lbl:
                            kind = ARCOS.Core.Engines.UserPanelControlKind.TextBlock;
                            role = ARCOS.Core.Engines.UserPanelElementRole.Indicator;
                            val = lbl.Text ?? "";
                            break;
                    }

                    if (kind != ARCOS.Core.Engines.UserPanelControlKind.Unknown)
                    {
                        discovered.Add(new ARCOS.Core.Engines.UserPanelControlInfo
                        {
                            PanelName = panelName,
                            Name = id,
                            Kind = kind,
                            Role = role,
                            Value = val,
                            Text = text,
                            IsEnabled = ctrl.IsEnabled,
                            IsVisible = ctrl.IsVisible,
                            BindingTarget = tag,
                            Options = options,
                            IsMountedOnUi = true
                        });
                    }
                }
            }

            if (node is Avalonia.LogicalTree.ILogical logical)
            {
                foreach (var child in logical.LogicalChildren)
                {
                    Traverse(child);
                }
            }
        }

        Traverse(root);
        _engines.Image.SetUserDisplayControls(imgNames);
        _engines.UserPanel.RegisterPanelControls(panelName, discovered, filePath, activate: activate, replacePanel: true);
        return imgNames;
    }

    private void ClearUserPanelBindingsAndBlink()
    {
        _userBlinkTimer?.Stop();
        foreach (var slot in _loadedUserPanels.Values)
        {
            slot.BlinkControls.Clear();
            slot.BlinkOriginalOpacity.Clear();
        }
        _suppressedUiControls.Clear();
        _engines.UserPanel.Clear();
        _engines.Image.SetUserDisplayControls(Array.Empty<string>());
    }

    private string? ReadUserPanelLiveControlValue(string controlName)
    {
        if (!Dispatcher.UIThread.CheckAccess()) return null;

        var (panelName, ctrlName) = ARCOS.Core.Engines.UserPanelEngine.SplitScopedTarget(controlName, _activeUserPanelName);
        var slot = ResolveUserPanelSlot(panelName);
        Control? searchRoot = slot?.RootControl ?? (this.FindControl<ContentControl>("UserFrontPanelContainer")?.Content as Control);
        if (searchRoot == null) return null;

        var target = FindUserPanelNamedControl(searchRoot, ctrlName);
        if (target == null) return null;

        return target switch
        {
            CheckBox cb => cb.IsChecked == true ? "ON" : "OFF",
            ToggleButton tog => tog.IsChecked == true ? "ON" : "OFF",
            TextBox tb => tb.Text ?? "",
            TextBlock tbl => tbl.Text ?? "",
            ComboBox cmb => cmb.SelectedItem?.ToString() ?? cmb.SelectedIndex.ToString(),
            TabControl tab => tab.SelectedIndex.ToString(),
            Button btn => btn.Content?.ToString() ?? "",
            _ => null
        };
    }

    private void OnUserPanelCommandRequested(ARCOS.Core.Engines.UserPanelCommandRequest req)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (req.CommandType == ARCOS.Core.Engines.UserPanelCommandType.SwitchPanel)
                {
                    if (!_isSyncingActivePanelFromEngine)
                    {
                        _isSyncingActivePanelFromEngine = true;
                        try { ActivateUserPanelSlot(req.ControlName); }
                        finally { _isSyncingActivePanelFromEngine = false; }
                    }
                    return;
                }

                if (req.CommandType == ARCOS.Core.Engines.UserPanelCommandType.ClosePanel)
                {
                    UnloadUserPanelSlot(req.ControlName);
                    return;
                }

                if (req.CommandType == ARCOS.Core.Engines.UserPanelCommandType.ParamRefresh)
                {
                    var refreshSlot = ResolveUserPanelSlot(req.ControlName);
                    Control? refreshRoot = refreshSlot?.RootControl ?? (this.FindControl<ContentControl>("UserFrontPanelContainer")?.Content as Control);
                    if (refreshRoot == null) return;

                    var m = refreshRoot.GetType().GetMethod("RefreshFromEngines") ?? refreshRoot.GetType().GetMethod("Init");
                    if (m != null) m.Invoke(refreshRoot, new object[] { _engines });
                    else if (refreshRoot.DataContext != null)
                    {
                        var m2 = refreshRoot.DataContext.GetType().GetMethod("RefreshFromEngines") ?? refreshRoot.DataContext.GetType().GetMethod("Init");
                        m2?.Invoke(refreshRoot.DataContext, new object[] { _engines });
                    }
                    SyncTaggedUserPanelElements(refreshRoot);
                    return;
                }

                var (parsedPanel, ctrlName) = ARCOS.Core.Engines.UserPanelEngine.SplitScopedTarget(
                    req.ControlName,
                    string.IsNullOrWhiteSpace(req.PanelName) ? _activeUserPanelName : req.PanelName);
                var slot = ResolveUserPanelSlot(parsedPanel);
                Control? searchRoot = slot?.RootControl ?? (this.FindControl<ContentControl>("UserFrontPanelContainer")?.Content as Control);
                if (searchRoot == null) return;

                if (req.CommandType == ARCOS.Core.Engines.UserPanelCommandType.SetBlinking)
                {
                    if (slot == null) return;
                    var blinkCtrl = FindUserPanelNamedControl(searchRoot, ctrlName);
                    if (req.BoolValue)
                    {
                        if (blinkCtrl != null && !slot.BlinkOriginalOpacity.ContainsKey(ctrlName))
                        {
                            slot.BlinkOriginalOpacity[ctrlName] = blinkCtrl.Opacity;
                        }
                        slot.BlinkControls.Add(ctrlName);
                        EnsureUserBlinkTimer();
                    }
                    else
                    {
                        slot.BlinkControls.Remove(ctrlName);
                        if (blinkCtrl != null && slot.BlinkOriginalOpacity.TryGetValue(ctrlName, out double origOp))
                        {
                            blinkCtrl.Opacity = origOp;
                            slot.BlinkOriginalOpacity.Remove(ctrlName);
                        }
                        else if (blinkCtrl != null)
                        {
                            blinkCtrl.Opacity = 1.0;
                        }
                    }
                    return;
                }

                var targetCtrl = FindUserPanelNamedControl(searchRoot, ctrlName);
                if (targetCtrl == null) return;

                ApplyControlCommandToTarget(targetCtrl, req);
            }
            catch { }
        });
    }

    private void ApplyControlCommandToTarget(Control targetCtrl, ARCOS.Core.Engines.UserPanelCommandRequest req)
    {
        switch (req.CommandType)
        {
            case ARCOS.Core.Engines.UserPanelCommandType.ButtonValue:
                ApplyButtonCommand(targetCtrl, req.BoolValue, req.TriggerEvent);
                break;

            case ARCOS.Core.Engines.UserPanelCommandType.SetEnabled:
                targetCtrl.IsEnabled = req.BoolValue;
                break;

            case ARCOS.Core.Engines.UserPanelCommandType.SetVisible:
                targetCtrl.IsVisible = req.BoolValue;
                break;

            case ARCOS.Core.Engines.UserPanelCommandType.ButtonText:
                if (targetCtrl is ContentControl cc)
                {
                    if (cc.Content is Panel panel)
                    {
                        var innerTb = panel.Children.OfType<TextBlock>().LastOrDefault();
                        if (innerTb != null) innerTb.Text = req.Value;
                        else cc.Content = req.Value;
                    }
                    else
                    {
                        cc.Content = req.Value;
                    }
                }
                break;

            case ARCOS.Core.Engines.UserPanelCommandType.TextValue:
                if (targetCtrl is TextBox tb)
                {
                    if (!req.TriggerEvent) _suppressedUiControls.Add(tb);
                    try { tb.Text = req.Value; }
                    finally { _suppressedUiControls.Remove(tb); }
                }
                else if (targetCtrl is TextBlock tbl)
                {
                    tbl.Text = req.Value;
                }
                break;

            case ARCOS.Core.Engines.UserPanelCommandType.SelectTab:
                if (targetCtrl is TabControl tab)
                {
                    int idx = ResolveTabIndex(tab, req.Value);
                    if (idx >= 0)
                    {
                        if (!req.TriggerEvent) _suppressedUiControls.Add(tab);
                        try { tab.SelectedIndex = idx; }
                        finally { _suppressedUiControls.Remove(tab); }
                    }
                }
                break;

            case ARCOS.Core.Engines.UserPanelCommandType.SelectCombo:
                if (targetCtrl is ComboBox cmb)
                {
                    int idx = ResolveComboIndex(cmb, req.Value);
                    if (idx >= 0)
                    {
                        if (!req.TriggerEvent) _suppressedUiControls.Add(cmb);
                        try { cmb.SelectedIndex = idx; }
                        finally { _suppressedUiControls.Remove(cmb); }
                    }
                }
                break;
        }
    }

    private void ApplyButtonCommand(Control targetCtrl, bool on, bool triggerEvent)
    {
        switch (targetCtrl)
        {
            case CheckBox cb:
                if (!triggerEvent) _suppressedUiControls.Add(cb);
                try { cb.IsChecked = on; }
                finally { _suppressedUiControls.Remove(cb); }
                if (triggerEvent)
                {
                    cb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                break;

            case ToggleButton tog:
                if (!triggerEvent) _suppressedUiControls.Add(tog);
                try { tog.IsChecked = on; }
                finally { _suppressedUiControls.Remove(tog); }
                if (triggerEvent)
                {
                    tog.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (tog.Command?.CanExecute(tog.CommandParameter) == true)
                        tog.Command.Execute(tog.CommandParameter);
                }
                break;

            case Button btn:
                btn.BorderBrush = new SolidColorBrush(Color.Parse(on ? "#10B981" : "#CBD5E1"));
                btn.BorderThickness = new Thickness(on ? 2 : 1);
                if (triggerEvent)
                {
                    btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (btn.Command?.CanExecute(btn.CommandParameter) == true)
                        btn.Command.Execute(btn.CommandParameter);
                }
                break;

            case Ellipse led:
                led.Fill = new SolidColorBrush(Color.Parse(on ? "#10B981" : "#64748B"));
                break;
        }
    }

    private void SyncTaggedUserPanelElements(Control root)
    {
        void Visit(object? node)
        {
            if (node is Control c && c.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
            {
                if (tag.StartsWith("IO:", StringComparison.OrdinalIgnoreCase))
                {
                    string port = tag[3..].Trim();
                    bool on = _engines.Io.GetPort(port);
                    if (c is Ellipse led)
                    {
                        led.Fill = new SolidColorBrush(Color.Parse(on ? "#10B981" : "#64748B"));
                    }
                    else if (c is ToggleButton tog)
                    {
                        _suppressedUiControls.Add(tog);
                        try { tog.IsChecked = on; }
                        finally { _suppressedUiControls.Remove(tog); }
                    }
                }
                else if (tag.StartsWith("VAR:", StringComparison.OrdinalIgnoreCase))
                {
                    string varKey = tag[4..].Trim();
                    string val = "";
                    try { val = _engines.Variable.Get(varKey); } catch { }
                    if (!string.IsNullOrEmpty(val))
                    {
                        if (c is TextBox tb)
                        {
                            _suppressedUiControls.Add(tb);
                            try { tb.Text = val; }
                            finally { _suppressedUiControls.Remove(tb); }
                        }
                        else if (c is TextBlock tbl)
                        {
                            tbl.Text = val;
                        }
                    }
                }
            }

            if (node is Avalonia.LogicalTree.ILogical logical)
            {
                foreach (var child in logical.LogicalChildren) Visit(child);
            }
        }
        Visit(root);
    }

    private void EnsureUserBlinkTimer()
    {
        if (_userBlinkTimer == null)
        {
            _userBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _userBlinkTimer.Tick += (_, _) =>
            {
                if (_loadedUserPanels.Values.All(s => s.BlinkControls.Count == 0))
                {
                    _userBlinkTimer.Stop();
                    return;
                }

                _blinkPhase = !_blinkPhase;
                foreach (var slot in _loadedUserPanels.Values.ToList())
                {
                    if (slot.BlinkControls.Count == 0) continue;
                    foreach (var name in slot.BlinkControls.ToList())
                    {
                        var ctrl = FindUserPanelNamedControl(slot.RootControl, name);
                        if (ctrl != null)
                        {
                            double orig = slot.BlinkOriginalOpacity.TryGetValue(name, out var o) ? o : 1.0;
                            ctrl.Opacity = _blinkPhase ? Math.Min(0.35, orig) : orig;
                        }
                    }
                }
            };
        }

        if (!_userBlinkTimer.IsEnabled)
        {
            _userBlinkTimer.Start();
        }
    }

    private static int ResolveTabIndex(TabControl tab, string pageIdxOrName)
    {
        if (int.TryParse(pageIdxOrName, out int idx) && idx >= 0 && idx < tab.Items.Count)
        {
            return idx;
        }
        for (int i = 0; i < tab.Items.Count; i++)
        {
            if (tab.Items[i] is TabItem ti)
            {
                if (string.Equals(ti.Name, pageIdxOrName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ti.Header?.ToString(), pageIdxOrName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }
        return -1;
    }

    private static int ResolveComboIndex(ComboBox cmb, string valOrIdx)
    {
        for (int i = 0; i < cmb.Items.Count; i++)
        {
            if (string.Equals(cmb.Items[i]?.ToString(), valOrIdx, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        if (int.TryParse(valOrIdx, out int idx) && idx >= 0 && idx < cmb.Items.Count)
        {
            return idx;
        }
        return -1;
    }

    private Image? FindUserPanelImageControl(Control root, string controlName)
    {
        var nsFound = NameScope.GetNameScope(root)?.Find<Image>(controlName);
        if (nsFound != null) return nsFound;

        Image? SearchLogical(object? node)
        {
            if (node is Image img)
            {
                string? name = img.Name;
                string? autoId = Avalonia.Automation.AutomationProperties.GetAutomationId(img);
                if (string.Equals(name, controlName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(autoId, controlName, StringComparison.OrdinalIgnoreCase))
                {
                    return img;
                }
            }

            if (node is Avalonia.LogicalTree.ILogical logical)
            {
                foreach (var child in logical.LogicalChildren)
                {
                    var res = SearchLogical(child);
                    if (res != null) return res;
                }
            }
            return null;
        }

        return SearchLogical(root);
    }

    private void OnUserDisplayRequested(string camName, string controlName)
    {
        var (targetPanel, targetCtrlName) = ARCOS.Core.Engines.UserPanelEngine.SplitScopedTarget(controlName, _activeUserPanelName);
        _engines.UserPanel.NotifyUiValueChangedInPanel(targetPanel, targetCtrlName, camName);
        var pngBytes = _engines.Image.RenderFrameWithOverlaysToPngBytes(camName);
        if (pngBytes == null || pngBytes.Length == 0) return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var slot = ResolveUserPanelSlot(targetPanel);
                var container = this.FindControl<ContentControl>("UserFrontPanelContainer");
                Control searchRoot = slot?.RootControl ?? (container?.Content as Control) ?? this;
                var imgCtrl = FindUserPanelImageControl(searchRoot, targetCtrlName);
                if (imgCtrl != null)
                {
                    using var ms = new MemoryStream(pngBytes);
                    imgCtrl.Source = new Avalonia.Media.Imaging.Bitmap(ms);
                }
            }
            catch { }
        });
    }

    private Control? FindUserPanelNamedControl(Control root, string controlName)
    {
        var nsFound = NameScope.GetNameScope(root)?.Find<Control>(controlName);
        if (nsFound != null) return nsFound;

        Control? SearchLogical(object? node)
        {
            if (node is Control ctrl)
            {
                string? name = ctrl.Name;
                string? autoId = Avalonia.Automation.AutomationProperties.GetAutomationId(ctrl);
                if (string.Equals(name, controlName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(autoId, controlName, StringComparison.OrdinalIgnoreCase))
                {
                    return ctrl;
                }
            }

            if (node is Avalonia.LogicalTree.ILogical logical)
            {
                foreach (var child in logical.LogicalChildren)
                {
                    var res = SearchLogical(child);
                    if (res != null) return res;
                }
            }
            return null;
        }

        return SearchLogical(root);
    }

    private void OnDaqUserDisplayRequested(string waveformName, string controlName)
    {
        var (targetPanel, targetCtrlName) = ARCOS.Core.Engines.UserPanelEngine.SplitScopedTarget(controlName, _activeUserPanelName);
        _engines.UserPanel.NotifyUiValueChangedInPanel(targetPanel, targetCtrlName, waveformName);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var slot = ResolveUserPanelSlot(targetPanel);
                var container = this.FindControl<ContentControl>("UserFrontPanelContainer");
                Control searchRoot = slot?.RootControl ?? (container?.Content as Control) ?? this;
                var targetCtrl = FindUserPanelNamedControl(searchRoot, targetCtrlName);
                if (targetCtrl == null) return;

                double[] yData = Array.Empty<double>();
                double dt = 0.001;
                try
                {
                    var entry = _engines.Daq.GetWaveform(waveformName);
                    if (entry.Channels.TryGetValue(0, out var ch))
                    {
                        yData = ch.Y;
                        dt = ch.Dt <= 0 ? 0.001 : ch.Dt;
                    }
                }
                catch { }

                // 1. 若目標是 ContentControl / Border / AvaPlot -> 自動升級為互動式 ScottPlot 向量圖表
                ScottPlot.Avalonia.AvaPlot? avaPlot = targetCtrl as ScottPlot.Avalonia.AvaPlot;
                if (avaPlot == null && targetCtrl is ContentControl cc)
                {
                    avaPlot = cc.Content as ScottPlot.Avalonia.AvaPlot;
                    if (avaPlot == null)
                    {
                        avaPlot = new ScottPlot.Avalonia.AvaPlot { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch };
                        cc.Content = avaPlot;
                    }
                }
                else if (avaPlot == null && targetCtrl is Border bdr)
                {
                    avaPlot = bdr.Child as ScottPlot.Avalonia.AvaPlot;
                    if (avaPlot == null && bdr.Child == null)
                    {
                        avaPlot = new ScottPlot.Avalonia.AvaPlot { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch };
                        bdr.Child = avaPlot;
                    }
                }

                if (avaPlot != null)
                {
                    avaPlot.Plot.Clear();
                    if (yData.Length > 0)
                    {
                        var sig = avaPlot.Plot.Add.Signal(yData, dt);
                        sig.LineWidth = 2.0f;
                        sig.Color = ScottPlot.Colors.MediumSeaGreen;
                        avaPlot.Plot.Axes.AutoScale();
                    }
                    avaPlot.Plot.Title($"DAQ: {waveformName}");
                    avaPlot.Refresh();
                    return;
                }

                // 2. 若目標是 Image 控制項 -> 使用 Avalonia 原生純向量 DrawingImage (PolylineGeometry，100% 向量無損縮放)
                if (targetCtrl is Image imgCtrl)
                {
                    double w = 480, h = 220;
                    var group = new DrawingGroup();

                    // 深色圓角背景
                    group.Children.Add(new GeometryDrawing
                    {
                        Brush = new SolidColorBrush(Color.Parse("#0F172A")),
                        Geometry = new RectangleGeometry(new Avalonia.Rect(0, 0, w, h))
                    });

                    // 向量格線
                    var gridPen = new Pen(new SolidColorBrush(Color.Parse("#1E293B")), 1);
                    var gridGeo = new StreamGeometry();
                    using (var ctx = gridGeo.Open())
                    {
                        for (double gx = 40; gx < w - 16; gx += 60)
                        {
                            ctx.BeginFigure(new Avalonia.Point(gx, 24), false);
                            ctx.LineTo(new Avalonia.Point(gx, h - 20));
                        }
                        for (double gy = 28; gy < h - 20; gy += 40)
                        {
                            ctx.BeginFigure(new Avalonia.Point(40, gy), false);
                            ctx.LineTo(new Avalonia.Point(w - 16, gy));
                        }
                    }
                    group.Children.Add(new GeometryDrawing { Pen = gridPen, Geometry = gridGeo });

                    // 向量波形曲線
                    if (yData.Length > 0)
                    {
                        double minV = yData.Min();
                        double maxV = yData.Max();
                        double span = Math.Max(1e-6, maxV - minV);
                        double plotL = 40, plotR = w - 16, plotT = 28, plotB = h - 20;
                        double plotW = plotR - plotL, plotH = plotB - plotT;

                        var pts = new List<Avalonia.Point>(yData.Length);
                        for (int i = 0; i < yData.Length; i++)
                        {
                            double px = plotL + (i * plotW / Math.Max(1, yData.Length - 1));
                            double py = plotB - ((yData[i] - minV) / span * plotH);
                            pts.Add(new Avalonia.Point(px, py));
                        }

                        var polyGeo = new PolylineGeometry(pts, false);
                        var wavePen = new Pen(new SolidColorBrush(Color.Parse("#10B981")), 2);
                        group.Children.Add(new GeometryDrawing { Pen = wavePen, Geometry = polyGeo });
                    }

                    imgCtrl.Source = new DrawingImage { Drawing = group };
                }
            }
            catch { }
        });
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

        _vm.OnProgramChanged += (progNo) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (mMain?.Context?.Program?.ProgramNumber == progNo)
                {
                    mMain.ReloadProgram();
                }
                for (int i = 1; i <= 12; i++)
                {
                    var mw = this.FindControl<ThreadMonitorControl>($"MonitorThread{i}");
                    if (mw?.Context?.Program?.ProgramNumber == progNo)
                    {
                        mw.ReloadProgram();
                    }
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

                demoProg.Add(CommandItem.Parse("System@Delay\t1.2", 0));

                demoProg.Add(CommandItem.Parse("IO@Set\tDO0,1", 1));

                demoProg.Add(CommandItem.Parse("System@Delay\t0.8", 2));

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

    private bool _endScriptExecuted = false;

    private bool _isExecutingEndScript = false;



    protected override void OnClosing(WindowClosingEventArgs e)

    {

        if (!_endScriptExecuted)

        {

            var endProg = _vm.GetProgram(98);

            if (endProg != null && endProg.Count > 0)

            {

                e.Cancel = true;

                if (_isExecutingEndScript) return;

                _isExecutingEndScript = true;



                _vm.MainThreads[0].Log("[SYSTEM] Executing 98: End Script before exit...");

                var endCtx = _vm.TriggerSystemHook(98);

                if (endCtx != null)

                {

                    endCtx.OnExecutionStopped += _ =>

                    {

                        _endScriptExecuted = true;

                        _isExecutingEndScript = false;

                        Dispatcher.UIThread.Post(() => this.Close());

                    };

                    if (!_vm.AutoSpawnBackgroundLoop)

                    {

                        endCtx.StartBackgroundLoop(_vm.Executor);

                    }

                }

                else

                {

                    _endScriptExecuted = true;

                    _isExecutingEndScript = false;

                    Dispatcher.UIThread.Post(() => this.Close());

                }

                return;

            }

            _endScriptExecuted = true;

        }

        _engines.Record.FlushSystemLogToFile();

        base.OnClosing(e);

    }

    private Task ShowSystemMessageBoxAsync(string threadName, string message)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var btnOk = new Button
                {
                    Name = "BtnMsgBoxOk",
                    Content = "確定 (OK)",
                    Width = 110,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Background = new SolidColorBrush(Color.Parse("#0284C7")),
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold,
                    Padding = new Thickness(12, 6),
                    CornerRadius = new CornerRadius(4)
                };

                var dlg = new Window
                {
                    Name = "SystemMessageBoxWindow",
                    Title = $"ARCOS MessageBox [{threadName}]",
                    Width = 420,
                    Height = 190,
                    CanResize = false,
                    Topmost = true,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = new SolidColorBrush(Color.Parse("#F8FAFC")),
                    Content = new Grid
                    {
                        RowDefinitions = new RowDefinitions("*,Auto"),
                        Margin = new Thickness(18),
                        Children =
                        {
                            new Border
                            {
                                Background = Brushes.White,
                                BorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1")),
                                BorderThickness = new Thickness(1),
                                CornerRadius = new CornerRadius(6),
                                Padding = new Thickness(14),
                                Child = new TextBlock
                                {
                                    Name = "TxtMsgBoxContent",
                                    Text = message,
                                    FontSize = 14,
                                    FontWeight = FontWeight.SemiBold,
                                    Foreground = new SolidColorBrush(Color.Parse("#1E293B")),
                                    TextWrapping = TextWrapping.Wrap,
                                    VerticalAlignment = VerticalAlignment.Center
                                }
                            },
                            new StackPanel
                            {
                                [Grid.RowProperty] = 1,
                                Orientation = Orientation.Horizontal,
                                HorizontalAlignment = HorizontalAlignment.Right,
                                Margin = new Thickness(0, 12, 0, 0),
                                Children = { btnOk }
                            }
                        }
                    }
                };

                btnOk.Click += (_, _) => dlg.Close();
                dlg.KeyDown += (_, ke) =>
                {
                    if (ke.Key == Key.Enter || ke.Key == Key.Escape) dlg.Close();
                };

                await dlg.ShowDialog(this);
                tcs.TrySetResult();
            }
            catch
            {
                tcs.TrySetResult();
            }
        });
        return tcs.Task;
    }

    private Task<bool> ShowSystemWarningBoxAsync(string threadName, string message)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                bool resultChoice = false;
                var btnYes = new Button
                {
                    Name = "BtnWarnBoxYes",
                    Content = "繼續執行 (Yes)",
                    Width = 130,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Background = new SolidColorBrush(Color.Parse("#16A34A")),
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold,
                    Padding = new Thickness(12, 6),
                    CornerRadius = new CornerRadius(4)
                };

                var btnNo = new Button
                {
                    Name = "BtnWarnBoxNo",
                    Content = "停止執行 (No)",
                    Width = 130,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Background = new SolidColorBrush(Color.Parse("#DC2626")),
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold,
                    Padding = new Thickness(12, 6),
                    CornerRadius = new CornerRadius(4)
                };

                var dlg = new Window
                {
                    Name = "SystemWarningBoxWindow",
                    Title = $"ARCOS WarningBox [{threadName}]",
                    Width = 460,
                    Height = 210,
                    CanResize = false,
                    Topmost = true,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = new SolidColorBrush(Color.Parse("#FFFBEB")),
                    Content = new Grid
                    {
                        RowDefinitions = new RowDefinitions("*,Auto"),
                        Margin = new Thickness(18),
                        Children =
                        {
                            new Border
                            {
                                Background = Brushes.White,
                                BorderBrush = new SolidColorBrush(Color.Parse("#F59E0B")),
                                BorderThickness = new Thickness(1.5),
                                CornerRadius = new CornerRadius(6),
                                Padding = new Thickness(14),
                                Child = new TextBlock
                                {
                                    Name = "TxtWarnBoxContent",
                                    Text = $"⚠️ {message}\n\n是否繼續往下執行？",
                                    FontSize = 14,
                                    FontWeight = FontWeight.SemiBold,
                                    Foreground = new SolidColorBrush(Color.Parse("#92400E")),
                                    TextWrapping = TextWrapping.Wrap,
                                    VerticalAlignment = VerticalAlignment.Center
                                }
                            },
                            new StackPanel
                            {
                                [Grid.RowProperty] = 1,
                                Orientation = Orientation.Horizontal,
                                HorizontalAlignment = HorizontalAlignment.Right,
                                Spacing = 10,
                                Margin = new Thickness(0, 12, 0, 0),
                                Children = { btnYes, btnNo }
                            }
                        }
                    }
                };

                btnYes.Click += (_, _) => { resultChoice = true; dlg.Close(); };
                btnNo.Click += (_, _) => { resultChoice = false; dlg.Close(); };

                await dlg.ShowDialog(this);
                tcs.TrySetResult(resultChoice);
            }
            catch
            {
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
    }



    public void ToggleTestThreadWindow()

    {

        // 必須切換至 SUPERVISOR 模式才能按 Ctrl+T 叫出 Test Thread

        if (Sequencer == null || !Sequencer.IsSupervisorMode)

        {

            _vm.MainThreads[0].Log("[AUTH] 請先切換至 SUPERVISOR 模式才可使用 Ctrl+T 呼叫 Test Thread 視窗。");

            return;

        }



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



