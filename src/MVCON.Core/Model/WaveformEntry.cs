namespace MVCON.Core.Model;

public enum DaqFilterType
{
    None,
    MovingAverage,
    Median,
    LowPass
}

public class WaveformChannelData
{
    public DateTime T0 { get; set; } = DateTime.Now;
    public double Dt { get; set; } = 0.001; // 1kHz 預設採樣率
    public double[] Y { get; set; } = Array.Empty<double>();

    public WaveformChannelData() { }

    public WaveformChannelData(DateTime t0, double dt, double[] y)
    {
        T0 = t0;
        Dt = dt;
        Y = y;
    }
}

public class WaveformEntry
{
    public Dictionary<int, WaveformChannelData> Channels { get; } = new();

    public WaveformChannelData GetOrCreateChannel(int channel = 0)
    {
        if (!Channels.TryGetValue(channel, out var chData))
        {
            chData = new WaveformChannelData();
            Channels[channel] = chData;
        }
        return chData;
    }
}
