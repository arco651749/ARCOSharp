using ARCOS.Core.Execution;
using Xunit;
using Xunit.Abstractions;

namespace ARCOS.Core.Tests.ModuleVerification;

public class DaqModuleVerificationTests
{
    private readonly ITestOutputHelper _output;
    public DaqModuleVerificationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Daq_WaveformGenerate_And_TimeDomainCompare_VerifyV2()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("""
            DAQ@WaveFormGenerate :: wav=W1 {type=Sine, rate=100, sec=1.0, amp=5.0};
            DAQ@CompareAvgInRange :: wav=W1, max=1.0, min=-1.0 {start=0.0, end=1.0};
            DAQ@CompareMaxMinInRange :: wav=W1, max=6.0, min=-6.0 {start=0.0, end=1.0};
            """);

        Assert.All(results, r => Assert.Equal(CommandStatus.Completed, r.Status));
        Assert.True(h.Io.GetPort("W1#OK?"));
    }

    [Fact]
    public void Daq_AllCsvis_GuideUiAndSchemaAudit()
    {
        var h = new ModuleVerificationHarness();
        var audits = h.AuditAllCsvisInModule("DAQ");
        Assert.Equal(18, audits.Count);
        foreach (var a in audits)
        {
            _output.WriteLine($"{a.FileName,-26} | Schema:{a.HasManifestSchema,-5} | AXAML:{a.HasAxamlGuideUi,-5} | Guide:{a.GuideOutputStatus}");
            Assert.True(a.HasManifestSchema, $"Missing Schema in {a.FileName}");
            Assert.True(a.HasAxamlGuideUi, $"Missing AXAML in {a.FileName}");
        }
    }
}
