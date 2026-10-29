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
    [Fact]
    public void IoDriver_MultipleDevices_HardwarePointMapping_ShouldWorkCleanly()
    {
        var (engines, manager) = CreateEnvironment();

        // 模擬使用者的配置：
        // IO1: COM1, Out: P0.0 (32點, P0.0~P3.7), In: P4.0 (32點, P4.0~P7.7)
        var ioDev1 = manager.AddDevice("IO", "IO1", "MockIO", "Device=com1,OutSart=p0.0,OutPorts=32,InputStart=p4.0,InPorts=32", engines) as MockIoDriver;
        Assert.NotNull(ioDev1);
        Assert.Equal("com1", ioDev1.Device);
        Assert.Equal("p0.0", ioDev1.OutStart);
        Assert.Equal(32, ioDev1.OutPorts);
        Assert.Equal("p4.0", ioDev1.InStart);
        Assert.Equal(32, ioDev1.InPorts);

        // IO2: 192.168.0.1:5000, Out: P8.0 (32點, P8.0~P11.7), In: P12.0 (32點, P12.0~P15.7)
        var ioDev2 = manager.AddDevice("IO", "IO2", "MockIO", "Device=192.168.0.1:5000,OutSart=p8.0,OutPorts=32,InputStart=p12.0,InPorts=32", engines) as MockIoDriver;
        Assert.NotNull(ioDev2);
        Assert.Equal("192.168.0.1:5000", ioDev2.Device);
        Assert.Equal("p8.0", ioDev2.OutStart);
        Assert.Equal(32, ioDev2.OutPorts);
        Assert.Equal("p12.0", ioDev2.InStart);
        Assert.Equal(32, ioDev2.InPorts);

        // 1. 驗證從上層 IoEngine 輸出，雙向反映至對應的 Driver ReadbackOutputs
        engines.Io.SetPort("P0.0", true);
        engines.Io.SetPort("P3.7", true); // 第 32 個 output (3*8 + 7 = 31)
        engines.Io.SetPort("P8.0", true);
        engines.Io.SetPort("P11.7", true); // 第 32 個 output (11*8 + 7 = 95 - 64 = 31)

        // 強制更新
        ioDev1.PostTicket(new DriverTicket("FORCE_UPDATE"));
        ioDev2.PostTicket(new DriverTicket("FORCE_UPDATE"));
        Thread.Sleep(50);

        Assert.True(ioDev1.ReadbackOutputs[0]);  // P0.0
        Assert.True(ioDev1.ReadbackOutputs[31]); // P3.7
        Assert.True(ioDev2.ReadbackOutputs[0]);  // P8.0
        Assert.True(ioDev2.ReadbackOutputs[31]); // P11.7

        // 2. 驗證 Driver 面板手動撥動 Input，自動寫入 IoEngine 正確點位且兩者不干擾
        ioDev1.SetSimulatedInput(0, true);   // P4.0
        ioDev1.SetSimulatedInput(31, true);  // P7.7
        ioDev2.SetSimulatedInput(0, true);   // P12.0
        ioDev2.SetSimulatedInput(31, true);  // P15.7

        Assert.True(engines.Io.GetPort("P4.0"));
        Assert.True(engines.Io.GetPort("P7.7"));
        Assert.True(engines.Io.GetPort("P12.0"));
        Assert.True(engines.Io.GetPort("P15.7"));

        // 未設置的點位保持 False
        Assert.False(engines.Io.GetPort("P4.1"));
        Assert.False(engines.Io.GetPort("P12.1"));

        manager.ShutdownAll();
    }
}