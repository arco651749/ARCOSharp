using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenCvSharp;
using Window = Avalonia.Controls.Window;
using ARCOS.Core.Drivers;

namespace ARCOS.App.Mcp;

/// <summary>
/// ARCOS.App 內嵌 MCP 與 UI 自動化伺服器 (JSON-RPC 2.0 over Stdio &amp; Local TCP 19528)
/// 支援 AI Agent 直接對 ARCOS 主控台與彈出之 CSVI 參數導引視窗進行：
/// 狀態檢視 (arcos_get_state)、視覺樹控制項枚舉 (arcos_list_controls)、
/// 點選與分頁切換 (arcos_click)、文字輸入與下拉選單選擇 (arcos_input)、
/// 腳本編輯與導引觸發 (arcos_sequencer_action)、IO 點位讀寫 (arcos_io_action)、
/// 以及高解析度畫面與對話框合成截圖 (arcos_capture_ui)。
/// </summary>
public static class ArcosMcpServer
{
    public const int DefaultAutomationPort = 19528;

    private static MainWindow? _mainWindow;
    private static TcpListener? _tcpListener;
    private static StreamWriter? _stdioWriter;
    private static readonly object _stdioLock = new();

    /// <summary>
    /// 在 MainWindow 啟動時自動開啟背景本地自動化 TCP 服務 (127.0.0.1:19528)
    /// </summary>
    public static void AttachMainWindow(MainWindow mainWindow, int port = DefaultAutomationPort)
    {
        _mainWindow = mainWindow;
        StartBackgroundTcpServer(port);
    }

