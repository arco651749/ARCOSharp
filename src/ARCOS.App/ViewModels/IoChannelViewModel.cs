using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace ARCOS.App.ViewModels;

/// <summary>
/// 單一 IO 點位 ViewModel，支援虛擬化綁定、即時狀態反射與名稱自訂
/// </summary>
public class IoChannelViewModel : INotifyPropertyChanged
{
    private static readonly IBrush BrushOn = new SolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush BrushOff = new SolidColorBrush(Color.Parse("#334155"));

    private string _port = string.Empty;
    private string _name = string.Empty;
    private bool _state;
    private bool _isVisible = true;

    /// <summary>
    /// IO 位址識別碼，例如 P0.0, P1.5, P12.7
    /// </summary>
    public string Port
    {
        get => _port;
        set
        {
            if (_port != value)
            {
                _port = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AutomationId));
            }
        }
    }

    /// <summary>
    /// 使用者自訂別名或註解，例如 "氣缸伸出", "進料光電感應器"
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AutomationId));
            }
        }
    }

    /// <summary>
    /// 給 UIA 自動化與測試腳本使用的唯一識別名稱，包含物理埠號與使用者自訂名稱
    /// 例如 "P1.1" 或 "P1.1_Disp Sensor"
    /// </summary>
    public string AutomationId => string.IsNullOrWhiteSpace(Name) ? Port : $"{Port}_{Name}";


    /// <summary>
    /// IO 點位即時邏輯狀態 (true: 導通/高電位; false: 關閉/低電位)
    /// </summary>
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
            }
        }
    }

    /// <summary>
    /// 依狀態返回對應的 LED 筆刷 (綠光 #10B981 / 灰暗 #94A3B8)
    /// </summary>
    public IBrush StateBrush => _state ? BrushOn : BrushOff;

    /// <summary>
    /// 是否符合當前快速搜尋/過濾條件
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}
