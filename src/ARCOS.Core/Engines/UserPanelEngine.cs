using System;
using System.Collections.Generic;
using System.Linq;

namespace ARCOS.Core.Engines;

public enum UserPanelElementRole
{
    Control,
    Indicator
}

public enum UserPanelControlKind
{
    Button,
    ToggleButton,
    CheckBox,
    TextBox,
    TextBlock,
    ComboBox,
    TabControl,
    Image,
    Plot,
    Led,
    Unknown
}

public enum UserPanelCommandType
{
    ButtonValue,
    ButtonText,
    TextValue,
    SelectTab,
    SelectCombo,
    SetBlinking,
    SetEnabled,
    SetVisible,
    ParamRefresh,
    SwitchPanel,
    ClosePanel
}

public sealed record UserPanelCommandRequest(
    UserPanelCommandType CommandType,
    string ControlName,
    string Value,
    bool BoolValue,
    bool TriggerEvent)
{
    public string PanelName { get; init; } = "";
}

public sealed record UserPanelSlotInfo(
    string PanelName,
    string FilePath,
    bool IsActive,
    int ControlCount);

public sealed class UserPanelControlInfo
{
    public string PanelName { get; set; } = "";
    public string Name { get; init; } = "";
    public UserPanelControlKind Kind { get; set; } = UserPanelControlKind.Unknown;
    public UserPanelElementRole Role { get; set; } = UserPanelElementRole.Control;
    public string Value { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsBlinking { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsVisible { get; set; } = true;
    public string BindingTarget { get; set; } = "";
    public string Options { get; set; } = "";
    public bool IsMountedOnUi { get; set; }
}

/// <summary>
/// UserPanel Control & Indicator 統一查詢、讀寫與多面板核心-UI 雙向橋接引擎。
/// </summary>
public class UserPanelEngine
{
    public const string DefaultPanelName = "Default";

    private sealed class PanelSlot
    {
        public string PanelName { get; set; } = DefaultPanelName;
        public string FilePath { get; set; } = "";
        public Dictionary<string, UserPanelControlInfo> Controls { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, PanelSlot> _panels = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _panelOrder = new();
    private string _activePanelName = DefaultPanelName;

    public event Action<UserPanelCommandRequest>? CommandRequested;
    public event Action? ControlsChanged;
    public event Action? PanelsChanged;

    /// <summary>
    /// 目前處於前景焦點的 UserPanel 名稱。
    /// </summary>
    public string ActivePanelName
    {
        get
        {
            lock (_lock) return _activePanelName;
        }
    }

    /// <summary>
    /// 由 UI 宿主 (MainWindow) 註冊的即時讀取委派；無頭測試時保持 null 並退回狀態快取。
    /// </summary>
    public Func<string, string?>? LiveReadHandler { get; set; }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _panels.TryGetValue(_activePanelName, out var slot) ? slot.Controls.Count : 0;
            }
        }
    }

    public static (string CsviPath, string PanelName, bool Activate) ParseOpenSpec(string? rawSpec)
    {
        if (string.IsNullOrWhiteSpace(rawSpec))
            return ("", DefaultPanelName, true);

        var parts = rawSpec.Split(',', StringSplitOptions.TrimEntries);
        string csviPath = parts.Length > 0 ? parts[0] : "";
        string alias = "";
        bool activate = true;

        for (int i = 1; i < parts.Length; i++)
        {
            string p = parts[i];
            if (string.IsNullOrWhiteSpace(p)) continue;

            if (p.Equals("Background", StringComparison.OrdinalIgnoreCase))
            {
                activate = false;
            }
            else if (p.StartsWith("Activate=", StringComparison.OrdinalIgnoreCase))
            {
                string val = p["Activate=".Length..].Trim();
                activate = ParseLogicOn(val);
            }
            else if (p.StartsWith("Name=", StringComparison.OrdinalIgnoreCase))
            {
                alias = p["Name=".Length..].Trim();
            }
            else if (p.StartsWith("Alias=", StringComparison.OrdinalIgnoreCase))
            {
                alias = p["Alias=".Length..].Trim();
            }
            else if (string.IsNullOrWhiteSpace(alias))
            {
                alias = p;
            }
        }

        if (string.IsNullOrWhiteSpace(alias))
        {
            alias = DerivePanelNameFromPath(csviPath);
        }

        return (csviPath, alias, activate);
    }

    public static string DerivePanelNameFromPath(string? csviPath)
    {
        if (string.IsNullOrWhiteSpace(csviPath)) return DefaultPanelName;
        string name = System.IO.Path.GetFileNameWithoutExtension(csviPath.Trim());
        return string.IsNullOrWhiteSpace(name) ? DefaultPanelName : name;
    }

    public static (string PanelName, string ControlName) SplitScopedTarget(string? rawTarget, string defaultPanel)
    {
        string fallback = string.IsNullOrWhiteSpace(defaultPanel) ? DefaultPanelName : defaultPanel.Trim();
        if (string.IsNullOrWhiteSpace(rawTarget)) return (fallback, "");
        string s = rawTarget.Trim();
        int colon = s.IndexOf(':');
        if (colon > 0 && colon < s.Length - 1)
        {
            return (s[..colon].Trim(), s[(colon + 1)..].Trim());
        }
        return (fallback, s);
    }

    public static string FormatScopedName(string panelName, string controlName)
        => string.IsNullOrWhiteSpace(panelName) ? controlName : $"{panelName.Trim()}:{controlName.Trim()}";

    public void EnsurePanelSlot(string panelName, string filePath = "", bool activate = true)
    {
        if (string.IsNullOrWhiteSpace(panelName)) return;
        lock (_lock)
        {
            var slot = GetOrCreateSlotLocked(panelName.Trim());
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                slot.FilePath = filePath.Trim();
            }
            if (activate || _panelOrder.Count == 1)
            {
                _activePanelName = slot.PanelName;
            }
        }
        PanelsChanged?.Invoke();
    }

