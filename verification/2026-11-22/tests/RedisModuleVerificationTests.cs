using ARCOS.Core.Execution;
using Xunit;
using Xunit.Abstractions;

namespace ARCOS.Core.Tests.ModuleVerification;

public class RedisModuleVerificationTests
{
    private readonly ITestOutputHelper _output;
    public RedisModuleVerificationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Redis_SetGetPushPop_LocalAutoStart_VerifyV2()
    {
        var h = new ModuleVerificationHarness();
        string k = $"arcos:verify:{Guid.NewGuid():N}";
        string q = $"arcos:queue:{Guid.NewGuid():N}";
        var (_, results) = h.RunScript($"""
            Redis@Set :: key={k}, val="HelloRedis";
            Redis@Get :: key={k} -> var=GotVal;
            Redis@LPush :: key={q}, val="Job1";
            Redis@LPop :: key={q} -> var=Popped;
            """);

        Assert.All(results, r => Assert.Equal(CommandStatus.Completed, r.Status));
        Assert.Equal("HelloRedis", h.Variable.Get("GotVal"));
        Assert.Equal("Job1", h.Variable.Get("Popped"));
    }

    [Fact]
    public void Redis_AllCsvis_GuideUiAndSchemaAudit()
    {
        var h = new ModuleVerificationHarness();
        var audits = h.AuditAllCsvisInModule("REDIS");
        Assert.Equal(9, audits.Count);
        foreach (var a in audits)
        {
            _output.WriteLine($"{a.FileName,-26} | Schema:{a.HasManifestSchema,-5} | AXAML:{a.HasAxamlGuideUi,-5} | Guide:{a.GuideOutputStatus}");
            Assert.True(a.HasManifestSchema, $"Missing Schema in {a.FileName}");
            Assert.True(a.HasAxamlGuideUi, $"Missing AXAML in {a.FileName}");
        }
    }
}
