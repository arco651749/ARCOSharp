using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;

namespace ARCOS.Panels.UserPanel;

public class StationIoItem : INotifyPropertyChanged
{
    public string Port { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsOutput { get; set; }

    private bool _state;
    public bool State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StateBrush));
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public IBrush StateBrush => State ? new SolidColorBrush(Color.Parse("#10B981")) : new SolidColorBrush(Color.Parse("#64748B"));
    public string StateText => State ? "ON" : "OFF";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

public class PanelElementRow
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
    public string BindingTarget { get; set; } = "";
    public IBrush RoleBrush => Role == "Control"
        ? new SolidColorBrush(Color.Parse("#0284C7"))
        : new SolidColorBrush(Color.Parse("#7C3AED"));
}

public class UserPanelViewModel : INotifyPropertyChanged
{
    private BasicEngines? _engines;
    private readonly DispatcherTimer _pollTimer;
    private string _roleFilter = "ALL";

    private int _totalParts = 0;
    public int TotalParts
    {
        get => _totalParts;
        set { _totalParts = value; OnPropertyChanged(); OnPropertyChanged(nameof(YieldRate)); }
    }

    private int _okParts = 0;
    public int OkParts
    {
        get => _okParts;
        set { _okParts = value; OnPropertyChanged(); OnPropertyChanged(nameof(YieldRate)); }
    }

    private int _ngParts = 0;
    public int NgParts
    {
        get => _ngParts;
        set { _ngParts = value; OnPropertyChanged(); OnPropertyChanged(nameof(YieldRate)); }
    }

    public string YieldRate => TotalParts > 0 ? $"{(OkParts * 100.0 / TotalParts):F1}%" : "100.0%";

    private string _cycleTime = "0.00 s";
    public string CycleTime
    {
        get => _cycleTime;
        set { _cycleTime = value; OnPropertyChanged(); }
    }

    private string _stationState = "READY (STANDBY)";
    public string StationState
    {
        get => _stationState;
        set { _stationState = value; OnPropertyChanged(); }
    }

    private IBrush _stationStateBrush = new SolidColorBrush(Color.Parse("#10B981"));
    public IBrush StationStateBrush
    {
        get => _stationStateBrush;
        set { _stationStateBrush = value; OnPropertyChanged(); }
    }

    private string _currentStep = "Step 0: Standby / Ready for Sequencer";
    public string CurrentStep
    {
        get => _currentStep;
        set { _currentStep = value; OnPropertyChanged(); }
    }

    private string _lastActionLog = "UserPanel initialized. IO / Variable / Capture / DAQ / User connected.";
    public string LastActionLog
    {
        get => _lastActionLog;
        set { _lastActionLog = value; OnPropertyChanged(); }
    }

    private string _recipe = "Master_V1";
    public string Recipe
    {
        get => _recipe;
        set { _recipe = value; OnPropertyChanged(); }
    }

    private string _lotNo = "LOT-2026-001";
    public string LotNo
    {
        get => _lotNo;
        set { _lotNo = value; OnPropertyChanged(); }
    }

    private string _runMode = "AUTO";
    public string RunMode
    {
        get => _runMode;
        set { _runMode = value; OnPropertyChanged(); }
    }

    private string _inspectionResult = "STANDBY";
    public string InspectionResult
    {
        get => _inspectionResult;
        set { _inspectionResult = value; OnPropertyChanged(); }
    }

