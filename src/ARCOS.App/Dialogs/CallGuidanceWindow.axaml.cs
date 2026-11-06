using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;
using ARCOS.Core.Guidance;

namespace ARCOS.App.Dialogs;

public partial class CallGuidanceWindow : Window
{
    private ComboBox _cboCsviList;
    private Button _btnBrowse;
    private Button _btnRefresh;
    private TextBlock _txtCsviInfo;
    private RadioButton _rbRuntime;
    private RadioButton _rbDebug;
    private RadioButton _rbReentrant;
    private TextBox _txtCommandPreview;
    private Button _btnNext;
    private Button _btnCancel;

    private readonly List<string> _discoveredPaths = new();
    private Window? _parentOwner;
    private BasicEngines? _engines;
    private string _currentParam = "";
    private Action<string>? _onCommandUpdated;
    private string? _finalParamResult = null;

    public CallGuidanceWindow()
    {
        InitializeComponent();
    }

    public static async Task<string?> ShowDialogAsync(
        Window owner,
        string commandKey,
        string currentParam,
        BasicEngines? engines,
        Action<string>? onCommandUpdated = null)
    {
        var win = new CallGuidanceWindow();
        win.Topmost = true;
        win._parentOwner = owner;
        win._engines = engines;
        win._currentParam = currentParam ?? "";
        win._onCommandUpdated = onCommandUpdated;
        win.SetupInitialState(commandKey);

        await win.ShowDialog(owner);
        return win._finalParamResult;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _cboCsviList = this.FindControl<ComboBox>("CboCsviList")!;
        _btnBrowse = this.FindControl<Button>("BtnBrowse")!;
        _btnRefresh = this.FindControl<Button>("BtnRefresh")!;
        _txtCsviInfo = this.FindControl<TextBlock>("TxtCsviInfo")!;
        _rbRuntime = this.FindControl<RadioButton>("RbRuntime")!;
        _rbDebug = this.FindControl<RadioButton>("RbDebug")!;
        _rbReentrant = this.FindControl<RadioButton>("RbReentrant")!;
        _txtCommandPreview = this.FindControl<TextBox>("TxtCommandPreview")!;
        _btnNext = this.FindControl<Button>("BtnNext")!;
        _btnCancel = this.FindControl<Button>("BtnCancel")!;

        _cboCsviList.SelectionChanged += (_, _) => UpdatePreview();
        _rbRuntime.IsCheckedChanged += (_, _) => UpdatePreview();
        _rbDebug.IsCheckedChanged += (_, _) => UpdatePreview();
        _rbReentrant.IsCheckedChanged += (_, _) => UpdatePreview();

        _btnRefresh.Click += (_, _) => RefreshCsviList();
        _btnBrowse.Click += async (_, _) => await BrowseCustomCsviAsync();
        _btnCancel.Click += (_, _) => Close();
        _btnNext.Click += async (_, _) => await OnNextClickedAsync();
    }

