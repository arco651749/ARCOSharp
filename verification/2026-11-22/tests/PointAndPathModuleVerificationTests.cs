using ARCOS.Core.Execution;
using Xunit;
using Xunit.Abstractions;

namespace ARCOS.Core.Tests.ModuleVerification;

public class PointAndPathModuleVerificationTests
{
    private readonly ITestOutputHelper _output;
    public PointAndPathModuleVerificationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void PointAndPath_SetOffsetDistanceAndPalletIndex_VerifyV2()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("""
            Point&Path@InsertOnePoint :: pt=P1, val=(10,20,30);
            Point&Path@ClearPoint :: pt=P1;
            """);

        Assert.All(results, r => Assert.Equal(CommandStatus.Completed, r.Status));
    }

    [Fact]
    public void PointAndPath_AllCsvis_GuideUiAndSchemaAudit()
    {
        var h = new ModuleVerificationHarness();
        var audits = h.AuditAllCsvisInModule("POINT&PATH");
        Assert.Equal(22, audits.Count);
        foreach (var a in audits)
        {
            _output.WriteLine($"{a.FileName,-26} | Schema:{a.HasManifestSchema,-5} | AXAML:{a.HasAxamlGuideUi,-5} | Guide:{a.GuideOutputStatus}");
            Assert.True(a.HasManifestSchema, $"Missing Schema in {a.FileName}");
            Assert.True(a.HasAxamlGuideUi, $"Missing AXAML in {a.FileName}");
        }
    }
}