    private static void StartBackgroundTcpServer(int port)
    {
        if (_tcpListener != null) return;
        Task.Run(async () =>
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Loopback, port);
                _tcpListener.Start();
                while (true)
                {
                    var client = await _tcpListener.AcceptTcpClientAsync();
                    _ = HandleTcpClientAsync(client);
                }
            }
            catch
            {
                // 若 Port 已被另一個 ARCOS.App 實例佔用則忽略
            }
        });
    }

    private static async Task HandleTcpClientAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string responseJson = await ProcessJsonRpcLineAsync(line);
                if (!string.IsNullOrEmpty(responseJson))
                {
                    await writer.WriteLineAsync(responseJson);
                }
            }
        }
    }

    /// <summary>
    /// 當以 ARCOS.App.exe --mcp 啟動時：
    /// 若已有執行中的 ARCOS.App GUI 實例 (Port 19528)，直接透明轉發至該實例；
    /// 否則在當前處理序啟動 MainWindow 並於背景執行緒處理 Stdio MCP 請求。
    /// </summary>
    public static bool TryForwardStdioToRunningInstance(int port = DefaultAutomationPort)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(IPAddress.Loopback, port);
            if (!connectTask.Wait(250) || !client.Connected)
                return false;

            var utf8NoBom = new UTF8Encoding(false);
            using var netStream = client.GetStream();
            using var netReader = new StreamReader(netStream, utf8NoBom);
            using var netWriter = new StreamWriter(netStream, utf8NoBom) { AutoFlush = true };
            using var stdinReader = new StreamReader(Console.OpenStandardInput(), utf8NoBom);
            using var stdoutWriter = new StreamWriter(Console.OpenStandardOutput(), utf8NoBom) { AutoFlush = true };

            string? line;
            while ((line = stdinReader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                netWriter.WriteLine(line);
                string? resp = netReader.ReadLine();
                if (!string.IsNullOrEmpty(resp))
                {
                    stdoutWriter.WriteLine(resp);
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 綁定當前處理序的 Stdio 作為 MCP 通訊管道 (搭配 Avalonia UI 主執行緒)
    /// </summary>
    public static void StartInProcessStdioMcpLoop(Stream rawStdin, Stream rawStdout)
    {
        var utf8NoBom = new UTF8Encoding(false);
        _stdioWriter = new StreamWriter(rawStdout, utf8NoBom) { AutoFlush = true };

        Task.Run(async () =>
        {
            // 等待 MainWindow 初始化就緒
            for (int i = 0; i < 100 && _mainWindow == null; i++)
            {
                await Task.Delay(50);
            }

            using var reader = new StreamReader(rawStdin, utf8NoBom);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string resp = await ProcessJsonRpcLineAsync(line);
                if (!string.IsNullOrEmpty(resp))
                {
                    lock (_stdioLock)
                    {
                        _stdioWriter.WriteLine(resp);
                    }
                }
            }

            // 當 MCP Stdin 關閉時，優雅結束應用程式
            _ = Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            });
        });
    }

    public static async Task<string> ProcessJsonRpcLineAsync(string line)
    {
        JsonNode? request = null;
        try
        {
            request = JsonNode.Parse(line);
            if (request == null) return string.Empty;

            string? method = request["method"]?.GetValue<string>();
            JsonNode? id = request["id"]?.DeepClone();
            JsonNode? @params = request["params"];

            if (method == "notifications/initialized")
            {
                return string.Empty;
            }

            object? result = method switch
            {
                "initialize" => HandleInitialize(),
                "tools/list" => HandleToolsList(),
                "tools/call" => await HandleToolCallAsync(@params),
                _ => throw new InvalidOperationException($"Unsupported MCP method: {method}")
            };

            var resp = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = JsonSerializer.SerializeToNode(result)
            };
            return resp.ToJsonString();
        }
        catch (Exception ex)
        {
            var err = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request?["id"]?.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = -32603,
                    ["message"] = ex.Message
                }
            };
            return err.ToJsonString();
        }
    }

    private static object HandleInitialize()
    {
        return new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { tools = new { } },
            serverInfo = new
            {
                name = "ARCOS-App-MCP-Server",
                version = "3.0.0"
            }
        };
    }

    private static object HandleToolsList()
    {
        return new
        {
            tools = new object[]
            {
                new
                {
                    name = "arcos_get_state",
                    description = "Get live state of ARCOS.App: active MainTab, SubPanel, Sequencer rows, PC pointer, open Parameter Guidance dialogs, IO port states, variables, and logs.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new { }
                    }
                },
                new
                {
                    name = "arcos_list_controls",
                    description = "List interactive and named UI controls in MainWindow, the currently open Dialog (Wizard), SubPanel, or Sequencer.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            scope = new { type = "string", description = "Auto | MainWindow | Dialog | SubPanel | Sequencer (default: Auto)" }
                        }
                    }
                },
                new
                {
                    name = "arcos_click",
                    description = "Click a button, toggle, tab item, or coordinate (x, y) in ARCOS.App or the active Parameter Guidance Dialog.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "Control x:Name (e.g., BtnTabSup, BtnMountIoPanel, BtnMountDriverPanel, BtnToggle_P0_3, BtnOk, BtnCancel, BtnStep)" },
                            text = new { type = "string", description = "Button or Tab text/label to click (e.g., 'Modules Info', 'User Tab', '套用參數', 'SET')" },
                            tabIndex = new { type = "integer", description = "Optional MainTabControl index (0=User Tab, 1=Capture, 4=Modules Info, etc.)" },
                            x = new { type = "number", description = "Optional X coordinate to click" },
                            y = new { type = "number", description = "Optional Y coordinate to click" },
                            scope = new { type = "string", description = "Auto | Dialog | MainWindow | SubPanel" }
                        }
                    }
                },
                new
                {
                    name = "arcos_input",
                    description = "Enter text into a TextBox or select an item/index in a ComboBox or ListBox in ARCOS.App or the active Dialog.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "Control x:Name (e.g., CmbSetPortName, TxtSetPortDelay, CmbSetPortForce, TxtParamOut, CmbSubPanelSelect, LstIoDrivers)" },
                            value = new { type = "string", description = "Text value to type or item text to select" },
                            selectedIndex = new { type = "integer", description = "Optional index to select in ComboBox/ListBox" },
                            pressEnter = new { type = "boolean", description = "Whether to simulate Enter key press after input" },
                            scope = new { type = "string", description = "Auto | Dialog | MainWindow | SubPanel" }
                        },
                        required = new[] { "name" }
                    }
                },
                new
                {
                    name = "arcos_sequencer_action",
                    description = "Control the ARCOS Sequencer: set Supervisor/Operator mode, select/edit/add/delete rows, open CSVI Parameter Guidance dialog for a row, or Step/Run/Pause/Reset/Jump.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new { type = "string", description = "set_mode | select_row | edit_row | add_row | delete_row | open_guidance | step | run | pause | reset | jump" },
                            rowIndex = new { type = "integer", description = "Target line index (0-based)" },
                            mode = new { type = "string", description = "Supervisor | Operator (for set_mode)" },
                            command = new { type = "string", description = "Command string (e.g. IO@SetPort) for edit_row / add_row" },
                            parameter = new { type = "string", description = "Parameter string (e.g. P0.3,0.5,ForceUpdate) for edit_row / add_row" }
                        },
                        required = new[] { "action" }
                    }
                },
                new
                {
                    name = "arcos_io_action",
                    description = "Read, write, toggle, or force-update IO ports and drivers in ARCOS.App.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new { type = "string", description = "get | set | toggle | force_update" },
                            port = new { type = "string", description = "Port or alias name (e.g. P0.0, P0.3, Safety Door Switch)" },
                            value = new { type = "boolean", description = "Target boolean state for 'set'" }
                        },
                        required = new[] { "action" }
                    }
                },
                new
                {
                    name = "arcos_capture_ui",
                    description = "Capture a high-resolution PNG screenshot of ARCOS.App (MainWindow, active Parameter Guidance Dialog, SubPanel, or Auto-composited MainWindow + Dialog).",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            outputPath = new { type = "string", description = "File path to save the screenshot (.png)" },
                            target = new { type = "string", description = "Auto | MainWindow | Dialog | SubPanel (default: Auto)" }
                        },
                        required = new[] { "outputPath" }
                    }
                }
            }
        };
    }

    private static async Task<object> HandleToolCallAsync(JsonNode? @params)
    {
        string? toolName = @params?["name"]?.GetValue<string>();
        JsonNode? args = @params?["arguments"];

        if (_mainWindow == null)
        {
            throw new InvalidOperationException("MainWindow is not initialized yet.");
        }

        string textResult = toolName switch
        {
            "arcos_get_state" => await Dispatcher.UIThread.InvokeAsync(() => ToolGetState(_mainWindow)),
            "arcos_list_controls" => await Dispatcher.UIThread.InvokeAsync(() => ToolListControls(_mainWindow, args)),
            "arcos_click" => await Dispatcher.UIThread.InvokeAsync(() => ToolClick(_mainWindow, args)),
            "arcos_input" => await Dispatcher.UIThread.InvokeAsync(() => ToolInput(_mainWindow, args)),
            "arcos_sequencer_action" => await ToolSequencerActionAsync(_mainWindow, args),
            "arcos_io_action" => await Dispatcher.UIThread.InvokeAsync(() => ToolIoAction(_mainWindow, args)),
            "arcos_capture_ui" => await Dispatcher.UIThread.InvokeAsync(() => ToolCaptureUi(_mainWindow, args)),
            _ => throw new InvalidOperationException($"Unknown tool: {toolName}")
        };

        return new
        {
            content = new[]
            {
                new { type = "text", text = textResult }
            }
        };
    }

    private static Window? GetActiveDialog(MainWindow mainWin)
    {
        var owned = mainWin.OwnedWindows.OfType<Window>().LastOrDefault(w => w.IsVisible);
        if (owned != null) return owned;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.Windows.LastOrDefault(w => w != mainWin && w.IsVisible);
        }
        return null;
    }

    private static string ToolGetState(MainWindow mainWin)
    {
        var mainTab = mainWin.FindControl<TabControl>("MainTabControl");
        int tabIdx = mainTab?.SelectedIndex ?? -1;
        string tabHeader = (mainTab?.SelectedItem as TabItem)?.Header?.ToString() ?? "";

        var cmbSub = mainWin.FindControl<ComboBox>("CmbSubPanelSelect");
        int subIdx = cmbSub?.SelectedIndex ?? 0;
        var subContainer = mainWin.FindControl<ContentControl>("UserFrontPanelContainer");
        string subContentType = subContainer?.Content?.GetType().FullName ?? "null";

        var seq = mainWin.Sequencer;
        var txtStatus = seq?.FindControl<TextBlock>("TxtMachineStatus");
        var pnlSup = seq?.FindControl<Border>("PnlSupervisor");
        var ctx = mainWin.VM.MainThreads[seq?.CurrentProgramNumber ?? 0];

        var activeDlg = GetActiveDialog(mainWin);

        var port0States = new Dictionary<string, bool>();
        for (int b = 0; b < 8; b++)
        {
            string p = $"P0.{b}";
            port0States[p] = mainWin.Engines.Io.GetPort(p);
        }

        var activePorts = mainWin.Engines.Io.GetAllSnapshot()
            .Where(s => s.Value && !s.Port.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
            .Select(s => new { s.Port, s.Alias, s.Value })
            .ToList();

        var txtLogs = mainWin.FindControl<TextBox>("TxtUserLogs");
        var recentLogs = (txtLogs?.Text ?? "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(15)
            .ToArray();

        var rows = seq?.Rows.Select(r => new
        {
            line = r.LineNumber,
            command = r.Command,
            parameter = r.Parameter,
            isCurrentPC = r.IsCurrentPC
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            mainTabIndex = tabIdx,
            mainTabHeader = tabHeader,
            subPanelIndex = subIdx,
            subPanelContentType = subContentType,
            machineStatus = txtStatus?.Text ?? "IDLE",
            isSupervisorMode = pnlSup?.IsVisible ?? false,
            isLocked = seq?.IsLocked ?? false,
            currentProgram = seq?.CurrentProgramNumber ?? 0,
            currentPC = ctx.ProgIndex,
            isThreadRunning = ctx.IsRunning,
            isSystemPaused = mainWin.Engines.Timer.IsSystemPaused,
            activeDialog = new
            {
                isOpen = activeDlg != null,
                title = activeDlg?.Title ?? "",
                type = activeDlg?.GetType().FullName ?? ""
            },
            port0States,
            activePorts,
            sequencerRows = rows,
            recentLogs
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static Control GetScopeRoot(MainWindow mainWin, string? scope)
    {
        string s = (scope ?? "Auto").Trim();
        var dlg = GetActiveDialog(mainWin);

        if (s.Equals("Dialog", StringComparison.OrdinalIgnoreCase))
        {
            return (Control?)dlg?.Content ?? dlg ?? (Control)mainWin;
        }
        if (s.Equals("SubPanel", StringComparison.OrdinalIgnoreCase))
        {
            var sub = mainWin.FindControl<ContentControl>("UserFrontPanelContainer");
            if (sub?.Content is Control subCtrl) return subCtrl;
        }
        if (s.Equals("Sequencer", StringComparison.OrdinalIgnoreCase))
        {
            if (mainWin.Sequencer != null) return mainWin.Sequencer;
        }
        if (s.Equals("MainWindow", StringComparison.OrdinalIgnoreCase))
        {
            return mainWin;
        }
        // Auto: prefer active dialog if open
        if (dlg != null)
        {
            return (Control?)dlg.Content ?? dlg;
        }
        return mainWin;
    }

    private static IEnumerable<Control> EnumerateControlsRecursive(Control root)
    {
        var visited = new HashSet<Control>();
        var queue = new Queue<Control>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!visited.Add(cur)) continue;
            yield return cur;

            foreach (var vChild in cur.GetVisualChildren().OfType<Control>())
            {
                queue.Enqueue(vChild);
            }
            foreach (var lChild in cur.GetLogicalChildren().OfType<Control>())
            {
                queue.Enqueue(lChild);
            }
            if (cur is ContentControl cc && cc.Content is Control cContent)
            {
                queue.Enqueue(cContent);
            }
        }
    }

    private static string ToolListControls(MainWindow mainWin, JsonNode? args)
    {
        string? scope = args?["scope"]?.GetValue<string>();
        var root = GetScopeRoot(mainWin, scope);
        var list = new List<object>();

        foreach (var ctrl in EnumerateControlsRecursive(root))
        {
            if (!ctrl.IsVisible) continue;
            bool isNamed = !string.IsNullOrEmpty(ctrl.Name);
            bool isInteractive = ctrl is Button or TextBox or ComboBox or ListBox or CheckBox or RadioButton or TabItem or DataGrid;
            if (!isNamed && !isInteractive) continue;

            string? text = ctrl switch
            {
                TextBox tb => tb.Text,
                TextBlock tbl => tbl.Text,
                ComboBox cb => (cb.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? cb.SelectedItem?.ToString(),
                ListBox lb => lb.SelectedItem?.ToString(),
                TabItem ti => ti.Header?.ToString(),
                CheckBox chk => $"{chk.Content} (Checked={chk.IsChecked})",
                RadioButton rb => $"{rb.Content} (Checked={rb.IsChecked})",
                Button btn => ExtractControlText(btn),
                _ => null
            };

            list.Add(new
            {
                name = ctrl.Name ?? "",
                type = ctrl.GetType().Name,
                text = text ?? "",
                bounds = new
                {
                    x = Math.Round(ctrl.Bounds.X, 1),
                    y = Math.Round(ctrl.Bounds.Y, 1),
                    w = Math.Round(ctrl.Bounds.Width, 1),
                    h = Math.Round(ctrl.Bounds.Height, 1)
                }
            });
        }

        return JsonSerializer.Serialize(new
        {
            scopeUsed = root.GetType().Name,
            activeDialogTitle = GetActiveDialog(mainWin)?.Title ?? "",
            controlCount = list.Count,
            controls = list
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ExtractControlText(Control ctrl)
    {
        if (ctrl is ContentControl cc)
        {
            if (cc.Content is string s) return s;
            if (cc.Content is TextBlock tb) return tb.Text ?? "";
            if (cc.Content is Control inner)
            {
                var texts = EnumerateControlsRecursive(inner).OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t));
                return string.Join(" ", texts);
            }
        }
        return "";
    }

    private static Control? FindControlDeep(Control root, string name)
    {
        try
        {
            var direct = root.FindControl<Control>(name);
            if (direct != null) return direct;
        }
        catch
        {
            // Ignore if NameScope is not attached on dynamic controls
        }

        return EnumerateControlsRecursive(root)
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string ToolClick(MainWindow mainWin, JsonNode? args)
    {
        string? name = args?["name"]?.GetValue<string>();
        string? text = args?["text"]?.GetValue<string>();
        int? tabIndex = args?["tabIndex"]?.GetValue<int>();
        double? x = args?["x"]?.GetValue<double>();
        double? y = args?["y"]?.GetValue<double>();
        string? scope = args?["scope"]?.GetValue<string>();

        // 1. 若指定 tabIndex，直接切換 MainTabControl
        if (tabIndex.HasValue)
        {
            var mainTab = mainWin.FindControl<TabControl>("MainTabControl");
            if (mainTab != null)
            {
                mainTab.SelectedIndex = tabIndex.Value;
                mainWin.UpdateLayout();
                return JsonSerializer.Serialize(new { success = true, action = "SwitchMainTab", tabIndex = tabIndex.Value });
            }
        }

        var root = GetScopeRoot(mainWin, scope);
        Control? target = null;

        // 2. 依名稱搜尋 (若 Auto 在 Dialog 找不到，也搜 MainWindow)
        if (!string.IsNullOrWhiteSpace(name))
        {
            target = FindControlDeep(root, name);
            if (target == null && root != mainWin)
            {
                target = FindControlDeep(mainWin, name);
            }
        }

        // 3. 依文字搜尋 (Button / TabItem / CheckBox / RadioButton)
        if (target == null && !string.IsNullOrWhiteSpace(text))
        {
            target = FindControlByText(root, text);
            if (target == null && root != mainWin)
            {
                target = FindControlByText(mainWin, text);
            }
        }

        // 4. 依座標 (x, y) HitTest 搜尋
        if (target == null && x.HasValue && y.HasValue)
        {
            var pt = new Avalonia.Point(x.Value, y.Value);
            var hit = root.GetVisualsAt(pt).OfType<Control>().FirstOrDefault();
            while (hit != null && hit is not (Button or TabItem or CheckBox or RadioButton or ComboBox or TextBox))
            {
                hit = hit.GetVisualParent() as Control;
            }
            target = hit;
        }

        if (target == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Target control not found (name='{name}', text='{text}', x={x}, y={y})."
            });
        }

        // 執行點擊動作
        if (target is TabItem tabItem)
        {
            tabItem.IsSelected = true;
            if (tabItem.Parent is TabControl tc)
            {
                tc.SelectedItem = tabItem;
            }
            mainWin.UpdateLayout();
            return JsonSerializer.Serialize(new { success = true, clicked = "TabItem", header = tabItem.Header?.ToString() });
        }

        if (target is ToggleButton toggle)
        {
            toggle.IsChecked = !(toggle.IsChecked ?? false);
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            mainWin.UpdateLayout();
            return JsonSerializer.Serialize(new { success = true, clicked = toggle.GetType().Name, name = toggle.Name, isChecked = toggle.IsChecked });
        }

        if (target is Button btn)
        {
            string btnLabel = ExtractControlText(btn);
            btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (btn.Command?.CanExecute(btn.CommandParameter) == true)
            {
                btn.Command.Execute(btn.CommandParameter);
            }
            mainWin.UpdateLayout();
            return JsonSerializer.Serialize(new { success = true, clicked = "Button", name = btn.Name ?? "", label = btnLabel });
        }

        return JsonSerializer.Serialize(new { success = false, error = $"Control '{target.Name}' ({target.GetType().Name}) is not clickable." });
    }

    private static Control? FindControlByText(Control root, string text)
    {
        foreach (var ctrl in EnumerateControlsRecursive(root))
        {
            if (!ctrl.IsVisible) continue;
            if (ctrl is TabItem ti && ti.Header?.ToString()?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
                return ti;
            if (ctrl is Button btn && ExtractControlText(btn).Contains(text, StringComparison.OrdinalIgnoreCase))
                return btn;
            if (ctrl is ToggleButton tb && tb.Content?.ToString()?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
                return tb;
        }
        return null;
    }

    private static string ToolInput(MainWindow mainWin, JsonNode? args)
    {
        string name = args?["name"]?.GetValue<string>() ?? throw new ArgumentException("name is required");
        string? val = args?["value"]?.GetValue<string>();
        int? selectedIndex = args?["selectedIndex"]?.GetValue<int>();
        bool pressEnter = args?["pressEnter"]?.GetValue<bool>() ?? false;
        string? scope = args?["scope"]?.GetValue<string>();

        var root = GetScopeRoot(mainWin, scope);
        var target = FindControlDeep(root, name) ?? (root != mainWin ? FindControlDeep(mainWin, name) : null);

        if (target == null)
        {
            return JsonSerializer.Serialize(new { success = false, error = $"Control '{name}' not found." });
        }

        if (target is TextBox tb)
        {
            tb.Text = val ?? "";
            tb.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyUpEvent,
                Key = pressEnter ? Key.Enter : Key.None
            });
            mainWin.UpdateLayout();
            return JsonSerializer.Serialize(new { success = true, control = name, type = "TextBox", newText = tb.Text });
        }

        if (target is SelectingItemsControl sic)
        {
            if (selectedIndex.HasValue)
            {
                sic.SelectedIndex = selectedIndex.Value;
            }
            else if (val != null)
            {
                bool matched = false;
                if (sic.ItemsSource != null)
                {
                    int idx = 0;
                    foreach (var item in sic.ItemsSource)
                    {
                        string itemStr = (item as ComboBoxItem)?.Content?.ToString() ?? item?.ToString() ?? "";
                        if (itemStr.Equals(val, StringComparison.OrdinalIgnoreCase) ||
                            itemStr.Contains(val, StringComparison.OrdinalIgnoreCase))
                        {
                            sic.SelectedIndex = idx;
                            matched = true;
                            break;
                        }
                        idx++;
                    }
                }
                if (!matched && sic.Items != null)
                {
                    for (int i = 0; i < sic.Items.Count; i++)
                    {
                        var item = sic.Items[i];
                        string itemStr = (item as ComboBoxItem)?.Content?.ToString() ?? item?.ToString() ?? "";
                        if (itemStr.Equals(val, StringComparison.OrdinalIgnoreCase) ||
                            itemStr.Contains(val, StringComparison.OrdinalIgnoreCase))
                        {
                            sic.SelectedIndex = i;
                            matched = true;
                            break;
                        }
                    }
                }
                if (!matched)
                {
                    sic.SelectedItem = val;
                }
            }
            mainWin.UpdateLayout();
            string selectedText = (sic.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? sic.SelectedItem?.ToString() ?? "";
            return JsonSerializer.Serialize(new
            {
                success = true,
                control = name,
                type = sic.GetType().Name,
                selectedIndex = sic.SelectedIndex,
                selectedItem = selectedText
            });
        }

        return JsonSerializer.Serialize(new { success = false, error = $"Control '{name}' ({target.GetType().Name}) does not support input." });
    }

    private static async Task<string> ToolSequencerActionAsync(MainWindow mainWin, JsonNode? args)
    {
        string action = args?["action"]?.GetValue<string>() ?? "";
        int rowIndex = args?["rowIndex"]?.GetValue<int>() ?? 0;
        string? mode = args?["mode"]?.GetValue<string>();
        string? command = args?["command"]?.GetValue<string>();
        string? parameter = args?["parameter"]?.GetValue<string>();

        if (action.Equals("open_guidance", StringComparison.OrdinalIgnoreCase))
        {
            // 非阻塞開啟 CSVI 參數導引視窗，讓後續 MCP 工具可檢視、輸入、截圖並點擊 OK/Cancel
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var seq = mainWin.Sequencer ?? throw new InvalidOperationException("SequencerEditor not found.");
                seq.SetSupervisorMode(true);
                _ = seq.OpenParameterGuidanceForRowAsync(rowIndex);
            });

            // 等待對話框開啟並完成初次排版
            Window? openedDlg = null;
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(30);
                openedDlg = await Dispatcher.UIThread.InvokeAsync(() => GetActiveDialog(mainWin));
                if (openedDlg != null) break;
            }

            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var seq = mainWin.Sequencer ?? throw new InvalidOperationException("SequencerEditor not found.");
                var row = (rowIndex >= 0 && rowIndex < seq.Rows.Count) ? seq.Rows[rowIndex] : null;
                return JsonSerializer.Serialize(new
                {
                    success = openedDlg != null,
                    action = "open_guidance",
                    rowIndex,
                    command = row?.Command ?? "",
                    initialParameter = row?.Parameter ?? "",
                    dialogOpened = openedDlg != null,
                    dialogTitle = openedDlg?.Title ?? ""
                }, new JsonSerializerOptions { WriteIndented = true });
            });
        }

        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var seq = mainWin.Sequencer ?? throw new InvalidOperationException("SequencerEditor not found.");
            switch (action.ToLowerInvariant())
            {
                case "set_mode":
                    bool isSup = !(mode?.Equals("Operator", StringComparison.OrdinalIgnoreCase) ?? false);
                    seq.SetSupervisorMode(isSup);
                    return JsonSerializer.Serialize(new { success = true, mode = isSup ? "Supervisor" : "Operator", isLocked = seq.IsLocked });

                case "select_row":
                    seq.SelectRow(rowIndex);
                    return JsonSerializer.Serialize(new { success = true, selectedRow = rowIndex });

                case "edit_row":
                    if (rowIndex < 0 || rowIndex >= seq.Rows.Count)
                        return JsonSerializer.Serialize(new { success = false, error = $"Invalid rowIndex {rowIndex}" });
                    seq.SetSupervisorMode(true);
                    if (command != null) seq.Rows[rowIndex].Command = command;
                    if (parameter != null) seq.Rows[rowIndex].Parameter = parameter;
                    seq.SyncUIToProgram();
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        rowIndex,
                        command = seq.Rows[rowIndex].Command,
                        parameter = seq.Rows[rowIndex].Parameter
                    });

                case "add_row":
                    seq.SetSupervisorMode(true);
                    seq.TriggerAddRow(rowIndex, command ?? "IO@SetPort", parameter ?? "P0.0");
                    return JsonSerializer.Serialize(new { success = true, rowCount = seq.Rows.Count });

                case "delete_row":
                    seq.SetSupervisorMode(true);
                    seq.TriggerDeleteRow(rowIndex);
                    return JsonSerializer.Serialize(new { success = true, rowCount = seq.Rows.Count });

                case "step":
                    seq.TriggerStep();
                    return JsonSerializer.Serialize(new { success = true, action = "step", currentPC = mainWin.VM.MainThreads[seq.CurrentProgramNumber].ProgIndex });

                case "run":
                    seq.TriggerAutoRun();
                    return JsonSerializer.Serialize(new { success = true, action = "run" });

                case "pause":
                    seq.TriggerPause();
                    return JsonSerializer.Serialize(new { success = true, action = "pause" });

                case "reset":
                    seq.TriggerReset();
                    return JsonSerializer.Serialize(new { success = true, action = "reset" });

                case "jump":
                    seq.TriggerJump(rowIndex);
                    return JsonSerializer.Serialize(new { success = true, action = "jump", currentPC = mainWin.VM.MainThreads[seq.CurrentProgramNumber].ProgIndex });

                default:
                    return JsonSerializer.Serialize(new { success = false, error = $"Unknown sequencer action: {action}" });
            }
        });
    }

    private static string ToolIoAction(MainWindow mainWin, JsonNode? args)
    {
        string action = args?["action"]?.GetValue<string>() ?? "get";
        string port = args?["port"]?.GetValue<string>() ?? "P0.0";
        bool val = args?["value"]?.GetValue<bool>() ?? true;

        switch (action.ToLowerInvariant())
        {
            case "get":
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    port,
                    value = mainWin.Engines.Io.GetPort(port)
                });

            case "set":
                mainWin.Engines.Io.SetPort(port, val);
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    port,
                    value = mainWin.Engines.Io.GetPort(port)
                });

            case "toggle":
                bool cur = mainWin.Engines.Io.GetPort(port);
                mainWin.Engines.Io.SetPort(port, !cur);
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    port,
                    value = mainWin.Engines.Io.GetPort(port)
                });

            case "force_update":
                var drv = mainWin.DriverManager.GetDevice("IO", "StationIO");
                var ticket = new DriverTicket("FORCE_UPDATE");
                drv?.PostTicket(ticket);
                ticket.Wait(200);
                return JsonSerializer.Serialize(new { success = true, action = "force_update" });

            default:
                return JsonSerializer.Serialize(new { success = false, error = $"Unknown IO action: {action}" });
        }
    }

    private static string ToolCaptureUi(MainWindow mainWin, JsonNode? args)
    {
        string outputPath = args?["outputPath"]?.GetValue<string>() ?? throw new ArgumentException("outputPath is required");
        string target = args?["target"]?.GetValue<string>() ?? "Auto";

        string fullOutPath = Path.GetFullPath(outputPath);
        string? dir = Path.GetDirectoryName(fullOutPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var activeDlg = GetActiveDialog(mainWin);

        if (target.Equals("Dialog", StringComparison.OrdinalIgnoreCase) && activeDlg != null)
        {
            using var dlgMat = RenderWindowToBgrMat(activeDlg, 640, 510, drawWindowFrame: true);
            Cv2.ImWrite(fullOutPath, dlgMat);
            return JsonSerializer.Serialize(new
            {
                success = true,
                target = "Dialog",
                dialogTitle = activeDlg.Title,
                outputPath = fullOutPath,
                fileSize = new FileInfo(fullOutPath).Length
            });
        }

        if (target.Equals("SubPanel", StringComparison.OrdinalIgnoreCase))
        {
            var sub = mainWin.FindControl<ContentControl>("UserFrontPanelContainer");
            var subCtrl = (sub?.Content as Control) ?? sub ?? (Control)mainWin;
            using var subMat = RenderControlToBgrMat(subCtrl, 760, 520);
            Cv2.ImWrite(fullOutPath, subMat);
            return JsonSerializer.Serialize(new
            {
                success = true,
                target = "SubPanel",
                outputPath = fullOutPath,
                fileSize = new FileInfo(fullOutPath).Length
            });
        }

        // MainWindow or Auto (MainWindow + ActiveDialog composited on top)
        using var mainMat = RenderWindowToBgrMat(mainWin, 1280, 820, drawWindowFrame: false);

        if (target.Equals("Auto", StringComparison.OrdinalIgnoreCase) && activeDlg != null)
        {
            using var dlgMat = RenderWindowToBgrMat(activeDlg, 640, 510, drawWindowFrame: true);

            // 半透明暗化背景遮罩，突顯模態參數導引視窗
            using var dimmed = new Mat();
            Cv2.AddWeighted(mainMat, 0.55, new Mat(mainMat.Size(), mainMat.Type(), new Scalar(15, 23, 42)), 0.45, 0, dimmed);
            dimmed.CopyTo(mainMat);

            int x = Math.Max(0, (mainMat.Width - dlgMat.Width) / 2);
            int y = Math.Max(0, (mainMat.Height - dlgMat.Height) / 2);
            int w = Math.Min(dlgMat.Width, mainMat.Width - x);
            int h = Math.Min(dlgMat.Height, mainMat.Height - y);

            if (w > 0 && h > 0)
            {
                using var roiDst = new Mat(mainMat, new OpenCvSharp.Rect(x, y, w, h));
                using var roiSrc = new Mat(dlgMat, new OpenCvSharp.Rect(0, 0, w, h));
                roiSrc.CopyTo(roiDst);
            }
        }

        Cv2.ImWrite(fullOutPath, mainMat);
        return JsonSerializer.Serialize(new
        {
            success = true,
            target = (target.Equals("Auto", StringComparison.OrdinalIgnoreCase) && activeDlg != null) ? "Auto (MainWindow + Dialog)" : "MainWindow",
            activeDialogTitle = activeDlg?.Title ?? "",
            outputPath = fullOutPath,
            fileSize = new FileInfo(fullOutPath).Length
        });
    }

    private static Mat RenderWindowToBgrMat(Window win, int defaultW, int defaultH, bool drawWindowFrame)
    {
        var contentCtrl = (Control?)win.Content ?? win;
        int w = (int)(win.Bounds.Width > 50 ? win.Bounds.Width : (win.Width > 50 ? win.Width : defaultW));
        int h = (int)(win.Bounds.Height > 50 ? win.Bounds.Height : (win.Height > 50 ? win.Height : defaultH));

        using var bodyMat = RenderControlToBgrMat(contentCtrl, w, h);
        if (!drawWindowFrame)
        {
            return bodyMat.Clone();
        }

        int titleBarH = 34;
        var framed = new Mat(bodyMat.Height + titleBarH + 4, bodyMat.Width + 4, MatType.CV_8UC3, new Scalar(226, 232, 240));
        // 繪製深色現代標題列
        Cv2.Rectangle(framed, new OpenCvSharp.Rect(0, 0, framed.Width, titleBarH), new Scalar(30, 41, 59), -1);
        Cv2.PutText(framed, win.Title ?? "Dialog", new OpenCvSharp.Point(12, 22), HersheyFonts.HersheySimplex, 0.52, new Scalar(248, 250, 252), 1, LineTypes.AntiAlias);

        using var bodyRoi = new Mat(framed, new OpenCvSharp.Rect(2, titleBarH + 2, bodyMat.Width, bodyMat.Height));
        bodyMat.CopyTo(bodyRoi);
        Cv2.Rectangle(framed, new OpenCvSharp.Rect(0, 0, framed.Width - 1, framed.Height - 1), new Scalar(2, 132, 199), 2);
        return framed;
    }

    private static Mat RenderControlToBgrMat(Control ctrl, int defaultW, int defaultH)
    {
        int w = (int)(ctrl.Bounds.Width > 50 ? ctrl.Bounds.Width : (ctrl.Width > 50 ? ctrl.Width : defaultW));
        int h = (int)(ctrl.Bounds.Height > 50 ? ctrl.Bounds.Height : (ctrl.Height > 50 ? ctrl.Height : defaultH));

        if (ctrl.Bounds.Width <= 50 || ctrl.Bounds.Height <= 50)
        {
            var sz = new Avalonia.Size(w, h);
            ctrl.Measure(sz);
            ctrl.Arrange(new Avalonia.Rect(sz));
        }
        ctrl.UpdateLayout();

        using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        rtb.Render(ctrl);

        using var ms = new MemoryStream();
        rtb.Save(ms);
        byte[] pngBytes = ms.ToArray();

        using var rgba = Cv2.ImDecode(pngBytes, ImreadModes.Unchanged);
        if (rgba.Channels() == 4)
        {
            // 將透明背景合成至淺灰白底色 (#F8FAFC -> BGR: 252, 250, 248)
            var bgr = new Mat(rgba.Size(), MatType.CV_8UC3, new Scalar(252, 250, 248));
            Cv2.Split(rgba, out Mat[] ch);
            using (ch[0]) using (ch[1]) using (ch[2]) using (ch[3])
            {
                using var fgBgr = new Mat();
                Cv2.Merge(new[] { ch[0], ch[1], ch[2] }, fgBgr);
                fgBgr.CopyTo(bgr, ch[3]);
            }
            return bgr;
        }
        return rgba.Clone();
    }
}
