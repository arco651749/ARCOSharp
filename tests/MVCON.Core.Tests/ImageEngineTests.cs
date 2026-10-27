using MVCON.Core.Engines;
using MVCON.Core.Model;
using OpenCvSharp;
using Xunit;

namespace MVCON.Core.Tests;

public class ImageEngineTests
{
    [Fact]
    public void SetSingleImage_ShouldUpdateFrameAndIoFlags()
    {
        var io = new IoEngine();
        using var imaq = new ImageEngine(io);

        using var mat = new Mat(50, 50, MatType.CV_8UC3, Scalar.All(255));
        imaq.SetSingleImage("Cam0", mat);

        Assert.True(io.GetPort("Cam0#OK?"));
        Assert.True(io.GetPort("Cam0#Last?"));

        var frame = imaq.GetFrame("Cam0");
        Assert.NotNull(frame);
        Assert.Equal(50, frame.Image.Width);
        Assert.Equal(50, frame.Image.Height);
    }

    [Fact]
    public void AppendImage_ShouldSupportFlyCaptureArray()
    {
        var io = new IoEngine();
        using var imaq = new ImageEngine(io);

        using var mat1 = new Mat(30, 30, MatType.CV_8UC1, Scalar.All(10));
        using var mat2 = new Mat(30, 30, MatType.CV_8UC1, Scalar.All(20));

        imaq.AppendImage("FlyArr", mat1);
        imaq.AppendImage("FlyArr", mat2);

        var array = imaq.GetFrameArray("FlyArr");
        Assert.Equal(2, array.Count);

        var f0 = imaq.GetFrame("FlyArr[0]");
        var f1 = imaq.GetFrame("FlyArr[1]");
        Assert.NotNull(f0);
        Assert.NotNull(f1);
    }

    [Fact]
    public void Overlays_AddAndClear_ShouldNotCorruptOriginalMat()
    {
        using var imaq = new ImageEngine();
        using var mat = new Mat(40, 40, MatType.CV_8UC3, Scalar.All(0));

        imaq.SetSingleImage("Cam1", mat);
        imaq.AddOverlay("Cam1", new OverlayShape
        {
            ShapeType = OverlayShapeType.Circle,
            X1 = 20,
            Y1 = 20,
            Radius = 10,
            ColorHex = "#FF0000"
        });

        var frame = imaq.GetFrame("Cam1");
        Assert.Single(frame.Overlays);

        imaq.ClearOverlays("Cam1");
        Assert.Empty(frame.Overlays);
    }

    [Fact]
    public void ClearImaq_ShouldDisposeAllFramesAndResetIoFlags()
    {
        var io = new IoEngine();
        using var imaq = new ImageEngine(io);

        using var mat = new Mat(20, 20, MatType.CV_8UC3, Scalar.All(100));
        imaq.SetSingleImage("Cam2", mat);
        Assert.True(io.GetPort("Cam2#OK?"));

        imaq.ClearImaq("Cam2");
        Assert.False(io.GetPort("Cam2#OK?"));
        Assert.False(io.GetPort("Cam2#Last?"));
        Assert.Empty(imaq.GetFrameArray("Cam2"));
    }
}
