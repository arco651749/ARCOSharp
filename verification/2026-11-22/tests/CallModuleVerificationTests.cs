using ARCOS.Core.Csvi;
using Xunit;
using Xunit.Abstractions;

namespace ARCOS.Core.Tests.ModuleVerification;

public class CallModuleVerificationTests
{
    private readonly ITestOutputHelper _output;
    public CallModuleVerificationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Call_CsviPackage_LoadsCleanly()
    {
        var h = new ModuleVerificationHarness();
        string path = Path.Combine(h.DistModulesDir, "CALL", "CALL.csvi");
        Assert.True(File.Exists(path), $"Missing CALL.csvi at {path}");
        var pkg = CsviPackage.Load(path);
        Assert.NotNull(pkg);
        _output.WriteLine($"CALL.csvi loaded cleanly. EmbeddedSourceFiles={pkg.EmbeddedSourceFiles.Count}");
    }
}
