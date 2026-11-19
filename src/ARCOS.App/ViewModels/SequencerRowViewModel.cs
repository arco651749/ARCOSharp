using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using ARCOS.Core.Common;
using ARCOS.Core.Guidance;
using Avalonia.Media;

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
    private CommandDisplayLevel _displayLevel = CommandDisplayLevel.Audit;
    private bool _multiLineLayout;

    /// <summary>
    /// 標記此列是否為編輯器最底部的「虛擬尾端空白列」，供捲動到底時仍可直接點擊插入/貼上/搬移至程式最尾端
    /// </summary>
    public bool IsTrailingPlaceholder
    {
        get => _isTrailingPlaceholder;
        set
        {
            if (SetField(ref _isTrailingPlaceholder, value))
            {
                NotifyProjectionChanged();
            }
        }
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
        set
        {
            if (SetField(ref _command, value))
            {
                NotifyProjectionChanged();
            }
        }
    }

    public string Parameter
    {
        get => _parameter;
        set
        {
            if (SetField(ref _parameter, value))
            {
                NotifyProjectionChanged();
            }
        }
    }

    public CommandDisplayLevel DisplayLevel
    {
        get => _displayLevel;
        set
        {
            if (SetField(ref _displayLevel, value))
            {
                NotifyProjectionChanged();
            }
        }
    }

    public bool MultiLineLayout
    {
        get => _multiLineLayout;
        set
        {
            if (SetField(ref _multiLineLayout, value))
            {
                NotifyProjectionChanged();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ParameterTextWrapping)));
            }
        }
    }

    /// <summary>
    /// 供 DataGrid 顯示之三層語意投影字串（嚴格與底層存檔 Parameter 分離）
    /// </summary>
    public string DisplayParameter
    {
        get
        {
            if (_isTrailingPlaceholder || string.IsNullOrWhiteSpace(_command) || string.IsNullOrWhiteSpace(_parameter))
            {
                return _parameter;
            }

            var schema = StandardCommandSchemas.Get(_command);
            if (schema == null)
            {
                return _parameter;
            }

            return CommandDisplayProjector.ProjectParameter(_parameter, schema, _displayLevel, _multiLineLayout);
        }
    }

    public bool HasCriticalAlert => !string.IsNullOrEmpty(DisplayParameter) && DisplayParameter.Contains('⚠');

    public string ParameterForeground => HasCriticalAlert ? "#B45309" : "#222222";

    public TextWrapping ParameterTextWrapping => _multiLineLayout ? TextWrapping.Wrap : TextWrapping.NoWrap;

    /// <summary>
    /// 滑鼠懸停檢視完整 L3 規範語法、指令摘要與隱式副作用
    /// </summary>
    public string? DisplayTooltip
    {
        get
        {
            if (_isTrailingPlaceholder || string.IsNullOrWhiteSpace(_command)) return null;

            var schema = StandardCommandSchemas.Get(_command);
            if (schema == null) return string.IsNullOrWhiteSpace(_parameter) ? _command : $"{_command} :: {_parameter}";

            var sb = new StringBuilder();
            sb.Append(schema.CommandKey);
            if (!string.IsNullOrWhiteSpace(schema.Summary))
            {
                sb.Append(" — ").Append(schema.Summary);
            }

            string canonical = string.IsNullOrWhiteSpace(_parameter)
                ? "(無參數)"
                : CommandDisplayProjector.ProjectParameter(_parameter, schema, CommandDisplayLevel.Canonical, multiLineLayout: true);
            sb.AppendLine().Append("Canonical: ").Append(canonical);

            if (!string.IsNullOrWhiteSpace(_parameter) &&
                !string.Equals(_parameter.Trim(), canonical.Trim(), StringComparison.Ordinal))
            {
                sb.AppendLine().Append("Raw: ").Append(_parameter.Trim());
            }

            if (schema.ImplicitEffects.Count > 0)
            {
                sb.AppendLine().Append("Effects: ").Append(string.Join("; ", schema.ImplicitEffects));
            }

            return sb.ToString();
        }
    }

    public void RefreshProjection() => NotifyProjectionChanged();

    private void NotifyProjectionChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayParameter)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCriticalAlert)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ParameterForeground)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayTooltip)));
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