    private bool _vacuumOn;
    public bool VacuumOn
    {
        get => _vacuumOn;
        set
        {
            if (_vacuumOn != value)
            {
                _vacuumOn = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VacuumStatusText));
            }
        }
    }

    public string VacuumStatusText => VacuumOn ? "Vacuum P0.5 : ON" : "Vacuum P0.5 : OFF";

    public ObservableCollection<string> RunModes { get; } = new() { "AUTO", "DRY_RUN", "STEP", "CALIBRATION" };
    public ObservableCollection<StationIoItem> StationIos { get; } = new();
    public ObservableCollection<PanelElementRow> PanelElements { get; } = new();

    public UserPanelViewModel()
    {
        InitializeDefaultIoList();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _pollTimer.Tick += (_, _) => RefreshIoStates();
    }

    public void Init(BasicEngines engines)
    {
        if (_engines != null)
        {
            _engines.UserPanel.ControlsChanged -= OnUserPanelControlsChanged;
        }

        _engines = engines;
        if (_engines != null)
        {
            _engines.UserPanel.ControlsChanged += OnUserPanelControlsChanged;
        }

        RefreshFromEngines(engines);
        if (!_pollTimer.IsEnabled)
        {
            _pollTimer.Start();
        }
    }

    private void OnUserPanelControlsChanged()
    {
        Dispatcher.UIThread.Post(RefreshPanelElements);
    }

    public void RefreshFromEngines(BasicEngines engines)
    {
        _engines = engines;
        if (_engines == null) return;

        string totalStr = GetVariableSafe("$TotalCount");
        if (int.TryParse(totalStr, out int t) && t >= 0) TotalParts = t;

        string okStr = GetVariableSafe("$OkCount");
        if (int.TryParse(okStr, out int ok) && ok >= 0) OkParts = ok;

        string ngStr = GetVariableSafe("$NgCount");
        if (int.TryParse(ngStr, out int ng) && ng >= 0) NgParts = ng;

        string ctStr = GetVariableSafe("$CycleTime");
        if (!string.IsNullOrWhiteSpace(ctStr)) CycleTime = ctStr;

        string recipeStr = GetVariableSafe("$Recipe");
        if (!string.IsNullOrWhiteSpace(recipeStr)) Recipe = recipeStr;

        string lotStr = GetVariableSafe("$LotNo");
        if (!string.IsNullOrWhiteSpace(lotStr)) LotNo = lotStr;

        string modeStr = GetVariableSafe("$RunMode");
        if (!string.IsNullOrWhiteSpace(modeStr)) RunMode = modeStr;

        string resStr = GetVariableSafe("$InspectionResult");
        if (!string.IsNullOrWhiteSpace(resStr)) InspectionResult = resStr;

        RefreshIoStates();
        RefreshPanelElements();
    }

    private string GetVariableSafe(string key)
    {
        if (_engines == null) return string.Empty;
        try
        {
            return _engines.Variable.Get(key);
        }
        catch (KeyNotFoundException)
        {
            return string.Empty;
        }
    }

    public void ApplyVariablesToEngine()
    {
        if (_engines == null) return;
        _engines.Variable.Set("$Recipe", Recipe ?? "");
        _engines.Variable.Set("$LotNo", LotNo ?? "");
        _engines.Variable.Set("$RunMode", RunMode ?? "AUTO");
        LastActionLog = $"[{DateTime.Now:HH:mm:ss}] Variables synced -> $Recipe={Recipe}, $LotNo={LotNo}, $RunMode={RunMode}";
    }

    public void SetRoleFilter(string filter)
    {
        _roleFilter = string.IsNullOrWhiteSpace(filter) ? "ALL" : filter.Trim().ToUpperInvariant();
        RefreshPanelElements();
    }

    public void RefreshPanelElements()
    {
        if (_engines == null) return;
        UserPanelElementRole? role = _roleFilter switch
        {
            "CONTROL" => UserPanelElementRole.Control,
            "INDICATOR" => UserPanelElementRole.Indicator,
            _ => null
        };

        var items = _engines.UserPanel.QueryControls(role, null);
        PanelElements.Clear();
        foreach (var c in items)
        {
            PanelElements.Add(new PanelElementRow
            {
                Name = c.Name,
                Role = c.Role.ToString(),
                Kind = c.Kind.ToString(),
                Value = string.IsNullOrEmpty(c.Value) ? c.Text : c.Value,
                BindingTarget = c.BindingTarget
            });
        }
    }

    public void RefreshIoStates()
    {
        if (_engines == null) return;

        foreach (var item in StationIos)
        {
            item.State = _engines.Io.GetPort(item.Port);
        }

        VacuumOn = _engines.Io.GetPort("P0.5");
    }

    public void SimulatePartFeed()
    {
        TotalParts++;
        CurrentStep = "Step 1: Part In Feeder Detected (P0.0=1)";
        LastActionLog = $"[{DateTime.Now:HH:mm:ss}] Part Feed triggered. Total Parts: {TotalParts}";

        if (_engines != null)
        {
            _engines.Io.SetPort("P0.0", true);
            _engines.Io.SetPort("Start?", true);
            _engines.Variable.Set("$TotalCount", TotalParts.ToString());
            _engines.Variable.Set("$PartFed", "1");
        }
        RefreshIoStates();
    }

    public void TriggerInspection()
    {
        OkParts++;
        InspectionResult = "PASS";
        CurrentStep = "Step 2: Vision Inspection Completed (PASS, P0.7 Triggered)";
        LastActionLog = $"[{DateTime.Now:HH:mm:ss}] Inspection Pass. OK: {OkParts}";

        if (_engines != null)
        {
            _engines.Io.SetPort("P0.7", true);
            _engines.Io.SetPort("RESULT", true);
            _engines.Variable.Set("$OkCount", OkParts.ToString());
            _engines.Variable.Set("$InspectionResult", "PASS");
        }
        RefreshIoStates();
    }

    public void RejectPart()
    {
        NgParts++;
        InspectionResult = "FAIL";
        CurrentStep = "Step 3: Defect Rejected via Cylinder (P1.2 Active)";
        LastActionLog = $"[{DateTime.Now:HH:mm:ss}] Defect Rejected. NG: {NgParts}";

        if (_engines != null)
        {
            _engines.Io.SetPort("P1.2", true);
            _engines.Io.SetPort("RESULT", false);
            _engines.Variable.Set("$NgCount", NgParts.ToString());
            _engines.Variable.Set("$InspectionResult", "FAIL");
        }
        RefreshIoStates();
    }

    public void SetVacuum(bool state)
    {
        VacuumOn = state;
        if (_engines != null)
        {
            _engines.Io.SetPort("P0.5", state);
        }
        LastActionLog = $"[{DateTime.Now:HH:mm:ss}] Vacuum Fixture (P0.5) set to {(state ? "ON" : "OFF")}";
        RefreshIoStates();
    }

    public void ToggleVacuum()
    {
        bool currentState = _engines?.Io.GetPort("P0.5") ?? VacuumOn;
        SetVacuum(!currentState);
    }

    public void ConfirmOperatorPrompt()
    {
        CurrentStep = "Operator Confirmed Prompt (UserPanel#Confirm? = ON)";
        LastActionLog = $"[{DateTime.Now:HH:mm:ss}] BtnConfirm clicked by operator / User@ButtonTrigger";
        if (_engines != null)
        {
            _engines.Io.SetPort("UserPanel#Confirm?", true);
        }
    }

    public void ResetStats()
    {
        TotalParts = 0;
        OkParts = 0;
        NgParts = 0;
        InspectionResult = "STANDBY";
        CurrentStep = "Step 0: Stats Reset. Ready for operation.";
        LastActionLog = $"[{DateTime.Now:HH:mm:ss}] Production statistics & IO reset to 0.";

        if (_engines != null)
        {
            _engines.Variable.Set("$TotalCount", "0");
            _engines.Variable.Set("$OkCount", "0");
            _engines.Variable.Set("$NgCount", "0");
            _engines.Variable.Set("$InspectionResult", "STANDBY");
            _engines.Io.SetPort("P0.0", false);
            _engines.Io.SetPort("P0.5", false);
            _engines.Io.SetPort("P0.7", false);
            _engines.Io.SetPort("P1.2", false);
            _engines.Io.SetPort("Start?", false);
            _engines.Io.SetPort("UserPanel#Confirm?", false);
        }
        RefreshIoStates();
    }

    public void ToggleIoBit(string port)
    {
        if (string.IsNullOrWhiteSpace(port)) return;

        bool current = false;
        if (_engines != null)
        {
            current = _engines.Io.GetPort(port);
            _engines.Io.SetPort(port, !current);
        }

        var match = StationIos.FirstOrDefault(i => i.Port.Equals(port, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            match.State = !current;
        }

        LastActionLog = $"[{DateTime.Now:HH:mm:ss}] IO {port} manual toggled to {!current}";
    }

    private void InitializeDefaultIoList()
    {
        StationIos.Clear();
        StationIos.Add(new StationIoItem { Port = "P0.0", Name = "Safety Door / Part In Feed", IsOutput = false });
        StationIos.Add(new StationIoItem { Port = "P0.1", Name = "Main Air Pressure Sensor", IsOutput = false });
        StationIos.Add(new StationIoItem { Port = "P0.2", Name = "Alignment Cylinder Forward", IsOutput = false });
        StationIos.Add(new StationIoItem { Port = "P0.3", Name = "Alignment Cylinder Back", IsOutput = false });
        StationIos.Add(new StationIoItem { Port = "P0.5", Name = "Fixture Vacuum Solenoid", IsOutput = true });
        StationIos.Add(new StationIoItem { Port = "P0.7", Name = "Camera Vision Trigger", IsOutput = true });
        StationIos.Add(new StationIoItem { Port = "P1.0", Name = "Conveyor Feeder Motor", IsOutput = true });
        StationIos.Add(new StationIoItem { Port = "P1.2", Name = "Defect Reject Actuator", IsOutput = true });
        StationIos.Add(new StationIoItem { Port = "P1.6", Name = "Buzzer Alarm / Tower Lamp", IsOutput = true });
        StationIos.Add(new StationIoItem { Port = "Start?", Name = "Virtual Start Flag", IsOutput = false });
        StationIos.Add(new StationIoItem { Port = "RESULT", Name = "Virtual Inspection Result", IsOutput = false });
        StationIos.Add(new StationIoItem { Port = "UserPanel#Confirm?", Name = "Operator Confirm Flag", IsOutput = false });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
