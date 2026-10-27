using MVCON.Core.Drivers;
using MVCON.Core.Engines;
using MVCON.Remoter;
using Xunit;

namespace MVCON.Core.Tests;

public class TelnetServerTests
{
    private (TelnetServer Server, BasicEngines Engines) CreateServer()
    {
        var io = new IoEngine();
        var varEng = new VariableEngine();
        var pt = new PointEngine(io);
        var img = new ImageEngine(io);
        var daq = new DaqEngine(io);
        var timer = new TimerEngine(io);

        var engines = new BasicEngines(io, varEng, pt, img, daq, timer);
        var drivers = new DriverManager(engines);
        var server = new TelnetServer(2800, engines, drivers);

        return (server, engines);
    }

    [Fact]
    public void ProcessCommand_SetAndGetIO_WorksCorrectly()
    {
        var (server, engines) = CreateServer();

        var setRes = server.ProcessCommand("$SetIO P0.0=1");
        Assert.Equal("OK", setRes);
        Assert.True(engines.Io.GetPort("P0.0"));

        var getRes = server.ProcessCommand("$GetIO P0.0");
        Assert.Equal("OK: True", getRes);
    }

    [Fact]
    public void ProcessCommand_SetAndGetVariable_WorksCorrectly()
    {
        var (server, engines) = CreateServer();

        var setRes = server.ProcessCommand("$SetVariable RecipeName=Model_X");
        Assert.Equal("OK", setRes);
        Assert.Equal("Model_X", engines.Variable.Get("RecipeName"));

        var getRes = server.ProcessCommand("$GetVariable RecipeName");
        Assert.Equal("OK: Model_X", getRes);
    }

    [Fact]
    public void ProcessCommand_SetAndGetPoint_WorksCorrectly()
    {
        var (server, engines) = CreateServer();

        var setRes = server.ProcessCommand("$SetPoint PickPos=(15.5,25.0,100.0,0,0,0)");
        Assert.Equal("OK", setRes);

        var pt = engines.Point.GetPoint("PickPos");
        Assert.Equal(15.5, pt.X);
        Assert.Equal(25.0, pt.Y);
        Assert.Equal(100.0, pt.Z);

        var getRes = server.ProcessCommand("$GetPoint PickPos");
        Assert.Equal("OK: (15.5,25,100,0,0,0)", getRes);
    }

    [Fact]
    public void ProcessCommand_ExecuteScript_ExecutesCommandsDirectly()
    {
        var (server, engines) = CreateServer();

        string scriptCmd = "$Execute IO@SetPort::P5.0;Variable@Set::RemoteVar,Success;";
        var res = server.ProcessCommand(scriptCmd);

        Assert.Equal("OK: Executed", res);
        Assert.True(engines.Io.GetPort("P5.0"));
        Assert.Equal("Success", engines.Variable.Get("RemoteVar"));
    }
}
