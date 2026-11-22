using ARCOS.Core.Execution;
using Xunit;
using Xunit.Abstractions;

namespace ARCOS.Core.Tests.ModuleVerification;

public class VariableModuleVerificationTests
{
    private readonly ITestOutputHelper _output;
    public VariableModuleVerificationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Variable_ScalarAndMath_SetIncDecCalculateAppendTrimSlice()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("""
            Variable@Set :: var=Cnt, val=10;
            Variable@Inc :: var=Cnt {step=2};
            Variable@Dec :: var=Cnt {step=1};
            Variable@Calculate :: var=Triple, expr="$(Cnt)*3";
            Variable@Set :: var=Msg, val="  Hello  ";
            Variable@Trim :: var=Msg;
            Variable@Append :: var=Msg, val="World";
            """);

        Assert.All(results, r => Assert.Equal(CommandStatus.Completed, r.Status));
        Assert.Equal("11", h.Variable.Get("Cnt"));
        Assert.Equal("33", h.Variable.Get("Triple"));
        Assert.Equal("HelloWorld", h.Variable.Get("Msg"));
    }

    [Fact]
    public void Variable_ListOperations_PushPopGetIndexSize()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("""
            Variable@RPush :: var=Q, val=A;
            Variable@RPush :: var=Q, val=B;
            Variable@LPush :: var=Q, val=Z;
            Variable@LGetIndexSize :: var=Q;
            Variable@LPop :: var=Q -> out=First;
            Variable@RPop :: var=Q -> out=Last;
            """);

        Assert.All(results, r => Assert.Equal(CommandStatus.Completed, r.Status));
        Assert.Equal("3", h.Variable.Get("Q#Size"));
        Assert.Equal("Z", h.Variable.Get("First"));
        Assert.Equal("B", h.Variable.Get("Last"));
    }

    [Fact]
    public void Variable_SetOperations_AddUnionIntersectionDiff()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("""
            Variable@SAdd :: var=S1, val=A;
            Variable@SAdd :: var=S1, val=B;
            Variable@SAdd :: var=S2, val=B;
            Variable@SAdd :: var=S2, val=C;
            Variable@SUnion :: var=SU, varA=S1, varB=S2;
            Variable@SIntersection :: var=SI, varA=S1, varB=S2;
            Variable@SDiff :: var=SD, varA=S1, varB=S2;
            """);

        Assert.All(results, r => Assert.Equal(CommandStatus.Completed, r.Status));
        Assert.Contains("B", h.Variable.Get("SI"));
        Assert.Contains("A", h.Variable.Get("SD"));
    }

    [Fact]
    public void Variable_RenameDeleteSaveLoad_WorkEndToEnd()
    {
        var h = new ModuleVerificationHarness();
        string tmpFile = Path.Combine(Path.GetTempPath(), $"arcos_var_{Guid.NewGuid():N}.txt");
        try
        {
            var (_, results) = h.RunScript($"""
                Variable@Set :: var=OldName, val=99;
                Variable@Rename :: var=OldName, to=NewName;
                Variable@VariableSave :: file="{tmpFile}";
                Variable@Delete :: var=NewName;
                Variable@VariableLoad :: file="{tmpFile}";
                """);

            Assert.All(results, r => Assert.Equal(CommandStatus.Completed, r.Status));
            Assert.Equal("99", h.Variable.Get("NewName"));
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    [Fact]
    public void Variable_AllCsvis_GuideUiAndSchemaAudit()
    {
        var h = new ModuleVerificationHarness();
        var audits = h.AuditAllCsvisInModule("VARIABLE");
        Assert.Equal(34, audits.Count);
        foreach (var a in audits)
        {
            _output.WriteLine($"{a.FileName,-26} | Schema:{a.HasManifestSchema,-5} | AXAML:{a.HasAxamlGuideUi,-5} | Guide:{a.GuideOutputStatus}");
            Assert.True(a.HasManifestSchema, $"Missing Schema in {a.FileName}");
            Assert.True(a.HasAxamlGuideUi, $"Missing AXAML in {a.FileName}");
        }
    }
}
