using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ARCOS.Core.Common;
using ARCOS.Core.Drivers;

namespace ARCOS.Driver.MockDAQ;

public enum MockWaveType
{
    TriangleCounter, // 循環三角波波型：最大值從 0 數到 1023 後再從 0 開始
    Sine,
    PressCurve,
    Square
}

/// <summary>
/// 模擬資料採集驅動 (MockDaqDriver)
/// 內部維護時間長度 × 取樣率 (BufferSeconds * SampleRate) 之環形緩衝區 (Circular Ring Buffer)：
/// 1. AddDAQ：依 Rate (Hz) 與 Sec (秒) 預先配置環形緩衝區
/// 2. Start / Stop：背景持續將硬體/模擬樣本寫入內部環形緩衝區
/// 3. AnalogIn：自環形緩衝區取得最新一筆單點數值
/// 4. AnalogInWaveForm：自環形緩衝區擷取最近指定秒數 (sec) 之連續波形
/// </summary>
public class MockDaqDriver : IDriver
{
    private BasicEngines? _engines;
    private readonly Channel<DriverTicket> _ticketQueue = Channel.CreateUnbounded<DriverTicket>();
    private readonly object _ringLock = new();
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    public string DeviceName { get; private set; } = string.Empty;
    public string Category => "DAQ";
    public string DriverType => "MockDAQ";

    public double SampleRate { get; private set; } = 1000.0; // 1kHz
    public double BufferSeconds { get; private set; } = 1.0; // 1.0 秒環形緩衝
    public int RingCapacity => Math.Max(1, (int)Math.Round(SampleRate * BufferSeconds));
    public int SampleCount => RingCapacity;
    public MockWaveType WaveType { get; private set; } = MockWaveType.TriangleCounter;

    private double[] _ringBuffer = new double[1000];
    private int _ringWriteIndex = 0;
    private int _ringCount = 0;
    private long _totalSampleIndex = 0;
    private long _lastAcquireTimestamp = 0;
    private double _fractionalSamples = 0.0;

    public bool IsSampling { get; private set; } = false;
    public double InstantInputVal { get; private set; } = 0.0;
    public double InstantValue => InstantInputVal;
    public double InstantOutputVal { get; private set; } = 0.0;
    public double[] LatestWaveform { get; private set; } = Array.Empty<double>();

    public event Action? DaqStateUpdated;

