using System.Collections.Concurrent;
using ARCOS.Core.Model;
using OpenCvSharp;

namespace ARCOS.Core.Engines;

/// <summary>
/// 動態多通道波形採樣佇列引擎 (DaqEngine)
/// 管理連續採樣波形、數位濾波、上下限包絡線比對，並連動 $(name)#OK? 旗標
/// </summary>
public class DaqEngine
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, WaveformEntry> _waveforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _userDisplayBindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly IoEngine? _ioEngine;

    /// <summary>
    /// 當腳本執行 DAQ@Display2User::$waveform, $control 時通知 UI 直接更新目標控制項：(WaveformName, ControlName)
    /// </summary>
    public event Action<string, string>? UserDisplayRequested;

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
    /// 平均值公差檢查，寫入 $(name)#OK? (對應 DAQ@CompareAvgInRange)
    /// </summary>
    public bool CompareAvgInRange(string name, double max, double min, int startIdx = 0, int endIdx = -1, int channel = 0)
    {
        if (_waveforms.TryGetValue(name, out var entry) && entry.Channels.TryGetValue(channel, out var chData))
        {
            if (chData.Y.Length == 0) return false;

            int s = Math.Clamp(startIdx, 0, chData.Y.Length - 1);
            int e = endIdx < 0 ? chData.Y.Length - 1 : Math.Clamp(endIdx, s, chData.Y.Length - 1);
            var slice = chData.Y.Skip(s).Take(e - s + 1).ToArray();
            if (slice.Length == 0) return false;

            double avg = slice.Average();
            bool isOk = (avg <= max) && (avg >= min);
            _ioEngine?.SetPort($"{name}#OK?", isOk);
            return isOk;
        }
        return false;
    }

    /// <summary>
    /// 極值公差檢查，寫入 $(name)#OK? (對應 DAQ@CompareMaxMinInRange)
    /// </summary>
    public bool CompareMaxMinInRange(string name, double max, double min, int channel = 0, int startIdx = 0, int endIdx = -1)
    {
        if (_waveforms.TryGetValue(name, out var entry) && entry.Channels.TryGetValue(channel, out var chData))
        {
            if (chData.Y.Length == 0) return false;

            int s = Math.Clamp(startIdx, 0, chData.Y.Length - 1);
            int e = endIdx < 0 ? chData.Y.Length - 1 : Math.Clamp(endIdx, s, chData.Y.Length - 1);
            var slice = chData.Y.Skip(s).Take(e - s + 1).ToArray();
            if (slice.Length == 0) return false;

            double currentMax = slice.Max();
            double currentMin = slice.Min();

            bool isOk = (currentMax <= max) && (currentMin >= min);
            _ioEngine?.SetPort($"{name}#OK?", isOk);
            return isOk;
        }
        return false;
    }

    /// <summary>
    /// 【包絡線檢測】逐點比對是否落在上下限波形包絡線內 (對應 DAQ@CompareCoverBy)
    /// </summary>
    public bool CompareCoverBy(string name, string upperSpecName, string? lowerSpecName = null, int channel = 0)
    {
        if (!_waveforms.TryGetValue(name, out var entry) || !entry.Channels.TryGetValue(channel, out var chData))
            return false;

        if (!_waveforms.TryGetValue(upperSpecName, out var upperEntry) || !upperEntry.Channels.TryGetValue(channel, out var upperCh))
            return false;

        var y = chData.Y;
        var upY = upperCh.Y;

        if (!string.IsNullOrWhiteSpace(lowerSpecName) &&
            _waveforms.TryGetValue(lowerSpecName, out var lowerEntry) &&
            lowerEntry.Channels.TryGetValue(channel, out var lowerCh))
        {
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
        else
        {
            // 若僅提供單一包絡線，檢查 y[i] <= upY[i]
            int checkCount = Math.Min(y.Length, upY.Length);
            if (checkCount == 0) return false;

            bool allWithinCover = true;
            for (int i = 0; i < checkCount; i++)
            {
                if (y[i] > upY[i])
                {
                    allWithinCover = false;
                    break;
                }
            }

            _ioEngine?.SetPort($"{name}#OK?", allWithinCover);
            return allWithinCover;
        }
    }

    /// <summary>
    /// 內建函數自訂產生模擬波形 (對應 DAQ@WaveFormGenerate)
    /// </summary>
    public void GenerateWaveform(string name, string waveType = "Sine", double rate = 1000.0, int count = 200, double amplitude = 5.0, double offset = 0.0, int channel = 0)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        count = Math.Max(1, count);
        double dt = rate > 0 ? 1.0 / rate : 0.001;
        var data = new double[count];

        for (int i = 0; i < count; i++)
        {
            if (waveType.Contains("Sine", StringComparison.OrdinalIgnoreCase))
            {
                data[i] = offset + amplitude * Math.Sin(2.0 * Math.PI * i / Math.Max(10, count / 4.0));
            }
            else if (waveType.Contains("Square", StringComparison.OrdinalIgnoreCase))
            {
                data[i] = offset + ((i % 40 < 20) ? amplitude : -amplitude);
            }
            else if (waveType.Contains("Const", StringComparison.OrdinalIgnoreCase))
            {
                data[i] = offset + amplitude;
            }
            else
            {
                // Triangle
                double phase = (i % 100) / 100.0;
                double tri = phase < 0.5 ? (phase * 4.0 - 1.0) : (3.0 - phase * 4.0);
                data[i] = offset + amplitude * tri;
            }
        }

        SetWaveform(name, DateTime.Now, dt, data, channel);
    }

    /// <summary>
    /// 波形間或波形與常數的四則運算 (對應 DAQ@Calculate::$waveform = $w1 {+-*/} $w2)
    /// </summary>
    public void CalculateWaveform(string targetName, string leftOperand, char op, string rightOperand, int channel = 0)
    {
        if (string.IsNullOrWhiteSpace(targetName) || string.IsNullOrWhiteSpace(leftOperand)) return;

        if (!_waveforms.TryGetValue(leftOperand, out var leftEntry) || !leftEntry.Channels.TryGetValue(channel, out var leftCh))
            return;

        var leftY = leftCh.Y;
        if (leftY.Length == 0) return;

        var resultY = new double[leftY.Length];
        if (_waveforms.TryGetValue(rightOperand, out var rightEntry) && rightEntry.Channels.TryGetValue(channel, out var rightCh) && rightCh.Y.Length > 0)
        {
            var rightY = rightCh.Y;
            for (int i = 0; i < leftY.Length; i++)
            {
                double r = i < rightY.Length ? rightY[i] : rightY[^1];
                resultY[i] = op switch
                {
                    '+' => leftY[i] + r,
                    '-' => leftY[i] - r,
                    '*' => leftY[i] * r,
                    '/' => Math.Abs(r) > 1e-12 ? leftY[i] / r : 0.0,
                    _ => leftY[i]
                };
            }
        }
        else if (double.TryParse(rightOperand, out double scalar))
        {
            for (int i = 0; i < leftY.Length; i++)
            {
                resultY[i] = op switch
                {
                    '+' => leftY[i] + scalar,
                    '-' => leftY[i] - scalar,
                    '*' => leftY[i] * scalar,
                    '/' => Math.Abs(scalar) > 1e-12 ? leftY[i] / scalar : 0.0,
                    _ => leftY[i]
                };
            }
        }

        SetWaveform(targetName, leftCh.T0, leftCh.Dt, resultY, channel);
    }

    /// <summary>
    /// 將多組波形依序合併為單一波形 (對應 DAQ@MergeWaveForm::$WaveForm, $WaveForm1, $WaveForm2...)
    /// </summary>
    public void MergeWaveforms(string targetName, IEnumerable<string> sourceNames, int channel = 0)
    {
        if (string.IsNullOrWhiteSpace(targetName) || sourceNames == null) return;

        var merged = new List<double>();
        double dt = 0.001;
        DateTime t0 = DateTime.Now;
        bool first = true;

        foreach (var src in sourceNames)
        {
            if (string.IsNullOrWhiteSpace(src)) continue;
            if (_waveforms.TryGetValue(src.Trim(), out var entry) && entry.Channels.TryGetValue(channel, out var chData))
            {
                if (first)
                {
                    dt = chData.Dt;
                    t0 = chData.T0;
                    first = false;
                }
                merged.AddRange(chData.Y);
            }
        }

        if (merged.Count > 0)
        {
            SetWaveform(targetName, t0, dt, merged.ToArray(), channel);
        }
    }

    /// <summary>
    /// 將指定波形綁定並推送顯示至 UserPanel 上的指定控制項 (對應 DAQ@Display2User::$waveform, $control)
    /// </summary>
    public void DisplayToUserControl(string waveformName, string controlName)
    {
        if (string.IsNullOrWhiteSpace(waveformName) || string.IsNullOrWhiteSpace(controlName)) return;
        string wf = waveformName.Trim();
        string ctrl = controlName.Trim();
        _userDisplayBindings[ctrl] = wf;
        UserDisplayRequested?.Invoke(wf, ctrl);
    }

    /// <summary>
    /// 查詢指定 UserPanel 控制項最後綁定的波形名稱
    /// </summary>
    public string? GetBoundWaveformForControl(string controlName)
    {
        if (string.IsNullOrWhiteSpace(controlName)) return null;
        return _userDisplayBindings.TryGetValue(controlName.Trim(), out var wf) ? wf : null;
    }

    /// <summary>
    /// 將指定波形渲染為標準無損向量 SVG 字串 (對應方案 C：SVG 向量輸出與渲染)
    /// </summary>
    public string RenderWaveformToSvg(string name, int channel = 0, int width = 600, int height = 280)
    {
        double[] yData = Array.Empty<double>();
        double dt = 0.001;
        if (_waveforms.TryGetValue(name, out var entry) && entry.Channels.TryGetValue(channel, out var chData) && chData.Y.Length > 0)
        {
            yData = chData.Y;
            dt = chData.Dt;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" width=\"100%\" height=\"100%\">");
        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" fill=\"#0F172A\"/>");

        int plotLeft = 48;
        int plotRight = width - 20;
        int plotTop = 36;
        int plotBottom = height - 28;
        int plotW = plotRight - plotLeft;
        int plotH = plotBottom - plotTop;

        // 繪製向量背景網格
        sb.AppendLine("  <g stroke=\"#1E293B\" stroke-width=\"1\">");
        for (int gx = plotLeft; gx <= plotRight; gx += 60)
            sb.AppendLine($"    <line x1=\"{gx}\" y1=\"{plotTop}\" x2=\"{gx}\" y2=\"{plotBottom}\"/>");
        for (int gy = plotTop; gy <= plotBottom; gy += 40)
            sb.AppendLine($"    <line x1=\"{plotLeft}\" y1=\"{gy}\" x2=\"{plotRight}\" y2=\"{gy}\"/>");
        sb.AppendLine("  </g>");

        if (yData.Length > 0)
        {
            double minVal = yData.Min();
            double maxVal = yData.Max();
            double avgVal = yData.Average();
            double span = Math.Max(1e-6, maxVal - minVal);

            var pts = new System.Text.StringBuilder();
            for (int i = 0; i < yData.Length; i++)
            {
                double x = plotLeft + (i * (double)plotW / Math.Max(1, yData.Length - 1));
                double y = plotBottom - ((yData[i] - minVal) / span * plotH);
                if (i > 0) pts.Append(' ');
                pts.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:F1},{1:F1}", x, y);
            }

            sb.AppendLine($"  <polyline fill=\"none\" stroke=\"#10B981\" stroke-width=\"2\" stroke-linejoin=\"round\" stroke-linecap=\"round\" points=\"{pts}\"/>");
            sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"  <text x=\"16\" y=\"22\" fill=\"#F8FAFC\" font-family=\"Consolas, monospace\" font-size=\"12\" font-weight=\"bold\">DAQ: {name} [Ch{channel}]  N={yData.Length}  Min={minVal:F2}  Max={maxVal:F2}  Avg={avgVal:F2}  dt={dt:G4}s</text>"));
        }
        else
        {
            sb.AppendLine($"  <text x=\"16\" y=\"24\" fill=\"#94A3B8\" font-family=\"Consolas, monospace\" font-size=\"12\">DAQ: {name} (No Data)</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// 將指定波形渲染為自包含互動式 HTML 網頁（內嵌無損 SVG 與統計摘要卡片）
    /// </summary>
    public string RenderWaveformToHtml(string name, int channel = 0)
    {
        string svg = RenderWaveformToSvg(name, channel, 800, 360);
        return $@"<!DOCTYPE html>
<html lang=""zh-Hant"">
<head>
<meta charset=""utf-8""/>
<title>ARCOS DAQ Waveform - {name}</title>
<style>
  body {{ margin: 0; padding: 16px; background: #090D16; color: #F8FAFC; font-family: 'Segoe UI', Consolas, sans-serif; }}
  .card {{ background: #0F172A; border: 1px solid #1E293B; border-radius: 10px; padding: 16px; box-shadow: 0 4px 20px rgba(0,0,0,0.4); }}
</style>
</head>
<body>
  <div class=""card"">
    {svg}
  </div>
</body>
</html>";
    }

    /// <summary>
    /// 將指定波形渲染為高品質趨勢圖 PNG byte[]，供舊版點陣相容需求使用
    /// </summary>
    public byte[]? RenderWaveformToPngBytes(string name, int channel = 0)
    {
        try
        {
            double[] yData;
            if (_waveforms.TryGetValue(name, out var entry) && entry.Channels.TryGetValue(channel, out var chData) && chData.Y.Length > 0)
            {
                yData = chData.Y;
            }
            else
            {
                yData = Array.Empty<double>();
            }

            int width = 480;
            int height = 240;
            using var canvas = new Mat(height, width, MatType.CV_8UC3, new Scalar(15, 23, 42));

            // 繪製背景網格
            var gridColor = new Scalar(30, 41, 59);
            for (int gx = 40; gx < width - 20; gx += 60)
                Cv2.Line(canvas, new Point(gx, 28), new Point(gx, height - 24), gridColor, 1);
            for (int gy = 30; gy < height - 20; gy += 40)
                Cv2.Line(canvas, new Point(40, gy), new Point(width - 16, gy), gridColor, 1);

            if (yData.Length > 0)
            {
                double minVal = yData.Min();
                double maxVal = yData.Max();
                double span = Math.Max(1e-6, maxVal - minVal);
                int plotLeft = 40;
                int plotRight = width - 16;
                int plotTop = 32;
                int plotBottom = height - 24;
                int plotW = plotRight - plotLeft;
                int plotH = plotBottom - plotTop;

                var lineColor = new Scalar(16, 185, 129); // Emerald Green (BGR)
                for (int i = 1; i < yData.Length; i++)
                {
                    int x1 = plotLeft + (int)((i - 1) * (double)plotW / Math.Max(1, yData.Length - 1));
                    int x2 = plotLeft + (int)(i * (double)plotW / Math.Max(1, yData.Length - 1));
                    int y1 = plotBottom - (int)((yData[i - 1] - minVal) / span * plotH);
                    int y2 = plotBottom - (int)((yData[i] - minVal) / span * plotH);
                    Cv2.Line(canvas, new Point(x1, y1), new Point(x2, y2), lineColor, 2, LineTypes.AntiAlias);
                }

                Cv2.PutText(canvas, $"DAQ: {name} (N={yData.Length}, Min={minVal:F2}, Max={maxVal:F2})",
                    new Point(14, 20), HersheyFonts.HersheySimplex, 0.45, new Scalar(248, 250, 252), 1);
            }
            else
            {
                Cv2.PutText(canvas, $"DAQ: {name} (No Data)",
                    new Point(14, 20), HersheyFonts.HersheySimplex, 0.5, new Scalar(148, 163, 184), 1);
            }

            Cv2.ImEncode(".png", canvas, out byte[] buf);
            return buf;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<string> GetAllNames()
    {
        return _waveforms.Keys.ToList();
    }

    /// <summary>
    /// 將指定波形資料儲存為 CSV 檔案至 WAVEFORM/ 目錄
    /// </summary>
    public void SaveWaveform(string name, string? filePath = null, int channel = 0)
    {
        var entry = GetWaveform(name);
        var chData = entry.GetOrCreateChannel(channel);

        string targetPath = filePath ?? Path.Combine(ARCOS.Core.Common.ArcosPaths.WaveformDirectory, $"{name}_Ch{channel}.csv");
        if (!Path.IsPathRooted(targetPath))
        {
            targetPath = Path.Combine(ARCOS.Core.Common.ArcosPaths.WaveformDirectory, targetPath);
        }

        string dir = Path.GetDirectoryName(targetPath) ?? "";
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Waveform: {name}, Channel: {channel}, T0: {chData.T0:yyyy-MM-dd HH:mm:ss.fff}, Dt: {chData.Dt}");
        sb.AppendLine("Index,Time_s,Value");
        for (int i = 0; i < chData.Y.Length; i++)
        {
            sb.AppendLine($"{i},{(i * chData.Dt):F6},{chData.Y[i]}");
        }

        File.WriteAllText(targetPath, sb.ToString(), System.Text.Encoding.UTF8);
    }

    /// <summary>
    /// 從 CSV 檔案載入波形資料至 DaqEngine
    /// </summary>
    public void LoadWaveform(string name, string? filePath = null, int channel = 0)
    {
        string targetPath = filePath ?? Path.Combine(ARCOS.Core.Common.ArcosPaths.WaveformDirectory, $"{name}_Ch{channel}.csv");
        if (!Path.IsPathRooted(targetPath) && !File.Exists(targetPath))
        {
            targetPath = Path.Combine(ARCOS.Core.Common.ArcosPaths.WaveformDirectory, targetPath);
        }

        if (!File.Exists(targetPath)) return;

        var lines = File.ReadAllLines(targetPath, System.Text.Encoding.UTF8);
        var values = new List<double>();
        double dt = 0.001;
        DateTime t0 = DateTime.Now;

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith('#'))
            {
                int dtIdx = line.IndexOf("Dt:", StringComparison.OrdinalIgnoreCase);
                if (dtIdx > 0)
                {
                    string dtStr = line.Substring(dtIdx + 3).Trim();
                    if (double.TryParse(dtStr, out double parsedDt)) dt = parsedDt;
                }
                continue;
            }
            if (line.StartsWith("Index", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 3 && double.TryParse(parts[2], out double val))
            {
                values.Add(val);
            }
            else if (parts.Length == 1 && double.TryParse(parts[0], out double singleVal))
            {
                values.Add(singleVal);
            }
        }

        SetWaveform(name, t0, dt, values.ToArray(), channel);
    }
}
