using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using ARCOS.Core.Drivers;

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

public class UserPanelViewModel : INotifyPropertyChanged
{
    private BasicEngines? _engines;
    private readonly DispatcherTimer _pollTimer;

    // KPI Counters
    private int _totalParts = 1248;
    public int TotalParts
    {
        get => _totalParts;
        set { _totalParts = value; OnPropertyChanged(); OnPropertyChanged(nameof(YieldRate)); }
    }

    private int _okParts = 1236;
    public int OkParts
    {
        get => _okParts;
        set { _okParts = value; OnPropertyChanged(); OnPropertyChanged(nameof(YieldRate)); }
    }

    private int _ngParts = 12;
    public int NgParts
    {
        get => _ngParts;
        set { _ngParts = value; OnPropertyChanged(); OnPropertyChanged(nameof(YieldRate)); }
    }

    public string YieldRate => TotalParts > 0 ? $"{(OkParts * 100.0 / TotalParts):F1}%" : "100.0%";

    private string _cycleTime = "1.82 s";
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

    private string _currentStep = "Step 0: Standby / Waiting Part Feed";
    public string CurrentStep
    {
        get => _currentStep;
        set { _currentStep = value; OnPropertyChanged(); }
    }

    private string _lastActionLog = "UserPanel initialized. Engine connected.";
    public string LastActionLog
    {
        get => _lastActionLog;
        set { _lastActionLog = value; OnPropertyChanged(); }
    }

    public ObservableCollection<StationIoItem> StationIos { get; } = new();

    public UserPanelViewModel()
    {
        InitializeDefaultIoList();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _pollTimer.Tick += (s, e) => RefreshIoStates();
    }

    public void Init(BasicEngines engines)
    {
        _engines = engines;
        RefreshFromEngines(engines);
        if (!_pollTimer.IsEnabled)
        {
            _pollTimer.Start();
        }
    }

    public void RefreshFromEngines(BasicEngines engines)
    {
        _engines = engines;
        if (_engines == null) return;

        // 從 VariableEngine 讀取產線計數變數 (使用安全讀取防呆)
        string totalStr = GetVariableSafe("$TotalCount");
        if (int.TryParse(totalStr, out int t) && t > 0) TotalParts = t;

        string okStr = GetVariableSafe("$OkCount");
        if (int.TryParse(okStr, out int ok) && ok >= 0) OkParts = ok;

        string ngStr = GetVariableSafe("$NgCount");
        if (int.TryParse(ngStr, out int ng) && ng >= 0) NgParts = ng;

        string ctStr = GetVariableSafe("$CycleTime");
        if (!string.IsNullOrWhiteSpace(ctStr)) CycleTime = ctStr;

        RefreshIoStates();
    }

    private string GetVariableSafe(string key)
    {
        if (_engines == null) return string.Empty;
        try
        {
            return _engines.Variable.Get(key);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return string.Empty;
        }
    }

    public void RefreshIoStates()
    {
        if (_engines == null) return;

        foreach (var item in StationIos)
        {
            item.State = _engines.Io.GetPort(item.Port);
        }
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

    public void ToggleVacuum()
    {
        bool currentState = false;
        if (_engines != null)
        {
            currentState = _engines.Io.GetPort("P0.5");
            _engines.Io.SetPort("P0.5", !currentState);
        }
        LastActionLog = $"[{DateTime.Now:HH:mm:ss}] Vacuum Fixture (P0.5) toggled to {!currentState}";
        RefreshIoStates();
    }

    public void ResetStats()
    {
        TotalParts = 0;
        OkParts = 0;
        NgParts = 0;
        CurrentStep = "Step 0: Stats Reset. Ready for operation.";
        LastActionLog = $"[{DateTime.Now:HH:mm:ss}] Production statistics reset to 0.";

        if (_engines != null)
        {
            _engines.Variable.Set("$TotalCount", "0");
            _engines.Variable.Set("$OkCount", "0");
            _engines.Variable.Set("$NgCount", "0");
            _engines.Io.SetPort("P0.0", false);
            _engines.Io.SetPort("P0.7", false);
            _engines.Io.SetPort("P1.2", false);
            _engines.Io.SetPort("Start?", false);
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
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
