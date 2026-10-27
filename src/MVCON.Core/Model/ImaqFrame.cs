using OpenCvSharp;

namespace MVCON.Core.Model;

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
}

/// <summary>
/// 影像幀 (ImaqFrame)
/// 包含原生 Mat、毫秒級時間戳記、非破壞性向量疊加層 (Overlays) 與 ROI 字串
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

    public ImaqFrame(Mat image, DateTime? timestamp = null)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        Timestamp = timestamp ?? DateTime.Now;
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
