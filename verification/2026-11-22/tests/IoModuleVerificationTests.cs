using ARCOS.Core.Execution;
using Xunit;
using Xunit.Abstractions;

namespace ARCOS.Core.Tests.ModuleVerification;

public class IoModuleVerificationTests
{
    private readonly ITestOutputHelper _output;
    public IoModuleVerificationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void IO_CoreCommands_VirtualIO_SetClr_CheckLogic_Wait_Flash_Shift()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("""
            IO@AddVirtualIO :: io=V_Start?, val=false;
            IO@AddVirtualIO :: io=V_Out1?, val=false;
            IO@AddVirtualIO :: io=V_Out2?, val=false;
            IO@SetPort :: io=V_Start?;
            IO@CheckLogicOn :: logic=V_Start?;
            IO@WaitPortOn :: io=V_Start? {timeout=0.5};
            IO@Shift :: ios="V_Start?;V_Out1?;V_Out2?", count=1;
            IO@ClrPort :: io=V_Start?;
            IO@CheckLogicOff :: logic=V_Start?;
            IO@WaitPortOff :: io=V_Start? {timeout=0.5};
            IO@ForceUpdate;
            """, maxSteps: 200);

        Assert.All(results, r => Assert.Equal(CommandStatus.Completed, r.Status));
        Assert.False(h.Io.GetPort("V_Start?"));
        Assert.True(h.Io.GetPort("V_Out1?"));
    }

    [Fact]
    public void IO_AllCsvis_GuideUiAndSchemaAudit()
    {
        var h = new ModuleVerificationHarness();
        var audits = h.AuditAllCsvisInModule("IO");
        Assert.Equal(14, audits.Count);
        foreach (var a in audits)
        {
            _output.WriteLine($"{a.FileName,-26} | Schema:{a.HasManifestSchema,-5} | AXAML:{a.HasAxamlGuideUi,-5} | Guide:{a.GuideOutputStatus}");
            Assert.True(a.HasManifestSchema, $"Missing Schema in {a.FileName}");
            Assert.True(a.HasAxamlGuideUi, $"Missing AXAML in {a.FileName}");
        }
    }
}
