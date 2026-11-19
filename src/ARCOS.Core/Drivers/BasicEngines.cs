using System;
using ARCOS.Core.Engines;

namespace ARCOS.Core.Drivers;

/// <summary>
/// 第一階段 Basic Engines 的集合容器，在 Driver 初始化時注入
/// 供 Driver 直接進行 IO 搬運、點位座標回讀、影像寫入與波形採樣
/// </summary>
public class BasicEngines
{
    public IoEngine Io { get; }
    public VariableEngine Variable { get; }
    public PointEngine Point { get; }
    public ImageEngine Image { get; }
    public ImageEngine Imaq => Image;
    public DaqEngine Daq { get; }
    public TimerEngine Timer { get; }
    public RecordEngine Record { get; }
    public UserPanelEngine UserPanel { get; }
    public ProgramEngine Program { get; }
    public MultiStationEngine MultiStation { get; }

    /// <summary>
    /// 可選的驅動程式總管參考，供 Guide UI 即時查詢已註冊驅動清單與執行 GetPos 等即時讀取
    /// </summary>
    public DriverManager? Drivers { get; set; }

    public BasicEngines() : this(
        new IoEngine(),
        new VariableEngine(),
        new PointEngine(),
        new ImageEngine(),
        new DaqEngine(),
        new TimerEngine(),
        new RecordEngine(),
        new UserPanelEngine(),
        new ProgramEngine())
    {
    }

    public BasicEngines(
        IoEngine io,
        VariableEngine variable,
        PointEngine point,
        ImageEngine image,
        DaqEngine daq,
        TimerEngine timer)
        : this(io, variable, point, image, daq, timer, new RecordEngine(), new UserPanelEngine())
    {
    }

    public BasicEngines(
        IoEngine io,
        VariableEngine variable,
        PointEngine point,
        ImageEngine image,
        DaqEngine daq,
        TimerEngine timer,
        RecordEngine record)
        : this(io, variable, point, image, daq, timer, record, new UserPanelEngine())
    {
    }

    public BasicEngines(
        IoEngine io,
        VariableEngine variable,
        PointEngine point,
        ImageEngine image,
        DaqEngine daq,
        TimerEngine timer,
        RecordEngine record,
        UserPanelEngine userPanel)
        : this(io, variable, point, image, daq, timer, record, userPanel, null)
    {
    }

    public BasicEngines(
        IoEngine io,
        VariableEngine variable,
        PointEngine point,
        ImageEngine image,
        DaqEngine daq,
        TimerEngine timer,
        RecordEngine record,
        UserPanelEngine userPanel,
        ProgramEngine? program)
    {
        Io = io ?? throw new ArgumentNullException(nameof(io));
        Variable = variable ?? throw new ArgumentNullException(nameof(variable));
        Point = point ?? throw new ArgumentNullException(nameof(point));
        Image = image ?? throw new ArgumentNullException(nameof(image));
        Daq = daq ?? throw new ArgumentNullException(nameof(daq));
        Timer = timer ?? throw new ArgumentNullException(nameof(timer));
        Record = record ?? throw new ArgumentNullException(nameof(record));
        UserPanel = userPanel ?? throw new ArgumentNullException(nameof(userPanel));
        Program = program ?? new ProgramEngine();
        MultiStation = new MultiStationEngine(Io, Variable, Point);
    }
}