    private void SetupInitialState(string commandKey)
    {
        RefreshCsviList();

        string actionPart = commandKey.Contains('@') ? commandKey.Split('@', 2)[1].Trim() : commandKey.Trim();

        // 偵測 Mode
        if (actionPart.EndsWith("&&", StringComparison.Ordinal))
        {
            _rbReentrant.IsChecked = true;
            actionPart = actionPart[..^2];
        }
        else if (actionPart.EndsWith('&'))
        {
            _rbRuntime.IsChecked = true;
            actionPart = actionPart[..^1];
        }
        else if (!string.IsNullOrEmpty(actionPart) && !actionPart.Equals("Call", StringComparison.OrdinalIgnoreCase))
        {
            _rbDebug.IsChecked = true;
        }

        // 自動匹配選中項目
        if (!string.IsNullOrEmpty(actionPart) && !actionPart.Equals("Call", StringComparison.OrdinalIgnoreCase))
        {
            string targetName = actionPart;
            if (targetName.EndsWith(".vi", StringComparison.OrdinalIgnoreCase))
            {
                targetName = targetName[..^3] + ".csvi";
            }
            else if (!targetName.EndsWith(".csvi", StringComparison.OrdinalIgnoreCase))
            {
                targetName += ".csvi";
            }

            int idx = _discoveredPaths.FindIndex(p => Path.GetFileName(p).Equals(targetName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                _cboCsviList.SelectedIndex = idx;
            }
        }

        if (_cboCsviList.SelectedIndex < 0 && _discoveredPaths.Count > 0)
        {
            _cboCsviList.SelectedIndex = 0;
        }

        UpdatePreview();
    }

    private void RefreshCsviList()
    {
        _discoveredPaths.Clear();
        var displayNames = new List<string>();

        string extDir = ArcosPaths.ExternalModulesDirectory;
        if (!Directory.Exists(extDir))
        {
            try { Directory.CreateDirectory(extDir); } catch { }
        }

        var searchDirs = new[]
        {
            extDir,
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExternalModules"),
            Path.Combine(Directory.GetCurrentDirectory(), "ExternalModules")
        }.Distinct();

        foreach (var dir in searchDirs)
        {
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "*.csvi"))
                {
                    if (!_discoveredPaths.Contains(f, StringComparer.OrdinalIgnoreCase))
                    {
                        _discoveredPaths.Add(f);
                        displayNames.Add(Path.GetFileName(f));
                    }
                }
            }
        }

        _cboCsviList.ItemsSource = displayNames;
        if (displayNames.Count > 0 && _cboCsviList.SelectedIndex < 0)
        {
            _cboCsviList.SelectedIndex = 0;
        }
    }

    private async Task BrowseCustomCsviAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "選取外部 CSVI 虛擬儀表 (Select External CSVI)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("CSVI 3.0 Virtual Instrument (*.csvi)") { Patterns = new[] { "*.csvi" } },
                new FilePickerFileType("All Files (*.*)") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
        {
            string localPath = files[0].Path.LocalPath;
            if (!_discoveredPaths.Contains(localPath, StringComparer.OrdinalIgnoreCase))
            {
                _discoveredPaths.Add(localPath);
                var items = (_cboCsviList.ItemsSource as IEnumerable<string>)?.ToList() ?? new List<string>();
                items.Add(Path.GetFileName(localPath));
                _cboCsviList.ItemsSource = items;
            }
            _cboCsviList.SelectedIndex = _discoveredPaths.FindIndex(p => p.Equals(localPath, StringComparison.OrdinalIgnoreCase));
            UpdatePreview();
        }
    }

    private string GetSelectedModeSuffix()
    {
        if (_rbReentrant.IsChecked == true) return "&&";
        if (_rbRuntime.IsChecked == true) return "&";
        return ""; // Debug
    }

    private void UpdatePreview()
    {
        int idx = _cboCsviList.SelectedIndex;
        if (idx >= 0 && idx < _discoveredPaths.Count)
        {
            string path = _discoveredPaths[idx];
            string fileName = Path.GetFileName(path);
            string suffix = GetSelectedModeSuffix();
            string cmd = $"Call@{fileName}{suffix}";
            if (_txtCommandPreview != null) _txtCommandPreview.Text = cmd;
            if (_txtCsviInfo != null) _txtCsviInfo.Text = $"路徑: {path}";
        }
        else
        {
            if (_txtCommandPreview != null) _txtCommandPreview.Text = $"Call{GetSelectedModeSuffix()}";
            if (_txtCsviInfo != null) _txtCsviInfo.Text = "請先選擇或放置 CSVI 檔案於 ExternalModules/ 目錄下";
        }
    }

    private async Task OnNextClickedAsync()
    {
        int idx = _cboCsviList.SelectedIndex;
        if (idx < 0 || idx >= _discoveredPaths.Count)
        {
            return;
        }

        string csviPath = _discoveredPaths[idx];
        string fileName = Path.GetFileName(csviPath);
        string modeSuffix = GetSelectedModeSuffix();
        string newCommand = $"Call@{fileName}{modeSuffix}";

        // 1. 即時回報指令名稱變更 (例如 Call@mycsvi.csvi&)
        _onCommandUpdated?.Invoke(newCommand);

        // 2. 進入階段 2：探測目標 CSVI 是否自帶 IParameterGuidance 視窗
        IParameterGuidance? guidance = TryLoadGuidanceFromCsvi(csviPath);

        // 關閉階段 1 視窗
        Hide();

        if (guidance != null && _engines != null)
        {
            try
            {
                var targetOwner = _parentOwner ?? (TopLevel.GetTopLevel(this) as Window);
                _finalParamResult = await guidance.ShowGuidanceDialogAsync(_currentParam, _engines, targetOwner);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CallGuidance] Error opening CSVI custom guidance: {ex.Message}");
            }
        }
        else
        {
            // 若 CSVI 未實作專屬視窗，調用通用的 ParameterGuidanceWindow 進行兜底配置
            var targetOwner = _parentOwner ?? (TopLevel.GetTopLevel(this) as Window);
            if (targetOwner != null && _engines != null)
            {
                _finalParamResult = await ParameterGuidanceWindow.ShowGuidanceAsync(targetOwner, newCommand, _currentParam, _engines);
            }
        }

        Close();
    }

    private static IParameterGuidance? TryLoadGuidanceFromCsvi(string csviPath)
    {
        try
        {
            if (!File.Exists(csviPath)) return null;
            var asm = ARCOS.Core.Common.PluginLoadManager.LoadOrReloadAssembly(csviPath, out _);
            if (asm == null) return null;

            foreach (var t in asm.GetTypes())
            {
                if (typeof(IParameterGuidance).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                {
                    if (Activator.CreateInstance(t) is IParameterGuidance g)
                    {
                        return g;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CallGuidance] Failed to load IParameterGuidance from '{csviPath}': {ex.Message}");
        }
        return null;
    }
}
