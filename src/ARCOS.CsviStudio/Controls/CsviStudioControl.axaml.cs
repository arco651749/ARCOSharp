using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using ARCOS.Core.Csvi;
using ARCOS.Core.Drivers;
using ARCOS.Core.Guidance;
using ARCOS.CsviStudio.Services;

namespace ARCOS.CsviStudio.Controls;

public partial class CsviStudioControl : UserControl
{
    private string? _currentFilePath;
    private byte[] _currentAssemblyBytes = Array.Empty<byte>();
    private CsviManifest _currentManifest = new();
    private string? _password;
    private bool _isLoadingFile = false;
    private readonly CsviCompilerService _compiler = new();
    private readonly SandboxExecutor _sandbox = new();

    private static readonly Regex XClassRegex = new(@"\bx:Class\s*=\s*""[^""]*""", RegexOptions.Compiled);

    public CsviStudioControl() : this(null) { }

    public CsviStudioControl(string? initialFilePath)
    {
        InitializeComponent();
        // Position is for Window only

        SourceTabs.SelectionChanged += (s, e) =>
        {
            if (_isLoadingFile) return;
            RefreshActivePreview();
        };

        WizardEditor.TextChanged += (s, e) =>
        {
            if (_isLoadingFile) return;
            if (IsTabSelected("Wizard"))
            {
                UpdateXamlPreview(WizardEditor.Text, isPanel: false);
            }
        };

        PanelEditor.TextChanged += (s, e) =>
        {
            if (_isLoadingFile) return;
            if (IsTabSelected("Panel"))
            {
                UpdateXamlPreview(PanelEditor.Text, isPanel: true);
            }
        };

        var cmdArgs = Environment.GetCommandLineArgs();
        if (string.IsNullOrEmpty(initialFilePath))
        {
            initialFilePath = cmdArgs
                .Select(a => a.Trim('"').Trim())
                .FirstOrDefault(a => !a.StartsWith("-") && (a.EndsWith(".csvi", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
        }

        // 檢查是否帶有自動截圖參數: --capture-target <Wizard|Panel> --capture-out <outPath>
        string? captureTarget = GetArgValue(cmdArgs, "--capture-target");
        string? captureOut = GetArgValue(cmdArgs, "--capture-out");

        if (string.IsNullOrEmpty(initialFilePath))
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "MockIO.csvi"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MODULES", "IO", "DRIVER", "MockIO", "MockIO.csvi"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "MODULES", "IO", "DRIVER", "MockIO", "MockIO.csvi"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MODULES", "IO", "DRIVER", "MockIO", "MockIO.csvi"),
                @"D:\AntiGravity\ARCOSharp\src\MODULES\IO\DRIVER\MockIO\MockIO.csvi"
            };
            initialFilePath = candidates.FirstOrDefault(File.Exists);
        }

        if (!string.IsNullOrEmpty(initialFilePath) && File.Exists(initialFilePath))
        {
            LoadFile(Path.GetFullPath(initialFilePath));
        }
        else
        {
            LoadDefaultTemplate();
        }

        // Automated capture skipped in UserControl

        // 啟動動態 MCP Server
        StartMcpServer(48800);
    }

