using MVCON.Core.Drivers;
using MVCON.Core.Engines;
using MVCON.Core.Model;
using MVCON.Driver.MockCamera;
using MVCON.Driver.MockDAQ;
using MVCON.Driver.MockIO;
using MVCON.Driver.MockMotion;
using Xunit;

namespace MVCON.Drivers.Tests;

public class DriverManagerTests
{
    private (BasicEngines Engines, DriverManager Manager) CreateEnvironment()
    {
        var io = new IoEngine();
        var varEngine = new VariableEngine();
        var point = new PointEngine(io);
        var image = new ImageEngine(io);
        var daq = new DaqEngine(io);
        var timer = new TimerEngine(io);

        var engines = new BasicEngines(io, varEngine, point, image, daq, timer);
        var manager = new DriverManager();

        // 註冊工廠
        manager.RegisterDriverFactory("MockIO", () => new MockIoDriver());
        manager.RegisterDriverFactory("MockMotion", () => new MockMotionDriver());
        manager.RegisterDriverFactory("MockCamera", () => new MockCameraDriver());
        manager.RegisterDriverFactory("MockDAQ", () => new MockDaqDriver());

        return (engines, manager);
    }

    [Fact]
    public void CrossCategory_SameDeviceName_ShouldBeIsolatedAndCoexist()
    {
        var (engines, manager) = CreateEnvironment();

        // 在不同類別下加入同名設備 "Station1"
        var ioDev = manager.AddDevice("IO", "Station1", "MockIO", "OutStart=P0.0,OutPorts=8,InStart=P1.0,InPorts=8", engines);
        var motDev = manager.AddDevice("Motion", "Station1", "MockMotion", "Axes=6", engines);
        var camDev = manager.AddDevice("Capture", "Station1", "MockCamera", "Width=640,Height=480", engines);
        var daqDev = manager.AddDevice("DAQ", "Station1", "MockDAQ", "Rate=1000", engines);

        // 驗證四個同名設備皆成功建立且獨立
        Assert.NotNull(ioDev);
        Assert.NotNull(motDev);
        Assert.NotNull(camDev);
        Assert.NotNull(daqDev);

        Assert.Equal("IO", manager.GetDevice("IO", "Station1")?.Category);
        Assert.Equal("Motion", manager.GetDevice("Motion", "Station1")?.Category);
        Assert.Equal("Capture", manager.GetDevice("Capture", "Station1")?.Category);
        Assert.Equal("DAQ", manager.GetDevice("DAQ", "Station1")?.Category);

        manager.ShutdownAll();
    }

    [Fact]
    public void PrivateReturnQueue_DriverTicket_ShouldAccuratelyReturnToCaller()
    {
        var (engines, manager) = CreateEnvironment();
        var motDev = manager.AddDevice("Motion", "M1", "MockMotion", "", engines);

        // 呼叫者 1 派發 Home 指令並帶專屬 ticket
        var ticket1 = new DriverTicket("Home", "", "Caller_Thread1");
        motDev.PostTicket(ticket1);

        var res1 = ticket1.Wait(2000);
        Assert.True(res1.Success);
        Assert.True(ticket1.IsCompleted);
        Assert.True(engines.Io.GetPort("M1#Homed?"));

        manager.ShutdownAll();
    }

    [Fact]
    public void MockMotion_GoPointAndInfAxisMask_ShouldWork()
    {
        var (engines, manager) = CreateEnvironment();
        var motDev = (MockMotionDriver)manager.AddDevice("Motion", "Arm1", "MockMotion", "", engines);

        // 先設定點位 P1 (100, 200, 300)
        engines.Point.SetPoint("P1", new MachinePoint(100, 200, 300));

        // 執行 GoPoint 移動
        var ticket = new DriverTicket("GoPoint", "P1");
        motDev.PostTicket(ticket);
        ticket.Wait(2000);

        Assert.Equal(100, motDev.CurrentPos.X);
        Assert.Equal(200, motDev.CurrentPos.Y);
        Assert.Equal(300, motDev.CurrentPos.Z);
        Assert.True(engines.Io.GetPort("Arm1#Done?"));

        // 執行 Go 且包含 Inf 屏蔽（僅移動 Z，X 和 Y 保持）
        var ticketZ = new DriverTicket("Go", "Z=500");
        motDev.PostTicket(ticketZ);
        ticketZ.Wait(2000);

        Assert.Equal(100, motDev.CurrentPos.X); // X 保持 100
        Assert.Equal(200, motDev.CurrentPos.Y); // Y 保持 200
        Assert.Equal(500, motDev.CurrentPos.Z); // Z 更新為 500

        manager.ShutdownAll();
    }

