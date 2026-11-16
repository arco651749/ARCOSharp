using System.Text.Json.Serialization;
using OpenCvSharp;

namespace ARCOS.Core.Model;

public enum OverlayShapeType
{
    Crosshair,
    Circle,
    Line,
    Rectangle,
    Text
}

public class OverlayShape
{
    public OverlayShapeType ShapeType { get; set; }
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public double Radius { get; set; }
    public string Text { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#00FF00"; // 預設綠色
    public int Thickness { get; set; } = 1;

    public OverlayShape Clone() => new()
    {
        ShapeType = ShapeType,
        X1 = X1,
        Y1 = Y1,
        X2 = X2,
        Y2 = Y2,
        Radius = Radius,
        Text = Text,
        ColorHex = ColorHex,
        Thickness = Thickness
    };
}

/// <summary>
/// 影像空間校正資訊 (對應 LabVIEW IMAQ Spatial Calibration Metadata)
/// </summary>
public class ImaqCalibrationData
{
    public bool IsCalibrated { get; set; }
    public double ScaleMmPerPx { get; set; } = 1.0;
    public double OriginPixelX { get; set; }
    public double OriginPixelY { get; set; }
    public double OriginWorldX { get; set; }
    public double OriginWorldY { get; set; }
    public double AngleDeg { get; set; }
    public string Unit { get; set; } = "mm";
    public string Method { get; set; } = "Manual";
    public List<MachinePoint> ReferencePoints { get; set; } = new();

    public ImaqCalibrationData Clone() => new()
    {
        IsCalibrated = IsCalibrated,
        ScaleMmPerPx = ScaleMmPerPx,
        OriginPixelX = OriginPixelX,
        OriginPixelY = OriginPixelY,
        OriginWorldX = OriginWorldX,
        OriginWorldY = OriginWorldY,
        AngleDeg = AngleDeg,
        Unit = Unit,
        Method = Method,
        ReferencePoints = ReferencePoints.Select(p => p.Clone()).ToList()
    };
}

/// <summary>
/// PNG 內嵌 Vision Metadata JSON 負載 (Chunk Keyword: ARCOS_IMAQ_META)
/// </summary>
public sealed class ImaqPngMetadataPayload
{
    public int Version { get; set; } = 1;
    public string TimestampIso { get; set; } = string.Empty;
    public string RoiString { get; set; } = string.Empty;
    public List<OverlayShape> Overlays { get; set; } = new();
    public ImaqCalibrationData? Calibration { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public DateTime? ParsedTimestamp =>
        DateTime.TryParse(TimestampIso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : null;
}

/// <summary>
/// 影像幀 (ImaqFrame)
/// 包含原生 Mat、毫秒級時間戳記、非破壞性向量疊加層 (Overlays)、ROI、空間校正資訊與自訂 Metadata 字典
/// </summary>
public class ImaqFrame : IDisposable
{
    private Mat? _image;
    private bool _disposed = false;

    public Mat Image
    {
        get => _image ?? throw new ObjectDisposedException(nameof(ImaqFrame));
        set => _image = value;
    }

    public DateTime Timestamp { get; set; } = DateTime.Now;
    public List<OverlayShape> Overlays { get; set; } = new();
    public string RoiString { get; set; } = string.Empty;
    public ImaqCalibrationData? Calibration { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ImaqFrame(Mat image, DateTime? timestamp = null)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        Timestamp = timestamp ?? DateTime.Now;
    }

    public void CopyMetadataTo(ImaqFrame target)
    {
        if (target == null) return;
        target.Timestamp = Timestamp;
        target.RoiString = RoiString;
        target.Overlays = Overlays.Select(o => o.Clone()).ToList();
        target.Calibration = Calibration?.Clone();
        target.Metadata = new Dictionary<string, string>(Metadata, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _image?.Dispose();
            _image = null;
            _disposed = true;
        }
    }
}

public class ImaqArrayEntry : IDisposable
{
    public List<ImaqFrame> Frames { get; } = new();
    public int CurrentIndex { get; set; } = 0;

    public ImaqFrame? CurrentFrame
    {
        get
        {
            if (Frames.Count == 0) return null;
            int idx = Math.Clamp(CurrentIndex, 0, Frames.Count - 1);
            return Frames[idx];
        }
    }

    public void Clear()
    {
        foreach (var frame in Frames)
        {
            frame.Dispose();
        }
        Frames.Clear();
        CurrentIndex = 0;
    }

    public void Dispose()
    {
        Clear();
    }
}
