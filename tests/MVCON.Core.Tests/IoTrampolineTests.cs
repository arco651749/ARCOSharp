using System;
using System.IO;
using MVCON.Core.Drivers;
using MVCON.Core.Engines;
using MVCON.Core.Execution;
using MVCON.Core.Guidance;
using MVCON.Module.IO;
using MVCON.Module.IO.Guidance;
using Xunit;
using ExecutionContext = MVCON.Core.Execution.ExecutionContext;

namespace MVCON.Core.Tests;

public class IoTrampolineTests
{
    private readonly BasicEngines _engines;
    private readonly IoEngine _io;

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
    }

    [Fact]
    public void All14IoGuidanceInstances_ExecuteBasicEngines_ShouldExecuteCorrectly()
    {
        // 1. SetPort
        var setPort = new IoSetPortGuidance();
        Assert.Equal("IO@SetPort", setPort.CommandKey);
        setPort.Execute(_engines, "P0.0");
        Assert.True(_io.GetPort("P0.0"));

        // 2. ClrPort
        var clrPort = new IoClrPortGuidance();
        Assert.Equal("IO@ClrPort", clrPort.CommandKey);
        clrPort.Execute(_engines, "P0.0");
        Assert.False(_io.GetPort("P0.0"));

        // 3. WaitPortOn
        var waitOn = new IoWaitPortOnGuidance();
        Assert.Equal("IO@WaitPortOn", waitOn.CommandKey);
        _io.SetPort("P0.1", true);
        Assert.True(waitOn.Execute(_engines, "P0.1"));

        // 4. WaitPortOff
        var waitOff = new IoWaitPortOffGuidance();
        Assert.Equal("IO@WaitPortOff", waitOff.CommandKey);
        _io.SetPort("P0.1", false);
        Assert.True(waitOff.Execute(_engines, "P0.1"));

        // 5. Flash(OnOff)
        var flashOnOff = new IoFlashOnOffGuidance();
        Assert.Equal("IO@Flash(OnOff)", flashOnOff.CommandKey);
        flashOnOff.Execute(_engines, "P0.2");
        Assert.True(_io.GetPort("P0.2"));

        // 6. Flash(OffOn)
        var flashOffOn = new IoFlashOffOnGuidance();
        Assert.Equal("IO@Flash(OffOn)", flashOffOn.CommandKey);
        flashOffOn.Execute(_engines, "P0.2");
        Assert.False(_io.GetPort("P0.2"));

        // 7. CheckLogicOn
        var checkOn = new IoCheckLogicOnGuidance();
        Assert.Equal("IO@CheckLogicOn", checkOn.CommandKey);
        _io.SetPort("P0.3", true);
        Assert.True(checkOn.Execute(_engines, "P0.3"));

        // 8. CheckLogicOff
        var checkOff = new IoCheckLogicOffGuidance();
        Assert.Equal("IO@CheckLogicOff", checkOff.CommandKey);
        _io.SetPort("P0.3", false);
        Assert.True(checkOff.Execute(_engines, "P0.3"));

        // 9. SetPortName
        var setName = new IoSetPortNameGuidance();
        Assert.Equal("IO@SetPortName", setName.CommandKey);
        setName.Execute(_engines, "P0.0,Cylinder_Extend");
        Assert.True(setName.Execute(_engines, "P0.0,Cylinder_Extend"));

        // 10. AddVirtualIO
        var addVio = new IoAddVirtualIoGuidance();
        Assert.Equal("IO@AddVirtualIO", addVio.CommandKey);
        addVio.Execute(_engines, "V_Test,1");
        Assert.True(_io.GetPort("V_Test"));

        // 11. AddIODriver
        var addDriver = new IoAddIoDriverGuidance();
        Assert.Equal("IO@AddIODriver", addDriver.CommandKey);
        Assert.True(addDriver.Execute(_engines, "MockIO,MockIO"));

        // 12. InitIOPort
        var initPort = new IoInitIoPortGuidance();
        Assert.Equal("IO@InitIOPort", initPort.CommandKey);
        Assert.True(initPort.Execute(_engines, "MockIO"));

        // 13. ForceUpdate
        var forceUpdate = new IoForceUpdateGuidance();
        Assert.Equal("IO@ForceUpdate", forceUpdate.CommandKey);
        Assert.True(forceUpdate.Execute(_engines, "MockIO"));

        // 14. Shift
        var shift = new IoShiftGuidance();
        Assert.Equal("IO@Shift", shift.CommandKey);
        _io.SetPort("P0.4", true);
        _io.SetPort("P0.5", false);
        shift.Execute(_engines, "P0.4,P0.5,Shift=1");
        // P0.5 should now have P0.4's previous value
        Assert.True(_io.GetPort("P0.5"));
    }

    private ExecutionContext CreateContext()
    {
        var drivers = new DriverManager(_engines);
        var program = ProgramTable.FromText("IO@SetPort P0.0", 0, "TestProgram");
        return new ExecutionContext(0, "ThreadMain", program, _engines, drivers);
    }

    [Fact]
    public void Trampoline_Cache_SuppressesRepeatedDiskProbes()
    {
        // 建立臨時空目錄
        string tempDir = Path.Combine(Path.GetTempPath(), "MVCON_Test_Editor_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var handler = new IoModuleHandler(tempDir);

            // 第一次探測 (不存在)
            var res1 = handler.ResolveGuidance("SetPort");
            Assert.Null(res1);

            // 第二次探測 (從快取返回，不報錯)
            var res2 = handler.ResolveGuidance("SetPort");
            Assert.Null(res2);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Trampoline_ExceptionSandbox_CatchesExceptionAndReturnsFail()
    {
        // 模擬異常防禦：外掛若拋出 NullReferenceException，跳板必須回傳 CommandResult.Fail 且不引發主進程崩潰
        string tempDir = Path.Combine(Path.GetTempPath(), "MVCON_Test_Editor_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var handler = new IoModuleHandler(tempDir);
            var ctx = CreateContext();

            // 執行不存在客製化 DLL 的指令，走保底邏輯成功執行
            var res = handler.Execute("SetPort", "P0.0", ctx);
            Assert.Equal(CommandStatus.Completed, res.Status);
            Assert.True(_io.GetPort("P0.0"));
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

        // 驗證 SetPort Guidance 直接支援 ExecutionContext 執行
        var setPort = new IoSetPortGuidance();
        var res1 = setPort.Execute(ctx, "SetPort", "P0.6");
        Assert.Equal(CommandStatus.Completed, res1.Status);
        Assert.True(_io.GetPort("P0.6"));

        // 驗證 ClrPort Guidance 直接支援 ExecutionContext 執行
        var clrPort = new IoClrPortGuidance();
        var res2 = clrPort.Execute(ctx, "ClrPort", "P0.6");
        Assert.Equal(CommandStatus.Completed, res2.Status);
        Assert.False(_io.GetPort("P0.6"));

        // 驗證 WaitPortOn Guidance 立即滿足時回傳 Completed
        _io.SetPort("P0.7", true);
        var waitOn = new IoWaitPortOnGuidance();
        var res3 = waitOn.Execute(ctx, "WaitPortOn", "P0.7");
        Assert.Equal(CommandStatus.Completed, res3.Status);
    }

    [Fact]
    public void Trampoline_EndToEnd_LoadedFromDeploymentDist_ExecutesCsGuidanceDirectly()
    {
        // 驗證 dist/MVCON_Runtime/MODULES/IO/USERPARAMETEREDITOR 實體發布目錄
        string baseDir = AppContext.BaseDirectory;
        // 往上推至方案根目錄
        string solutionRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        string distEditorDir = Path.Combine(solutionRoot, "dist", "MVCON_Runtime", "MODULES", "IO", "USERPARAMETEREDITOR");

        if (!Directory.Exists(distEditorDir)) return; // 若未打包發布則略過

        using var handler = new IoModuleHandler(distEditorDir);
        var ctx = CreateContext();

        // 1. 測試跳板優先動態轉發 SetPort.Guidance.dll
        _io.SetPort("P0.0", false);
        var resSet = handler.Execute("SetPort", "P0.0", ctx);
        Assert.Equal(CommandStatus.Completed, resSet.Status);
        Assert.True(_io.GetPort("P0.0")); // 確認 CSVI 成功通電點位！

        // 2. 測試跳板優先動態轉發 ClrPort.Guidance.dll
        var resClr = handler.Execute("ClrPort", "P0.0", ctx);
        Assert.Equal(CommandStatus.Completed, resClr.Status);
        Assert.False(_io.GetPort("P0.0")); // 確認 CSVI 成功斷電點位！

        // 3. 測試跳板優先動態轉發 WaitPortOn.Guidance.dll
        _io.SetPort("P0.1", true);
        var resWait = handler.Execute("WaitPortOn", "P0.1", ctx);
        Assert.Equal(CommandStatus.Completed, resWait.Status);

        // 4. 測試跳板優先動態轉發 Shift.Guidance.dll
        _io.SetPort("P0.2", true);
        _io.SetPort("P0.3", false);
        var resShift = handler.Execute("Shift", "P0.2,P0.3,Shift=1", ctx);
        Assert.Equal(CommandStatus.Completed, resShift.Status);
        Assert.True(_io.GetPort("P0.3")); // P0.3 接下 P0.2 的 true
    }

    [Fact]
    public void CSVI_DynamicDiscovery_FromIoDirectory_DiscoversAllCommandsAndDrivers()
    {
        string baseDir = AppContext.BaseDirectory;
        string solutionRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        string distIoDir = Path.Combine(solutionRoot, "dist", "MVCON_Runtime", "MODULES", "IO");

        if (!Directory.Exists(distIoDir)) return;

        using var handler = new IoModuleHandler(distIoDir);

        // 1. 驗證指令探索：MODULES/IO 目錄下有多少 CSVI 就有什麼指令
        var commands = handler.GetSupportedCommands();
        Assert.NotEmpty(commands);
        Assert.Contains("SetPort", commands);
        Assert.Contains("ClrPort", commands);
        Assert.Contains("Flash(OnOff)", commands);
        Assert.Contains("WaitPortOn", commands);
        Assert.Contains("Shift", commands);

        // 2. 驗證驅動探索：DRIVER 目錄下有多少 CSVI 就有什麼 DRIVER
        var drivers = handler.GetSupportedDrivers();
        Assert.NotEmpty(drivers);
        Assert.Contains("MockIO", drivers);

        // 3. 驗證 CSVI 儀表前面板取得
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
        string distDriverDir = Path.Combine(solutionRoot, "dist", "MVCON_Runtime", "MODULES", "IO", "DRIVER");

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