    [Fact]
    public void MockCamera_Snap_ShouldUpdateImageEngineAndOkFlag()
    {
        var (engines, manager) = CreateEnvironment();
        var camDev = manager.AddDevice("Capture", "Cam0", "MockCamera", "Width=320,Height=240,Exposure=10", engines);

        var ticket = new DriverTicket("Snap");
        camDev.PostTicket(ticket);
        var res = ticket.Wait(2000);

        Assert.True(res.Success);
        Assert.True(engines.Io.GetPort("Cam0#OK?"));

        var frame = engines.Image.GetFrame("Cam0");
        Assert.NotNull(frame);
        Assert.Equal(320, frame.Image.Width);
        Assert.Equal(240, frame.Image.Height);

        manager.ShutdownAll();
    }

    [Fact]
    public void MockDaq_Sample_ShouldWriteWaveformAndSetOkFlag()
    {
        var (engines, manager) = CreateEnvironment();
        var daqDev = manager.AddDevice("DAQ", "PressDaq", "MockDAQ", "Rate=500,Count=100,Wave=Press", engines);

        var ticket = new DriverTicket("Sample");
        daqDev.PostTicket(ticket);
        var res = ticket.Wait(2000);

        Assert.True(res.Success);
        Assert.True(engines.Io.GetPort("PressDaq#OK?"));

        var wf = engines.Daq.GetWaveform("PressDaq");
        Assert.Equal(100, wf.GetOrCreateChannel(0).Y.Length);

        // 測試 AnalogOut 單點輸出指令
        var ticketAo = new DriverTicket("AnalogOut", "Value=7.5");
        daqDev.PostTicket(ticketAo);
        var resAo = ticketAo.Wait(2000);
        Assert.True(resAo.Success);
        Assert.Equal(7.5, ((MockDaqDriver)daqDev).InstantOutputVal);

        manager.ShutdownAll();
    }

    [Fact]
    public void DriverViews_ShouldInstantiateSuccessfully()
    {
        var (engines, manager) = CreateEnvironment();

        var io = manager.AddDevice("IO", "IO1", "MockIO", "", engines);
        var mot = manager.AddDevice("Motion", "M1", "MockMotion", "", engines);
        var cam = manager.AddDevice("Capture", "C1", "MockCamera", "", engines);
        var daq = manager.AddDevice("DAQ", "D1", "MockDAQ", "", engines);

        Assert.NotNull(io.CreateDriverView());
        Assert.NotNull(mot.CreateDriverView());
        Assert.NotNull(cam.CreateDriverView());
        Assert.NotNull(daq.CreateDriverView());

        manager.ShutdownAll();
    }

    [Fact]
    public void DriverLoader_GetAvailableDrivers_DiscoversInstalledDrivers()
    {
        var manager = new DriverManager();
        var ioDrivers = manager.GetAvailableDrivers("IO");
        var motionDrivers = manager.GetAvailableDrivers("Motion");
        var captureDrivers = manager.GetAvailableDrivers("Capture");
        var daqDrivers = manager.GetAvailableDrivers("DAQ");

        Assert.Contains("MockIO", ioDrivers);
        Assert.Contains("MockMotion", motionDrivers);
        Assert.Contains("MockCamera", captureDrivers);
        Assert.Contains("MockDAQ", daqDrivers);
    }
}
