using System.Diagnostics;
using ARCOS.Core.Execution;
using Xunit;
using Xunit.Abstractions;

namespace ARCOS.Core.Tests.ModuleVerification;

public class SystemModuleVerificationTests
{
    private readonly ITestOutputHelper _output;

    public SystemModuleVerificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void System_01_Delay_WaitsSpecifiedSeconds()
    {
        var h = new ModuleVerificationHarness();
        var sw = Stopwatch.StartNew();
        var (_, results) = h.RunScript("System@Delay :: sec=0.06;", maxSteps: 5000);
        sw.Stop();

        Assert.NotEmpty(results);
        Assert.Equal(CommandStatus.Completed, results[^1].Status);
        Assert.True(sw.ElapsedMilliseconds >= 40, $"Expected >= 40ms delay, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void System_02_LogMessage_AppendsMessageToLogs()
    {
        var h = new ModuleVerificationHarness();
        var logs = new List<string>();
        h.Engines.Record.Recorded += (ch, _, msg) => { if (ch == "SYSTEMLOG") logs.Add(msg); };
        var (_, results) = h.RunScript("""System@Log Message :: msg="Production Started";""");

        Assert.Single(results);
        Assert.Equal(CommandStatus.Completed, results[0].Status);
        Assert.Contains(logs, m => m.Contains("Production Started", StringComparison.Ordinal));
    }

    [Fact]
    public void System_03_LogError_FailsOrRecordsError()
    {
        var h = new ModuleVerificationHarness();
        var logs = new List<string>();
        h.Engines.Record.Recorded += (ch, _, msg) => { if (ch == "SYSTEMLOG") logs.Add(msg); };
        var (_, results) = h.RunScript("""System@Log Error :: msg="Vacuum Sensor Fault";""");

        Assert.Single(results);
        Assert.True(
            results[0].Status == CommandStatus.Error ||
            logs.Any(m => m.Contains("Vacuum Sensor Fault", StringComparison.Ordinal)),
            $"Expected error status or log, got Status={results[0].Status}, Err={results[0].ErrorMessage}");
    }

    [Fact]
    public void System_04_ClearMessage_ExecutesCleanly()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("""
            System@Log Message :: msg="TempMsg";
            System@Clear Message;
            """);

        Assert.Equal(2, results.Count);
        Assert.Equal(CommandStatus.Completed, results[1].Status);
    }

    [Fact]
    public void System_05_ClearError_ClearsErrorState()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("System@Clear Error;");

        Assert.Single(results);
        Assert.Equal(CommandStatus.Completed, results[0].Status);
    }

    [Fact]
    public void System_06_Note_HasNoSideEffects()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("""System@Note :: text="=== 進料段初始化 ===";""");

