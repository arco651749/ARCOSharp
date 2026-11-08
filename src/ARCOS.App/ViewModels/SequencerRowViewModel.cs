using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ARCOS.App.ViewModels;

public sealed class SequencerRowViewModel : INotifyPropertyChanged
{
    private int _lineNumber;
    private bool _isCurrentPC;
    private bool _isRangeSelected;
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
        set
        {
            if (SetField(ref _isCurrentPC, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRangeSelectedAndNotPC)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDefaultState)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CellBackground)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CellBorderBrush)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CellBorderThickness)));
            }
        }
    }

    public bool IsRangeSelected
    {
        get => _isRangeSelected;
        set
        {
            if (SetField(ref _isRangeSelected, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRangeSelectedAndNotPC)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDefaultState)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CellBackground)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CellBorderBrush)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CellBorderThickness)));
            }
        }
    }

    public bool IsRangeSelectedAndNotPC => _isRangeSelected && !_isCurrentPC;
    public bool IsDefaultState => !_isCurrentPC && !_isRangeSelected;
    
    public string CellBackground
    {
        get
        {
            if (_isCurrentPC) return "#FFE4E6";
            if (_isRangeSelected) return "#E0F2FE";
            return "Transparent";
        }
    }

    public string CellBorderBrush
    {
        get
        {
            if (_isCurrentPC) return "#EF4444";
            if (_isRangeSelected) return "#BAE6FD";
            return "Transparent";
        }
    }

    public string CellBorderThickness => _isCurrentPC ? "1.5" : (_isRangeSelected ? "1" : "0");

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

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
        return false;
    }
}
