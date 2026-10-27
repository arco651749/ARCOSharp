using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using MVCON.Core.Drivers;

namespace MVCON.Module.IO.Guidance.Views;

public class IoBitItem : INotifyPropertyChanged
{
    public string Port { get; set; } = "";
    public string Alias { get; set; } = "";

    private bool _state;
    public bool State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                OnPropertyChanged(nameof(State));
                OnPropertyChanged(nameof(StateBrush));
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public IBrush StateBrush => State ? new SolidColorBrush(Color.Parse("#10B981")) : new SolidColorBrush(Color.Parse("#475569"));
    public string StateText => State ? "ON" : "OFF";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

public class IoFrontPanelViewModel : INotifyPropertyChanged
{
    public ObservableCollection<IoBitItem> Bits { get; } = new();

    private string _lastCommand = "IO@SetPort P0.0";
    public string LastCommand
    {
        get => _lastCommand;
        set { _lastCommand = value; OnPropertyChanged(nameof(LastCommand)); }
    }

    private string _lastExecTime = DateTime.Now.ToString("HH:mm:ss");
    public string LastExecTime
    {
        get => _lastExecTime;
        set { _lastExecTime = value; OnPropertyChanged(nameof(LastExecTime)); }
    }

    private int _activeCount = 0;
    public int ActiveCount
    {
        get => _activeCount;
        set { _activeCount = value; OnPropertyChanged(nameof(ActiveCount)); }
    }

    private string _driverName = "MockIO.csvi (Virtual IO Driver)";
    public string DriverName
    {
        get => _driverName;
        set { _driverName = value; OnPropertyChanged(nameof(DriverName)); }
    }

    public BasicEngines? AttachedEngines { get; set; }

    public IoFrontPanelViewModel()
    {
        // 初始化 Bank 0 (P0.0 ~ P0.7) 與 Bank 1 (P1.0 ~ P1.7)
        for (int p = 0; p < 2; p++)
        {
            for (int b = 0; b < 8; b++)
            {
                string port = $"P{p}.{b}";
                string alias = port switch
                {
                    "P0.0" => "Safety Door",
                    "P0.1" => "Air Pressure",
                    "P0.2" => "Feed Sensor",
                    "P0.3" => "Cyl Align",
                    "P0.4" => "Cyl Back",
                    "P0.5" => "Vacuum 1",
                    "P0.6" => "Vacuum 2",
                    "P0.7" => "Cam Trigger",
                    "P1.0" => "Feeder Motor",
                    "P1.1" => "Discharge",
                    "P1.2" => "Reject Cyl",
                    "P1.3" => "Lamp Green",
                    "P1.4" => "Lamp Yellow",
                    "P1.5" => "Lamp Red",
                    "P1.6" => "Buzzer",
                    "P1.7" => "Ionizer",
                    _ => port
                };

                Bits.Add(new IoBitItem
                {
                    Port = port,
                    Alias = alias,
                    State = false
                });
            }
        }
    }

    public void NotifyExecution(string action, string parameter, BasicEngines engines)
    {
        AttachedEngines = engines;
        LastCommand = $"IO@{action} {parameter}";
        LastExecTime = DateTime.Now.ToString("HH:mm:ss.fff");
        RefreshFromEngines(engines);
    }

    public void RefreshFromEngines(BasicEngines engines)
    {
        AttachedEngines = engines;
        int on = 0;
        foreach (var bit in Bits)
        {
            bit.State = engines.Io.GetPort(bit.Port);
            if (bit.State) on++;
        }
        ActiveCount = on;
    }

    public void ToggleBit(string port)
    {
        if (AttachedEngines != null)
        {
            bool cur = AttachedEngines.Io.GetPort(port);
            AttachedEngines.Io.SetPort(port, !cur);
            RefreshFromEngines(AttachedEngines);
        }
        else
        {
            var bit = Bits.FirstOrDefault(x => x.Port == port);
            if (bit != null)
            {
                bit.State = !bit.State;
                ActiveCount = Bits.Count(x => x.State);
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
