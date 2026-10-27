using MVCON.Core.Engines;

namespace MVCON.Core.Drivers;

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
    public DaqEngine Daq { get; }
    public TimerEngine Timer { get; }

    public BasicEngines() : this(
        new IoEngine(),
        new VariableEngine(),
        new PointEngine(),
        new ImageEngine(),
        new DaqEngine(),
        new TimerEngine())
    {
    }

    public BasicEngines(
        IoEngine io,
        VariableEngine variable,
        PointEngine point,
        ImageEngine image,
        DaqEngine daq,
        TimerEngine timer)
    {
        Io = io ?? throw new ArgumentNullException(nameof(io));
        Variable = variable ?? throw new ArgumentNullException(nameof(variable));
        Point = point ?? throw new ArgumentNullException(nameof(point));
        Image = image ?? throw new ArgumentNullException(nameof(image));
        Daq = daq ?? throw new ArgumentNullException(nameof(daq));
        Timer = timer ?? throw new ArgumentNullException(nameof(timer));
    }
}
