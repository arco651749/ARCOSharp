using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ARCOS.App.ViewModels;

public sealed class SequencerRowViewModel : INotifyPropertyChanged
{
    private int _lineNumber;
    private bool _isCurrentPC;
    private string _command = "";
    private string _parameter = "";
    private string _statePreview = "";
    private bool _hasStateData;
    private string _stateTooltip = "";

    public int LineNumber
    {
        get => _lineNumber;
        set => SetField(ref _lineNumber, value);
    }

    public bool IsCurrentPC
    {
        get => _isCurrentPC;
        set => SetField(ref _isCurrentPC, value);
    }

    public string Command
    {
        get => _command;
        set => SetField(ref _command, value);
    }

    public string Parameter
    {
        get => _parameter;
        set => SetField(ref _parameter, value);
    }

    public string StatePreview
    {
        get => _statePreview;
        set => SetField(ref _statePreview, value);
    }

    public bool HasStateData
    {
        get => _hasStateData;
        set => SetField(ref _hasStateData, value);
    }

    public string StateTooltip
    {
        get => _stateTooltip;
        set => SetField(ref _stateTooltip, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
