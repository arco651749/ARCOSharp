using ARCOS.Core.Engines;
using ARCOS.Core.Model;
using OpenCvSharp;
using Xunit;

namespace ARCOS.Core.Tests;

public class ImagePngMetadataTests
{
    [Fact]
    public void SaveAndLoadPng_PreservesPurePixelsAndRestoresOverlaysRoiCalibrationAndMetadata()
    {
        var io = new IoEngine();
        using var engine = new ImageEngine(io);
        string tempDir = Path.Combine(Path.GetTempPath(), $"arcos_png_meta_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            using var rawMat = new Mat(120, 160, MatType.CV_8UC3, new Scalar(15, 25, 35));
            var ts = new DateTime(2026, 11, 16, 13, 20, 45, 321);
            var meta = new Dictionary<string, string>
            {
                ["Driver"] = "DirectShowCamera",
                ["CameraName"] = "Integrated Test Cam",
                ["Exposure"] = "-5"
            };
            var overlays = new[]
            {
                new OverlayShape { ShapeType = OverlayShapeType.Circle, X1 = 80, Y1 = 60, Radius = 25, ColorHex = "#FF0000", Thickness = 2 },
                new OverlayShape { ShapeType = OverlayShapeType.Rectangle, X1 = 10, Y1 = 10, X2 = 50, Y2 = 40, ColorHex = "#00FF00", Thickness = 1 },
                new OverlayShape { ShapeType = OverlayShapeType.Text, X1 = 12, Y1 = 90, Text = "PASS", ColorHex = "#38BDF8", Thickness = 1 }
            };

            engine.SetManualCalibration("CamA", scaleMmPerPx: 0.05, originPxX: 80, originPxY: 60, angleDeg: 0, method: "Manual2D");
            engine.SetSingleImage("CamA", rawMat, meta, calibration: null, roi: "Rect(10,10,40,30)", overlays: overlays, timestamp: ts);

            // Save without extension -> auto-appends .png, keeps raw pixels clean, embeds ARCOS_IMAQ_META chunk.
            string basePath = Path.Combine(tempDir, "inspection_result");
            string savedPath = engine.SaveImageWithMetadata("CamA", basePath, includeOverlays: false, includeMetadata: true);
            Assert.EndsWith(".png", savedPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(savedPath));

            // Standard OpenCV ImRead must decode cleanly AND original pixels remain untouched (not overwritten by overlays).
            using (var decoded = Cv2.ImRead(savedPath, ImreadModes.Color))
            {
                var px = decoded.At<Vec3b>(60, 105); // on red circle boundary if burned in, otherwise original (15,25,35)
                Assert.Equal(new Vec3b(15, 25, 35), px);
            }

            // Extract raw PNG iTXt chunk directly
            var extracted = ImaqPngMetadataCodec.ExtractMetadata(File.ReadAllBytes(savedPath));
            Assert.NotNull(extracted);
            Assert.Equal("Rect(10,10,40,30)", extracted!.RoiString);
            Assert.Equal(3, extracted.Overlays.Count);
            Assert.True(extracted.Calibration?.IsCalibrated);
            Assert.Equal(0.05, extracted.Calibration!.ScaleMmPerPx, 6);
            Assert.Equal("DirectShowCamera", extracted.Metadata["Driver"]);

            // Load into a fresh ImageEngine channel -> restores every per-frame property independently
            using var engine2 = new ImageEngine(new IoEngine());
            engine2.LoadImage("OfflineCam", savedPath);

            var restored = engine2.GetFrame("OfflineCam");
            Assert.Equal(ts, restored.Timestamp);
            Assert.Equal("Rect(10,10,40,30)", restored.RoiString);
            Assert.Equal(3, restored.Overlays.Count);
            Assert.Equal(OverlayShapeType.Rectangle, restored.Overlays[1].ShapeType);
            Assert.Equal("DirectShowCamera", restored.Metadata["Driver"]);
            Assert.Equal("-5", restored.Metadata["Exposure"]);

            // PixelToWorld must immediately use restored frame's calibration
            var (wx, wy, isCal) = engine2.PixelToWorld("OfflineCam", 100, 80); // (+20 px, +20 px) * 0.05 = (1.0, 1.0)
            Assert.True(isCal);
            Assert.Equal(1.0, wx, 3);
            Assert.Equal(1.0, wy, 3);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PerFrameCalibration_DoesNotPolluteAcrossFramesWhenSwitchingIndex()
    {
        using var engine = new ImageEngine(new IoEngine());
        using var mat = new Mat(64, 64, MatType.CV_8UC3, new Scalar(0, 0, 0));

        var cal1 = new ImaqCalibrationData { IsCalibrated = true, ScaleMmPerPx = 0.1, Method = "Lens1x" };
        var cal2 = new ImaqCalibrationData { IsCalibrated = true, ScaleMmPerPx = 0.02, Method = "Lens5x" };

        engine.AppendImage("FlyArr", mat, metadata: new Dictionary<string, string> { ["Shot"] = "1" }, calibration: cal1);
        engine.AppendImage("FlyArr", mat, metadata: new Dictionary<string, string> { ["Shot"] = "2" }, calibration: cal2);

        engine.SetIndex("FlyArr", 0);
        var (wx0, _, ok0) = engine.PixelToWorld("FlyArr", 10, 0);
        Assert.True(ok0);
        Assert.Equal(1.0, wx0, 3);
        Assert.Equal("1", engine.GetMetadata("FlyArr")["Shot"]);

        engine.SetIndex("FlyArr", 1);
        var (wx1, _, ok1) = engine.PixelToWorld("FlyArr", 10, 0);
        Assert.True(ok1);
        Assert.Equal(0.2, wx1, 3);
        Assert.Equal("2", engine.GetMetadata("FlyArr")["Shot"]);
    }
}
