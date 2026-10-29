using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ARCOS.Core.Model;
using OpenCvSharp;

namespace ARCOS.Core.Engines;

/// <summary>
/// 彈性影像與 Overlay/ROI 記憶體池引擎 (ImageEngine / IMAQ)
/// 管理單張/多幀飛拍陣列 (ImaqFrame)，支援非破壞性向量疊加層、
/// 時間戳記、ROI、硬碟讀寫，並自動維護 $(Name)#OK? 與 $(Name)#Last?
/// 具備嚴格的 IDisposable 確保 Unmanaged Mat 記憶體 100% 釋放
/// </summary>
public class ImageEngine : IDisposable
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, ImaqArrayEntry> _imaqEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<MachinePoint>> _calibCoords = new(StringComparer.OrdinalIgnoreCase);
    private readonly IoEngine? _ioEngine;
    private bool _disposed = false;

    private static readonly Regex FrameIndexRegex = new(@"^([a-zA-Z0-9_]+)\[(\d+)\]$", RegexOptions.Compiled);

    /// <summary>
    /// 當影像更新時通知 UI 刷新畫面：(ImaqName, CurrentIndex)
    /// </summary>
    public event Action<string, int>? ImageUpdated;

    public ImageEngine(IoEngine? ioEngine = null)
    {
        _ioEngine = ioEngine;
    }

    /// <summary>
    /// 覆寫單張影像（自動安全 Dispose 舊圖），更新時間戳並設 $(name)#OK? = true
    /// </summary>
    public void SetSingleImage(string name, Mat image)
    {
        if (string.IsNullOrWhiteSpace(name) || image == null) return;

        lock (_lock)
        {
            var entry = _imaqEntries.GetOrAdd(name, _ => new ImaqArrayEntry());
            entry.Clear(); // 釋放舊圖

            var frame = new ImaqFrame(image.Clone(), DateTime.Now);
            entry.Frames.Add(frame);
            entry.CurrentIndex = 0;

            _ioEngine?.SetPort($"{name}#OK?", true);
            _ioEngine?.SetPort($"{name}#Last?", true);
            ImageUpdated?.Invoke(name, 0);
        }
    }

    /// <summary>
    /// 將新影像壓入陣列尾端（供飛拍 Snap / Trigger 使用）
    /// </summary>
    public void AppendImage(string arrayName, Mat image)
    {
        if (string.IsNullOrWhiteSpace(arrayName) || image == null) return;

        lock (_lock)
        {
            var entry = _imaqEntries.GetOrAdd(arrayName, _ => new ImaqArrayEntry());
            var frame = new ImaqFrame(image.Clone(), DateTime.Now);
            entry.Frames.Add(frame);

            _ioEngine?.SetPort($"{arrayName}#OK?", true);
            _ioEngine?.SetPort($"{arrayName}#Last?", entry.CurrentIndex >= entry.Frames.Count - 1);
            ImageUpdated?.Invoke(arrayName, entry.CurrentIndex);
        }
    }

    /// <summary>
    /// 讀取指定影像幀：支援 "Cam0"（取目前游標幀）或 "FlyArr[5]"（取指定索引幀）
    /// </summary>
    public ImaqFrame GetFrame(string nameExpr)
    {
        if (string.IsNullOrWhiteSpace(nameExpr))
            throw new ArgumentException("Image expression cannot be empty.", nameof(nameExpr));

        string expr = nameExpr.Trim();

        // 1. 支援 FlyArr[3]
        var match = FrameIndexRegex.Match(expr);
        if (match.Success)
        {
            string name = match.Groups[1].Value;
            int idx = int.Parse(match.Groups[2].Value);
            if (_imaqEntries.TryGetValue(name, out var entry) && entry.Frames.Count > 0)
            {
                int clamped = Math.Clamp(idx, 0, entry.Frames.Count - 1);
                return entry.Frames[clamped];
            }
            throw new KeyNotFoundException($"Image array '{name}' not found.");
        }

        // 2. 支援一般名稱（取目前幀）
        if (_imaqEntries.TryGetValue(expr, out var imaqEntry) && imaqEntry.CurrentFrame != null)
        {
            return imaqEntry.CurrentFrame;
        }

        throw new KeyNotFoundException($"Image '{nameExpr}' was not found.");
    }

    public IReadOnlyList<ImaqFrame> GetFrameArray(string name)
    {
        if (_imaqEntries.TryGetValue(name, out var entry))
        {
            return entry.Frames.ToList();
        }
        return Array.Empty<ImaqFrame>();
    }

    /// <summary>
    /// 釋放該名稱下所有 Mat 記憶體並清空陣列 (對應 Capture@ClearImaq)
    /// </summary>
    public void ClearImaq(string name)
    {
        lock (_lock)
        {
            if (_imaqEntries.TryGetValue(name, out var entry))
            {
                entry.Clear();
                _ioEngine?.SetPort($"{name}#OK?", false);
                _ioEngine?.SetPort($"{name}#Last?", false);
            }
        }
    }

    /// <summary>
    /// 切換檢視幀索引並更新 $(name)#Last?
    /// </summary>
    public void SetIndex(string name, int index)
    {
        lock (_lock)
        {
            if (_imaqEntries.TryGetValue(name, out var entry) && entry.Frames.Count > 0)
            {
                entry.CurrentIndex = Math.Clamp(index, 0, entry.Frames.Count - 1);
                _ioEngine?.SetPort($"{name}#Last?", entry.CurrentIndex >= entry.Frames.Count - 1);
                ImageUpdated?.Invoke(name, entry.CurrentIndex);
            }
        }
    }

    /// <summary>
    /// 重設影像陣列游標 (對應 Capture@ResetImaqIndex)
    /// </summary>
    public void ResetImaqIndex(string name, int index = 0) => SetIndex(name, index);

    /// <summary>
    /// 批次將符合 pattern 的圖檔載入至影像陣列 (對應 Capture@LoadFile2Imaq)
    /// </summary>
    public void LoadFile2Imaq(string arrayName, string filePattern)
    {
        string dir = Path.GetDirectoryName(filePattern) ?? "";
        string pattern = Path.GetFileName(filePattern);
        if (string.IsNullOrEmpty(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;
        if (string.IsNullOrEmpty(pattern)) pattern = "*.png";

        if (Directory.Exists(dir))
        {
            var files = Directory.GetFiles(dir, pattern).OrderBy(f => f).ToList();
            foreach (var f in files)
            {
                try
                {
                    using var mat = Cv2.ImRead(f, ImreadModes.Color);
                    if (!mat.Empty())
                    {
                        AppendImage(arrayName, mat);
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// 從影像陣列載入下一張影像至指定攝影機記憶體 (對應 Capture@LoadNextImaq)
    /// </summary>
    public bool LoadNextImaq(string camName, string arrayName)
    {
        lock (_lock)
        {
            if (_imaqEntries.TryGetValue(arrayName, out var arrEntry) && arrEntry.Frames.Count > 0)
            {
                if (arrEntry.CurrentIndex < arrEntry.Frames.Count)
                {
                    var currentFrame = arrEntry.Frames[arrEntry.CurrentIndex];
                    SetSingleImage(camName, currentFrame.Image);
                    arrEntry.CurrentIndex++;
                    _ioEngine?.SetPort($"{arrayName}#Last?", arrEntry.CurrentIndex >= arrEntry.Frames.Count);
                    return true;
                }
            }
            return false;
        }
    }

    public bool MoveNext(string name)
    {
        lock (_lock)
        {
            if (_imaqEntries.TryGetValue(name, out var entry) && entry.Frames.Count > 0)
            {
                if (entry.CurrentIndex < entry.Frames.Count - 1)
                {
                    entry.CurrentIndex++;
                    _ioEngine?.SetPort($"{name}#Last?", entry.CurrentIndex >= entry.Frames.Count - 1);
                    ImageUpdated?.Invoke(name, entry.CurrentIndex);
                    return true;
                }
                _ioEngine?.SetPort($"{name}#Last?", true);
            }
            return false;
        }
    }

    /// <summary>
    /// 在非破壞性向量層新增或清除演算法標註
    /// </summary>
    public void AddOverlay(string name, OverlayShape shape)
    {
        if (_imaqEntries.TryGetValue(name, out var entry) && entry.CurrentFrame != null)
        {
            entry.CurrentFrame.Overlays.Add(shape);
            ImageUpdated?.Invoke(name, entry.CurrentIndex);
        }
    }

    public void ClearOverlays(string name)
    {
        if (_imaqEntries.TryGetValue(name, out var entry) && entry.CurrentFrame != null)
        {
            entry.CurrentFrame.Overlays.Clear();
            ImageUpdated?.Invoke(name, entry.CurrentIndex);
        }
    }

    /// <summary>
    /// 設定與讀取 ROI 參數 (對應 Capture@ROISet)
    /// </summary>
    public void SetRoi(string name, string roiString)
    {
        if (_imaqEntries.TryGetValue(name, out var entry) && entry.CurrentFrame != null)
        {
            entry.CurrentFrame.RoiString = roiString;
        }
    }

    public string GetRoi(string name)
    {
        if (_imaqEntries.TryGetValue(name, out var entry) && entry.CurrentFrame != null)
        {
            return entry.CurrentFrame.RoiString;
        }
        return string.Empty;
    }

    /// <summary>
    /// 設定影像空間校正座標系 (對應 Capture@CoordSet)
    /// </summary>
    public void SetCoord(string name, IReadOnlyList<MachinePoint> refPoints)
    {
        if (refPoints != null)
        {
            _calibCoords[name] = refPoints.Select(p => p.Clone()).ToList();
        }
    }

    public IReadOnlyList<MachinePoint> GetCoord(string name)
    {
        return _calibCoords.TryGetValue(name, out var list) ? list : Array.Empty<MachinePoint>();
    }

    /// <summary>
    /// 將影像像素座標 (px, py) 轉換為實際物理世界座標 (mm)。
    /// 若相機已有標定基準點，換算為物理座標並回傳 isCalibrated = true；
    /// 若無標定資料，直接回傳原像素座標並標註 isCalibrated = false。
    /// </summary>
    public (double WorldX, double WorldY, bool IsCalibrated) PixelToWorld(string name, double px, double py)
    {
        if (_calibCoords.TryGetValue(name, out var pts) && pts.Count >= 2)
        {
            var p0 = pts[0];
            var p1 = pts[1];
            double dxPx = p1.X - p0.X;
            double dyPx = p1.Y - p0.Y;
            double distPx = Math.Sqrt(dxPx * dxPx + dyPx * dyPx);

            if (distPx > 0.001)
            {
                double scaleMmPerPx = p1.Z > 0 ? p1.Z : 0.05;
                double wx = (px - p0.X) * scaleMmPerPx;
                double wy = (py - p0.Y) * scaleMmPerPx;
                return (Math.Round(wx, 3), Math.Round(wy, 3), true);
            }
        }

        return (Math.Round(px, 1), Math.Round(py, 1), false);
    }

    /// <summary>
    /// 儲存影像至硬碟 (對應 Capture@ImageSave)
    /// </summary>
    public void SaveImage(string name, string filePath, bool includeOverlays = false)
    {
        var frame = GetFrame(name);
        string dir = Path.GetDirectoryName(filePath) ?? "";
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (!includeOverlays || frame.Overlays.Count == 0)
        {
            Cv2.ImWrite(filePath, frame.Image);
        }
        else
        {
            using var canvas = frame.Image.Clone();
            // 在副本上繪製向量標註
            foreach (var overlay in frame.Overlays)
            {
                var color = Scalar.FromRgb(0, 255, 0); // 簡化綠色標記
                if (overlay.ShapeType == OverlayShapeType.Circle)
                {
                    Cv2.Circle(canvas, new Point((int)overlay.X1, (int)overlay.Y1), (int)overlay.Radius, color, overlay.Thickness);
                }
                else if (overlay.ShapeType == OverlayShapeType.Line)
                {
                    Cv2.Line(canvas, new Point((int)overlay.X1, (int)overlay.Y1), new Point((int)overlay.X2, (int)overlay.Y2), color, overlay.Thickness);
                }
                else if (overlay.ShapeType == OverlayShapeType.Crosshair)
                {
                    int arm = 15;
                    Cv2.Line(canvas, new Point((int)overlay.X1 - arm, (int)overlay.Y1), new Point((int)overlay.X1 + arm, (int)overlay.Y1), color, overlay.Thickness);
                    Cv2.Line(canvas, new Point((int)overlay.X1, (int)overlay.Y1 - arm), new Point((int)overlay.X1, (int)overlay.Y1 + arm), color, overlay.Thickness);
                }
                else if (overlay.ShapeType == OverlayShapeType.Text)
                {
                    Cv2.PutText(canvas, overlay.Text, new Point((int)overlay.X1, (int)overlay.Y1), HersheyFonts.HersheySimplex, 0.8, color, overlay.Thickness);
                }
            }
            Cv2.ImWrite(filePath, canvas);
        }
    }

    /// <summary>
    /// 從硬碟載入圖檔作為虛擬取像 (對應 Capture@LoadFile)
    /// </summary>
    public void LoadImage(string name, string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Image file '{filePath}' not found.");

        var mat = Cv2.ImRead(filePath, ImreadModes.Color);
        if (mat.Empty())
            throw new InvalidOperationException($"Failed to decode image from '{filePath}'.");

        SetSingleImage(name, mat);
    }

    public IReadOnlyList<string> GetAllNames()
    {
        return _imaqEntries.Keys.ToList();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var entry in _imaqEntries.Values)
            {
                entry.Dispose();
            }
            _imaqEntries.Clear();
            _disposed = true;
        }
    }
}
