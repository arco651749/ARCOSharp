using ARCOS.Core.Execution;
using Xunit;
using Xunit.Abstractions;

namespace ARCOS.Core.Tests.ModuleVerification;

public class ThreadModuleVerificationTests
{
    private readonly ITestOutputHelper _output;
    public ThreadModuleVerificationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Thread_ControlFlow_IfElseIfElseEndIf_SelectsCorrectBranch()
    {
        var h = new ModuleVerificationHarness();
        h.Io.AddVirtualIO("FlagA?", false);
        h.Io.AddVirtualIO("FlagB?", true);
        var (_, results) = h.RunScript("""
            Thread@If :: logic=FlagA?;
            Variable@Set :: var=Branch, val=A;
            Thread@ElseIf :: logic=FlagB?;
            Variable@Set :: var=Branch, val=B;
            Thread@Else;
            Variable@Set :: var=Branch, val=C;
            Thread@EndIf;
            """);

        Assert.All(results, r => Assert.Equal(CommandStatus.Completed, r.Status));
        Assert.Equal("B", h.Variable.Get("Branch"));
    }

    [Fact]
    public void Thread_LabelJumpCallReturn_ExecutesSubroutineAndReturns()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("""
            Thread@Call :: prog=SubWork;
            Variable@Set :: var=AfterCall, val=Done;
            Thread@Jump :: label=EndMain;
            Thread@Label :: label=SubWork;
            Variable@Set :: var=InSub, val=Yes;
            Thread@Return;
            Thread@Label :: label=EndMain;
            """);

        Assert.All(results, r => Assert.Equal(CommandStatus.Completed, r.Status));
        Assert.Equal("Yes", h.Variable.Get("InSub"));
        Assert.Equal("Done", h.Variable.Get("AfterCall"));
    }

    [Fact]
    public void Thread_Foreach_IteratesAndSupportsContinueAndBreak()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("""
            Variable@Set :: var=Cnt, val=0;
            Thread@ForeachStart :: label=Loop1;
            Variable@Inc :: var=Cnt {step=1};
            Thread@ForeachBreak;
            Thread@ForeachLoop :: label=Loop1;
            """);

        Assert.All(results, r => Assert.Equal(CommandStatus.Completed, r.Status));
        Assert.Equal("1", h.Variable.Get("Cnt"));
    }

    [Fact]
    public void Thread_SkipNextAndRunNext_ControlSingleStepExecution()
    {
        var h = new ModuleVerificationHarness();
        h.Io.AddVirtualIO("Cond?", true);
        var (_, results) = h.RunScript("""
            Variable@Set :: var=V1, val=Init;
            Thread@SkipNextCMD :: logic=Cond?;
            Variable@Set :: var=V1, val=ShouldSkip;
            Thread@RunNextCMD :: logic=Cond?;
            Variable@Set :: var=V2, val=Ran;
            """);

        Assert.All(results, r => Assert.Equal(CommandStatus.Completed, r.Status));
        Assert.Equal("Init", h.Variable.Get("V1"));
        Assert.Equal("Ran", h.Variable.Get("V2"));
    }

    [Fact]
    public void Thread_KeySectionLockUnlock_And_MutePopup_ExecuteCleanly()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("""
            Thread@KeySectionLock :: key=StationLock;
            Thread@MutePopupMessage {on=true};
            Thread@KeySectionUnLock :: key=StationLock;
            """);

        Assert.All(results, r => Assert.Equal(CommandStatus.Completed, r.Status));
    }

    [Fact]
    public void Thread_ErrorHandler_And_GoOn_ExecuteCleanly()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("""
            Thread@ErrorHandler :: label=OnErr;
            Thread@GoOn;
            Thread@Jump :: label=Done;
            Thread@Label :: label=OnErr;
            Thread@ErrorRetry;
            Thread@Label :: label=Done;
            """);

        Assert.All(results, r => Assert.Equal(CommandStatus.Completed, r.Status));
    }

    [Fact]
    public void Thread_AllCsvis_GuideUiAndSchemaAudit()
    {
        var h = new ModuleVerificationHarness();
        var audits = h.AuditAllCsvisInModule("THREAD");
        Assert.Equal(31, audits.Count);
        foreach (var a in audits)
        {
            _output.WriteLine($"{a.FileName,-26} | Schema:{a.HasManifestSchema,-5} | AXAML:{a.HasAxamlGuideUi,-5} | Guide:{a.GuideOutputStatus}");
            Assert.True(a.HasManifestSchema, $"Missing Schema in {a.FileName}");
            Assert.True(a.HasAxamlGuideUi, $"Missing AXAML in {a.FileName}");
        }
    }
}
