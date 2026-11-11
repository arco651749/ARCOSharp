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
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;
using ARCOS.Core.Execution;
using ARCOS.Core.Model;
using ARCOS.Core.Serialization;
using ARCOS.App.Services;

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
                },
                new
                {
                    name = "arcos_basic_engine",
                    description = "Basic Read/Write/List access for all 8 ARCOS Basic Engines: SYSTEM (Record/Timer/Counter), THREAD (status, load program, start/stop), VARIABLE (list/get/set), POINT (list/get/set coordinates), IO (list/get/set), MOTION (list/get/set position), IMAQ (list/get/load/save image), WAVEFORM (list/get/set DAQ waveform).",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            engine = new { type = "string", description = "SYSTEM | THREAD | VARIABLE | POINT | IO | MOTION | IMAQ | WAVEFORM" },
                            action = new { type = "string", description = "list | get | set | start | stop | set_program | load | save" },
                            subType = new { type = "string", description = "For SYSTEM: Record | Timer | Counter" },
                            name = new { type = "string", description = "Target item name (e.g. Variable key, Point name, IO port/alias, Timer/Counter/Record name, Motion device, IMAQ name, Waveform name)" },
                            section = new { type = "string", description = "Section name for VARIABLE (default: Global)" },
                            value = new { type = "string", description = "Value to write (Variable string, Record message, IO true/false, Timer seconds, Counter count, or file path)" },
                            threadId = new { type = "integer", description = "Thread index (0..12) for THREAD" },
                            progNo = new { type = "integer", description = "Program slot (0..99) for THREAD" },
                            commands = new { type = "array", description = "Array of { command, parameter } or 'Cmd::Param' strings for THREAD set_program" },
                            x = new { type = "number", description = "X coordinate for POINT / MOTION" },
                            y = new { type = "number", description = "Y coordinate for POINT / MOTION" },
                            z = new { type = "number", description = "Z coordinate for POINT / MOTION" },
                            u = new { type = "number", description = "U coordinate for POINT / MOTION" },
                            v = new { type = "number", description = "V coordinate for POINT / MOTION" },
                            w = new { type = "number", description = "W coordinate for POINT / MOTION" },
                            channel = new { type = "integer", description = "Channel index for WAVEFORM (default: 0)" },
                            dt = new { type = "number", description = "Sampling interval dt for WAVEFORM set" },
                            samples = new { type = "array", description = "Numeric array Y[] for WAVEFORM set" }
                        },
                        required = new[] { "engine" }
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
            "arcos_run_command" => await Dispatcher.UIThread.InvokeAsync(() => ToolRunCommand(_mainWindow, args)),
            "arcos_test_thread" => await Dispatcher.UIThread.InvokeAsync(() => ToolTestThread(_mainWindow, args)),
            "arcos_clipboard_action" => await Dispatcher.UIThread.InvokeAsync(() => ToolClipboardAction(_mainWindow, args)),
            "arcos_basic_engine" => await Dispatcher.UIThread.InvokeAsync(() => ToolBasicEngine(_mainWindow, args)),
            "arcos_capture_ui" or "arcos_screenshot" => await Dispatcher.UIThread.InvokeAsync(() => ToolCaptureUi(_mainWindow, args)),
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
            isCurrentPC = r.IsCurrentPC,
            isRangeSelected = r.IsRangeSelected
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

        if (target is ComboBox cb)
        {
            cb.IsDropDownOpen = !cb.IsDropDownOpen;
            mainWin.UpdateLayout();
            return JsonSerializer.Serialize(new { success = true, clicked = "ComboBox", name = cb.Name ?? "", isDropDownOpen = cb.IsDropDownOpen });
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
        int? targetRowIndex = args?["rowIndex"] != null ? args["rowIndex"]!.GetValue<int>() : null;
        int rowIndex = targetRowIndex ?? 0;
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
                    seq.TriggerAddRow(targetRowIndex, command ?? "IO@SetPort", parameter ?? "P0.0");
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

                case "undo":
                    seq.PerformUndo();
                    return JsonSerializer.Serialize(new { success = true, action = "undo", currentProg = seq.CurrentProgramNumber, undoCount = seq.GetUndoCount(), redoCount = seq.GetRedoCount(), rowCount = seq.Rows.Count });

                case "redo":
                    seq.PerformRedo();
                    return JsonSerializer.Serialize(new { success = true, action = "redo", currentProg = seq.CurrentProgramNumber, undoCount = seq.GetUndoCount(), redoCount = seq.GetRedoCount(), rowCount = seq.Rows.Count });

                case "search":
                    seq.PerformSearch(parameter ?? command, forward: true);
                    var txtSearchStatus = seq.FindControl<TextBlock>("TxtSearchStatus");
                    var gridCmds = seq.FindControl<DataGrid>("GridCommands");
                    return JsonSerializer.Serialize(new { success = true, action = "search", keyword = parameter ?? command, status = txtSearchStatus?.Text ?? "", selectedIndex = gridCmds?.SelectedIndex ?? -1 });

                case "get_history":
                    return JsonSerializer.Serialize(new { success = true, currentProg = seq.CurrentProgramNumber, undoCount = seq.GetUndoCount(), redoCount = seq.GetRedoCount(), rowCount = seq.Rows.Count });

                case "clear_section":
                    seq.SetSupervisorMode(true);
                    seq.TriggerClearSection();
                    return JsonSerializer.Serialize(new { success = true, action = "clear_section", currentProg = seq.CurrentProgramNumber, rowCount = seq.Rows.Count, undoCount = seq.GetUndoCount() });

                case "send_key":
                    string keyStr = args?["key"]?.GetValue<string>() ?? "";
                    string modStr = args?["modifiers"]?.GetValue<string>() ?? "";
                    KeyModifiers km = KeyModifiers.None;
                    if (modStr.Contains("Ctrl", StringComparison.OrdinalIgnoreCase) || modStr.Contains("Control", StringComparison.OrdinalIgnoreCase)) km |= KeyModifiers.Control;
                    if (modStr.Contains("Shift", StringComparison.OrdinalIgnoreCase)) km |= KeyModifiers.Shift;
                    if (modStr.Contains("Alt", StringComparison.OrdinalIgnoreCase)) km |= KeyModifiers.Alt;
                    if (Enum.TryParse<Key>(keyStr, true, out var parsedKey))
                    {
                        if (km.HasFlag(KeyModifiers.Control) && parsedKey == Key.T)
                        {
                            mainWin.ToggleTestThreadWindow();
                            return JsonSerializer.Serialize(new { success = true, action = "send_key", key = keyStr, toggledTestThread = true });
                        }
                        seq.SimulateKeyDown(parsedKey, km);
                        return JsonSerializer.Serialize(new { success = true, action = "send_key", key = keyStr, modifiers = modStr, rowCount = seq.Rows.Count, undoCount = seq.GetUndoCount(), redoCount = seq.GetRedoCount() });
                    }
                    return JsonSerializer.Serialize(new { success = false, error = $"Invalid key: {keyStr}" });

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

            case "set_port_name":
                string alias = args?["alias"]?.GetValue<string>() ?? "";
                mainWin.Engines.Io.SetPortName(port, alias);
                return JsonSerializer.Serialize(new { success = true, action = "set_port_name", port, alias });

            case "add_virtual_io":
                mainWin.Engines.Io.AddVirtualIO(port, val);
                return JsonSerializer.Serialize(new { success = true, action = "add_virtual_io", name = port, value = val });

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

    private static string ToolRunCommand(MainWindow mainWin, JsonNode? args)
    {
        string cmd = args?["command"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(cmd))
            return JsonSerializer.Serialize(new { success = false, error = "command is required" });

        var ctx = mainWin.VM.MainThreads[0];
        var result = mainWin.VM.Executor.ExecuteCommandLine(ctx, cmd);
        return JsonSerializer.Serialize(new
        {
            success = result.Status != ARCOS.Core.Execution.CommandStatus.Error,
            status = result.Status.ToString(),
            errorMessage = result.ErrorMessage,
            command = cmd
        });
    }

    private static string ToolTestThread(MainWindow mainWin, JsonNode? args)
    {
        string action = args?["action"]?.GetValue<string>() ?? "toggle";
        if (action.Equals("toggle", StringComparison.OrdinalIgnoreCase))
        {
            mainWin.ToggleTestThreadWindow();
        }
        else if (action.Equals("set_opacity", StringComparison.OrdinalIgnoreCase))
        {
            double op = args?["opacity"]?.GetValue<double>() ?? 1.0;
            if (mainWin.TestThreadWindow != null)
            {
                mainWin.TestThreadWindow.Opacity = op;
                var slider = mainWin.TestThreadWindow.FindControl<Slider>("SliderOpacity");
                if (slider != null) slider.Value = op;
            }
        }
        else if (action.Equals("set_section", StringComparison.OrdinalIgnoreCase))
        {
            string sec = args?["section"]?.GetValue<string>() ?? "Test";
            if (mainWin.TestThreadWindow != null)
            {
                var cmb = mainWin.TestThreadWindow.FindControl<ComboBox>("CmbVariableSection");
                if (cmb != null) cmb.SelectedItem = sec;
            }
        }
        else if (action.Equals("load_prog", StringComparison.OrdinalIgnoreCase) || action.Equals("load_pro", StringComparison.OrdinalIgnoreCase))
        {
            string proPath = args?["path"]?.GetValue<string>() ?? Path.Combine(ArcosPaths.RootDirectory, "Master.prog");
            if (!File.Exists(proPath) && File.Exists(Path.Combine(ArcosPaths.RootDirectory, "Master.pro")))
            {
                proPath = Path.Combine(ArcosPaths.RootDirectory, "Master.pro");
            }
            if (File.Exists(proPath) && mainWin.VM != null)
            {
                var progs = ScriptSerializer.LoadProgFile(proPath);
                foreach (var p in progs)
                {
                    mainWin.VM.LoadProgram(p.ProgramNumber, p);
                }
                mainWin.VM.NotifyProgramLoaded(proPath, true);
                if (mainWin.TestThreadWindow != null)
                {
                    mainWin.TestThreadWindow.LoadProgramSlot(0);
                }
            }
        }
        else if (action.Equals("step", StringComparison.OrdinalIgnoreCase))
        {
            if (mainWin.TestThreadWindow != null)
            {
                var btnStep = mainWin.TestThreadWindow.FindControl<Button>("BtnStep");
                btnStep?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }
        else if (action.Equals("open_guidance", StringComparison.OrdinalIgnoreCase))
        {
            int rowIdx = args?["rowIndex"]?.GetValue<int>() ?? 0;
            if (mainWin.TestThreadWindow != null)
            {
                _ = mainWin.TestThreadWindow.OpenParameterGuidanceForRowAsync(rowIdx);
            }
        }
        else if (action.Equals("open_com_select", StringComparison.OrdinalIgnoreCase))
        {
            int rowIdx = args?["rowIndex"]?.GetValue<int>() ?? 0;
            if (mainWin.TestThreadWindow != null)
            {
                _ = mainWin.TestThreadWindow.OpenCommandSelectForRowAsync(rowIdx);
            }
        }

        var win = mainWin.TestThreadWindow;
        return JsonSerializer.Serialize(new
        {
            success = true,
            isCreated = win != null,
            isVisible = win?.IsVisible ?? false,
            windowState = win?.WindowState.ToString(),
            opacity = win?.Opacity ?? 1.0,
            topmost = win?.Topmost ?? false
        });
    }

    private static string ToolClipboardAction(MainWindow mainWin, JsonNode? args)
    {
        string op = args?["action"]?.GetValue<string>() ?? "select";
        string target = args?["target"]?.GetValue<string>() ?? "UserTab";
        int start = args?["start"]?.GetValue<int>() ?? 0;
        int end = args?["end"]?.GetValue<int>() ?? 0;
        int index = args?["index"]?.GetValue<int>() ?? 0;

        var userSeq = mainWin.Sequencer;
        var testWin = mainWin.TestThreadWindow;

        bool isUser = target.Equals("UserTab", StringComparison.OrdinalIgnoreCase);

        if (op.Equals("select", StringComparison.OrdinalIgnoreCase))
        {
            if (isUser) userSeq?.SelectRange(start, end);
            else testWin?.SelectRange(start, end);
        }
        else if (op.Equals("copy_insert", StringComparison.OrdinalIgnoreCase))
        {
            if (isUser) userSeq?.PasteFromClipboard(index);
            else testWin?.PasteFromClipboard(index);
        }
        else if (op.Equals("move", StringComparison.OrdinalIgnoreCase))
        {
            if (isUser) userSeq?.MoveSelectedRange(index);
            else testWin?.MoveSelectedRange(index);
        }
        else if (op.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            if (isUser) userSeq?.DeleteSelectedRange();
            else testWin?.DeleteSelectedRange();
        }

        var userRows = userSeq?.Rows.Select(r => new { r.LineNumber, r.Command, r.Parameter, r.IsRangeSelected }).ToList();
        var testRows = testWin?.Rows.Select(r => new { r.LineNumber, r.Command, r.Parameter, r.IsRangeSelected }).ToList();

        return JsonSerializer.Serialize(new
        {
            success = true,
            action = op,
            target = target,
            clipboardCount = SequencerClipboardManager.Instance.Buffer.Count,
            userRows,
            testRows
        });
    }

    /// <summary>
    /// 8 大 Basic Engine 基礎資料讀寫與清單查詢 (SYSTEM, THREAD, VARIABLE, POINT, IO, MOTION, IMAQ, WAVEFORM)
    /// </summary>
    private static string ToolBasicEngine(MainWindow mainWin, JsonNode? args)
    {
        string engine = args?["engine"]?.GetValue<string>()?.Trim().ToUpperInvariant() ?? "SYSTEM";
        string action = args?["action"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "list";
        string name = args?["name"]?.GetValue<string>()?.Trim() ?? "";
        string section = args?["section"]?.GetValue<string>()?.Trim() ?? "Global";
        string value = args?["value"]?.GetValue<string>() ?? "";

        var engines = mainWin.Engines;
        var vm = mainWin.VM;
        var drivers = mainWin.DriverManager;

        switch (engine)
        {
            // 1. SYSTEM (Record / Timer / Counter)
            case "SYSTEM":
            {
                string subType = args?["subType"]?.GetValue<string>()?.Trim().ToUpperInvariant() ?? "RECORD";
                if (subType == "TIMER")
                {
                    if (action == "set" || action == "write" || action == "reset")
                    {
                        double sec = double.TryParse(value, out double d) ? d : (args?["seconds"]?.GetValue<double>() ?? 1.0);
                        engines.Timer.TimerReset(name, sec);
                    }
                    bool isTimeout = !string.IsNullOrEmpty(name) && engines.Timer.TimerCheck(name);
                    long elapsedMs = !string.IsNullOrEmpty(name) ? engines.Timer.GetElapsedMs(name) : 0;
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        engine = "SYSTEM",
                        subType = "TIMER",
                        name,
                        elapsedMs,
                        isTimeout,
                        isSystemPaused = engines.Timer.IsSystemPaused
                    });
                }
                if (subType == "COUNTER")
                {
                    if (action == "set" || action == "write" || action == "reset")
                    {
                        int countVal = int.TryParse(value, out int c) ? c : (args?["count"]?.GetValue<int>() ?? 0);
                        engines.Timer.CounterReset(name, countVal);
                    }
                    int currentCount = !string.IsNullOrEmpty(name) ? engines.Timer.GetCount(name) : 0;
                    bool isOver = !string.IsNullOrEmpty(name) && engines.Io.GetPort($"{name}#Over?");
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        engine = "SYSTEM",
                        subType = "COUNTER",
                        name,
                        count = currentCount,
                        isOver
                    });
                }

                // 預設：RECORD
                string recName = string.IsNullOrEmpty(name) ? "SYSTEMLOG" : name;
                if (action == "set" || action == "write")
                {
                    engines.Record.Record(recName, value);
                }
                var ch = engines.Record.GetChannel(recName);
                var entries = ch?.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Message))
                    .TakeLast(50)
                    .Select(e => new { e.Timestamp, e.Message })
                    .ToList();
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    engine = "SYSTEM",
                    subType = "RECORD",
                    channels = engines.Record.GetAllRecordNames(),
                    channel = recName,
                    elapsedSeconds = Math.Round(engines.Record.GetRecordElapsedSeconds(recName), 4),
                    entries
                });
            }

            // 2. THREAD (狀態查詢、下程式 set_program、啟動 start、停止 stop)
            case "THREAD":
            {
                int threadId = args?["threadId"]?.GetValue<int>() ?? 0;
                int progNo = args?["progNo"]?.GetValue<int>() ?? threadId;
                progNo = Math.Clamp(progNo, 0, 99);
                threadId = Math.Clamp(threadId, 0, 12);

                if (action == "set_program" || action == "set" || action == "write")
                {
                    var prog = new ProgramTable { ProgramNumber = progNo, Name = string.IsNullOrEmpty(name) ? $"Program_{progNo}" : name };
                    if (args?["commands"] is JsonArray cmdArr)
                    {
                        int lineIdx = 0;
                        foreach (var item in cmdArr)
                        {
                            if (item is JsonObject obj)
                            {
                                string c = obj["command"]?.GetValue<string>() ?? "";
                                string p = obj["parameter"]?.GetValue<string>() ?? "";
                                prog.Add(CommandItem.Parse($"{c}\t{p}", lineIdx++));
                            }
                            else if (item != null)
                            {
                                string raw = item.GetValue<string>();
                                string normalized = raw.Contains("::") ? raw.Replace("::", "\t").TrimEnd(';') : raw;
                                prog.Add(CommandItem.Parse(normalized, lineIdx++));
                            }
                        }
                    }
                    vm.LoadProgram(progNo, prog);
                    if (mainWin.Sequencer?.CurrentProgramNumber == progNo)
                    {
                        mainWin.Sequencer.LoadCurrentProgramToUI();
                    }
                }
                else if (action == "start" || action == "run")
                {
                    vm.StartThread(threadId, progNo, 1);
                }
                else if (action == "stop" || action == "suspend")
                {
                    vm.SuspendThread(threadId);
                }
                else if (action == "stop_all")
                {
                    vm.StopAll();
                }

                var targetProg = vm.GetProgram(progNo);
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    engine = "THREAD",
                    threads = vm.MainThreads.Select(t => new
                    {
                        t.ThreadId,
                        t.ThreadName,
                        t.IsRunning,
                        t.ProgIndex,
                        programNumber = t.Program?.ProgramNumber ?? 0
                    }).ToList(),
                    subThreads = vm.SubThreads.Values.Select(s => new
                    {
                        s.ThreadName,
                        s.IsRunning,
                        s.ProgIndex,
                        programNumber = s.Program?.ProgramNumber ?? 0
                    }).ToList(),
                    program = new
                    {
                        progNo = targetProg.ProgramNumber,
                        name = targetProg.Name,
                        count = targetProg.Count,
                        items = targetProg.Items.Select((it, idx) => new { line = idx, command = it.Command, parameter = it.RawParameter }).ToList()
                    }
                });
            }

            // 3. VARIABLE (list / get / set)
            case "VARIABLE":
            {
                if (action == "set" || action == "write")
                {
                    engines.Variable.Set(name, value, section);
                }
                if (!string.IsNullOrEmpty(name) && (action == "get" || action == "read" || action == "set" || action == "write"))
                {
                    string val = "";
                    string rawVal = "";
                    try
                    {
                        val = engines.Variable.Get(name, section);
                        rawVal = engines.Variable.GetRaw(name, section);
                    }
                    catch { }
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        engine = "VARIABLE",
                        section,
                        name,
                        value = val,
                        rawValue = rawVal
                    });
                }

                var sections = engines.Variable.GetAllSections();
                var snapshot = engines.Variable.GetSectionSnapshot(section);
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    engine = "VARIABLE",
                    sections,
                    section,
                    variables = snapshot
                });
            }

            // 4. POINT / POINT&PATH (list / get / set)
            case "POINT":
            case "POINT&PATH":
            {
                if (action == "set" || action == "write")
                {
                    double x = args?["x"]?.GetValue<double>() ?? 0;
                    double y = args?["y"]?.GetValue<double>() ?? 0;
                    double z = args?["z"]?.GetValue<double>() ?? 0;
                    double u = args?["u"]?.GetValue<double>() ?? 0;
                    double v = args?["v"]?.GetValue<double>() ?? 0;
                    double w = args?["w"]?.GetValue<double>() ?? 0;
                    engines.Point.SetPoint(name, new MachinePoint(x, y, z, u, v, w));
                }

                if (!string.IsNullOrEmpty(name) && action != "list")
                {
                    var pt = engines.Point.GetPoint(name);
                    var arr = engines.Point.GetPointArray(name);
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        engine = "POINT",
                        name,
                        current = new { pt.X, pt.Y, pt.Z, pt.U, pt.V, pt.W },
                        count = arr.Count,
                        points = arr.Select(p => new { p.X, p.Y, p.Z, p.U, p.V, p.W }).ToList()
                    });
                }

                var allPts = engines.Point.GetAllPoints().ToDictionary(
                    k => k.Key,
                    k => new { k.Value.X, k.Value.Y, k.Value.Z, k.Value.U, k.Value.V, k.Value.W });
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    engine = "POINT",
                    names = engines.Point.GetAllNames(),
                    points = allPts
                });
            }

            // 5. IO (list / get / set / set_alias)
            case "IO":
            {
                if (action == "set" || action == "write")
                {
                    bool bVal = args?["boolValue"]?.GetValue<bool>()
                        ?? (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("on", StringComparison.OrdinalIgnoreCase));
                    engines.Io.SetPort(name, bVal);
                }
                else if (action == "set_alias")
                {
                    engines.Io.SetPortName(name, value);
                }

                if (!string.IsNullOrEmpty(name) && action != "list")
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        engine = "IO",
                        name,
                        value = engines.Io.GetPort(name)
                    });
                }

                var snap = engines.Io.GetAllSnapshot()
                    .Where(s => s.Value || !string.IsNullOrEmpty(s.Alias) || s.IsVirtual)
                    .Select(s => new { s.Port, s.Alias, s.Value, s.IsVirtual })
                    .ToList();
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    engine = "IO",
                    drivers = engines.Io.GetRegisteredDrivers(),
                    activeOrNamedPorts = snap
                });
            }

            // 6. MOTION (list / get / set position)
            case "MOTION":
            {
                var motionDevices = drivers.GetDevicesByCategory("Motion");
                if (!string.IsNullOrEmpty(name) && (action == "set" || action == "write" || action == "go" || action == "home" || action == "stop"))
                {
                    var dev = drivers.GetDevice("Motion", name);
                    if (action == "home")
                    {
                        dev?.PostTicket(new DriverTicket("Home", "", "MCP"));
                    }
                    else if (action == "stop")
                    {
                        dev?.PostTicket(new DriverTicket("Stop", "", "MCP"));
                    }
                    else
                    {
                        double x = args?["x"]?.GetValue<double>() ?? 0;
                        double y = args?["y"]?.GetValue<double>() ?? 0;
                        double z = args?["z"]?.GetValue<double>() ?? 0;
                        double u = args?["u"]?.GetValue<double>() ?? 0;
                        double v = args?["v"]?.GetValue<double>() ?? 0;
                        double w = args?["w"]?.GetValue<double>() ?? 0;
                        engines.Point.SetPoint($"{name}_CurPos", new MachinePoint(x, y, z, u, v, w));
                        dev?.PostTicket(new DriverTicket("Go", $"X={x},Y={y},Z={z},U={u},V={v},W={w}", "MCP"));
                    }
                }

                var motionStates = motionDevices.Select(d =>
                {
                    MachinePoint cur = new();
                    try { cur = engines.Point.GetPoint($"{d.DeviceName}_CurPos"); } catch { }
                    return new
                    {
                        deviceName = d.DeviceName,
                        driverType = d.DriverType,
                        busy = engines.Io.GetPort($"{d.DeviceName}#Busy?"),
                        done = engines.Io.GetPort($"{d.DeviceName}#Done?"),
                        homed = engines.Io.GetPort($"{d.DeviceName}#Homed?"),
                        alarm = engines.Io.GetPort($"{d.DeviceName}#Alarm?"),
                        position = new { cur.X, cur.Y, cur.Z, cur.U, cur.V, cur.W }
                    };
                }).ToList();

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    engine = "MOTION",
                    devices = motionStates
                });
            }

            // 7. IMAQ / CAPTURE (list / get / load / save / set_roi)
            case "IMAQ":
            case "CAPTURE":
            {
                if (!string.IsNullOrEmpty(name))
                {
                    if (action == "load" && !string.IsNullOrEmpty(value))
                    {
                        engines.Image.LoadImage(name, value);
                    }
                    else if (action == "save" && !string.IsNullOrEmpty(value))
                    {
                        engines.Image.SaveImage(name, value, includeOverlays: true);
                    }
                    else if (action == "set" || action == "set_roi")
                    {
                        engines.Image.SetRoi(name, value);
                    }

                    if (action != "list")
                    {
                        var frames = engines.Image.GetFrameArray(name);
                        var curFrame = frames.Count > 0 ? engines.Image.GetFrame(name) : null;
                        return JsonSerializer.Serialize(new
                        {
                            success = true,
                            engine = "IMAQ",
                            name,
                            frameCount = frames.Count,
                            width = curFrame?.Image.Width ?? 0,
                            height = curFrame?.Image.Height ?? 0,
                            roi = engines.Image.GetRoi(name),
                            ok = engines.Io.GetPort($"{name}#OK?")
                        });
                    }
                }

                var allImaq = engines.Image.GetAllNames().Select(n =>
                {
                    var arr = engines.Image.GetFrameArray(n);
                    var f = arr.Count > 0 ? engines.Image.GetFrame(n) : null;
                    return new
                    {
                        name = n,
                        frameCount = arr.Count,
                        width = f?.Image.Width ?? 0,
                        height = f?.Image.Height ?? 0,
                        roi = engines.Image.GetRoi(n)
                    };
                }).ToList();

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    engine = "IMAQ",
                    images = allImaq
                });
            }

            // 8. WAVEFORM / DAQ (list / get / set)
            case "WAVEFORM":
            case "DAQ":
            {
                int chIdx = args?["channel"]?.GetValue<int>() ?? 0;
                if (!string.IsNullOrEmpty(name))
                {
                    if (action == "set" || action == "write")
                    {
                        double dt = args?["dt"]?.GetValue<double>() ?? 0.001;
                        var yList = new List<double>();
                        if (args?["samples"] is JsonArray sArr)
                        {
                            foreach (var s in sArr)
                            {
                                if (s != null && double.TryParse(s.ToString(), out double dv))
                                    yList.Add(dv);
                            }
                        }
                        else if (!string.IsNullOrEmpty(value))
                        {
                            foreach (var part in value.Split(',', ';'))
                            {
                                if (double.TryParse(part.Trim(), out double dv))
                                    yList.Add(dv);
                            }
                        }
                        engines.Daq.SetWaveform(name, DateTime.Now, dt, yList.ToArray(), chIdx);
                    }

                    if (action != "list")
                    {
                        try
                        {
                            var wf = engines.Daq.GetWaveform(name);
                            var chData = wf.GetOrCreateChannel(chIdx);
                            return JsonSerializer.Serialize(new
                            {
                                success = true,
                                engine = "WAVEFORM",
                                name,
                                channel = chIdx,
                                t0 = chData.T0.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                                dt = chData.Dt,
                                count = chData.Y.Length,
                                instantValue = engines.Daq.GetInstantValue(name, chIdx),
                                samples = chData.Y
                            });
                        }
                        catch
                        {
                            return JsonSerializer.Serialize(new { success = false, engine = "WAVEFORM", error = $"Waveform '{name}' not found." });
                        }
                    }
                }

                var wfNames = engines.Daq.GetAllNames();
                var summaries = wfNames.Select(n =>
                {
                    var wf = engines.Daq.GetWaveform(n);
                    return new
                    {
                        name = n,
                        channels = wf.Channels.Select(c => new
                        {
                            channel = c.Key,
                            dt = c.Value.Dt,
                            sampleCount = c.Value.Y.Length,
                            instantValue = c.Value.Y.Length > 0 ? c.Value.Y[^1] : 0.0
                        }).ToList()
                    };
                }).ToList();

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    engine = "WAVEFORM",
                    waveforms = summaries
                });
            }

            default:
                throw new ArgumentException($"Unsupported engine '{engine}'. Expected: SYSTEM | THREAD | VARIABLE | POINT | IO | MOTION | IMAQ | WAVEFORM");
        }
    }

    private static string ToolCaptureUi(MainWindow mainWin, JsonNode? args)
    {
        string outputPath = args?["outputPath"]?.GetValue<string>() ?? args?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("outputPath or filePath is required");
        string target = args?["target"]?.GetValue<string>() ?? "Auto";

        string fullOutPath = Path.GetFullPath(outputPath);
        string? dir = Path.GetDirectoryName(fullOutPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var activeDlg = GetActiveDialog(mainWin);

        if ((target.Equals("TestThread", StringComparison.OrdinalIgnoreCase) || target.Equals("TestWindow", StringComparison.OrdinalIgnoreCase)) && mainWin.TestThreadWindow != null)
        {
            using var testMat = RenderWindowToBgrMat(mainWin.TestThreadWindow, 420, 780, drawWindowFrame: true);
            Cv2.ImWrite(fullOutPath, testMat);
            return JsonSerializer.Serialize(new
            {
                success = true,
                target = "TestThread",
                dialogTitle = mainWin.TestThreadWindow.Title,
                outputPath = fullOutPath,
                fileSize = new FileInfo(fullOutPath).Length
            });
        }

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

        // 若有開啟的 ComboBox 下拉選單，將其下拉列表視覺合成在 mainMat 之上
        var openCombos = EnumerateControlsRecursive(mainWin).OfType<ComboBox>().Where(c => c.IsDropDownOpen).ToList();
        foreach (var oc in openCombos)
        {
            var origin = oc.TranslatePoint(new Avalonia.Point(0, oc.Bounds.Height), mainWin) ?? new Avalonia.Point(oc.Bounds.X, oc.Bounds.Y + oc.Bounds.Height);
            int px = (int)origin.X;
            int py = (int)origin.Y;
            int pw = (int)Math.Max(oc.Bounds.Width > 50 ? oc.Bounds.Width : 120, 100);

            var itemsList = new List<string>();
            if (oc.ItemsSource != null)
            {
                foreach (var item in oc.ItemsSource)
                {
                    string s = (item as ComboBoxItem)?.Content?.ToString() ?? item?.ToString() ?? "";
                    itemsList.Add(s);
                }
            }
            else if (oc.Items != null)
            {
                foreach (var item in oc.Items)
                {
                    string s = (item as ComboBoxItem)?.Content?.ToString() ?? item?.ToString() ?? "";
                    itemsList.Add(s);
                }
            }

            if (itemsList.Count > 0)
            {
                int itemH = 24;
                int maxVisibleItems = Math.Min(itemsList.Count, 13);
                int ph = maxVisibleItems * itemH + 6;

                using var dropMat = new Mat(ph, pw, MatType.CV_8UC3, new Scalar(255, 255, 255));
                Cv2.Rectangle(dropMat, new OpenCvSharp.Rect(0, 0, pw - 1, ph - 1), new Scalar(2, 132, 199), 1);

                for (int i = 0; i < maxVisibleItems; i++)
                {
                    int iy = 3 + i * itemH;
                    bool isSel = (i == oc.SelectedIndex);
                    if (isSel)
                    {
                        Cv2.Rectangle(dropMat, new OpenCvSharp.Rect(1, iy, pw - 2, itemH), new Scalar(240, 249, 255), -1);
                    }
                    var textColor = isSel ? new Scalar(199, 132, 2) : new Scalar(51, 65, 85);
                    Cv2.PutText(dropMat, itemsList[i], new OpenCvSharp.Point(8, iy + 17), HersheyFonts.HersheySimplex, 0.42, textColor, isSel ? 2 : 1, LineTypes.AntiAlias);
                }

                int renderW = Math.Min(dropMat.Width, mainMat.Width - px);
                int renderH = Math.Min(dropMat.Height, mainMat.Height - py);
                if (renderW > 0 && renderH > 0 && px >= 0 && py >= 0)
                {
                    using var dstRoi = new Mat(mainMat, new OpenCvSharp.Rect(px, py, renderW, renderH));
                    using var srcRoi = new Mat(dropMat, new OpenCvSharp.Rect(0, 0, renderW, renderH));
                    srcRoi.CopyTo(dstRoi);
                }
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
