using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using ARCOS.Driver.DirectShowCamera;
using Xunit;

namespace ARCOS.Drivers.Tests;

public class DirectShowCameraDriverTests
{
    private static BasicEngines CreateEngines()
    {
        var io = new IoEngine();
        return new BasicEngines(io, new(), new(io), new(io), new(io), new(io));
    }

    [Fact]
    public void Init_ParsesAllParametersCorrectlyWithoutOpeningHardware()
    {
        var engines = CreateEngines();
        using var driver = new DirectShowCameraDriver();

        driver.Init(
            "CamNotebook",
            "Index=2,Width=1920,Height=1080,Fps=60,FourCC=YUY2,Exposure=-6,Flip=H,AutoConnect=false",
            engines);

        Assert.Equal("CamNotebook", driver.DeviceName);
        Assert.Equal("Capture", driver.Category);
        Assert.Equal("DirectShowCamera", driver.DriverType);
        Assert.Equal(2, driver.CameraIndex);
        Assert.Equal(1920, driver.Width);
        Assert.Equal(1080, driver.Height);
        Assert.Equal(60.0, driver.Fps);
        Assert.Equal("YUY2", driver.FourCC);
        Assert.Equal(-6.0, driver.Exposure);
        Assert.Equal("H", driver.FlipModeName);
    }

    [Fact]
    public void ApplyParameterString_UpdatesSettingsAndDetectsReopenNeed()
    {
        var engines = CreateEngines();
        using var driver = new DirectShowCameraDriver();
        driver.Init("Cam0", "Index=0,Width=640,Height=480,FourCC=MJPG,AutoConnect=false", engines);

        // Changing exposure/flip only should not require reopening VideoCapture stream.
        bool needReopenSameStream = driver.ApplyParameterString("Exposure=-4,Flip=HV", isInitialInit: false);
        Assert.False(needReopenSameStream);
        Assert.Equal(-4.0, driver.Exposure);
        Assert.Equal("HV", driver.FlipModeName);

        // Changing Exposure back to Auto sets Exposure == null.
        driver.ApplyParameterString("Exposure=Auto", isInitialInit: false);
        Assert.Null(driver.Exposure);

        // Changing resolution/FourCC requires reopening DirectShow stream.
        bool needReopenResChange = driver.ApplyParameterString("Width=1280,Height=720,FourCC=YUY2", isInitialInit: false);
        Assert.True(needReopenResChange);
        Assert.Equal(1280, driver.Width);
        Assert.Equal(720, driver.Height);
        Assert.Equal("YUY2", driver.FourCC);
    }

    [Fact]
    public void Enumerator_ResolveDeviceIndex_HandlesIndexDisplayNameAndFriendlyName()
    {
        var mockList = new List<DirectShowDeviceInfo>
        {
            new(0, "Integrated Camera", @"\\?\usb#vid_04f2"),
            new(1, "Logitech BRIO", @"\\?\usb#vid_046d")
        };

        Assert.Equal(0, DirectShowDeviceEnumerator.ResolveDeviceIndex("0", mockList));
        Assert.Equal(1, DirectShowDeviceEnumerator.ResolveDeviceIndex("1: Logitech BRIO", mockList));
        Assert.Equal(1, DirectShowDeviceEnumerator.ResolveDeviceIndex("Logitech BRIO", mockList));
        Assert.Equal(1, DirectShowDeviceEnumerator.ResolveDeviceIndex("BRIO", mockList));
        Assert.Equal(0, DirectShowDeviceEnumerator.ResolveDeviceIndex("UnknownCam", mockList));

        // Real COM enumeration should never throw regardless of hardware availability.
        var realList = DirectShowDeviceEnumerator.EnumerateVideoInputDevices();
        Assert.NotNull(realList);
    }

