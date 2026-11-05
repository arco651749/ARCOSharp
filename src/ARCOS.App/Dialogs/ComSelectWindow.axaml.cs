using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ARCOS.Core.Execution;

namespace ARCOS.App.Dialogs;

public partial class ComSelectWindow : Window
{
    public sealed class CategoryItem
    {
        public string DisplayName { get; init; } = "";
        public string ModuleKey { get; init; } = "";
    }

    private readonly Dictionary<string, List<string>> _moduleActions = new(StringComparer.OrdinalIgnoreCase);
    private string _selectedModule = "System";
    public string? SelectedCommand { get; private set; }

    public ComSelectWindow()
    {
        InitializeComponent();
        InitializeDefaultCommands();
        LoadCategories();
        SelectModule("System");
    }

    public ComSelectWindow(ModuleRegistry registry) : this()
    {
        if (registry != null)
        {
            // 動態從註冊表擴充模組指令
            foreach (var cmd in registry.GetAllCommands("*"))
            {
                var parts = cmd.Split('@', 2);
                if (parts.Length == 2)
                {
                    string mod = parts[0];
                    string act = parts[1];
                    if (!_moduleActions.TryGetValue(mod, out var list))
                    {
                        list = new List<string>();
                        _moduleActions[mod] = list;
                    }
                    if (!list.Contains(act)) list.Add(act);
                }
            }
            LoadCategories();
            SelectModule("System");
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void InitializeDefaultCommands()
    {
        // 1. System
        _moduleActions["System"] = new List<string>
        {
            "Note", "Log Message", "Log Error", "Clear Message",
            "Clear Error", "Delay", "Record", "RecordWrite",
            "RecordRead", "LogRecordCycleTime", "TimerReset", "TimerCheck",
            "CounterReset", "CounterCheck", "MessageBox", "WarningBox",
            "ErrorBox", "ParameterSet", "SystemPause", "AddExternalDevice", "Nop"
        };

        // 2. Thread
        _moduleActions["Thread"] = new List<string>
        {
            "Thread Label", "Jump", "JumpIf", "JumpIfNot",
            "Run", "Pause", "Resume", "Stop",
            "Call", "Return", "Loop", "LoopEnd",
            "ErrorHandler", "ErrorRetry", "ErrorGoto"
        };

        // 3. IO
        _moduleActions["IO"] = new List<string>
        {
            "BitOn", "BitOff", "BitWait", "BitCheck",
            "PortWrite", "PortRead", "AddIODriver"
        };

        // 4. Motion
        _moduleActions["Motion"] = new List<string>
        {
            "AbsMove", "RelMove", "Jog", "Home",
            "Stop", "WaitDone", "AddMotion", "GetPos"
        };

        // 5. Capture
        _moduleActions["Capture"] = new List<string>
        {
            "Snap", "Live", "Stop", "SaveImage",
            "LoadImage", "FindCircle", "FindPattern", "Threshold", "Binarize"
        };

        // 6. Point&Path
        _moduleActions["Point&Path"] = new List<string>
        {
            "TeachPoint", "MovePoint", "LoadPath", "RunPath", "SetCoord"
        };

        // 7. Variable
        _moduleActions["Variable"] = new List<string>
        {
            "Set", "Get", "Calc", "Compare", "Clear"
        };

        // 8. StationInfo
        _moduleActions["StationInfo"] = new List<string>
        {
            "SetStation", "GetStation", "SetBatch", "GetYield", "ResetStats"
        };

        // 9. MultiStation
        _moduleActions["MultiStation"] = new List<string>
        {
            "SyncWait", "SyncSignal", "Handoff", "CheckStatus"
        };

        // 10. DAQ
        _moduleActions["DAQ"] = new List<string>
        {
            "SampleOnce", "StartContinuous", "StopContinuous", "ReadBuffer"
        };

        // 11. User
        _moduleActions["User"] = new List<string>
        {
            "ShowDialog", "InputText", "Confirm", "SelectRecipe"
        };

        // 12. Call (動態掃描 ExternalModules/ 目錄下所有可用之 CSVI 虛擬儀表)
        var callActions = new List<string>();
        try
        {
            string extDir = ARCOS.Core.Common.ArcosPaths.ExternalModulesDirectory;
            if (Directory.Exists(extDir))
            {
                foreach (var f in Directory.GetFiles(extDir, "*.csvi"))
                {
                    callActions.Add(Path.GetFileName(f));
                }
            }
        }
        catch { }
        if (callActions.Count == 0)
        {
            callActions.Add("mycsvi.csvi");
        }
        _moduleActions["Call"] = callActions;

        // 13. Python
        _moduleActions["Python"] = new List<string>
        {
            "Script", "File", "SetVariable", "GetVariable",
            "SetPoint", "GetPoint", "SetCapture", "GetCapture",
            "SetCaptureFile", "ResetSession", "Init"
        };

        // 14. AiAgent
        _moduleActions["AiAgent"] = new List<string>
        {
            "InspectDefect", "SuggestCorrection", "AuditLogs", "AutonomousRun"
        };
    }

    private void LoadCategories()
    {
        var itemsCtrl = this.FindControl<ItemsControl>("ItemsCategories");
        if (itemsCtrl != null)
        {
            var categories = _moduleActions.Keys.Select(k => new CategoryItem
            {
                DisplayName = $"{k} >",
                ModuleKey = k
            }).ToList();
            itemsCtrl.ItemsSource = categories;
        }
    }

    private void SelectModule(string moduleKey)
    {
        _selectedModule = moduleKey;
        var txtCategory = this.FindControl<TextBlock>("TxtSelectedCategory");
        if (txtCategory != null) txtCategory.Text = $"目前分類: [ {moduleKey} > ]";

        var txtHeader = this.FindControl<TextBlock>("TxtSubCategoryHeader");
        if (txtHeader != null) txtHeader.Text = $"2. 具體指令 (Com Select2.vi : {moduleKey})";

        var itemsActions = this.FindControl<ItemsControl>("ItemsActions");
        if (itemsActions != null)
        {
            if (_moduleActions.TryGetValue(moduleKey, out var acts))
            {
                itemsActions.ItemsSource = acts;
            }
            else
            {
                itemsActions.ItemsSource = Array.Empty<string>();
            }
        }
    }

    private void OnCategoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string modKey)
        {
            SelectModule(modKey);
        }
    }

    private void OnActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Content is string actName)
        {
            SelectedCommand = $"{_selectedModule}@{actName}";
            Close(true);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        SelectedCommand = null;
        Close(false);
    }

    public static async Task<string?> SelectCommandAsync(Window owner, ModuleRegistry? registry = null)
    {
        var win = registry != null ? new ComSelectWindow(registry) : new ComSelectWindow();
        win.Topmost = true;
        bool? ok = await win.ShowDialog<bool?>(owner);
        return (ok == true) ? win.SelectedCommand : null;
    }
}