    private static string? GetArgValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1].Trim('"');
            }
        }
        return null;
    }

    private bool IsTabSelected(string keyword)
    {
        if (SourceTabs.SelectedItem is TabItem ti)
        {
            string h = ti.Header?.ToString() ?? "";
            return h.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public void RefreshActivePreview()
    {
        if (IsTabSelected("Panel"))
        {
            UpdateXamlPreview(PanelEditor.Text, isPanel: true);
        }
        else
        {
            // 在 Logic.cs 或 Wizard.axaml 時預設顯示 Wizard 預覽，若 Wizard 為空則顯示 Panel
            if (!string.IsNullOrWhiteSpace(WizardEditor.Text))
            {
                UpdateXamlPreview(WizardEditor.Text, isPanel: false);
            }
            else if (!string.IsNullOrWhiteSpace(PanelEditor.Text))
            {
                UpdateXamlPreview(PanelEditor.Text, isPanel: true);
            }
            else
            {
                TryRenderCompiledFrontPanel();
            }
        }
    }

    private void LoadDefaultTemplate()
    {
        _isLoadingFile = true;
        try
        {
            _currentFilePath = null;
            _currentAssemblyBytes = Array.Empty<byte>();
            _currentManifest = new CsviManifest
            {
                Module = "IO",
                CommandKey = "IO@SetPort",
                ComponentType = CsviComponentType.Command,
                PackagingMode = CsviPackagingMode.PortableOpen
            };

            CurrentFileBlock.Text = "New CSVI Component (Unsaved)";
            ModeCombo.SelectedIndex = 0;
            ModeStatusBlock.Text = "Mode: PortableOpen";

            LogicEditor.Text = @"using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ARCOS.Core.Drivers;
using ARCOS.Core.Execution;
using ARCOS.Core.Guidance;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Module.IO;

public class IoSetPortGuidance : IParameterGuidance, IModuleHandler
{
    public string CommandKey => ""IO@SetPort"";
    public string ModuleName => ""IO"";

    public IReadOnlyList<string> GetSupportedCommands() => new[] { ""SetPort"" };

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        if (string.IsNullOrWhiteSpace(filteredParam)) return CommandResult.Done();
        var parts = filteredParam.Split(',', ';');
        string port = parts[0].Trim();
        ctx.Engines.Io.SetPort(port, true);
        return CommandResult.Done();
    }

    public bool Execute(BasicEngines engines, string parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter)) return true;
        var parts = parameter.Split(',', ';');
        string port = parts[0].Trim();
        engines.Io.SetPort(port, true);
        return true;
    }

    public CommandResult Execute(ExecutionContext ctx, string action, string parameter)
        => Execute(action, parameter, ctx);

    public Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
        => Task.FromResult<string?>(""P0.0,0.5,ForceUpdate"");

    public object? GetFrontPanelControl() => null;
}
";

            WizardEditor.Text = @"<UserControl xmlns=""https://github.com/avaloniaui""
             xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
    <StackPanel Margin=""14"" Spacing=""8"">
        <TextBlock Text=""⚙️ IO@SetPort Parameter Guidance"" FontWeight=""Bold"" FontSize=""14""/>
        <TextBlock Text=""Port Name ($port):"" FontWeight=""SemiBold""/>
        <TextBox Text=""P0.0""/>
        <TextBlock Text=""Delay Seconds ($second):"" FontWeight=""SemiBold""/>
        <TextBox Text=""0.5""/>
        <CheckBox Content=""ForceUpdate Immediate"" IsChecked=""True""/>
    </StackPanel>
</UserControl>
";

            PanelEditor.Text = @"<UserControl xmlns=""https://github.com/avaloniaui""
             xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
    <Border Background=""#1E293B"" CornerRadius=""6"" Padding=""14"">
        <TextBlock Text=""⚡ IO@SetPort Live FrontPanel Active"" Foreground=""#34D399"" FontWeight=""Bold"" HorizontalAlignment=""Center""/>
    </Border>
</UserControl>
";

            HistoryViewer.Text = "Rev 1: Initial Created CSVI 3.0 template";
        }
        finally
        {
            _isLoadingFile = false;
        }

        RefreshActivePreview();
        AppendLog("Loaded standard CSVI 3.0 IO@SetPort template.");
    }

    public void LoadFile(string path)
    {
        _isLoadingFile = true;
        try
        {
            _currentFilePath = path;
            byte[] bytes = File.ReadAllBytes(path);
            _currentAssemblyBytes = bytes;

            // 先不強制解包以讀取 Manifest，若為 PortableOpen 再解包
            var pkg = CsviPackage.LoadFromBytes(bytes, _password, unpackSources: false);
            if (pkg.HasSourceBundleResource && (pkg.Manifest.PackagingMode == CsviPackagingMode.PortableOpen || !string.IsNullOrEmpty(_password)))
            {
                try
                {
                    pkg = CsviPackage.LoadFromBytes(bytes, _password, unpackSources: true);
                }
                catch (Exception ex)
                {
                    AppendLog($"[Warning] Source bundle locked or failed to unpack: {ex.Message}");
                }
            }

            _currentManifest = pkg.Manifest;
            if (string.IsNullOrEmpty(_currentManifest.CommandKey))
            {
                _currentManifest.CommandKey = Path.GetFileNameWithoutExtension(path);
            }

            CurrentFileBlock.Text = $"{Path.GetFileName(path)} [{_currentManifest.CommandKey}] ({path})";
            ModeCombo.SelectedIndex = (int)pkg.Manifest.PackagingMode;
            ModeStatusBlock.Text = $"Mode: {pkg.Manifest.PackagingMode}";

            // 徹底清空舊編輯器內容，杜絕切換不同 CSVI 時殘留上一顆檔案的原始碼與 XAML！
            LogicEditor.Text = string.Empty;
            WizardEditor.Text = string.Empty;
            PanelEditor.Text = string.Empty;
            HistoryViewer.Text = string.Empty;

            if (pkg.EmbeddedSourceFiles.TryGetValue("Logic.cs", out var logic))
            {
                LogicEditor.Text = logic;
            }
            else
            {
                // 若有多個 .cs 檔案，載入第一個
                var firstCs = pkg.EmbeddedSourceFiles.FirstOrDefault(k => k.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(firstCs.Value)) LogicEditor.Text = firstCs.Value;
            }

            if (pkg.EmbeddedSourceFiles.TryGetValue("Wizard.axaml", out var wiz))
            {
                WizardEditor.Text = wiz;
            }
            else if (pkg.EmbeddedRuntimeXaml.TryGetValue("CSVI.RuntimeXaml.Wizard.axaml", out var rWiz))
            {
                WizardEditor.Text = rWiz;
            }

            if (pkg.EmbeddedSourceFiles.TryGetValue("Panel.axaml", out var pan))
            {
                PanelEditor.Text = pan;
            }
            else if (pkg.EmbeddedRuntimeXaml.TryGetValue("CSVI.RuntimeXaml.Panel.axaml", out var rPan))
            {
                PanelEditor.Text = rPan;
            }

            if (pkg.Manifest.RevisionHistory.Count > 0)
            {
                var lines = pkg.Manifest.RevisionHistory.Select(r => $"[Rev {r.Revision}] {r.TimestampUtc} by {r.Author}: {r.Summary}");
                HistoryViewer.Text = string.Join(Environment.NewLine, lines);
            }
            else
            {
                HistoryViewer.Text = "(No revision history embedded in this component)";
            }

            AppendLog($"Successfully opened {Path.GetFileName(path)} (Key: {_currentManifest.CommandKey}, Mode: {pkg.Manifest.PackagingMode}, Sources: {pkg.EmbeddedSourceFiles.Count}, Size: {bytes.Length} bytes)");
        }
        catch (Exception ex)
        {
            AppendLog("Failed to open CSVI: " + ex.Message);
        }
        finally
        {
            _isLoadingFile = false;
        }

        // 自動切換至最適合預覽的分頁並強制刷新右側畫面
        if (IsTabSelected("Panel") && !string.IsNullOrWhiteSpace(PanelEditor.Text))
        {
            UpdateXamlPreview(PanelEditor.Text, isPanel: true);
        }
        else if (!string.IsNullOrWhiteSpace(WizardEditor.Text))
        {
            UpdateXamlPreview(WizardEditor.Text, isPanel: false);
        }
        else if (!string.IsNullOrWhiteSpace(PanelEditor.Text))
        {
            UpdateXamlPreview(PanelEditor.Text, isPanel: true);
        }
        else
        {
            TryRenderCompiledFrontPanel();
        }
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open CSVI Instrument",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("CSVI Virtual Instrument") { Patterns = new[] { "*.csvi", "*.dll" } }
            }
        });

        if (files.Count > 0)
        {
            string path = files[0].Path.LocalPath;
            LoadFile(path);
        }
    }

    private void OnNewClick(object? sender, RoutedEventArgs e)
    {
        LoadDefaultTemplate();
    }

    private async void OnBuildClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath))
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save CSVI Component",
                DefaultExtension = "csvi",
                SuggestedFileName = "SetPort.csvi"
            });

            if (file == null) return;
            _currentFilePath = file.Path.LocalPath;
        }

        var sources = new Dictionary<string, string>
        {
            ["Logic.cs"] = LogicEditor.Text ?? string.Empty,
            ["Wizard.axaml"] = WizardEditor.Text ?? string.Empty,
            ["Panel.axaml"] = PanelEditor.Text ?? string.Empty
        };

        var mode = (CsviPackagingMode)ModeCombo.SelectedIndex;
        _currentManifest.PackagingMode = mode;

        var compRes = _compiler.Compile(Path.GetFileNameWithoutExtension(_currentFilePath), sources, _currentManifest, _password);
        if (compRes.Success)
        {
            _currentAssemblyBytes = compRes.AssemblyBytes;
            File.WriteAllBytes(_currentFilePath, compRes.AssemblyBytes);
            AppendLog($"✔ Roslyn Compilation Succeeded in {compRes.ElapsedMilliseconds} ms -> {_currentFilePath} ({compRes.AssemblyBytes.Length} bytes)");
            CurrentFileBlock.Text = $"{Path.GetFileName(_currentFilePath)} [{_currentManifest.CommandKey}] ({_currentFilePath})";
            RefreshActivePreview();
        }
        else
        {
            AppendLog("❌ Compilation Failed:\n" + string.Join("\n", compRes.Errors));
        }
    }

    private void OnSetPasswordClick(object? sender, RoutedEventArgs e)
    {
        AppendLog("Password protection mode configured.");
    }

    private void OnExportSourceClick(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentFilePath))
        {
            string outDir = Path.Combine(Path.GetDirectoryName(_currentFilePath)!, Path.GetFileNameWithoutExtension(_currentFilePath) + "_Source");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "Logic.cs"), LogicEditor.Text ?? "");
            File.WriteAllText(Path.Combine(outDir, "Wizard.axaml"), WizardEditor.Text ?? "");
            File.WriteAllText(Path.Combine(outDir, "Panel.axaml"), PanelEditor.Text ?? "");
            AppendLog($"Exported source files to: {outDir}");
        }
    }

    private void OnRunSandboxClick(object? sender, RoutedEventArgs e)
    {
        var sources = new Dictionary<string, string>
        {
            ["Logic.cs"] = LogicEditor.Text ?? string.Empty,
            ["Wizard.axaml"] = WizardEditor.Text ?? string.Empty,
            ["Panel.axaml"] = PanelEditor.Text ?? string.Empty
        };

        var comp = _compiler.Compile("SandboxTestAssembly", sources, _currentManifest, _password);
        if (!comp.Success)
        {
            SandboxOutputBlock.Text = "Compilation failed:\n" + string.Join("\n", comp.Errors);
            return;
        }

        string param = TestParamBox.Text ?? "";
        string cmdKey = !string.IsNullOrEmpty(_currentManifest.CommandKey) ? _currentManifest.CommandKey : "IO@SetPort";

        var run = _sandbox.Execute(comp.AssemblyBytes, cmdKey, param);
        if (run.Success)
        {
            SandboxOutputBlock.Text = $"Status: SUCCESS\nOutput: {run.Output}\nElapsed: {run.ElapsedMilliseconds:F4} ms";
        }
        else
        {
            SandboxOutputBlock.Text = $"Status: FAILED\nError: {run.Error}\nElapsed: {run.ElapsedMilliseconds:F4} ms";
        }
    }

    private void AppendLog(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            if (DiagnosticsBox != null)
            {
                DiagnosticsBox.Text += $"[{timestamp}] {message}\n";
            }
        });
    }

    /// <summary>
    /// 更新右側 Live XAML 預覽區：
    /// 1. 若為 Panel 且當前組件實作了 IDriver.CreateDriverView() 或 IParameterGuidance.GetFrontPanelControl()，優先嘗試直接實例化完整互動視圖（解決動態生成點位與 x:Class 問題）
    /// 2. 否則透過 AvaloniaRuntimeXamlLoader 解析 XAML（若遇到未知 x:Class 自動剝離該屬性重試，徹底解決 MockIO Panel 報錯顯示不出來的問題！）
    /// </summary>
    public void UpdateXamlPreview(string? xamlText, bool isPanel = false)
    {
        try
        {
            // 若在預覽 Panel 且編輯器內容未大幅偏離組件內建的完整視圖，嘗試實例化真實 Driver/Guidance View 以呈現完整的動態燈號與事件
            if (isPanel && _currentAssemblyBytes.Length > 0)
            {
                var liveCtrl = TryInstantiateLiveControlFromAssembly(_currentAssemblyBytes, _currentManifest.CommandKey);
                if (liveCtrl != null && string.IsNullOrWhiteSpace(xamlText))
                {
                    XamlPreviewHost.Child = liveCtrl;
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(xamlText))
            {
                if (!TryRenderCompiledFrontPanel())
                {
                    XamlPreviewHost.Child = new TextBlock
                    {
                        Text = "No XAML source embedded in this CSVI.",
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(12)
                    };
                }
                return;
            }

            Control? parsed = null;
            try
            {
                parsed = AvaloniaRuntimeXamlLoader.Parse<Control>(xamlText);
            }
            catch
            {
                // 若因 x:Class="MVCON.Driver.MockIO.MockIoView" 等外部類別不在當前 AppDomain 導致解析失敗，
                // 先嘗試若為 Panel 且組件有編譯好的 CreateDriverView() 則掛載真實互動面板，否則剝離 x:Class 後解析純 XAML 視覺樹
                if (isPanel && _currentAssemblyBytes.Length > 0)
                {
                    var liveCtrl = TryInstantiateLiveControlFromAssembly(_currentAssemblyBytes, _currentManifest.CommandKey);
                    if (liveCtrl != null)
                    {
                        XamlPreviewHost.Child = liveCtrl;
                        return;
                    }
                }

                string sanitizedXaml = XClassRegex.Replace(xamlText, "");
                parsed = AvaloniaRuntimeXamlLoader.Parse<Control>(sanitizedXaml);
            }

            XamlPreviewHost.Child = parsed;
        }
        catch (Exception ex)
        {
            XamlPreviewHost.Child = new TextBlock
            {
                Text = "XAML Preview Error:\n" + ex.Message,
                Foreground = Brushes.Crimson,
                Margin = new Thickness(10),
                TextWrapping = TextWrapping.Wrap
            };
        }
    }

    private bool TryRenderCompiledFrontPanel()
    {
        if (_currentAssemblyBytes.Length == 0) return false;
        var ctrl = TryInstantiateLiveControlFromAssembly(_currentAssemblyBytes, _currentManifest.CommandKey);
        if (ctrl != null)
        {
            XamlPreviewHost.Child = ctrl;
            return true;
        }
        return false;
    }

    public static Control? TryInstantiateLiveControlFromAssembly(byte[] assemblyBytes, string? commandKey)
    {
        try
        {
            var asm = ARCOS.Core.Common.PluginLoadManager.LoadFromBytes($"CsviStudioPreview_{commandKey ?? "Control"}", assemblyBytes);
            var engines = new BasicEngines();

            // 1. 檢查是否為 IDriver (例如 MockIO.csvi)
            var driverType = asm.GetExportedTypes()
                .FirstOrDefault(t => typeof(IDriver).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
            if (driverType != null && Activator.CreateInstance(driverType) is IDriver drv)
            {
                drv.Init("PreviewIO", "Device=com1,OutStart=P0.0,OutPorts=16,InStart=P1.0,InPorts=16,Interval=50", engines);
                if (drv.CreateDriverView() is Control drvCtrl)
                {
                    return drvCtrl;
                }
            }

            // 2. 檢查是否為 IParameterGuidance
            foreach (var t in asm.GetExportedTypes())
            {
                if (typeof(IParameterGuidance).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                {
                    if (Activator.CreateInstance(t) is IParameterGuidance g)
                    {
                        if (string.IsNullOrEmpty(commandKey) ||
                            g.CommandKey.Equals(commandKey, StringComparison.OrdinalIgnoreCase) ||
                            g.CommandKey.EndsWith("@" + commandKey, StringComparison.OrdinalIgnoreCase))
                        {
                            if (g.GetFrontPanelControl() is Control gCtrl)
                            {
                                return gCtrl;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // 忽略反射例外
        }
        return null;
    }

    private async Task PerformAutomatedCaptureAsync(string target, string outPath)
    {
        try
        {
            // 切換至指定頁籤 (Wizard 或 Panel) 與右側 Live XAML Preview 分頁
            if (target.Equals("Panel", StringComparison.OrdinalIgnoreCase))
            {
                SourceTabs.SelectedIndex = 2; // Panel.axaml
                UpdateXamlPreview(PanelEditor.Text, isPanel: true);
            }
            else
            {
                SourceTabs.SelectedIndex = 1; // Wizard.axaml
                UpdateXamlPreview(WizardEditor.Text, isPanel: false);
            }

            // 將右側 TabControl 切到第 2 個頁籤 (Live XAML Preview)
            if (XamlPreviewHost.Parent?.Parent is TabItem previewTab && previewTab.Parent is TabControl rightTabs)
            {
                rightTabs.SelectedItem = previewTab;
            }

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(250);

            string? dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 直接使用 Avalonia RenderTargetBitmap 將整個 CSVI Studio 視窗渲染輸出高解析度截圖
            int w = (int)Math.Max(800, Bounds.Width > 0 ? Bounds.Width : Width);
            int h = (int)Math.Max(600, Bounds.Height > 0 ? Bounds.Height : Height);
            Measure(new Size(w, h));
            Arrange(new Rect(0, 0, w, h));

            var pixelSize = new PixelSize(w, h);
            using var rtb = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
            rtb.Render(this);
            rtb.Save(outPath);
        }
        catch (Exception ex)
        {
            AppendLog("Capture error: " + ex.Message);
        }
        finally
        {
            if (TopLevel.GetTopLevel(this) is Window w) w.Close();
        }
    }

    // =========================================================
    // Dynamic MCP Server Support (TCP JSON-RPC 2.0 with Auto-Port)
    // =========================================================
    public int McpPort { get; private set; } = 48800;
    private System.Net.Sockets.TcpListener? _mcpListener;
    private CancellationTokenSource? _mcpCts;
    private readonly ARCOS.CsviStudio.Mcp.McpServer _mcpServer = new();

    public void StartMcpServer(int basePort = 48800)
    {
        for (int p = basePort; p < basePort + 500; p++)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p);
                listener.Start();
                _mcpListener = listener;
                McpPort = p;
                break;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Port occupied, scan next
            }
        }

        if (_mcpListener != null)
        {
            _mcpServer.OnLoadFileRequested = path => { Dispatcher.UIThread.Post(() => LoadFile(path)); };
            _mcpCts = new CancellationTokenSource();
            if (McpStatusBlock != null)
            {
                McpStatusBlock.Text = $"🟢 MCP Active: 127.0.0.1:{McpPort}";
            }
            _ = Task.Run(() => ListenMcpClientsAsync(_mcpListener, _mcpCts.Token));
            AppendLog($"[MCP Server] Active and listening on 127.0.0.1:{McpPort} (JSON-RPC 2.0 over TCP)");
        }
    }

    private async Task ListenMcpClientsAsync(System.Net.Sockets.TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleMcpClientAsync(client, ct), ct);
            }
            catch when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AppendLog($"[MCP] Accept error: {ex.Message}");
                try { await Task.Delay(500, ct); } catch { break; }
            }
        }
    }

    private async Task HandleMcpClientAsync(System.Net.Sockets.TcpClient client, CancellationToken ct)
    {
        var utf8NoBom = new System.Text.UTF8Encoding(false);
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, utf8NoBom))
        using (var writer = new StreamWriter(stream, utf8NoBom) { AutoFlush = true })
        {
            while (!ct.IsCancellationRequested && client.Connected)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var req = System.Text.Json.Nodes.JsonNode.Parse(line);
                    if (req == null) continue;
                    string? method = req["method"]?.GetValue<string>();
                    var @params = req["params"];
                    var id = req["id"];

                    var res = _mcpServer.HandleRequestDirect(method, @params, id);
                    if (res != null)
                    {
                        await writer.WriteLineAsync(res.ToJsonString());
                    }
                }
                catch (Exception ex)
                {
                    var err = new System.Text.Json.Nodes.JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = null,
                        ["error"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["code"] = -32603,
                            ["message"] = ex.Message
                        }
                    };
                    await writer.WriteLineAsync(err.ToJsonString());
                }
            }
        }
    }

}
