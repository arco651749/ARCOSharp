using ARCOS.Core.Execution;
using Xunit;
using Xunit.Abstractions;

namespace ARCOS.Core.Tests.ModuleVerification;

public class MultiStationModuleVerificationTests
{
    private readonly ITestOutputHelper _output;
    public MultiStationModuleVerificationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void MultiStation_AddSetOnSetOKFindFirstOKAndFindEmpty_VerifyV2()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("""
            MultiStation@AddStation :: station=TrayA {slots=3};
            MultiStation@SetOn :: station=TrayA.1, logic=true;
            MultiStation@SetOK :: station=TrayA.1, logic=true;
            MultiStation@FindFirstOK :: station=TrayA -> var=FirstOkStation, index=FirstOkIdx;
            MultiStation@FindEmpty :: station=TrayA -> var=FirstEmptyStation, index=FirstEmptyIdx;
            """);

        Assert.All(results, r => Assert.Equal(CommandStatus.Completed, r.Status));
        Assert.Equal("TrayA.1", h.Variable.Get("FirstOkStation"));
        Assert.Equal("0", h.Variable.Get("FirstOkIdx"));
        Assert.Equal("TrayA.2", h.Variable.Get("FirstEmptyStation"));
        Assert.Equal("1", h.Variable.Get("FirstEmptyIdx"));
    }

    [Fact]
    public void MultiStation_AllCsvis_GuideUiAndSchemaAudit()
    {
        var h = new ModuleVerificationHarness();
        var audits = h.AuditAllCsvisInModule("MULTISTATION");
        Assert.Equal(12, audits.Count);
        foreach (var a in audits)
        {
            _output.WriteLine($"{a.FileName,-26} | Schema:{a.HasManifestSchema,-5} | AXAML:{a.HasAxamlGuideUi,-5} | Guide:{a.GuideOutputStatus}");
            Assert.True(a.HasManifestSchema, $"Missing Schema in {a.FileName}");
            Assert.True(a.HasAxamlGuideUi, $"Missing AXAML in {a.FileName}");
        }
    }
}