    [Fact]
    public void Snap_WhenDeviceUnavailable_SetsOkFalseReturnsTimestampedFallbackAndContinuesFlow()
    {
        var engines = CreateEngines();
        using var driver = new DirectShowCameraDriver();
        driver.Init("CamMissing", "Index=99,Width=640,Height=480,AutoConnect=false", engines);

        var ticket = new DriverTicket("Snap");
        driver.PostTicket(ticket);
        var res = ticket.Wait(5000);

        Assert.True(res.Success);
        Assert.Equal("OFFLINE_FALLBACK", res.ErrorCode);
        Assert.Equal(false, res.Payload);
        Assert.False(engines.Io.GetPort("CamMissing#OK?"));
        var frame = engines.Image.GetFrame("CamMissing");
        Assert.NotNull(frame.Image);
        Assert.False(frame.Image.Empty());
        Assert.Equal(640, frame.Image.Width);
        Assert.Equal(480, frame.Image.Height);
        Assert.Equal("DirectShowCamera", frame.Metadata["Driver"]);
        Assert.Equal("OFFLINE_FALLBACK", frame.Metadata["ConnectionState"]);
        Assert.Equal("False", frame.Metadata["OK"]);
    }

    [Fact]
    public void DirectShowDriver_SnapWithCalibrationOverlaysAndSavePng_RestoresFullVisionMetadata()
    {
        var engines = CreateEngines();
        using var driver = new DirectShowCameraDriver();
        driver.Init("CamTop", "Index=99,Width=640,Height=480,FourCC=MJPG,AutoConnect=false", engines);

        // Configure camera-level calibration before Snap -> captured frame inherits it by default.
        engines.Image.SetManualCalibration("CamTop", scaleMmPerPx: 0.05, originPxX: 320, originPxY: 240, method: "CamCalib");

        var ticket = new DriverTicket("Snap");
        driver.PostTicket(ticket);
        var res = ticket.Wait(5000);
        Assert.True(res.Success);
        Assert.False(engines.Io.GetPort("CamTop#OK?"));

        engines.Image.SetRoi("CamTop", "Rect(100,100,200,150)");
        engines.Image.AddOverlay("CamTop", new ARCOS.Core.Model.OverlayShape
        {
            ShapeType = ARCOS.Core.Model.OverlayShapeType.Crosshair,
            X1 = 360,
            Y1 = 280,
            ColorHex = "#10B981",
            Thickness = 2
        });

        string tmpPng = Path.Combine(Path.GetTempPath(), $"dshow_meta_{Guid.NewGuid():N}");
        string savedPath = engines.Image.SaveImageWithMetadata("CamTop", tmpPng);
        try
        {
            Assert.EndsWith(".png", savedPath, StringComparison.OrdinalIgnoreCase);

            var payload = ImaqPngMetadataCodec.ExtractMetadata(File.ReadAllBytes(savedPath));
            Assert.NotNull(payload);
            Assert.Equal("DirectShowCamera", payload!.Metadata["Driver"]);
            Assert.Equal("99", payload.Metadata["CameraIndex"]);
            Assert.Equal("False", payload.Metadata["OK"]);
            Assert.Equal("Rect(100,100,200,150)", payload.RoiString);
            Assert.Single(payload.Overlays);
            Assert.True(payload.Calibration?.IsCalibrated);
            Assert.Equal(0.05, payload.Calibration!.ScaleMmPerPx, 6);

            // Load calibration cleanly via LoadCalibrationFromFile
            var engines2 = CreateEngines();
            Assert.True(engines2.Image.LoadCalibrationFromFile("CamTop", savedPath));
            var (wx, wy, isCal) = engines2.Image.PixelToWorld("CamTop", 360, 280); // (+40,+40) * 0.05 = (2.0, 2.0)
            Assert.True(isCal);
            Assert.Equal(2.0, wx, 3);
            Assert.Equal(2.0, wy, 3);
        }
        finally
        {
            if (File.Exists(savedPath)) File.Delete(savedPath);
        }
    }
}
