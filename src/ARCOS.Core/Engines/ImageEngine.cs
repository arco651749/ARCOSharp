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
    private readonly ConcurrentDictionary<string, ImaqCalibrationData> _cameraDefaultCalibs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _userDisplayBindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _userDisplayControls = new();
    private readonly IoEngine? _ioEngine;
    private bool _disposed = false;

    private static readonly Regex FrameIndexRegex = new(@"^([a-zA-Z0-9_]+)\[(\d+)\]$", RegexOptions.Compiled);

    /// <summary>
    /// 當影像更新時通知 UI 刷新畫面：(ImaqName, CurrentIndex)
    /// </summary>
    public event Action<string, int>? ImageUpdated;

    /// <summary>
    /// 當腳本執行 Capture@Display2User::$cam, $control 時通知 UI 直接更新目標 Image 控制項：(CamName, ControlName)
    /// </summary>
    public event Action<string, string>? UserDisplayRequested;

    public ImageEngine(IoEngine? ioEngine = null)
    {
        _ioEngine = ioEngine;
    }

    private ImaqFrame BuildFrameWithDefaults(
        string channelName,
        Mat image,
        IReadOnlyDictionary<string, string>? metadata = null,
        ImaqCalibrationData? calibration = null,
        string? roi = null,
        IEnumerable<OverlayShape>? overlays = null,
        DateTime? timestamp = null)
    {
        var frame = new ImaqFrame(image.Clone(), timestamp ?? DateTime.Now);

        // 若未單獨指定校正資料，取像時預設繼承該相機/通道的校正設定；每張圖仍各自持有獨立複本。
        if (calibration != null)
        {
            frame.Calibration = calibration.Clone();
        }
        else if (_cameraDefaultCalibs.TryGetValue(channelName, out var defaultCalib))
        {
            frame.Calibration = defaultCalib.Clone();
        }

        if (metadata != null)
        {
            foreach (var kv in metadata)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key))
                    frame.Metadata[kv.Key.Trim()] = kv.Value ?? string.Empty;
            }
        }

        if (!string.IsNullOrEmpty(roi))
            frame.RoiString = roi;

        if (overlays != null)
        {
            foreach (var ov in overlays)
                frame.Overlays.Add(ov.Clone());
        }

        return frame;
    }

    /// <summary>
    /// 覆寫單張影像（自動安全 Dispose 舊圖），更新時間戳並設 $(name)#OK? = true
    /// </summary>
    public void SetSingleImage(string name, Mat image) =>
        SetSingleImage(name, image, null, null, null, null, null);

    public void SetSingleImage(
        string name,
        Mat image,
        IReadOnlyDictionary<string, string>? metadata,
        ImaqCalibrationData? calibration = null,
        string? roi = null,
        IEnumerable<OverlayShape>? overlays = null,
        DateTime? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(name) || image == null) return;

        lock (_lock)
        {
            var entry = _imaqEntries.GetOrAdd(name, _ => new ImaqArrayEntry());
            entry.Clear(); // 釋放舊圖

            var frame = BuildFrameWithDefaults(name, image, metadata, calibration, roi, overlays, timestamp);
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
    public void AppendImage(string arrayName, Mat image) =>
        AppendImage(arrayName, image, null, null, null, null, null);

    public void AppendImage(
        string arrayName,
        Mat image,
        IReadOnlyDictionary<string, string>? metadata,
        ImaqCalibrationData? calibration = null,
        string? roi = null,
        IEnumerable<OverlayShape>? overlays = null,
        DateTime? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(arrayName) || image == null) return;

        lock (_lock)
        {
            var entry = _imaqEntries.GetOrAdd(arrayName, _ => new ImaqArrayEntry());
            var frame = BuildFrameWithDefaults(arrayName, image, metadata, calibration, roi, overlays, timestamp);
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
    /// 批次將符合 pattern 的圖檔載入至影像陣列 (對應 Capture@LoadFile2Imaq)，並各自還原每張圖的 PNG Metadata
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
                    var (mat, metaPayload) = DecodeImageFileWithMetadata(f);
                    using (mat)
                    {
                        if (!mat.Empty())
                        {
                            AppendImage(
                                arrayName,
                                mat,
                                metaPayload?.Metadata,
                                metaPayload?.Calibration,
                                metaPayload?.RoiString,
                                metaPayload?.Overlays,
                                metaPayload?.ParsedTimestamp);
                        }
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// 從影像陣列載入下一張影像至指定攝影機記憶體 (對應 Capture@LoadNextImaq)，完整保留時間戳、ROI、Overlays、校正與 Metadata
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
                    SetSingleImage(
                        camName,
                        currentFrame.Image,
                        currentFrame.Metadata,
                        currentFrame.Calibration,
                        currentFrame.RoiString,
                        currentFrame.Overlays,
                        currentFrame.Timestamp);
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

    public void SetMetadata(string name, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(key)) return;
        lock (_lock)
        {
            if (_imaqEntries.TryGetValue(name, out var entry) && entry.CurrentFrame != null)
            {
                entry.CurrentFrame.Metadata[key.Trim()] = value ?? string.Empty;
            }
        }
    }

    public IReadOnlyDictionary<string, string> GetMetadata(string name)
    {
        lock (_lock)
        {
            if (_imaqEntries.TryGetValue(name, out var entry) && entry.CurrentFrame != null)
            {
                return new Dictionary<string, string>(entry.CurrentFrame.Metadata, StringComparer.OrdinalIgnoreCase);
            }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 設定相機或目前影像的空間校正資訊（設定相機預設校正，並可選擇同步套用至目前幀）。
    /// </summary>
    public void SetCalibration(string name, ImaqCalibrationData calibration, bool applyToCurrentFrame = true)
    {
        if (string.IsNullOrWhiteSpace(name) || calibration == null) return;

        lock (_lock)
        {
            _cameraDefaultCalibs[name] = calibration.Clone();
            if (calibration.ReferencePoints.Count > 0)
            {
                _calibCoords[name] = calibration.ReferencePoints.Select(p => p.Clone()).ToList();
            }
            if (applyToCurrentFrame && _imaqEntries.TryGetValue(name, out var entry) && entry.CurrentFrame != null)
            {
                entry.CurrentFrame.Calibration = calibration.Clone();
            }
        }
    }

    public void SetManualCalibration(string name, double scaleMmPerPx, double originPxX = 0, double originPxY = 0, double angleDeg = 0, string method = "Manual")
    {
        var calib = new ImaqCalibrationData
        {
            IsCalibrated = scaleMmPerPx > 0,
            ScaleMmPerPx = scaleMmPerPx > 0 ? scaleMmPerPx : 1.0,
            OriginPixelX = originPxX,
            OriginPixelY = originPxY,
            AngleDeg = angleDeg,
            Method = method
        };
        SetCalibration(name, calib, applyToCurrentFrame: true);
    }

    public ImaqCalibrationData? GetCalibration(string name)
    {
        lock (_lock)
        {
            if (_imaqEntries.TryGetValue(name, out var entry) && entry.CurrentFrame?.Calibration != null)
            {
                return entry.CurrentFrame.Calibration.Clone();
            }
            if (_cameraDefaultCalibs.TryGetValue(name, out var defCalib))
            {
                return defCalib.Clone();
            }
            return null;
        }
    }

    /// <summary>
    /// 從 PNG 圖檔中的 ARCOS_IMAQ_META 區塊載入並套用空間校正資訊至目標相機 (對應 Capture@CalibrateLoad)
    /// </summary>
    public bool LoadCalibrationFromFile(string name, string filePath)
    {
        if (string.IsNullOrWhiteSpace(name) || !File.Exists(filePath)) return false;
        try
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            var payload = ImaqPngMetadataCodec.ExtractMetadata(bytes);
            if (payload?.Calibration != null && payload.Calibration.IsCalibrated)
            {
                SetCalibration(name, payload.Calibration, applyToCurrentFrame: true);
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 設定影像空間校正座標系 (對應 Capture@CoordSet)
    /// </summary>
    public void SetCoord(string name, IReadOnlyList<MachinePoint> refPoints)
    {
        if (refPoints != null)
        {
            var cloned = refPoints.Select(p => p.Clone()).ToList();
            _calibCoords[name] = cloned;

            if (cloned.Count >= 2)
            {
                var p0 = cloned[0];
                var p1 = cloned[1];
                double dxPx = p1.X - p0.X;
                double dyPx = p1.Y - p0.Y;
                double distPx = Math.Sqrt(dxPx * dxPx + dyPx * dyPx);
                double scale = p1.Z > 0 ? p1.Z : 0.05;
                var calib = new ImaqCalibrationData
                {
                    IsCalibrated = distPx > 0.001,
                    ScaleMmPerPx = scale,
                    OriginPixelX = p0.X,
                    OriginPixelY = p0.Y,
                    AngleDeg = Math.Round(Math.Atan2(dyPx, dxPx) * 180.0 / Math.PI, 4),
                    Method = "CoordSet",
                    ReferencePoints = cloned.Select(p => p.Clone()).ToList()
                };
                SetCalibration(name, calib, applyToCurrentFrame: true);
            }
        }
    }

    public IReadOnlyList<MachinePoint> GetCoord(string name)
    {
        var calib = GetCalibration(name);
        if (calib?.ReferencePoints.Count > 0)
            return calib.ReferencePoints;
        return _calibCoords.TryGetValue(name, out var list) ? list : Array.Empty<MachinePoint>();
    }

    /// <summary>
    /// 將影像像素座標 (px, py) 轉換為實際物理世界座標 (mm)。
    /// 優先採用目前影像幀自帶的 Calibration，若無則退回相機預設校正 / 座標系基準點。
    /// </summary>
    public (double WorldX, double WorldY, bool IsCalibrated) PixelToWorld(string name, double px, double py)
    {
        var calib = GetCalibration(name);
        if (calib != null && calib.IsCalibrated)
        {
            if (calib.ReferencePoints.Count >= 2)
            {
                var p0 = calib.ReferencePoints[0];
                var p1 = calib.ReferencePoints[1];
                double dxPx = p1.X - p0.X;
                double dyPx = p1.Y - p0.Y;
                double distPx = Math.Sqrt(dxPx * dxPx + dyPx * dyPx);
                if (distPx > 0.001)
                {
                    double scaleMmPerPx = p1.Z > 0 ? p1.Z : (calib.ScaleMmPerPx > 0 ? calib.ScaleMmPerPx : 0.05);
                    double wx = (px - p0.X) * scaleMmPerPx + calib.OriginWorldX;
                    double wy = (py - p0.Y) * scaleMmPerPx + calib.OriginWorldY;
                    return (Math.Round(wx, 3), Math.Round(wy, 3), true);
                }
            }

            if (calib.ScaleMmPerPx > 0)
            {
                double dx = px - calib.OriginPixelX;
                double dy = py - calib.OriginPixelY;
                if (Math.Abs(calib.AngleDeg) > 1e-6)
                {
                    double rad = -calib.AngleDeg * Math.PI / 180.0;
                    double cos = Math.Cos(rad);
                    double sin = Math.Sin(rad);
                    double rx = dx * cos - dy * sin;
                    double ry = dx * sin + dy * cos;
                    dx = rx;
                    dy = ry;
                }
                double wx = dx * calib.ScaleMmPerPx + calib.OriginWorldX;
                double wy = dy * calib.ScaleMmPerPx + calib.OriginWorldY;
                return (Math.Round(wx, 3), Math.Round(wy, 3), true);
            }
        }

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
    /// 儲存影像至硬碟 (對應 Capture@ImageSave)。
    /// 未指定副檔名時預設為 .png，並於 .png 中無損嵌入 ARCOS_IMAQ_META (Timestamp/ROI/Overlays/Calibration/Metadata)。
    /// </summary>
    public void SaveImage(string name, string filePath, bool includeOverlays = false) =>
        SaveImageWithMetadata(name, filePath, includeOverlays, includeMetadata: true);

    public string SaveImageWithMetadata(string name, string filePath, bool includeOverlays = false, bool includeMetadata = true)
    {
        var frame = GetFrame(name);
        string resolvedPath = string.IsNullOrWhiteSpace(Path.GetExtension(filePath))
            ? $"{filePath}.png"
            : filePath;

        string dir = Path.GetDirectoryName(resolvedPath) ?? "";
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using Mat? renderedCanvas = (includeOverlays && frame.Overlays.Count > 0) ? frame.Image.Clone() : null;
        if (renderedCanvas != null)
        {
            RenderOverlaysOnMat(renderedCanvas, frame.Overlays);
        }
        Mat matToSave = renderedCanvas ?? frame.Image;

        string ext = Path.GetExtension(resolvedPath);
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            Cv2.ImEncode(".png", matToSave, out byte[] rawPng);
            if (includeMetadata)
            {
                var payload = new ImaqPngMetadataPayload
                {
                    Version = 1,
                    TimestampIso = frame.Timestamp.ToString("O"),
                    RoiString = frame.RoiString,
                    Overlays = frame.Overlays.Select(o => o.Clone()).ToList(),
                    Calibration = frame.Calibration?.Clone(),
                    Metadata = new Dictionary<string, string>(frame.Metadata, StringComparer.OrdinalIgnoreCase)
                };
                rawPng = ImaqPngMetadataCodec.EmbedMetadata(rawPng, payload);
            }
            File.WriteAllBytes(resolvedPath, rawPng);
        }
        else
        {
            Cv2.ImWrite(resolvedPath, matToSave);
        }

        return resolvedPath;
    }

    /// <summary>
    /// 從硬碟載入圖檔作為虛擬取像 (對應 Capture@LoadFile)，若為帶 ARCOS_IMAQ_META 的 PNG 則自動還原於該幀上
    /// </summary>
    public void LoadImage(string name, string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Image file '{filePath}' not found.");

        var (mat, metaPayload) = DecodeImageFileWithMetadata(filePath);
        using (mat)
        {
            if (mat.Empty())
                throw new InvalidOperationException($"Failed to decode image from '{filePath}'.");

            SetSingleImage(
                name,
                mat,
                metaPayload?.Metadata,
                metaPayload?.Calibration,
                metaPayload?.RoiString,
                metaPayload?.Overlays,
                metaPayload?.ParsedTimestamp);
        }
    }

    private static (Mat Image, ImaqPngMetadataPayload? Payload) DecodeImageFileWithMetadata(string filePath)
    {
        var mat = Cv2.ImRead(filePath, ImreadModes.Color);
        ImaqPngMetadataPayload? payload = null;
        if (!mat.Empty() && Path.GetExtension(filePath).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                payload = ImaqPngMetadataCodec.ExtractMetadata(File.ReadAllBytes(filePath));
            }
            catch { }
        }
        return (mat, payload);
    }

    public IReadOnlyList<string> GetAllNames()
    {
        return _imaqEntries.Keys.ToList();
    }

    /// <summary>
    /// 設定目前 UserPanel.csvi 上自動掃描到的可顯示影像控制項名稱清單 (供 Display2User Wizard 使用)
    /// </summary>
    public void SetUserDisplayControls(IEnumerable<string> controlNames)
    {
        lock (_lock)
        {
            _userDisplayControls.Clear();
            if (controlNames != null)
            {
                foreach (var name in controlNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    _userDisplayControls.Add(name.Trim());
                }
            }
        }
    }

    /// <summary>
    /// 取得目前 UserPanel.csvi 上可顯示影像的控制項名稱清單
    /// </summary>
    public IReadOnlyList<string> GetUserDisplayControls()
    {
        lock (_lock)
        {
            return _userDisplayControls.ToList();
        }
    }

    /// <summary>
    /// 將指定攝影機/影像名稱綁定並推送顯示至 UserPanel 上的指定控制項 (對應 Capture@Display2User::$cam, $control)
    /// </summary>
    public void DisplayToUserControl(string camName, string controlName)
    {
        if (string.IsNullOrWhiteSpace(camName) || string.IsNullOrWhiteSpace(controlName)) return;
        string cam = camName.Trim();
        string ctrl = controlName.Trim();
        _userDisplayBindings[ctrl] = cam;
        UserDisplayRequested?.Invoke(cam, ctrl);
    }

    /// <summary>
    /// 查詢指定 UserPanel 控制項最後綁定的攝影機名稱
    /// </summary>
    public string? GetBoundCameraForControl(string controlName)
    {
        if (string.IsNullOrWhiteSpace(controlName)) return null;
        return _userDisplayBindings.TryGetValue(controlName.Trim(), out var cam) ? cam : null;
    }

    /// <summary>
    /// 將指定攝影機目前畫面連同非破壞性 Overlay 標註與 ROI 框渲染為 PNG byte[]，供 UI Image 控制項直接轉為 Bitmap 顯示
    /// </summary>
    public byte[]? RenderFrameWithOverlaysToPngBytes(string name)
    {
        try
        {
            ImaqFrame? frame = null;
            try
            {
                frame = GetFrame(name);
            }
            catch (KeyNotFoundException)
            {
                using var synth = new Mat(240, 320, MatType.CV_8UC3, new Scalar(30, 41, 59));
                Cv2.Circle(synth, new Point(160, 120), 45, new Scalar(16, 185, 129), 2);
                Cv2.Line(synth, new Point(100, 120), new Point(220, 120), new Scalar(56, 189, 248), 1);
                Cv2.Line(synth, new Point(160, 60), new Point(160, 180), new Scalar(56, 189, 248), 1);
                Cv2.PutText(synth, $"CAM: {name}", new Point(16, 28), HersheyFonts.HersheySimplex, 0.6, new Scalar(248, 250, 252), 1);
                SetSingleImage(name, synth);
                frame = GetFrame(name);
            }

            if (frame?.Image == null || frame.Image.Empty()) return null;

            using var canvas = frame.Image.Clone();
            RenderOverlaysOnMat(canvas, frame.Overlays);

            Cv2.ImEncode(".png", canvas, out byte[] buf);
            return buf;
        }
        catch
        {
            return null;
        }
    }

    private static void RenderOverlaysOnMat(Mat canvas, IEnumerable<OverlayShape> overlays)
    {
        foreach (var overlay in overlays)
        {
            var color = ParseOverlayColor(overlay.ColorHex);
            int thickness = Math.Max(1, overlay.Thickness);
            switch (overlay.ShapeType)
            {
                case OverlayShapeType.Circle:
                    Cv2.Circle(canvas, new Point((int)overlay.X1, (int)overlay.Y1), (int)overlay.Radius, color, thickness);
                    break;
                case OverlayShapeType.Line:
                    Cv2.Line(canvas, new Point((int)overlay.X1, (int)overlay.Y1), new Point((int)overlay.X2, (int)overlay.Y2), color, thickness);
                    break;
                case OverlayShapeType.Rectangle:
                {
                    int x = (int)Math.Min(overlay.X1, overlay.X2);
                    int y = (int)Math.Min(overlay.Y1, overlay.Y2);
                    int w = Math.Max(1, (int)Math.Abs(overlay.X2 - overlay.X1));
                    int h = Math.Max(1, (int)Math.Abs(overlay.Y2 - overlay.Y1));
                    Cv2.Rectangle(canvas, new OpenCvSharp.Rect(x, y, w, h), color, thickness);
                    break;
                }
                case OverlayShapeType.Crosshair:
                {
                    int arm = 15;
                    Cv2.Line(canvas, new Point((int)overlay.X1 - arm, (int)overlay.Y1), new Point((int)overlay.X1 + arm, (int)overlay.Y1), color, thickness);
                    Cv2.Line(canvas, new Point((int)overlay.X1, (int)overlay.Y1 - arm), new Point((int)overlay.X1, (int)overlay.Y1 + arm), color, thickness);
                    break;
                }
                case OverlayShapeType.Text:
                    Cv2.PutText(canvas, overlay.Text, new Point((int)overlay.X1, (int)overlay.Y1), HersheyFonts.HersheySimplex, 0.8, color, thickness);
                    break;
            }
        }
    }

    private static Scalar ParseOverlayColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            string h = hex.Trim().TrimStart('#');
            if (h.Length == 6 &&
                byte.TryParse(h[0..2], System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
                byte.TryParse(h[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
                byte.TryParse(h[4..6], System.Globalization.NumberStyles.HexNumber, null, out byte b))
            {
                return Scalar.FromRgb(r, g, b);
            }
        }
        return Scalar.FromRgb(0, 255, 0);
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