    public IReadOnlyList<UserPanelSlotInfo> GetLoadedPanels()
    {
        lock (_lock)
        {
            return _panelOrder
                .Select(name => _panels.TryGetValue(name, out var s) ? s : null)
                .Where(s => s != null)
                .Select(s => new UserPanelSlotInfo(
                    s!.PanelName,
                    s.FilePath,
                    string.Equals(s.PanelName, _activePanelName, StringComparison.OrdinalIgnoreCase),
                    s.Controls.Count))
                .ToList();
        }
    }

    public IReadOnlyList<string> GetLoadedPanelNames()
    {
        lock (_lock) return _panelOrder.ToList();
    }

    public bool SwitchPanel(string panelName)
    {
        if (string.IsNullOrWhiteSpace(panelName)) return false;
        string resolvedName;
        lock (_lock)
        {
            var slot = ResolveSlotLocked(panelName.Trim());
            if (slot == null) return false;
            _activePanelName = slot.PanelName;
            resolvedName = slot.PanelName;
        }
        CommandRequested?.Invoke(new UserPanelCommandRequest(UserPanelCommandType.SwitchPanel, resolvedName, resolvedName, true, true));
        PanelsChanged?.Invoke();
        ControlsChanged?.Invoke();
        return true;
    }

    public bool ClosePanel(string panelName)
    {
        if (string.IsNullOrWhiteSpace(panelName)) return false;
        string resolvedName;
        lock (_lock)
        {
            var slot = ResolveSlotLocked(panelName.Trim());
            if (slot == null) return false;
            resolvedName = slot.PanelName;
            _panels.Remove(resolvedName);
            _panelOrder.RemoveAll(n => string.Equals(n, resolvedName, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(_activePanelName, resolvedName, StringComparison.OrdinalIgnoreCase))
            {
                _activePanelName = _panelOrder.Count > 0 ? _panelOrder[^1] : DefaultPanelName;
            }
        }
        CommandRequested?.Invoke(new UserPanelCommandRequest(UserPanelCommandType.ClosePanel, resolvedName, "", false, false));
        PanelsChanged?.Invoke();
        ControlsChanged?.Invoke();
        return true;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _panels.Clear();
            _panelOrder.Clear();
            _activePanelName = DefaultPanelName;
        }
        PanelsChanged?.Invoke();
        ControlsChanged?.Invoke();
    }