    public void Init(string deviceName, string param, BasicEngines engines)
    {
        DeviceName = deviceName;
        _engines = engines;

        // 解析參數：Rate=1000,Sec=5,Wave=Triangle（向下相容舊式 Count=...）
        var args = ArgParaHelper.ParseKeyValueArgs(param, out _);
        if (args.TryGetValue("Rate", out var rateStr) && double.TryParse(rateStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double r) && r > 0)
            SampleRate = r;

        if ((args.TryGetValue("Sec", out var secStr) || args.TryGetValue("Duration", out secStr) || args.TryGetValue("BufferSec", out secStr)) &&
            double.TryParse(secStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double sec) && sec > 0)
        {
            BufferSeconds = sec;
        }
        else if (args.TryGetValue("Count", out var cntStr) && int.TryParse(cntStr, out int c) && c > 0)
        {
            BufferSeconds = c / SampleRate;
        }

        if (args.TryGetValue("Wave", out var waveStr))
        {
            ApplyWaveType(waveStr);
        }

        AllocateRingBuffer();

        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoop(_cts.Token));
    }

    private void ApplyWaveType(string waveStr)
    {
        if (waveStr.Contains("Sine", StringComparison.OrdinalIgnoreCase)) WaveType = MockWaveType.Sine;
        else if (waveStr.Contains("Square", StringComparison.OrdinalIgnoreCase)) WaveType = MockWaveType.Square;
        else if (waveStr.Contains("Press", StringComparison.OrdinalIgnoreCase)) WaveType = MockWaveType.PressCurve;
        else WaveType = MockWaveType.TriangleCounter;
    }

    private void AllocateRingBuffer()
    {
        lock (_ringLock)
        {
            _ringBuffer = new double[RingCapacity];
            _ringWriteIndex = 0;
            _ringCount = 0;
            _fractionalSamples = 0.0;
        }
    }

    public void PostTicket(DriverTicket ticket)
    {
        _ticketQueue.Writer.TryWrite(ticket);
    }

    public object? CreateDriverView()
    {
        return new MockDaqView(this);
    }

    public async Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
    {
        _engines = engines;
        if (owner is not Window ownerWin) return currentParam;

        var root = new Grid { Margin = new Thickness(16), Background = Brush.Parse("#F8FAFC") };
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var hdrGrid = new Grid();
        hdrGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        hdrGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        hdrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        hdrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var lblSyntax = new TextBlock { Text = "Rate=...,Count=...,Wave=...", Foreground = Brush.Parse("#38BDF8"), FontSize = 11, FontWeight = FontWeight.SemiBold, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(lblSyntax, 1);
        var txtParamIn = new TextBlock { Text = string.IsNullOrWhiteSpace(currentParam) ? "ParameterIn: (空白)" : $"ParameterIn: {currentParam.Trim()}", Foreground = Brush.Parse("#94A3B8"), FontSize = 11, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetRow(txtParamIn, 1); Grid.SetColumnSpan(txtParamIn, 2);
        hdrGrid.Children.Add(new TextBlock { Text = "DAQ / MockDAQ - 驅動參數導引精靈", Foreground = Brush.Parse("#F8FAFC"), FontSize = 15, FontWeight = FontWeight.Bold });
        hdrGrid.Children.Add(lblSyntax);
        hdrGrid.Children.Add(txtParamIn);
        root.Children.Add(new Border { Background = Brush.Parse("#1E293B"), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 0, 10), Child = hdrGrid });

        var bodyGrid = new Grid();
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(112)));
        var fields = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };

        int wfCount = engines?.Daq?.GetAllNames().Count ?? 0;
        fields.Children.Add(new Border
        {
            Background = Brush.Parse("#F1F5F9"),
            BorderBrush = Brush.Parse("#CBD5E1"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 6),
            Child = new TextBlock { Text = $"目前 DaqEngine 波形數: {wfCount} | 實例: {(string.IsNullOrWhiteSpace(DeviceName) ? "Daq0" : DeviceName)}", Foreground = Brush.Parse("#475569"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap }
        });

        static (StackPanel Panel, ComboBox Combo, TextBox Box, TextBlock? Meta) MakeField(string label, string meta, string watermark)
        {
            var hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            hdr.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            hdr.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#DC2626"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            TextBlock? metaLbl = null;
            if (!string.IsNullOrEmpty(meta))
            {
                metaLbl = new TextBlock { Text = meta, Foreground = Brush.Parse("#64748B"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(metaLbl, 1); hdr.Children.Add(metaLbl);
            }
            var cmb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(8, 3, 34, 3), Foreground = Brushes.Transparent, FontSize = 13 };
            var txt = new TextBox { Margin = new Thickness(2, 2, 32, 2), BorderThickness = new Thickness(0), Background = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 3), FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#DC2626"), Watermark = watermark };
            return (new StackPanel { Spacing = 3, Children = { hdr, new Border { BorderBrush = Brush.Parse("#DC2626"), BorderThickness = new Thickness(1.8), CornerRadius = new CornerRadius(5), Background = Brushes.White, Child = new Grid { Children = { cmb, txt } } } } }, cmb, txt, metaLbl);
        }

        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); row1.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10))); row1.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var (rP, cmbRate, txtRate, txtDurMeta) = MakeField("Rate (取樣頻率 Hz) *", "dt = 1.0 ms", "500, 1000, 2000...");
        var (cP, cmbCount, txtCount, _) = MakeField("Count (緩衝樣本點數) *", "", "100, 500, 1000...");
        Grid.SetColumn(cP, 2); row1.Children.Add(rP); row1.Children.Add(cP); fields.Children.Add(row1);

        var (wP, cmbWave, txtWave, txtWaveMeta) = MakeField("Wave (模擬波形類型) *", "0~1023 循環三角波", "Triangle, Sine, Press, Square...");
        fields.Children.Add(wP);

        var btnOk = new Button { Background = Brush.Parse("#10B981"), Foreground = Brushes.White, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(4, 12, 0, 4), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand), Content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = "✓", FontSize = 34, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center }, new TextBlock { Text = "套用參數", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center } } } };
        Grid.SetColumn(btnOk, 1); bodyGrid.Children.Add(fields); bodyGrid.Children.Add(btnOk);
        var bodyBorder = new Border { Background = Brushes.White, BorderBrush = Brush.Parse("#CBD5E1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14), Child = bodyGrid };
        Grid.SetRow(bodyBorder, 1); root.Children.Add(bodyBorder);

        var ftrGrid = new Grid();
        ftrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); ftrGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var txtOut = new TextBox { IsReadOnly = true, Background = Brush.Parse("#F1F5F9"), Foreground = Brush.Parse("#0284C7"), FontWeight = FontWeight.Bold, FontFamily = new FontFamily("Consolas"), FontSize = 13, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
        var btnCancel = new Button { Content = "取消", Width = 86, Height = 36, VerticalAlignment = VerticalAlignment.Bottom, Background = Brush.Parse("#E2E8F0"), Foreground = Brush.Parse("#334155"), FontWeight = FontWeight.SemiBold, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(5), Cursor = new Cursor(StandardCursorType.Hand) };
        Grid.SetColumn(btnCancel, 1);
        ftrGrid.Children.Add(new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 12, 0), Children = { new TextBlock { Text = "ParameterOut (組合預覽):", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#475569") }, txtOut } });
        ftrGrid.Children.Add(btnCancel);
        var ftrBorder = new Border { Background = Brushes.White, BorderBrush = Brush.Parse("#CBD5E1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10), Margin = new Thickness(0, 10, 0, 0), Child = ftrGrid };
        Grid.SetRow(ftrBorder, 2); root.Children.Add(ftrBorder);

        var dlg = new Window { Title = "DAQ@AddDAQ::MockDAQ - Parameter Guidance", Width = 740, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#F8FAFC"), Content = root };

        var kv = ArgParaHelper.ParseKeyValueArgs(currentParam ?? "", out _);
        txtRate.Text = kv.TryGetValue("Rate", out var vr) && !string.IsNullOrWhiteSpace(vr) ? vr : SampleRate.ToString("G");
        txtCount.Text = kv.TryGetValue("Count", out var vc) && !string.IsNullOrWhiteSpace(vc) ? vc : SampleCount.ToString();
        txtWave.Text = kv.TryGetValue("Wave", out var vw) && !string.IsNullOrWhiteSpace(vw) ? vw : "Triangle";

        var vars = engines?.Variable?.GetAllVariableNames().Select(v => $"${v}").ToList() ?? new List<string>();
        bool syncing = false;
        void UpdatePreview()
        {
            string r = txtRate.Text?.Trim() ?? "", c = txtCount.Text?.Trim() ?? "", w = txtWave.Text?.Trim() ?? "";
            if (txtDurMeta != null)
                txtDurMeta.Text = double.TryParse(r, out double dr) && dr > 0 && int.TryParse(c, out int ic) && ic > 0 ? $"T={(ic / dr * 1000.0):F0} ms" : "Rate/Count";
            if (txtWaveMeta != null)
            {
                txtWaveMeta.Text = w.Contains("Sine", StringComparison.OrdinalIgnoreCase) ? "5Hz 正弦波 (512±511)"
                    : w.Contains("Square", StringComparison.OrdinalIgnoreCase) ? "週期 50 方波 (0 / 1023)"
                    : w.Contains("Press", StringComparison.OrdinalIgnoreCase) ? "壓合應力上升-持壓-卸壓曲線"
                    : "0~1023 循環三角波計數";
            }
            bool okAll = !string.IsNullOrWhiteSpace(r) && !string.IsNullOrWhiteSpace(c) && !string.IsNullOrWhiteSpace(w);
            btnOk.IsEnabled = okAll;
            txtOut.Text = okAll ? $"Rate={r},Count={c},Wave={w}" : string.Empty;
        }

        var rateOpts = new List<string> { "500", "1000", "2000", "5000", "10000" }; rateOpts.AddRange(vars);
        var countOpts = new List<string> { "100", "200", "500", "1000", "2048" }; countOpts.AddRange(vars);
        var waveOpts = new List<string> { "Triangle", "Sine", "Press", "Square" }; waveOpts.AddRange(vars);

        WireWizardCombo(cmbRate, txtRate, () => rateOpts, "", () => syncing, v => syncing = v, UpdatePreview);
        WireWizardCombo(cmbCount, txtCount, () => countOpts, "", () => syncing, v => syncing = v, UpdatePreview);
        WireWizardCombo(cmbWave, txtWave, () => waveOpts, "", () => syncing, v => syncing = v, UpdatePreview);

        void ConfirmClose() { var r = txtOut.Text?.Trim() ?? ""; if (!string.IsNullOrWhiteSpace(r)) dlg.Close(r); }
        btnCancel.Click += (_, _) => dlg.Close(null);
        btnOk.Click += (_, _) => ConfirmClose();
        dlg.KeyDown += (_, e) =>
        {
            if (cmbRate.IsDropDownOpen || cmbCount.IsDropDownOpen || cmbWave.IsDropDownOpen) return;
            if (e.Key == Key.Enter) ConfirmClose(); else if (e.Key == Key.Escape) dlg.Close(null);
        };
        UpdatePreview();
        return await dlg.ShowDialog<string?>(ownerWin);
    }

    private static void WireWizardCombo(ComboBox? cmb, TextBox? txt, Func<List<string>> getItems, string clearSentinel,
        Func<bool> getSync, Action<bool> setSync, Action onUpdated)
    {
        if (cmb == null || txt == null) return;
        cmb.ItemsSource = getItems();
        cmb.DropDownOpened += (_, _) => { cmb.ItemsSource = getItems(); setSync(true); cmb.SelectedIndex = -1; setSync(false); };
        cmb.SelectionChanged += (_, _) =>
        {
            if (getSync() || cmb.SelectedItem == null) return;
            string sel = cmb.SelectedItem.ToString() ?? "";
            setSync(true);
            txt.Text = (!string.IsNullOrEmpty(clearSentinel) && sel == clearSentinel) ? "" : sel;
            cmb.SelectedIndex = -1;
            setSync(false);
            onUpdated();
        };
        txt.TextChanged += (_, _) => { if (!getSync()) onUpdated(); };
    }

    private async Task WorkerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // 1. 處理主程式發來的 Queue 指令
                while (_ticketQueue.Reader.TryRead(out var ticket))
                {
                    ExecuteCase(ticket);
                }

                // 2. 若處於連續採樣狀態，持續將樣本寫入驅動內部環形緩衝區
                if (IsSampling)
                {
                    AcquireElapsedToRingBuffer();
                    await Task.Delay(10, token);
                }
                else
                {
                    // 空閒時阻塞等待下一個 Ticket
                    var ticket = await _ticketQueue.Reader.ReadAsync(token);
                    ExecuteCase(ticket);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 容錯防禦
            }
        }
    }

    private void ExecuteCase(DriverTicket ticket)
    {
        string cmd = ticket.Command.Trim();

        switch (cmd)
        {
            case "Sample":
            case "AnalogInWaveForm":
            {
                var slice = ExtractRecentWaveform(ticket.Parameter);
                ticket.SetCompleted(DriverResponse.Ok(slice));
                break;
            }

            case "Start":
                IsSampling = true;
                _lastAcquireTimestamp = Stopwatch.GetTimestamp();
                _fractionalSamples = 0.0;
                WriteSamplesToRing(1);
                ticket.SetCompleted();
                break;

            case "Stop":
                if (IsSampling)
                {
                    AcquireElapsedToRingBuffer();
                    IsSampling = false;
                }
                ticket.SetCompleted();
                break;

            case "AnalogIn":
            {
                // 連續採樣中直接取環形緩衝區最新一筆；未啟動連續採樣則即時擷取一筆
                if (IsSampling)
                {
                    AcquireElapsedToRingBuffer();
                }
                double latest = ReadLatestSingleSample(acquireIfEmptyOrStopped: !IsSampling);
                InstantInputVal = latest;
                ticket.SetCompleted(DriverResponse.Ok(latest));
                break;
            }

            case "AnalogOut":
                var argsAo = ArgParaHelper.ParseKeyValueArgs(ticket.Parameter, out var posArgsAo);
                if (argsAo.TryGetValue("Value", out var vStr) && double.TryParse(vStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double vVal))
                {
                    InstantOutputVal = vVal;
                }
                else if (posArgsAo.Count > 0 && double.TryParse(posArgsAo[^1], NumberStyles.Any, CultureInfo.InvariantCulture, out double pVal))
                {
                    InstantOutputVal = pVal;
                }
                else
                {
                    var rawParts = ticket.Parameter.Split(',', StringSplitOptions.TrimEntries);
                    if (rawParts.Length > 0 && double.TryParse(rawParts[^1], NumberStyles.Any, CultureInfo.InvariantCulture, out double rVal))
                    {
                        InstantOutputVal = rVal;
                    }
                }
                ticket.SetCompleted();
                break;

            case "AnalogOutWaveForm":
                if (_engines != null && !string.IsNullOrEmpty(ticket.Parameter))
                {
                    try
                    {
                        var entry = _engines.Daq.GetWaveform(ticket.Parameter);
                        if (entry.Channels.TryGetValue(0, out var chData) && chData.Y.Length > 0)
                        {
                            InstantOutputVal = chData.Y[^1];
                        }
                    }
                    catch { }
                }
                ticket.SetCompleted();
                break;

            case "Config":
            {
                var args = ArgParaHelper.ParseKeyValueArgs(ticket.Parameter, out _);
                bool reallocate = false;
                if (args.TryGetValue("Rate", out var r) && double.TryParse(r, NumberStyles.Any, CultureInfo.InvariantCulture, out double dr) && dr > 0)
                {
                    SampleRate = dr;
                    reallocate = true;
                }
                if ((args.TryGetValue("Sec", out var sStr) || args.TryGetValue("Duration", out sStr) || args.TryGetValue("BufferSec", out sStr)) &&
                    double.TryParse(sStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double ds) && ds > 0)
                {
                    BufferSeconds = ds;
                    reallocate = true;
                }
                else if (args.TryGetValue("Count", out var c) && int.TryParse(c, out int dc) && dc > 0)
                {
                    BufferSeconds = dc / SampleRate;
                    reallocate = true;
                }
                if (args.TryGetValue("Wave", out var waveStr))
                {
                    ApplyWaveType(waveStr);
                }
                if (reallocate) AllocateRingBuffer();
                ticket.SetCompleted();
                break;
            }

            case "Close":
                Shutdown();
                ticket.SetCompleted();
                break;

            default:
                ticket.SetCompleted(DriverResponse.Ok());
                break;
        }

        DaqStateUpdated?.Invoke();
    }

    private double GenerateSingleSample(long index)
    {
        double dt = 1.0 / SampleRate;
        if (WaveType == MockWaveType.Sine)
        {
            double t = index * dt;
            return 512.0 + 511.0 * Math.Sin(2 * Math.PI * 5 * t);
        }
        if (WaveType == MockWaveType.Square)
        {
            return (index % 50 < 25) ? 1023.0 : 0.0;
        }
        if (WaveType == MockWaveType.PressCurve)
        {
            int cap = RingCapacity;
            double progress = (double)(index % cap) / cap;
            if (progress < 0.3) return progress / 0.3 * 800.0;
            if (progress < 0.7) return 800.0 + (progress - 0.3) / 0.4 * 223.0;
            return 1023.0 * (1.0 - (progress - 0.7) / 0.3);
        }
        // 循環三角波波型：最大值從 0 數到 1023 後再從 0 開始
        return index % 1024;
    }

    private void WriteSamplesToRing(int count)
    {
        if (count <= 0) return;
        lock (_ringLock)
        {
            int cap = _ringBuffer.Length;
            double last = InstantInputVal;
            for (int i = 0; i < count; i++)
            {
                last = GenerateSingleSample(_totalSampleIndex++);
                _ringBuffer[_ringWriteIndex] = last;
                _ringWriteIndex = (_ringWriteIndex + 1) % cap;
                if (_ringCount < cap) _ringCount++;
            }
            InstantInputVal = last;
        }
    }

    private void AcquireElapsedToRingBuffer()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastAcquireTimestamp == 0)
        {
            _lastAcquireTimestamp = now;
            return;
        }
        double elapsedSec = (double)(now - _lastAcquireTimestamp) / Stopwatch.Frequency;
        if (elapsedSec <= 0) return;
        _lastAcquireTimestamp = now;

        double total = elapsedSec * SampleRate + _fractionalSamples;
        int n = (int)Math.Floor(total);
        _fractionalSamples = total - n;
        if (n > 0)
        {
            // 單次補齊上限不超過整個環形緩衝區容量
            int cap = RingCapacity;
            if (n > cap)
            {
                _totalSampleIndex += (n - cap);
                n = cap;
            }
            WriteSamplesToRing(n);
            DaqStateUpdated?.Invoke();
        }
    }

    private double ReadLatestSingleSample(bool acquireIfEmptyOrStopped)
    {
        lock (_ringLock)
        {
            if (acquireIfEmptyOrStopped || _ringCount == 0)
            {
                double v = GenerateSingleSample(_totalSampleIndex++);
                int cap = _ringBuffer.Length;
                _ringBuffer[_ringWriteIndex] = v;
                _ringWriteIndex = (_ringWriteIndex + 1) % cap;
                if (_ringCount < cap) _ringCount++;
                InstantInputVal = v;
                return v;
            }
            int lastIdx = (_ringWriteIndex - 1 + _ringBuffer.Length) % _ringBuffer.Length;
            return _ringBuffer[lastIdx];
        }
    }

    private double ParseRequestedDurationSec(string? parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter)) return BufferSeconds;
        var args = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        if ((args.TryGetValue("Sec", out var sStr) || args.TryGetValue("Duration", out sStr)) &&
            double.TryParse(sStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double sKv) && sKv > 0)
        {
            return sKv;
        }
        // 正規化後格式為 "Daq0, 0.5, PressCurve" 或 "0.5"
        if (posArgs.Count >= 2 && double.TryParse(posArgs[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double sPos1) && sPos1 > 0)
        {
            return sPos1;
        }
        if (posArgs.Count == 1 && double.TryParse(posArgs[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double sPos0) && sPos0 > 0)
        {
            return sPos0;
        }
        return BufferSeconds;
    }

    private double[] ExtractRecentWaveform(string? parameter)
    {
        double reqSec = ParseRequestedDurationSec(parameter);
        int requested = Math.Clamp((int)Math.Round(reqSec * SampleRate), 1, RingCapacity);

        if (IsSampling)
        {
            AcquireElapsedToRingBuffer();
        }

        double[] slice;
        lock (_ringLock)
        {
            if (!IsSampling && _ringCount < requested)
            {
                int need = requested - _ringCount;
                int cap = _ringBuffer.Length;
                for (int i = 0; i < need; i++)
                {
                    double v = GenerateSingleSample(_totalSampleIndex++);
                    _ringBuffer[_ringWriteIndex] = v;
                    _ringWriteIndex = (_ringWriteIndex + 1) % cap;
                    if (_ringCount < cap) _ringCount++;
                }
            }

            int take = Math.Min(requested, _ringCount);
            if (take <= 0)
            {
                double v = GenerateSingleSample(_totalSampleIndex++);
                slice = new[] { v };
            }
            else
            {
                slice = new double[take];
                int cap = _ringBuffer.Length;
                int startIdx = (_ringWriteIndex - take + cap) % cap;
                for (int i = 0; i < take; i++)
                {
                    slice[i] = _ringBuffer[(startIdx + i) % cap];
                }
            }
            InstantInputVal = slice[^1];
        }

        LatestWaveform = slice;
        double dt = 1.0 / SampleRate;
        if (_engines != null)
        {
            _engines.Daq.SetWaveform(DeviceName, DateTime.Now, dt, slice);
            _engines.Io.SetPort($"{DeviceName}#OK?", true);
        }
        return slice;
    }

    public void Shutdown()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        Shutdown();
        _cts?.Dispose();
    }
}
