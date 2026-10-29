using System.Collections.Concurrent;
using ARCOS.Core.Model;

namespace ARCOS.Core.Engines;

/// <summary>
/// 動態多通道波形採樣佇列引擎 (DaqEngine)
/// 管理連續採樣波形、數位濾波、上下限包絡線比對，並連動 $(name)#OK? 旗標
/// </summary>
public class DaqEngine
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, WaveformEntry> _waveforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly IoEngine? _ioEngine;

    public DaqEngine(IoEngine? ioEngine = null)
    {
        _ioEngine = ioEngine;
    }

    /// <summary>
    /// 存入完整採樣波形
    /// </summary>
    public void SetWaveform(string name, DateTime t0, double dt, double[] yData, int channel = 0)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_lock)
        {
            var entry = _waveforms.GetOrAdd(name, _ => new WaveformEntry());
            var chData = entry.GetOrCreateChannel(channel);
            chData.T0 = t0;
            chData.Dt = dt <= 0 ? 0.001 : dt;
            chData.Y = yData != null ? (double[])yData.Clone() : Array.Empty<double>();

            _ioEngine?.SetPort($"{name}#OK?", true);
        }
    }

    /// <summary>
    /// 連續採樣模式下批次追加樣本點
    /// </summary>
    public void AppendSamples(string name, ReadOnlySpan<double> samples, int channel = 0)
    {
        if (samples.IsEmpty || string.IsNullOrWhiteSpace(name)) return;
        lock (_lock)
        {
            var entry = _waveforms.GetOrAdd(name, _ => new WaveformEntry());
            var chData = entry.GetOrCreateChannel(channel);

            int oldLen = chData.Y.Length;
            var newArr = new double[oldLen + samples.Length];
            if (oldLen > 0)
            {
                Array.Copy(chData.Y, newArr, oldLen);
            }
            samples.CopyTo(newArr.AsSpan(oldLen));
            chData.Y = newArr;

            _ioEngine?.SetPort($"{name}#OK?", true);
        }
    }

    /// <summary>
    /// 取得最新單點數值 (對應 DAQ@AnalogIn)
    /// </summary>
    public double GetInstantValue(string name, int channel = 0)
    {
        if (_waveforms.TryGetValue(name, out var entry) && entry.Channels.TryGetValue(channel, out var chData))
        {
            if (chData.Y.Length > 0)
            {
                return chData.Y[^1];
            }
        }
        return 0.0;
    }

    public WaveformEntry GetWaveform(string name)
    {
        if (_waveforms.TryGetValue(name, out var entry))
        {
            return entry;
        }
        throw new KeyNotFoundException($"Waveform '{name}' was not found.");
    }

    /// <summary>
    /// 數位濾波去毛刺 (對應 DAQ@Filter)
    /// </summary>
    public void ApplyFilter(string name, DaqFilterType filterType, double param = 0, int channel = 0)
    {
        lock (_lock)
        {
            if (!_waveforms.TryGetValue(name, out var entry) || !entry.Channels.TryGetValue(channel, out var chData))
                return;

            var y = chData.Y;
            if (y.Length < 3) return;

            if (filterType == DaqFilterType.MovingAverage)
            {
                int window = (int)Math.Max(3, param);
                if (window % 2 == 0) window++;
                int half = window / 2;
                var filtered = new double[y.Length];

                for (int i = 0; i < y.Length; i++)
                {
                    double sum = 0;
                    int count = 0;
                    for (int w = -half; w <= half; w++)
                    {
                        int idx = i + w;
                        if (idx >= 0 && idx < y.Length)
                        {
                            sum += y[idx];
                            count++;
                        }
                    }
                    filtered[i] = sum / count;
                }
                chData.Y = filtered;
            }
            else if (filterType == DaqFilterType.Median)
            {
                int window = (int)Math.Max(3, param);
                int half = window / 2;
                var filtered = new double[y.Length];
                var buffer = new List<double>(window);

                for (int i = 0; i < y.Length; i++)
                {
                    buffer.Clear();
                    for (int w = -half; w <= half; w++)
                    {
                        int idx = i + w;
                        if (idx >= 0 && idx < y.Length) buffer.Add(y[idx]);
                    }
                    buffer.Sort();
                    filtered[i] = buffer[buffer.Count / 2];
                }
                chData.Y = filtered;
            }
        }
    }

    /// <summary>
    /// 極值公差檢查，寫入 $(name)#OK? (對應 DAQ@CompareMaxMinInRange)
    /// </summary>
    public bool CompareMaxMinInRange(string name, double max, double min, int channel = 0)
    {
        if (_waveforms.TryGetValue(name, out var entry) && entry.Channels.TryGetValue(channel, out var chData))
        {
            if (chData.Y.Length == 0) return false;

            double currentMax = chData.Y.Max();
            double currentMin = chData.Y.Min();

            bool isOk = (currentMax <= max) && (currentMin >= min);
            _ioEngine?.SetPort($"{name}#OK?", isOk);
            return isOk;
        }
        return false;
    }

    /// <summary>
    /// 【包絡線檢測】逐點比對是否落在上下限波形包絡線內 (對應 DAQ@CompareCoverBy)
    /// </summary>
    public bool CompareCoverBy(string name, string upperSpecName, string lowerSpecName, int channel = 0)
    {
        if (!_waveforms.TryGetValue(name, out var entry) || !entry.Channels.TryGetValue(channel, out var chData))
            return false;

        if (!_waveforms.TryGetValue(upperSpecName, out var upperEntry) || !upperEntry.Channels.TryGetValue(channel, out var upperCh))
            return false;

        if (!_waveforms.TryGetValue(lowerSpecName, out var lowerEntry) || !lowerEntry.Channels.TryGetValue(channel, out var lowerCh))
            return false;

        var y = chData.Y;
        var upY = upperCh.Y;
        var lowY = lowerCh.Y;

        int checkCount = Math.Min(y.Length, Math.Min(upY.Length, lowY.Length));
        if (checkCount == 0) return false;

        bool allWithinCover = true;
        for (int i = 0; i < checkCount; i++)
        {
            if (y[i] > upY[i] || y[i] < lowY[i])
            {
                allWithinCover = false;
                break;
            }
        }

        _ioEngine?.SetPort($"{name}#OK?", allWithinCover);
        return allWithinCover;
    }

    public IReadOnlyList<string> GetAllNames()
    {
        return _waveforms.Keys.ToList();
    }
}
