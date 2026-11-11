using System;
using System.IO;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using ARCOS.Core.Execution;
using ARCOS.Core.Guidance;
using ARCOS.Module.IO;
using Xunit;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Core.Tests;

public class IoTrampolineTests
{
    static IoTrampolineTests()
    {
        _ = typeof(Avalonia.Data.RelativeSource).Assembly;
        _ = typeof(Avalonia.Controls.Control).Assembly;
        _ = typeof(Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader).Assembly;
    }

    private readonly BasicEngines _engines;
    private readonly IoEngine _io;
    private readonly ModuleRegistry _registry;

    public IoTrampolineTests()
    {
        _io = new IoEngine();
        _engines = new BasicEngines(
            _io,
            new VariableEngine(),
            new PointEngine(_io),
            new ImageEngine(_io),
            new DaqEngine(_io),
            new TimerEngine(_io)
        );
        _registry = ModuleRegistry.CreateDefault();
    }

    private IParameterGuidance LoadGuidance(string action)
    {
        var handler = _registry.LoadCommandCsviHandler("IO", action);
        Assert.NotNull(handler);
        var guidance = handler as IParameterGuidance;
        Assert.NotNull(guidance);
        return guidance!;
    }

    [Fact]
    public void All14IoGuidanceInstances_ExecuteBasicEngines_ShouldExecuteCorrectly()
    {
        // 1. SetPort
        var setPort = LoadGuidance("SetPort");
        Assert.Equal("IO@SetPort", setPort.CommandKey);
        setPort.Execute(_engines, "P0.0");
        Assert.True(_io.GetPort("P0.0"));

        // 2. ClrPort
        var clrPort = LoadGuidance("ClrPort");
        Assert.Equal("IO@ClrPort", clrPort.CommandKey);
        clrPort.Execute(_engines, "P0.0");
        Assert.False(_io.GetPort("P0.0"));

        // 3. WaitPortOn
        var waitOn = LoadGuidance("WaitPortOn");
        Assert.Equal("IO@WaitPortOn", waitOn.CommandKey);
        _io.SetPort("P0.1", true);
        Assert.True(waitOn.Execute(_engines, "P0.1"));

        // 4. WaitPortOff
        var waitOff = LoadGuidance("WaitPortOff");
        Assert.Equal("IO@WaitPortOff", waitOff.CommandKey);
        _io.SetPort("P0.1", false);
        Assert.True(waitOff.Execute(_engines, "P0.1"));

        // 5. Flash(OnOff)
        var flashOnOff = LoadGuidance("Flash(OnOff)");
        Assert.Equal("IO@Flash(OnOff)", flashOnOff.CommandKey);
        flashOnOff.Execute(_engines, "P0.2,0.2,0.2");
        Assert.True(_io.GetPort("P0.2"));

        // 6. Flash(OffOn)
        var flashOffOn = LoadGuidance("Flash(OffOn)");
        Assert.Equal("IO@Flash(OffOn)", flashOffOn.CommandKey);
        flashOffOn.Execute(_engines, "P0.2,0.2,0.2");
        Assert.False(_io.GetPort("P0.2"));

        // 7. CheckLogicOn
        var checkOn = LoadGuidance("CheckLogicOn");
        Assert.Equal("IO@CheckLogicOn", checkOn.CommandKey);
        _io.SetPort("P0.3", true);
        Assert.True(checkOn.Execute(_engines, "P0.3"));

        // 8. CheckLogicOff
        var checkOff = LoadGuidance("CheckLogicOff");
        Assert.Equal("IO@CheckLogicOff", checkOff.CommandKey);
        _io.SetPort("P0.3", false);
        Assert.True(checkOff.Execute(_engines, "P0.3"));

        // 9. SetPortName
        var setName = LoadGuidance("SetPortName");
        Assert.Equal("IO@SetPortName", setName.CommandKey);
        Assert.True(setName.Execute(_engines, "P0.0,Cylinder_Extend"));

        // 10. AddVirtualIO (支援 0/1/on/off 與 $logic 複合邏輯運算式)
        var addVio = LoadGuidance("AddVirtualIO");
        Assert.Equal("IO@AddVirtualIO", addVio.CommandKey);
        addVio.Execute(_engines, "V_Test,1");
        Assert.True(_io.GetPort("V_Test"));

        _io.SetPort("P0.0", true);
        _io.SetPort("P0.1", true);
        addVio.Execute(_engines, "V_Logic,P0.0&P0.1");
        Assert.True(_io.GetPort("V_Logic"));

        _io.SetPort("P0.1", false);
        addVio.Execute(_engines, "V_Logic,P0.0&P0.1");
        Assert.False(_io.GetPort("V_Logic"));

        // 11. AddIODriver
        var addDriver = LoadGuidance("AddIODriver");
        Assert.Equal("IO@AddIODriver", addDriver.CommandKey);
        Assert.True(addDriver.Execute(_engines, "MockIO,MockIO"));

        // 12. InitIOPort
        var initPort = LoadGuidance("InitIOPort");
        Assert.Equal("IO@InitIOPort", initPort.CommandKey);
        Assert.True(initPort.Execute(_engines, "MockIO"));

        // 13. ForceUpdate
        var forceUpdate = LoadGuidance("ForceUpdate");
        Assert.Equal("IO@ForceUpdate", forceUpdate.CommandKey);
        Assert.True(forceUpdate.Execute(_engines, "MockIO"));

        // 14. Shift
        var shift = LoadGuidance("Shift");
        Assert.Equal("IO@Shift", shift.CommandKey);
        _io.SetPort("P0.4", true);
        _io.SetPort("P0.5", false);
        shift.Execute(_engines, "P0.4,P0.5,Shift=1");
        Assert.True(_io.GetPort("P0.5"));
    }