        Assert.Single(results);
        Assert.Equal(CommandStatus.Completed, results[0].Status);
    }

    [Fact]
    public void System_07_MessageBox_ExecutesInHeadlessMode()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("""System@MessageBox :: msg="請確認治具已鎖緊";""");

        Assert.Single(results);
        Assert.Equal(CommandStatus.Completed, results[0].Status);
    }

    [Fact]
    public void System_08_WarningBox_EvaluatesLogicCondition()
    {
        var h = new ModuleVerificationHarness();
        h.Io.AddVirtualIO("LowTray?", false);
        var (_, resFalse) = h.RunScript("""System@WarningBox :: msg="料盤即將耗盡" {logic=LowTray?};""");
        Assert.Single(resFalse);
        Assert.Equal(CommandStatus.Completed, resFalse[0].Status);

        h.Io.SetPort("LowTray?", true);
        var (_, resTrue) = h.RunScript("""System@WarningBox :: msg="料盤即將耗盡" {logic=LowTray?};""");
        Assert.Single(resTrue);
        Assert.Equal(CommandStatus.Completed, resTrue[0].Status);
    }

    [Fact]
    public void System_09_10_PauseAndResume_ExecuteWithV2Syntax()
    {
        var h = new ModuleVerificationHarness();
        h.Io.AddVirtualIO("DoorOpen?", false);
        var (_, results) = h.RunScript("""
            System@Pause {threads=Thread1, logic=DoorOpen?};
            System@Resume {threads=Thread1};
            """);

        Assert.Equal(2, results.Count);
        Assert.Equal(CommandStatus.Completed, results[0].Status);
        Assert.Equal(CommandStatus.Completed, results[1].Status);
    }

    [Fact]
    public void System_11_12_TimerResetAndCheck_UpdateTimeoutFlag()
    {
        var h = new ModuleVerificationHarness();
        var (_, res1) = h.RunScript("System@TimerReset :: timer=T_Heat {target=0.05};");
        Assert.Equal(CommandStatus.Completed, res1[0].Status);
        Assert.False(h.Io.GetPort("T_Heat#Timeout?"));

        Thread.Sleep(70);
        var (_, res2) = h.RunScript("System@TimerCheck :: timer=T_Heat;");
        Assert.Equal(CommandStatus.Completed, res2[0].Status);
        Assert.True(h.Io.GetPort("T_Heat#Timeout?"));
    }

    [Fact]
    public void System_13_14_CounterResetAndCheck_UpdateOverFlag()
    {
        var h = new ModuleVerificationHarness();
        var (_, res1) = h.RunScript("System@CounterReset :: counter=C_Retry {target=2};");
        Assert.Equal(CommandStatus.Completed, res1[0].Status);
        Assert.False(h.Io.GetPort("C_Retry#Over?"));

        var (_, res2) = h.RunScript("""
            System@CounterCheck :: counter=C_Retry;
            System@CounterCheck :: counter=C_Retry;
            """);
        Assert.Equal(CommandStatus.Completed, res2[1].Status);
        Assert.True(h.Io.GetPort("C_Retry#Over?"));
    }

    [Fact]
    public void System_15_16_RecordAndLogRecordCycleTime_OutputsCycleSec()
    {
        var h = new ModuleVerificationHarness();
        var logs = new List<string>();
        h.Engines.Record.Recorded += (ch, _, msg) => { if (ch == "SYSTEMLOG") logs.Add(msg); };
        var (_, res1) = h.RunScript("System@Record :: rec=CT_Pick {msg=Start};");
        Assert.Equal(CommandStatus.Completed, res1[0].Status);

        Thread.Sleep(30);
        var (_, res2) = h.RunScript("""System@LogRecordCycleTime :: rec=CT_Pick {prefix="Pick CT:"} -> var=CycleSec;""");
        Assert.Equal(CommandStatus.Completed, res2[0].Status);

        string cycleVal = h.Variable.Get("CycleSec");
        Assert.True(double.TryParse(cycleVal, out double sec) && sec > 0, $"Expected positive CycleSec, got '{cycleVal}'");
        Assert.Contains(logs, m => m.Contains("Pick CT:", StringComparison.Ordinal));
    }

    [Fact]
    public void System_17_ProgramLoad_LoadsTempProgFile()
    {
        var h = new ModuleVerificationHarness();
        string tmpProg = Path.Combine(Path.GetTempPath(), $"arcos_sys_test_{Guid.NewGuid():N}.prog");
        try
        {
            File.WriteAllText(tmpProg, "System@Note :: text=\"Loaded\";\n");
            var (_, results) = h.RunScript($"System@ProgramLoad :: file=\"{tmpProg}\";");
            Assert.Single(results);
            Assert.Equal(CommandStatus.Completed, results[0].Status);
        }
        finally
        {
            if (File.Exists(tmpProg)) File.Delete(tmpProg);
        }
    }

    [Fact]
    public void System_18_ProgramSection_SwitchesSection()
    {
        var h = new ModuleVerificationHarness();
        var (ctx, results) = h.RunScript("System@ProgramSection :: section=StationA {title=Load};");
        Assert.Single(results);
        Assert.Equal(CommandStatus.Completed, results[0].Status);
        Assert.Equal("StationA", ctx.CurrentSection);
    }

    [Fact]
    public void System_19_IncludeModule_LoadsIOModule()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("System@IncludeModule :: module=IO;");
        Assert.Single(results);
        Assert.Equal(CommandStatus.Completed, results[0].Status);
        Assert.True(h.Registry.HasModule("IO"));
    }

    [Fact]
    public void System_20_21_UserPanelOpenAndClose_ExecuteCleanly()
    {
        var h = new ModuleVerificationHarness();
        var (_, results) = h.RunScript("""
            System@UserPanelOpen :: panel=UserPanel.csvi {alias=StationB, activate=false};
            System@UserPanelClose :: panel=StationB;
            """);

        Assert.Equal(2, results.Count);
        Assert.Equal(CommandStatus.Completed, results[0].Status);
        Assert.Equal(CommandStatus.Completed, results[1].Status);
    }

    [Fact]
    public void System_All21Csvis_GuideUiAndSchemaAudit()
    {
        var h = new ModuleVerificationHarness();
        string[] csvis =
        [
            "Delay.csvi", "Log Message.csvi", "Log Error.csvi", "Clear Message.csvi", "Clear Error.csvi",
            "Note.csvi", "MessageBox.csvi", "WarningBox.csvi", "Pause.csvi", "Resume.csvi",
            "TimerReset.csvi", "TimerCheck.csvi", "CounterReset.csvi", "CounterCheck.csvi",
            "Record.csvi", "LogRecordCycleTime.csvi", "ProgramLoad.csvi", "ProgramSection.csvi",
            "IncludeModule.csvi", "UserPanelOpen.csvi", "UserPanelClose.csvi"
        ];

        foreach (var f in csvis)
        {
            var audit = h.AuditCsviGuideUi("SYSTEM", f);
            _output.WriteLine($"{audit.FileName,-26} | Schema:{audit.HasManifestSchema,-5} | AXAML:{audit.HasAxamlGuideUi,-5} | Guide:{audit.GuideOutputStatus,-20} | {audit.SampleCodeSnippet}");
            Assert.True(audit.Exists, $"Missing {f}");
            Assert.True(audit.HasManifestSchema, $"Missing Manifest.Schema in {f}");
            Assert.True(audit.HasAxamlGuideUi, $"Missing AXAML Guide UI in {f}");
        }
    }
}