    public void RegisterControls(IEnumerable<UserPanelControlInfo> controls, bool replaceAll = true)
        => RegisterPanelControls(ActivePanelName, controls, "", activate: false, replacePanel: replaceAll);

    public void RegisterPanelControls(
        string panelName,
        IEnumerable<UserPanelControlInfo> controls,
        string filePath = "",
        bool activate = true,
        bool replacePanel = true)
    {
        string targetPanel = string.IsNullOrWhiteSpace(panelName) ? ActivePanelName : panelName.Trim();
        lock (_lock)
        {
            var slot = GetOrCreateSlotLocked(targetPanel);
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                slot.FilePath = filePath.Trim();
            }

            Dictionary<string, UserPanelControlInfo>? pendingUnmounted = null;
            if (replacePanel)
            {
                pendingUnmounted = slot.Controls
                    .Where(kv => !kv.Value.IsMountedOnUi)
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                slot.Controls.Clear();
            }

            if (controls != null)
            {
                foreach (var item in controls)
                {
                    if (string.IsNullOrWhiteSpace(item.Name)) continue;
                    string key = item.Name.Trim();
                    var entry = new UserPanelControlInfo
                    {
                        PanelName = slot.PanelName,
                        Name = key,
                        Kind = item.Kind,
                        Role = item.Role,
                        Value = item.Value ?? "",
                        Text = item.Text ?? "",
                        IsBlinking = item.IsBlinking,
                        IsEnabled = item.IsEnabled,
                        IsVisible = item.IsVisible,
                        BindingTarget = item.BindingTarget ?? "",
                        Options = item.Options ?? "",
                        IsMountedOnUi = item.IsMountedOnUi
                    };

                    if (pendingUnmounted != null && pendingUnmounted.TryGetValue(key, out var preCmd))
                    {
                        if (!string.IsNullOrEmpty(preCmd.Value)) entry.Value = preCmd.Value;
                        if (!string.IsNullOrEmpty(preCmd.Text)) entry.Text = preCmd.Text;
                        entry.IsBlinking = preCmd.IsBlinking;
                        entry.IsEnabled = preCmd.IsEnabled;
                        entry.IsVisible = preCmd.IsVisible;
                    }

                    slot.Controls[key] = entry;
                }
            }

            if (activate || _panelOrder.Count == 1)
            {
                _activePanelName = slot.PanelName;
            }
        }
        PanelsChanged?.Invoke();
        ControlsChanged?.Invoke();
    }

    public void UpsertControl(UserPanelControlInfo item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Name)) return;
        lock (_lock)
        {
            var (panelName, ctrlName) = SplitScopedTarget(
                string.IsNullOrWhiteSpace(item.PanelName) ? item.Name : $"{item.PanelName}:{item.Name}",
                _activePanelName);
            var slot = GetOrCreateSlotLocked(panelName);
            slot.Controls[ctrlName] = new UserPanelControlInfo
            {
                PanelName = slot.PanelName,
                Name = ctrlName,
                Kind = item.Kind,
                Role = item.Role,
                Value = item.Value ?? "",
                Text = item.Text ?? "",
                IsBlinking = item.IsBlinking,
                IsEnabled = item.IsEnabled,
                IsVisible = item.IsVisible,
                BindingTarget = item.BindingTarget ?? "",
                Options = item.Options ?? "",
                IsMountedOnUi = item.IsMountedOnUi
            };
        }
        ControlsChanged?.Invoke();
    }

    public IReadOnlyList<UserPanelControlInfo> GetAllControls() => QueryControls(null, null);

    public IReadOnlyList<UserPanelControlInfo> QueryControls(UserPanelElementRole? role = null, UserPanelControlKind? kind = null)
        => QueryControlsInPanel(null, role, kind);

    public IReadOnlyList<UserPanelControlInfo> QueryControlsInPanel(string? panelName, UserPanelElementRole? role = null, UserPanelControlKind? kind = null)
    {
        lock (_lock)
        {
            IEnumerable<UserPanelControlInfo> source;
            if (string.Equals(panelName?.Trim(), "*", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(panelName?.Trim(), "ALL", StringComparison.OrdinalIgnoreCase))
            {
                source = _panelOrder
                    .Select(n => _panels.TryGetValue(n, out var s) ? s : null)
                    .Where(s => s != null)
                    .SelectMany(s => s!.Controls.Values);
            }
            else
            {
                var slot = string.IsNullOrWhiteSpace(panelName)
                    ? (_panels.TryGetValue(_activePanelName, out var active) ? active : null)
                    : ResolveSlotLocked(panelName.Trim());
                source = slot != null ? slot.Controls.Values : Enumerable.Empty<UserPanelControlInfo>();
            }

            return source
                .Where(c => (!role.HasValue || c.Role == role.Value) && (!kind.HasValue || c.Kind == kind.Value))
                .Select(CloneInfo)
                .OrderBy(c => c.PanelName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Role)
                .ThenBy(c => c.Kind)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyList<string> QueryControlNames(UserPanelElementRole? role = null, UserPanelControlKind? kind = null)
        => QueryControls(role, kind).Select(c => c.Name).ToList();

    public IReadOnlyList<string> QueryControlNamesInPanel(string? panelName, UserPanelElementRole? role = null, UserPanelControlKind? kind = null)
        => QueryControlsInPanel(panelName, role, kind).Select(c => c.Name).ToList();

    public bool HasControl(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_lock)
        {
            var (panel, ctrl) = ResolvePanelAndControlLocked(name);
            return _panels.TryGetValue(panel, out var slot) && slot.Controls.ContainsKey(ctrl);
        }
    }

    public bool TryGetControl(string name, out UserPanelControlInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_lock)
        {
            var (panel, ctrl) = ResolvePanelAndControlLocked(name);
            if (_panels.TryGetValue(panel, out var slot) && slot.Controls.TryGetValue(ctrl, out var found))
            {
                info = CloneInfo(found);
                return true;
            }
        }
        return false;
    }

    public string ReadControlValue(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        string panel;
        string ctrl;
        bool isActivePanel;
        lock (_lock)
        {
            (panel, ctrl) = ResolvePanelAndControlLocked(name);
            isActivePanel = string.Equals(panel, _activePanelName, StringComparison.OrdinalIgnoreCase);
        }

        string liveQueryKey = isActivePanel ? ctrl : FormatScopedName(panel, ctrl);
        string? live = LiveReadHandler?.Invoke(liveQueryKey);
        if (live != null)
        {
            lock (_lock)
            {
                if (_panels.TryGetValue(panel, out var slot) && slot.Controls.TryGetValue(ctrl, out var existing))
                {
                    existing.Value = live;
                }
            }
            return live;
        }

        lock (_lock)
        {
            return (_panels.TryGetValue(panel, out var slot) && slot.Controls.TryGetValue(ctrl, out var existing))
                ? existing.Value
                : "";
        }
    }

    public void NotifyUiValueChanged(string name, string value, string? text = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_lock)
        {
            var (panel, ctrl) = ResolvePanelAndControlLocked(name);
            if (_panels.TryGetValue(panel, out var slot) && slot.Controls.TryGetValue(ctrl, out var existing))
            {
                existing.Value = value ?? "";
                if (text != null) existing.Text = text;
            }
        }
        ControlsChanged?.Invoke();
    }

    public void NotifyUiValueChangedInPanel(string panelName, string controlName, string value, string? text = null)
        => NotifyUiValueChanged(FormatScopedName(panelName, controlName), value, text);

    public void WriteButtonValue(string name, bool on, bool triggerEvent)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string val = on ? "ON" : "OFF";
        var (panel, ctrl) = EnsureControlEntry(name, UserPanelControlKind.Button, UserPanelElementRole.Control, c => c.Value = val);
        CommandRequested?.Invoke(new UserPanelCommandRequest(UserPanelCommandType.ButtonValue, ctrl, val, on, triggerEvent) { PanelName = panel });
        ControlsChanged?.Invoke();
    }

    public void SetButtonText(string name, string text)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string val = text ?? "";
        var (panel, ctrl) = EnsureControlEntry(name, UserPanelControlKind.Button, UserPanelElementRole.Control, c => c.Text = val);
        CommandRequested?.Invoke(new UserPanelCommandRequest(UserPanelCommandType.ButtonText, ctrl, val, false, false) { PanelName = panel });
        ControlsChanged?.Invoke();
    }

    public void SetBlinking(string name, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var (panel, ctrl) = EnsureControlEntry(name, UserPanelControlKind.Unknown, UserPanelElementRole.Control, c => c.IsBlinking = enabled);
        CommandRequested?.Invoke(new UserPanelCommandRequest(UserPanelCommandType.SetBlinking, ctrl, enabled ? "1" : "0", enabled, false) { PanelName = panel });
        ControlsChanged?.Invoke();
    }

    public void SetEnabled(string name, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var (panel, ctrl) = EnsureControlEntry(name, UserPanelControlKind.Unknown, UserPanelElementRole.Control, c => c.IsEnabled = enabled);
        CommandRequested?.Invoke(new UserPanelCommandRequest(UserPanelCommandType.SetEnabled, ctrl, enabled ? "1" : "0", enabled, false) { PanelName = panel });
        ControlsChanged?.Invoke();
    }

    public void SetVisible(string name, bool visible)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var (panel, ctrl) = EnsureControlEntry(name, UserPanelControlKind.Unknown, UserPanelElementRole.Control, c => c.IsVisible = visible);
        CommandRequested?.Invoke(new UserPanelCommandRequest(UserPanelCommandType.SetVisible, ctrl, visible ? "1" : "0", visible, false) { PanelName = panel });
        ControlsChanged?.Invoke();
    }

    public void WriteTextValue(string name, string text, bool triggerEvent)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string val = text ?? "";
        var (panel, ctrl) = EnsureControlEntry(name, UserPanelControlKind.TextBox, UserPanelElementRole.Control, c =>
        {
            c.Value = val;
            c.Text = val;
        });
        CommandRequested?.Invoke(new UserPanelCommandRequest(UserPanelCommandType.TextValue, ctrl, val, false, triggerEvent) { PanelName = panel });
        ControlsChanged?.Invoke();
    }

    public void SelectTab(string name, string pageIdxOrName, bool triggerEvent)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string val = pageIdxOrName?.Trim() ?? "0";
        var (panel, ctrl) = EnsureControlEntry(name, UserPanelControlKind.TabControl, UserPanelElementRole.Control, c => c.Value = val);
        CommandRequested?.Invoke(new UserPanelCommandRequest(UserPanelCommandType.SelectTab, ctrl, val, false, triggerEvent) { PanelName = panel });
        ControlsChanged?.Invoke();
    }

    public void SelectCombo(string name, string valueOrIndex, bool triggerEvent)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string val = valueOrIndex?.Trim() ?? "";
        var (panel, ctrl) = EnsureControlEntry(name, UserPanelControlKind.ComboBox, UserPanelElementRole.Control, c => c.Value = val);
        CommandRequested?.Invoke(new UserPanelCommandRequest(UserPanelCommandType.SelectCombo, ctrl, val, false, triggerEvent) { PanelName = panel });
        ControlsChanged?.Invoke();
    }

    public void WriteGenericControl(string name, string value, bool triggerEvent = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        UserPanelControlKind kind = UserPanelControlKind.Unknown;
        lock (_lock)
        {
            var (panel, ctrl) = ResolvePanelAndControlLocked(name);
            if (_panels.TryGetValue(panel, out var slot) && slot.Controls.TryGetValue(ctrl, out var existing))
            {
                kind = existing.Kind;
            }
        }

        switch (kind)
        {
            case UserPanelControlKind.Button:
            case UserPanelControlKind.ToggleButton:
            case UserPanelControlKind.CheckBox:
            case UserPanelControlKind.Led:
                bool on = ParseLogicOn(value);
                WriteButtonValue(name, on, triggerEvent);
                break;
            case UserPanelControlKind.TabControl:
                SelectTab(name, value, triggerEvent);
                break;
            case UserPanelControlKind.ComboBox:
                SelectCombo(name, value, triggerEvent);
                break;
            default:
                WriteTextValue(name, value, triggerEvent);
                break;
        }
    }

    public void RequestParamRefresh() => RequestParamRefresh("");

    public void RequestParamRefresh(string? panelName)
    {
        string targetPanel = string.IsNullOrWhiteSpace(panelName) ? ActivePanelName : panelName.Trim();
        CommandRequested?.Invoke(new UserPanelCommandRequest(UserPanelCommandType.ParamRefresh, targetPanel, "", false, false));
    }

    public static bool ParseLogicOn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim();
        return s.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("HIGH", StringComparison.OrdinalIgnoreCase);
    }

    private PanelSlot GetOrCreateSlotLocked(string panelName)
    {
        var existing = ResolveSlotLocked(panelName);
        if (existing != null) return existing;

        string key = string.IsNullOrWhiteSpace(panelName) ? DefaultPanelName : panelName.Trim();
        var created = new PanelSlot { PanelName = key };
        _panels[key] = created;
        _panelOrder.Add(key);
        return created;
    }

    private PanelSlot? ResolveSlotLocked(string panelName)
    {
        if (string.IsNullOrWhiteSpace(panelName)) return null;
        string key = panelName.Trim();
        if (_panels.TryGetValue(key, out var exact)) return exact;

        string noExt = System.IO.Path.GetFileNameWithoutExtension(key);
        foreach (var kv in _panels)
        {
            if (string.Equals(kv.Key, noExt, StringComparison.OrdinalIgnoreCase))
                return kv.Value;

            if (!string.IsNullOrWhiteSpace(kv.Value.FilePath))
            {
                string fName = System.IO.Path.GetFileName(kv.Value.FilePath);
                string fStem = System.IO.Path.GetFileNameWithoutExtension(kv.Value.FilePath);
                if (string.Equals(fName, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fStem, noExt, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value;
                }
            }
        }
        return null;
    }

    private (string PanelName, string ControlName) ResolvePanelAndControlLocked(string rawTarget)
    {
        var (panel, ctrl) = SplitScopedTarget(rawTarget, _activePanelName);
        var slot = ResolveSlotLocked(panel);
        return (slot?.PanelName ?? panel, ctrl);
    }

    private (string PanelName, string ControlName) EnsureControlEntry(string rawName, UserPanelControlKind defaultKind, UserPanelElementRole defaultRole, Action<UserPanelControlInfo> update)
    {
        lock (_lock)
        {
            var (panel, ctrl) = ResolvePanelAndControlLocked(rawName);
            var slot = GetOrCreateSlotLocked(panel);
            if (!slot.Controls.TryGetValue(ctrl, out var info))
            {
                info = new UserPanelControlInfo
                {
                    PanelName = slot.PanelName,
                    Name = ctrl,
                    Kind = defaultKind,
                    Role = defaultRole,
                    IsMountedOnUi = false
                };
                slot.Controls[ctrl] = info;
            }
            update(info);
            return (slot.PanelName, ctrl);
        }
    }

    private static UserPanelControlInfo CloneInfo(UserPanelControlInfo c) => new()
    {
        PanelName = c.PanelName,
        Name = c.Name,
        Kind = c.Kind,
        Role = c.Role,
        Value = c.Value,
        Text = c.Text,
        IsBlinking = c.IsBlinking,
        IsEnabled = c.IsEnabled,
        IsVisible = c.IsVisible,
        BindingTarget = c.BindingTarget,
        Options = c.Options,
        IsMountedOnUi = c.IsMountedOnUi
    };
}
