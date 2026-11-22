using ARCOS.Core.Execution;
using Xunit;
using Xunit.Abstractions;

namespace ARCOS.Core.Tests.ModuleVerification;

public class UserModuleVerificationTests
{
    private readonly ITestOutputHelper _output;
    public UserModuleVerificationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void User_AllCsvis_GuideUiAndSchemaAudit()
    {
        var h = new ModuleVerificationHarness();
        var audits = h.AuditAllCsvisInModule("USER");
        Assert.Equal(21, audits.Count);
        foreach (var a in audits)
        {
            _output.WriteLine($"{a.FileName,-26} | Schema:{a.HasManifestSchema,-5} | AXAML:{a.HasAxamlGuideUi,-5} | Guide:{a.GuideOutputStatus}");
            Assert.True(a.HasManifestSchema, $"Missing Schema in {a.FileName}");
            Assert.True(a.HasAxamlGuideUi, $"Missing AXAML in {a.FileName}");
        }
    }
}