    private ExecutionContext CreateContext()
    {
        var drivers = new DriverManager(_engines);
        var program = ProgramTable.FromText("IO@SetPort::P0.0", 0, "TestProgram");
        return new ExecutionContext(0, "ThreadMain", program, _engines, drivers)
        {
            Registry = _registry
        };
    }

    [Fact]
    public void Trampoline_Cache_SuppressesRepeatedDiskProbes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ARCOS_Test_Editor_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var handler = new IoModuleHandler(tempDir);

            var res1 = handler.ResolveGuidance("SetPort");
            Assert.Null(res1);

            var res2 = handler.ResolveGuidance("SetPort");
            Assert.Null(res2);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Trampoline_MissingCsvi_ReturnsFailWithoutFallback()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ARCOS_Test_Editor_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var handler = new IoModuleHandler(tempDir);
            var ctx = CreateContext();

            // 缺少對應 .csvi 時絕不退回內建保底，直接報錯 (CommandStatus.Error)
            var res = handler.Execute("SetPort", "P0.0", ctx);
            Assert.Equal(CommandStatus.Error, res.Status);
            Assert.False(_io.GetPort("P0.0"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Trampoline_ExecutionContext_DirectGuidanceExecution()
    {
        var ctx = CreateContext();

        var setPort = LoadGuidance("SetPort");
        var res1 = setPort.Execute(ctx, "SetPort", "P0.6");
        Assert.Equal(CommandStatus.Completed, res1.Status);
        Assert.True(_io.GetPort("P0.6"));

        var clrPort = LoadGuidance("ClrPort");
        var res2 = clrPort.Execute(ctx, "ClrPort", "P0.6");
        Assert.Equal(CommandStatus.Completed, res2.Status);
        Assert.False(_io.GetPort("P0.6"));

        _io.SetPort("P0.7", true);
        var waitOn = LoadGuidance("WaitPortOn");
        var res3 = waitOn.Execute(ctx, "WaitPortOn", "P0.7");
        Assert.Equal(CommandStatus.Completed, res3.Status);
    }

    [Fact]
    public void CSVI_DynamicDiscovery_FromIoDirectory_DiscoversAllCommandsAndDrivers()
    {
        string baseDir = AppContext.BaseDirectory;
        string solutionRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        string distIoDir = Path.Combine(solutionRoot, "dist", "ARCOS_Runtime", "MODULES", "IO");

        if (!Directory.Exists(distIoDir)) return;

        using var handler = new IoModuleHandler(distIoDir);

        var commands = handler.GetSupportedCommands();
        Assert.NotEmpty(commands);
        Assert.Contains("SetPort", commands);
        Assert.Contains("ClrPort", commands);
        Assert.Contains("Flash(OnOff)", commands);
        Assert.Contains("WaitPortOn", commands);
        Assert.Contains("Shift", commands);

        var drivers = handler.GetSupportedDrivers();
        Assert.NotEmpty(drivers);
        Assert.Contains("MockIO", drivers);

        var guidance = handler.ResolveGuidance("SetPort");
        Assert.NotNull(guidance);
        var panel = guidance.GetFrontPanelControl();
        Assert.NotNull(panel);
    }

    [Fact]
    public void CSVI_DriverDiscovery_DriverManagerFindsMockIoFromCsvi()
    {
        string baseDir = AppContext.BaseDirectory;
        string solutionRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        string distDriverDir = Path.Combine(solutionRoot, "dist", "ARCOS_Runtime", "MODULES", "IO", "DRIVER");

        if (!Directory.Exists(distDriverDir)) return;

        var driverMgr = new DriverManager(_engines, distDriverDir);
        var available = driverMgr.Loader.GetAvailableDrivers("IO");
        Assert.Contains("MockIO", available);

        var loaded = driverMgr.Loader.LoadDriver("IO", "MockIO");
        Assert.NotNull(loaded);
        loaded.Init("StationIO", "StartPort=P0.0,BitCount=16", _engines);
        var view = loaded.CreateDriverView();
        Assert.NotNull(view);
        loaded.Shutdown();
    }
}
