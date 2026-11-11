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
    private bool _isTrailingPlaceholder;

    /// <summary>
    /// 標記此列是否為編輯器最底部的「虛擬尾端空白列」，供捲動到底時仍可直接點擊插入/貼上/搬移至程式最尾端
    /// </summary>
    public bool IsTrailingPlaceholder
    {
        get => _isTrailingPlaceholder;
        set => SetField(ref _isTrailingPlaceholder, value);
    }

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
    
    public string CellBackground => _isRangeSelected ? "#E0F2FE" : "Transparent";
    public string CellBorderBrush => _isRangeSelected ? "#BAE6FD" : "Transparent";
    public string CellBorderThickness => _isRangeSelected ? "1" : "0";

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
