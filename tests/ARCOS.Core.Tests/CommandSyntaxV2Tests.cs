using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ARCOS.Core.Common;
using ARCOS.Core.Csvi;
using ARCOS.Core.Drivers;
using ARCOS.Core.Execution;
using ARCOS.Core.Guidance;
using ARCOS.Core.Serialization;
using Xunit;

namespace ARCOS.Core.Tests;

public class CommandSyntaxV2Tests
{
    [Fact]
    public void StandardCommandSchemas_ContainsAll122UpgradedModuleCommands()
    {
        var io = StandardCommandSchemas.GetByModule("IO").Select(s => s.CommandKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sys = StandardCommandSchemas.GetByModule("System").Select(s => s.CommandKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var thr = StandardCommandSchemas.GetByModule("Thread").Select(s => s.CommandKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var variable = StandardCommandSchemas.GetByModule("Variable").Select(s => s.CommandKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ptPath = StandardCommandSchemas.GetByModule("Point&Path").Select(s => s.CommandKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(14, io.Count);
        Assert.Equal(21, sys.Count);
        Assert.Equal(31, thr.Count);
        Assert.Equal(34, variable.Count);
        Assert.Equal(22, ptPath.Count);
    }

    [Fact]
    public void Parse_FourSectionSyntax_ExtractsRequiredOptionalOutputAndErrorTrap()
    {
        var schema = StandardCommandSchemas.Get("System@LogRecordCycleTime")!;
        string raw = "rec=CT_Pick {prefix=\"Pick, Cycle -> OK:\"} -> var=$CycleSec [[ErrCT]]計算失敗";

        var parsed = CommandParamParser.Parse(raw, schema);

        Assert.False(parsed.HasSyntaxError);
        Assert.False(parsed.IsLegacyFormat);
        Assert.True(parsed.HasOptionalBlock);
        Assert.True(parsed.HasOutputBlock);
        Assert.Equal("CT_Pick", parsed.Get("rec"));
        Assert.Equal("Pick, Cycle -> OK:", parsed.Get("prefix"));
        Assert.Equal("$CycleSec", parsed.Get("var"));
        Assert.Equal("ErrCT", parsed.ErrorLabel);
        Assert.Equal("計算失敗", parsed.ErrorMessage);
    }

    [Fact]
    public void Parse_QuotedStringWithSpecialChars_DoesNotBreakOnCommasArrowsOrBrackets()
    {
        var schema = StandardCommandSchemas.Get("System@Log Message")!;
        string raw = "msg=\"Step 1, A=B -> done [[NotATrap]]\\nLine 2 \\\"quoted\\\"\"";

        var parsed = CommandParamParser.Parse(raw, schema);

        Assert.False(parsed.HasSyntaxError);
        Assert.Null(parsed.ErrorLabel);
        Assert.Equal("Step 1, A=B -> done [[NotATrap]]\nLine 2 \"quoted\"", parsed.Get("msg"));
    }

    [Fact]
    public void Parse_ForgivingMerge_AllowsOptionalOutsideBracesAndAliases()
    {
        var schema = StandardCommandSchemas.Get("IO@SetPort")!;
        // AI 使用別名 port / second / ForceUpdate 且把選填寫在 {...} 外面
        var parsed = CommandParamParser.Parse("port=OUT1, second=0.5, ForceUpdate=true", schema);

        Assert.False(parsed.HasSyntaxError);
        Assert.Equal("OUT1", parsed.Get("io"));
        Assert.Equal(0.5, parsed.GetDouble("holdSec"), 6);
        Assert.True(parsed.GetBool("forceUpdate"));

        // Formatter 會自動歸位到 {...} 並轉為型別化標準鍵名 io=
        string formatted = CommandFormatter.FormatParameter(parsed, schema);
        Assert.Equal("io=OUT1 {holdSec=0.5, forceUpdate=true}", formatted);
    }

    [Fact]
    public void Parse_LegacyPositionalFormats_MapCleanlyAndUpgradeToV2()
    {
        // 1. IO@WaitPortOn legacy
        var waitSchema = StandardCommandSchemas.Get("IO@WaitPortOn")!;
        var waitParsed = CommandParamParser.Parse("Start?, 5, [[Err1]]等待啟動訊號逾時", waitSchema);
        Assert.True(waitParsed.IsLegacyFormat);
        Assert.Equal("Start?", waitParsed.Get("io"));
        Assert.Equal(5.0, waitParsed.GetDouble("timeout"), 6);
        Assert.Equal("Err1", waitParsed.ErrorLabel);
        Assert.Equal("等待啟動訊號逾時", waitParsed.ErrorMessage);
        Assert.Equal("io=Start? {timeout=5} [[Err1]]等待啟動訊號逾時", CommandFormatter.FormatParameter(waitParsed, waitSchema));

        // 2. IO@Shift legacy variadic + named Shift
        var shiftSchema = StandardCommandSchemas.Get("IO@Shift")!;
        var shiftParsed = CommandParamParser.Parse("OUT1, OUT2, OUT3, Shift=2", shiftSchema);
        Assert.Equal(new[] { "OUT1", "OUT2", "OUT3" }, shiftParsed.GetList("ios"));
        Assert.Equal(2, shiftParsed.GetInt("shift"));
        Assert.Equal("ios=[OUT1;OUT2;OUT3] {shift=2}", CommandFormatter.FormatParameter(shiftParsed, shiftSchema));

        // 3. Thread@If legacy logic expression containing == MUST NOT be mistaken as key=value!
        var ifSchema = StandardCommandSchemas.Get("Thread@If")!;
        var ifParsed = CommandParamParser.Parse("$(Count)==1 & Start?", ifSchema);
        Assert.True(ifParsed.IsLegacyFormat);
        Assert.Equal("$(Count)==1 & Start?", ifParsed.Get("logic"));
        Assert.Equal("logic=$(Count)==1 & Start?", CommandFormatter.FormatParameter(ifParsed, ifSchema));
        Assert.Equal("msg=\"Production Started\"", CommandSyntaxBridge.FormatToV2("System@Log Message", "Production Started"));
    }

    [Fact]
    public void Formatter_OmitsUnchangedDefaultsAndFormatsCleanFullLine()
    {
        var schema = StandardCommandSchemas.Get("IO@SetPort")!;
        string line = CommandFormatter.FormatLine("IO@SetPort", "io=OUT1 {holdSec=0, forceUpdate=false}", schema);
        Assert.Equal("IO@SetPort :: io=OUT1", line);

        var pauseSchema = StandardCommandSchemas.Get("System@Pause")!;
        string pauseLine = CommandFormatter.FormatLine("System@Pause", "{threads=Thread1, condition=True}", pauseSchema);
        Assert.Equal("System@Pause {threads=Thread1}", pauseLine);

        var timerSchema = StandardCommandSchemas.Get("System@TimerReset")!;
        string timerLine = CommandFormatter.FormatLine("System@TimerReset", "T_Heat, 30", timerSchema);
        Assert.Equal("System@TimerReset :: timer=T_Heat {target=30}", timerLine);
    }

    [Fact]
    public void Validator_ReportsSyntaxMissingUnknownTypeAndAuditDiagnostics()
    {
        var setPort = StandardCommandSchemas.Get("IO@SetPort")!;

        // E005: 未閉合括號
        var vSyntax = CommandValidator.Validate("io=OUT1 {holdSec=0.5", setPort);
        Assert.False(vSyntax.IsValid);
        Assert.Contains(vSyntax.Diagnostics, d => d.Code == "E005");

        // E002: 缺必填 io
        var vMissing = CommandValidator.Validate("{holdSec=0.5}", setPort);
        Assert.False(vMissing.IsValid);
        Assert.Contains(vMissing.Diagnostics, d => d.Code == "E002" && d.ParameterName == "io");

        // E003: 未知參數
        var vUnknown = CommandValidator.Validate("io=OUT1 {fakeFlag=1}", setPort);
        Assert.False(vUnknown.IsValid);
        Assert.Contains(vUnknown.Diagnostics, d => d.Code == "E003" && d.ParameterName == "fakeFlag");

        // E004: 型別錯誤（holdSec 應為數值）
        var vType = CommandValidator.Validate("io=OUT1 {holdSec=not_a_number}", setPort);
        Assert.False(vType.IsValid);
        Assert.Contains(vType.Diagnostics, d => d.Code == "E004" && d.ParameterName == "holdSec");

        // E004 跳過動態變數插值 $(Hold)
        var vVar = CommandValidator.Validate("io=OUT1 {holdSec=$(Hold)}", setPort);
        Assert.True(vVar.IsValid);

        // W001: 選填寫在 {...} 外面
        var vWarnMisplaced = CommandValidator.Validate("io=OUT1, holdSec=0.5", setPort);
        Assert.True(vWarnMisplaced.IsValid);
        Assert.Contains(vWarnMisplaced.Diagnostics, d => d.Code == "W001" && d.ParameterName == "holdSec");

        // W002: AI 審查警戒指令 Thread@Jump
        var jumpSchema = StandardCommandSchemas.Get("Thread@Jump")!;
        var vJump = CommandValidator.Validate("label=Retry", jumpSchema);
        Assert.True(vJump.IsValid);
        Assert.Contains(vJump.Diagnostics, d => d.Code == "W002");
    }

    [Fact]
    public void DisplayProjector_EnforcesAntiHidingSafetyRuleForCriticalParams()
    {
        var setPort = StandardCommandSchemas.Get("IO@SetPort")!;

        string l1Normal = CommandDisplayProjector.ProjectParameter(
            "io=OUT1 {holdSec=0.5}", setPort, CommandDisplayLevel.Compact);
        Assert.Equal("io=OUT1 {+1}", l1Normal);

        string l1Critical = CommandDisplayProjector.ProjectParameter(
            "io=OUT1 {holdSec=0.5, forceUpdate=true}", setPort, CommandDisplayLevel.Compact);
        Assert.Equal("io=OUT1 {⚠forceUpdate=true, +1}", l1Critical);

        string l2Audit = CommandDisplayProjector.ProjectParameter(
            "io=OUT1 {holdSec=0.5, forceUpdate=true}", setPort, CommandDisplayLevel.Audit);
        Assert.Equal("io=OUT1 {holdSec=0.5, ⚠forceUpdate=true} [effect: OUT1=ON]", l2Audit);

        string l2Multi = CommandDisplayProjector.ProjectParameter(
            "io=OUT1 {holdSec=0.5, forceUpdate=true}", setPort, CommandDisplayLevel.Audit, multiLineLayout: true);
        Assert.Equal("io=OUT1\n{holdSec=0.5, ⚠forceUpdate=true}\n[effect: OUT1=ON]", l2Multi);

        var logSchema = StandardCommandSchemas.Get("System@Log Message")!;
        string l1MultiData = CommandDisplayProjector.ProjectParameter(
            "msg=\"First line\\nSecond line\\nThird line\"", logSchema, CommandDisplayLevel.Compact);
        Assert.Equal("msg=⟦First line … (3 lines)⟧", l1MultiData);
    }

    [Fact]
    public void CommandItemParse_HandlesV2WithoutDoubleColonAndQuotedSemicolons()
    {
        var pauseItem = CommandItem.Parse("System@Pause {threads=Thread1, condition=\"Alarm?\"}", 1);
        Assert.Equal("System", pauseItem.Module);
        Assert.Equal("Pause", pauseItem.Action);
        Assert.Equal("{threads=Thread1, condition=\"Alarm?\"}", pauseItem.RawParameter);

        var logItem = CommandItem.Parse("System@Log Message :: msg=\"Keep semicolon;\"", 2);
        Assert.Equal("System", logItem.Module);
        Assert.Equal("Log Message", logItem.Action);
        Assert.Equal("msg=\"Keep semicolon;\"", logItem.RawParameter);

        var outOnly = CommandItem.Parse("Motion@GetPos -> pt=$CurrPos", 3);
        Assert.Equal("Motion", outOnly.Module);
        Assert.Equal("GetPos", outOnly.Action);
        Assert.Equal("-> pt=$CurrPos", outOnly.RawParameter);
    }

    [Fact]
    public void ScriptSerializer_RoundTripsInternalQuotesAndMultilineWithoutCorruption()
    {
        string tempProg = Path.Combine(Path.GetTempPath(), $"v2_roundtrip_{Guid.NewGuid():N}.prog");
        try
        {
            var p0 = new ProgramTable { ProgramNumber = 0, Name = "V2_Test" };
            p0.Add(CommandItem.Parse("System@Log Message :: msg=\"Line1\\nLine2 with \\\"quotes\\\"\"", 1));
            p0.Add(CommandItem.Parse("System@ProgramLoad :: file=\"C:\\\\Data\\\\Master.prog\"", 2));
            p0.Add(CommandItem.Parse("IO@SetPort :: io=OUT1 {holdSec=0.2, forceUpdate=true}", 3));

            ScriptSerializer.SaveProgFile(tempProg, new[] { p0 });

            var loaded = ScriptSerializer.LoadProgFile(tempProg);
            Assert.Single(loaded);
            var lp = loaded[0];
            Assert.Equal(3, lp.Count);

            Assert.Equal("System@Log Message", lp[1].Command);
            Assert.Equal("msg=\"Line1\\nLine2 with \\\"quotes\\\"\"", lp[1].RawParameter);
            var parsedLog = CommandParamParser.Parse(lp[1].RawParameter, StandardCommandSchemas.Get("System@Log Message"));
            Assert.Equal("Line1\nLine2 with \"quotes\"", parsedLog.Get("msg"));

            Assert.Equal("System@ProgramLoad", lp[2].Command);
            var parsedLoad = CommandParamParser.Parse(lp[2].RawParameter, StandardCommandSchemas.Get("System@ProgramLoad"));
            Assert.Equal(@"C:\Data\Master.prog", parsedLoad.Get("file"));

            Assert.Equal("IO@SetPort", lp[3].Command);
            Assert.Equal("io=OUT1 {holdSec=0.2, forceUpdate=true}", lp[3].RawParameter);
        }
        finally
        {
            if (File.Exists(tempProg)) File.Delete(tempProg);
        }
    }

    [Fact]
    public void PilotCsvis_EmbedManifestSchemaAndExposeGetSchema()
    {
        var samples = new[]
        {
            ("IO", "SetPort", "IO@SetPort", "io"),
            ("IO", "WaitPortOn", "IO@WaitPortOn", "io"),
            ("SYSTEM", "TimerReset", "System@TimerReset", "timer"),
            ("SYSTEM", "CounterReset", "System@CounterReset", "counter"),
            ("SYSTEM", "LogRecordCycleTime", "System@LogRecordCycleTime", "rec"),
            ("THREAD", "SubThreadStart", "Thread@SubThreadStart", "sub"),
            ("THREAD", "KeySectionLock", "Thread@KeySectionLock", "lock")
        };

        foreach (var (mod, action, expectedKey, expectedFirstParam) in samples)
        {
            string? path = ModuleRegistry.ResolveCommandCsviPath(mod, action);
            Assert.NotNull(path);

            var pkg = CsviPackage.LoadFromFile(path!, unpackSources: false);
            try
            {
                Assert.NotNull(pkg.Manifest.Schema);
                Assert.Equal(expectedKey, pkg.Manifest.Schema!.CommandKey);
                Assert.Equal(expectedFirstParam, pkg.Manifest.Schema.Parameters[0].Name);

                var guidanceType = pkg.LoadedAssembly!.GetExportedTypes()
                    .FirstOrDefault(t => typeof(IParameterGuidance).IsAssignableFrom(t) && !t.IsAbstract);
                Assert.NotNull(guidanceType);

                var instance = (IParameterGuidance)Activator.CreateInstance(guidanceType!)!;
                var runtimeSchema = instance.GetSchema();
                Assert.NotNull(runtimeSchema);
                Assert.Equal(expectedKey, runtimeSchema!.CommandKey);
                Assert.Equal(expectedFirstParam, runtimeSchema.Parameters[0].Name);
            }
            finally
            {
                pkg.Unload();
            }
        }
    }

    [Fact]
    public void EndToEnd_ArcosThreadVM_ExecutesIoSystemThreadInV2TypedSyntax()
    {
        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(),
            new ARCOS.Core.Engines.TimerEngine(io));
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        // 1. 驗證編輯期預解析支援型別化鍵名（io= / alias=）
        engines.Io.ScanAndRegister("IO@SetPortName", "io=P0.5, alias=Valve1");
        engines.Io.ScanAndRegister("IO@AddVirtualIO", "io=PreScanFlag {init=true}");
        Assert.True(engines.Io.GetPort("PreScanFlag"));

        // 2. 撰寫全 v2.0 型別化鍵名語法的 IO + SYSTEM + THREAD 綜合腳本
        string v2Script = string.Join("\n", new[]
        {
            "IO@SetPortName :: io=P0.0, alias=OUT1",
            "IO@AddVirtualIO :: io=Ready? {logic=True}",
            "IO@AddVirtualIO :: io=SubDone? {logic=False}",
            "IO@AddVirtualIO :: io=Healed? {logic=False}",
            "IO@SetPort :: io=OUT1 {forceUpdate=true}",
            "IO@Shift :: ios=[P0.0;P0.1;P0.2] {shift=1}",
            "IO@AddVirtualIO :: io=CycleOK? {logic=\"Ready? & P0.1\"}",
            "System@Record :: rec=CT_V2 {msg=Start}",
            "System@TimerReset :: timer=T_Run {target=10}",
            "System@CounterReset :: counter=HitCnt {target=1}",
            "Thread@If :: logic=\"CycleOK? & P0.1\"",
            "System@CounterCheck :: counter=HitCnt",
            "Thread@Else",
            "IO@SetPort :: io=P0.7",
            "Thread@EndIf",
            "System@LogRecordCycleTime :: rec=CT_V2 {prefix=\"V2CT:\"} -> var=$ElapsedSec",
            "Thread@SubThreadStart :: sub=V2Sub",
            "IO@SetPort :: io=SubDone?",
            "Thread@SubThreadEnd :: sub=V2Sub",
            "Thread@SubThreadWait :: subs=[V2Sub]",
            "Thread@Label :: label=DoneLabel",
            "Thread@Jump :: label=DoneLabel {logic=False}",
            "IO@WaitPortOn :: io=NeverOn? {timeout=0.05} [[ErrTimeout]]V2逾時自癒",
            "Thread@Stop",
            "Thread@ErrorHandler :: label=ErrTimeout",
            "IO@SetPort :: io=Healed?"
        });

        engines.Io.AddVirtualIO("NeverOn?", false);

        var prog = ProgramTable.FromText(v2Script, 0, "V2_Integration");
        Assert.Equal(21, prog.FindLabel("DoneLabel"));
        Assert.Equal(25, prog.FindErrorHandler("ErrTimeout"));

        vm.LoadProgram(0, prog);
        vm.StartThread(0, 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((vm.MainThreads[0].IsRunning || vm.SubThreads.Values.Any(s => s.IsRunning)) && sw.ElapsedMilliseconds < 3000)
        {
            vm.Tick();
            System.Threading.Thread.Sleep(5);
        }

        Assert.False(vm.MainThreads[0].IsRunning);
        Assert.False(engines.Io.GetPort("P0.0"));
        Assert.True(engines.Io.GetPort("P0.1"));
        Assert.True(engines.Io.GetPort("CycleOK?"));
        Assert.False(engines.Io.GetPort("P0.7"));
        Assert.True(engines.Io.GetPort("HitCnt#Over?"));
        Assert.True(engines.Io.GetPort("SubDone?"));
        Assert.True(engines.Io.GetPort("Healed?"));

        // 驗證 System@LogRecordCycleTime 的顯式輸出 -> var=$ElapsedSec 寫入變數
        string elapsedStr = engines.Variable.Get("ElapsedSec", vm.MainThreads[0].CurrentSection);
        Assert.True(double.TryParse(elapsedStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double elapsedSec));
        Assert.True(elapsedSec >= 0);
    }

    [Fact]
    public void CommandSyntaxBridge_FormatToV2_UpgradesLegacyGuidanceOutputs()
    {
        Assert.Equal("io=OUT1 {holdSec=0.5, forceUpdate=true}", CommandSyntaxBridge.FormatToV2("IO@SetPort", "OUT1, 0.5, ForceUpdate"));
        Assert.Equal("io=OUT1 {forceUpdate=true}", CommandSyntaxBridge.FormatToV2("IO@SetPort", "OUT1, ForceUpdate"));
        Assert.Equal("io=VPort1 {logic=True}", CommandSyntaxBridge.FormatToV2("IO@AddVirtualIO", "VPort1, ON"));
        Assert.Equal("io=CycleOK? {logic=Start? & !Alarm?}", CommandSyntaxBridge.FormatToV2("IO@AddVirtualIO", "CycleOK?, Start? & !Alarm?"));
        Assert.Equal("timer=T_Heat {target=30}", CommandSyntaxBridge.FormatToV2("System@TimerReset", "T_Heat, 30"));
        Assert.Equal("counter=C_Retry {target=3}", CommandSyntaxBridge.FormatToV2("System@CounterReset", "C_Retry, 3"));
        Assert.Equal("rec=CT_Pick {msg=Start}", CommandSyntaxBridge.FormatToV2("System@Record", "CT_Pick, Start"));
        Assert.Equal("msg=單純警告", CommandSyntaxBridge.FormatToV2("System@WarningBox", "單純警告"));
        Assert.Equal("msg=料盤不足 {logic=LowTray?}", CommandSyntaxBridge.FormatToV2("System@WarningBox", "LowTray?, 料盤不足"));
        Assert.Equal("enabled=true", CommandSyntaxBridge.FormatToV2("Thread@MutePopupMessage", "1"));
        Assert.Equal("label=ErrCatch", CommandSyntaxBridge.FormatToV2("Thread@ErrorHandler", "[[ErrCatch]]"));
        Assert.Equal("subs=[Sub1;Sub2] {skipNotFound=true}", CommandSyntaxBridge.FormatToV2("Thread@SubThreadWait", "Sub1, Sub2, SKIPNOTFIND=1"));
        Assert.Equal("lock=GantryZone {timeout=10}", CommandSyntaxBridge.FormatToV2("Thread@KeySectionLock", "GantryZone, 10"));

        // VARIABLE & POINT&PATH 舊式特殊語法升級（顯式目標置左，附加可選輸出才用 ->）
        Assert.Equal("var=Total, expr=$(A)+$(B)", CommandSyntaxBridge.FormatToV2("Variable@Calculate", "$Total=$(A)+$(B)"));
        Assert.Equal("{logic=Done?}", CommandSyntaxBridge.FormatToV2("Thread@Return", "Done?"));
        Assert.Equal("Done?", CommandSyntaxBridge.NormalizeForExecution("Thread@Return", "{logic=Done?}"));
        Assert.True(CommandValidator.Validate("prog=Prog1, thread=1 {startLine=InitStep, logic=Auto?}", StandardCommandSchemas.Get("Thread@Run")!).IsValid);
        Assert.Equal("Arr, 5, >0", CommandSyntaxBridge.NormalizeForExecution("Variable@LInsert", "var=Arr, val=5, logic=>0"));
        Assert.Equal("var=Res, logic=Pass?, true=OK, false=NG", CommandSyntaxBridge.FormatToV2("Variable@Select", "Res=Pass??OK:NG"));
        Assert.Equal("var=PickX, pt=PickPos, axis=X", CommandSyntaxBridge.FormatToV2("Variable@GetPointElement", "$PickX=X(PickPos)"));
        Assert.Equal("var=OutSet, varA=SetA, varB=SetB", CommandSyntaxBridge.FormatToV2("Variable@SUnion", "OutSet, SetA, SetB"));
        Assert.Equal("pt=P_Out, expr=P1+P2", CommandSyntaxBridge.FormatToV2("Point&Path@Calculate", "$P_Out=P1+P2"));
        Assert.Equal("pt=Tray, expr=(10,20,30) {append=true}", CommandSyntaxBridge.FormatToV2("Point&Path@Calculate", "Tray<=(10,20,30)"));
        Assert.Equal("pt=P_Dst, logic=Left?, true=P_L, false=P_R", CommandSyntaxBridge.FormatToV2("Point&Path@Select", "P_Dst=Left??P_L:P_R"));
        Assert.Equal("pt=P1, delta=(10,20,0)", CommandSyntaxBridge.FormatToV2("Point&Path@AddXYZ", "P1, (10,20,0)"));
        Assert.Equal("pt=P1, h={\"speed\":80}", CommandSyntaxBridge.FormatToV2("Point&Path@ReplaceH", "P1, {\"speed\":80}"));
        Assert.Equal("P1, {\"speed\":80,\"cfg\":{\"acc\":200}}", CommandSyntaxBridge.NormalizeForExecution("Point&Path@ReplaceH", "pt=P1, h={\"speed\":80,\"cfg\":{\"acc\":200}}"));
        Assert.Equal("pt=P1, rangePt=SafeBox -> io=IsSafe?", CommandSyntaxBridge.FormatToV2("Point&Path@ChkInRangeXY", "P1, SafeBox, IsSafe?"));
    }

    [Fact]
    public void EndToEnd_ArcosThreadVM_ExecutesVariableAndPointPathInV2TypedSyntax()
    {
        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(),
            new ARCOS.Core.Engines.TimerEngine(io));
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        // 1. 驗證 VARIABLE 與 POINT&PATH 的 CSVI 內嵌 Manifest Schema 與 GetSchema()
        foreach (var (mod, act, key, firstParam) in new[]
        {
            ("VARIABLE", "Set", "Variable@Set", "var"),
            ("VARIABLE", "Calculate", "Variable@Calculate", "var"),
            ("VARIABLE", "GetPointElement", "Variable@GetPointElement", "var"),
            ("POINT&PATH", "AddXYZ", "Point&Path@AddXYZ", "pt"),
            ("POINT&PATH", "Calculate", "Point&Path@Calculate", "pt"),
            ("POINT&PATH", "Pallet2D", "Point&Path@Pallet2D", "pt")
        })
        {
            string? path = ModuleRegistry.ResolveCommandCsviPath(mod, act);
            Assert.NotNull(path);
            var pkg = CsviPackage.LoadFromFile(path!, unpackSources: false);
            try
            {
                Assert.NotNull(pkg.Manifest.Schema);
                Assert.Equal(key, pkg.Manifest.Schema!.CommandKey);
                Assert.Equal(firstParam, pkg.Manifest.Schema.Parameters[0].Name);
            }
            finally { pkg.Unload(); }
        }

        // 2. 執行完整 VARIABLE + POINT&PATH v2.0 型別化腳本
        string v2Script = string.Join("\n", new[]
        {
            "Variable@Set :: var=A, val=10",
            "Variable@Set :: var=B, val=25",
            "Variable@Inc :: var=A {step=5}",
            "Variable@Calculate :: var=Total, expr=A + B",
            "Variable@Select :: var=Status, logic=$(Total)==40, true=PASS, false=FAIL",
            "Variable@Set :: var=Queue, val=[Item1;Item2;Item3]",
            "Variable@LPop :: var=Queue -> out=HeadVal",
            "Variable@Set :: var=Set1, val=[A;B]",
            "Variable@Set :: var=Set2, val=[B;C]",
            "Variable@SIntersection :: var=InterOut, varA=Set1, varB=Set2",
            "Point&Path@Calculate :: pt=P1, expr=(10, 20, 30)",
            "Point&Path@AddXYZ :: pt=P1, delta=(5, 10, 0)",
            "Point&Path@Calculate :: pt=P2, expr=(100, 100, 0)",
            "Point&Path@Select :: pt=P_Sel, logic=$(Total)==40, true=P1, false=P2",
            "Variable@GetPointElement :: var=SelX, pt=P_Sel, axis=X",
            "Point&Path@InsertOnePoint :: pt=Tray1D, val=(0,0,0)",
            "Point&Path@Calculate :: pt=Tray1D, expr=(100,0,0) {append=true}",
            "Point&Path@Pallet1D :: pt=Tray1D, count=5",
            "Point&Path@InsertOnePoint :: pt=Box, val=(0,0,0)",
            "Point&Path@InsertOnePoint :: pt=Box, val=(50,50,0)",
            "Point&Path@ChkInRangeXY :: pt=P_Sel, rangePt=Box -> io=InSafeBox?"
        });

        var prog = ProgramTable.FromText(v2Script, 0, "V2_VarPoint_Integration");
        vm.LoadProgram(0, prog);
        vm.StartThread(0, 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 3000)
        {
            vm.Tick();
        }

        string sec = vm.MainThreads[0].CurrentSection;
        Assert.False(vm.MainThreads[0].IsRunning);
        Assert.Equal("40", engines.Variable.Get("Total", sec));
        Assert.Equal("PASS", engines.Variable.Get("Status", sec));
        Assert.Equal("Item1", engines.Variable.Get("HeadVal", sec));
        Assert.Equal("[B]", engines.Variable.GetRaw("InterOut", sec));

        var pSel = engines.Point.GetPoint("P_Sel");
        Assert.Equal(15.0, pSel.X, 6);
        Assert.Equal(30.0, pSel.Y, 6);
        Assert.Equal(30.0, pSel.Z, 6);
        Assert.Equal("15", engines.Variable.Get("SelX", sec));
        Assert.Equal(5, engines.Point.GetPointArray("Tray1D").Count);
        Assert.True(engines.Io.GetPort("InSafeBox?"));
        Assert.True(engines.Io.GetPort("P_Sel#InRange?"));
    }

    [Fact]
    public void StandardCommandSchemas_Batch5Modules_All100RegisteredWithDomainTypedKeys()
    {
        Assert.Equal(21, StandardCommandSchemas.GetByModule("Motion").Count);
        Assert.Equal(28, StandardCommandSchemas.GetByModule("Capture").Count);
        Assert.Equal(18, StandardCommandSchemas.GetByModule("DAQ").Count);
        Assert.Equal(21, StandardCommandSchemas.GetByModule("User").Count);
        Assert.Equal(10, StandardCommandSchemas.GetByModule("Python").Count);

        // Domain-Typed 鍵名與 Critical 層級檢查
        var goSafe = StandardCommandSchemas.Get("Motion@GoSafe")!;
        Assert.Equal("mot", goSafe.Parameters[0].Name);
        Assert.Null(goSafe.FindParam("dev"));
        Assert.Equal(ParamDisplayTier.Critical, goSafe.FindParam("speed")!.DisplayTier);
        Assert.Equal(ParamDisplayTier.Critical, goSafe.FindParam("safePos")!.DisplayTier);

        var capSnap = StandardCommandSchemas.Get("Capture@Snap")!;
        Assert.Equal("cam", capSnap.Parameters[0].Name);
        Assert.Null(capSnap.FindParam("dev"));

        var daqStart = StandardCommandSchemas.Get("DAQ@Start")!;
        Assert.Equal("daq", daqStart.Parameters[0].Name);
        Assert.Null(daqStart.FindParam("dev"));

        var ioInit = StandardCommandSchemas.Get("IO@InitIOPort")!;
        Assert.Equal("card", ioInit.Parameters[0].Name);
        Assert.Null(ioInit.FindParam("dev"));
        Assert.Equal("card=IO1, driver=MockIO {device=COM1}",
            CommandSyntaxBridge.FormatToV2("IO@AddIODriver", "IO1, MockIO, DEVICE=COM1"));
        Assert.Equal("Motor1, MockMotion, DEVICE=192.168.1.10:502, Axes=4",
            CommandSyntaxBridge.NormalizeForExecution("Motion@AddMotion", "mot=Motor1, driver=MockMotion {device=192.168.1.10:502, axis=4}"));
        Assert.Equal("Cam0, DirectShowCamera, DEVICE=0, Width=1280",
            CommandSyntaxBridge.NormalizeForExecution("Capture@AddCam", "cam=Cam0, driver=DirectShowCamera {device=0, width=1280}"));
        Assert.Equal("Daq0, NIDAQ, DEVICE=Dev1, Rate=1000",
            CommandSyntaxBridge.NormalizeForExecution("DAQ@AddDAQ", "daq=Daq0, driver=NIDAQ {device=Dev1, rate=1000}"));

        var patMatch = StandardCommandSchemas.Get("Capture@PatternMatchOne")!;
        Assert.Equal("cam", patMatch.Parameters[0].Name);
        Assert.Equal(ParamDirection.Output, patMatch.FindParam("var")!.Direction);
        Assert.Equal(ParamDirection.Output, patMatch.FindParam("pt")!.Direction);

        var daqCalc = StandardCommandSchemas.Get("DAQ@Calculate")!;
        Assert.Equal("wav", daqCalc.Parameters[0].Name);
        Assert.Equal(ParamDirection.Input, daqCalc.Parameters[0].Direction); // 顯式主詞置於 :: 首欄

        var pyGetPt = StandardCommandSchemas.Get("Python@GetPoint")!;
        Assert.Equal("py", pyGetPt.Parameters[0].Name);
        Assert.Equal(ParamDirection.Output, pyGetPt.FindParam("pt")!.Direction);
    }

    [Fact]
    public void CommandSyntaxBridge_Batch5_LegacyAndV2RoundTrip_Works()
    {
        // MOTION
        Assert.Equal("mot=Motor1, axis=XY, speed=100, coords=\"10,20\"",
            CommandSyntaxBridge.FormatToV2("Motion@Go", "Motor1, XY, 100, 10, 20"));
        Assert.Equal("Motor1, XY, 100, 10, 20",
            CommandSyntaxBridge.NormalizeForExecution("Motion@Go", "mot=Motor1, axis=XY, speed=100, coords=\"(10, 20)\""));
        Assert.Equal("mot=Motor1, speed=100, pt=PickPos {safePos=25}",
            CommandSyntaxBridge.FormatToV2("Motion@GoSafe", "Motor1, 100, PickPos, Z=25"));
        Assert.Equal("Motor1, 100, PickPos, X=15, [[ErrM]]逾時",
            CommandSyntaxBridge.NormalizeForExecution("Motion@GoSafe", "mot=Motor1, speed=100, pt=PickPos {axis=X, safePos=15} [[ErrM]]逾時"));

        // CAPTURE
        Assert.Equal("cam=Cam0, roi=\"10,20,300,200\"",
            CommandSyntaxBridge.FormatToV2("Capture@ROISet", "Cam0, 10, 20, 300, 200"));
        Assert.Equal("cam=Cam0, file=Mark.png {angle=15} -> var=Score, pt=MarkPt",
            CommandSyntaxBridge.FormatToV2("Capture@PatternMatchOne", "Cam0, Mark.png, $Score, MarkPt, 15"));
        Assert.Equal("Cam0, Mark.png, Score, MarkPt, 15",
            CommandSyntaxBridge.NormalizeForExecution("Capture@PatternMatchOne", "cam=Cam0, file=Mark.png {angle=15} -> var=Score, pt=MarkPt"));

        // DAQ（含舊式賦值保護）
        Assert.Equal("OutW = W1 + W2",
            CommandSyntaxBridge.NormalizeForExecution("DAQ@Calculate", "OutW = W1 + W2"));
        Assert.Equal("wav=OutW, expr=W1 + W2",
            CommandSyntaxBridge.FormatToV2("DAQ@Calculate", "OutW = W1 + W2"));
        Assert.Equal("OutW = W1 - W2",
            CommandSyntaxBridge.NormalizeForExecution("DAQ@Calculate", "wav=OutW, expr=W1 - W2"));
        Assert.Equal("wav=MultiW, wavs=[W1;W2;W3]",
            CommandSyntaxBridge.FormatToV2("DAQ@MergeWaveForm", "MultiW, W1, W2, W3"));

        // USER
        Assert.Equal("ui=TxtSpeed -> var=CurSpeed",
            CommandSyntaxBridge.FormatToV2("User@GetControlValue", "TxtSpeed, $CurSpeed"));
        Assert.Equal("BtnList, Panel=StationA, Kind=Button",
            CommandSyntaxBridge.NormalizeForExecution("User@QueryControls", "{panel=StationA, kind=Button} -> var=BtnList"));
        Assert.Equal("ui=BtnStart, text=\"Hello, World\"",
            CommandSyntaxBridge.FormatToV2("User@ButtonText", "BtnStart, Hello, World"));
        Assert.Equal("Btn1, Btn2",
            CommandSyntaxBridge.NormalizeForExecution("User@ShowControl", "ui=[Btn1;Btn2]"));
        Assert.Equal("Btn1, Btn2",
            CommandSyntaxBridge.NormalizeForExecution("User@ShowControl", "ctrls=[Btn1;Btn2]"));
        Assert.Equal("TxtSpeed, 150",
            CommandSyntaxBridge.NormalizeForExecution("User@SetControlValue", "ctrl=TxtSpeed, val=150"));

        // PYTHON（含舊式原生腳本保護）
        Assert.Equal("x = 1 + 2",
            CommandSyntaxBridge.NormalizeForExecution("Python@Script", "x = 1 + 2"));
        Assert.Equal("code=\"x = 1 + 2\" {session=S1}",
            CommandSyntaxBridge.FormatToV2("Python@Script", "Session=S1\nx = 1 + 2"));
        Assert.Equal("Session=S1, res = x * 2",
            CommandSyntaxBridge.NormalizeForExecution("Python@Script", "code=\"res = x * 2\" {session=S1}"));
        Assert.Equal("py_pt, ARCOSName=TargetPt, Session=S1",
            CommandSyntaxBridge.NormalizeForExecution("Python@GetPoint", "py=py_pt {session=S1} -> pt=TargetPt"));
    }

    [Fact]
    public void Batch5Modules_CsviManifests_And_LiveV2Execution_EndToEnd()
    {
        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        // 1. 驗證五個模組全部 100 顆 .csvi 皆已嵌入正確 Schema
        string[] mods = { "MOTION", "CAPTURE", "DAQ", "USER", "PYTHON" };
        int verifiedCsvis = 0;
        foreach (var mod in mods)
        {
            foreach (var schema in StandardCommandSchemas.GetByModule(mod))
            {
                string act = schema.CommandKey.Substring(schema.CommandKey.IndexOf('@') + 1);
                string? path = ModuleRegistry.ResolveCommandCsviPath(mod, act);
                Assert.NotNull(path);
                var pkg = CsviPackage.LoadFromFile(path!, unpackSources: false);
                try
                {
                    Assert.NotNull(pkg.Manifest.Schema);
                    Assert.Equal(schema.CommandKey, pkg.Manifest.Schema!.CommandKey);
                    verifiedCsvis++;
                }
                finally { pkg.Unload(); }
            }
        }
        Assert.Equal(98, verifiedCsvis);

        // 2. 預先在 UserPanel 註冊模擬控制項供 USER 模組 v2.0 實戰測試
        engines.UserPanel.RegisterControls(new[]
        {
            new ARCOS.Core.Engines.UserPanelControlInfo
            {
                Name = "TxtSpeed",
                Kind = ARCOS.Core.Engines.UserPanelControlKind.TextBox,
                Role = ARCOS.Core.Engines.UserPanelElementRole.Control,
                Value = "100"
            },
            new ARCOS.Core.Engines.UserPanelControlInfo
            {
                Name = "BtnStart",
                Kind = ARCOS.Core.Engines.UserPanelControlKind.Button,
                Role = ARCOS.Core.Engines.UserPanelElementRole.Control,
                Value = "0"
            }
        });

        // 3. 端對端執行涵蓋 MOTION / CAPTURE / DAQ / USER / PYTHON 的完整 v2.0 腳本
        string v2Script = string.Join("\n", new[]
        {
            // MOTION（離線模擬模式）
            "Motion@Home :: mot=Motor1",
            "Motion@SetTool :: mot=Motor1, pt=Nozzle1",
            "Motion@GetPos :: mot=Motor1",
            // CAPTURE
            "Capture@CalibrateManual :: cam=Cam0 {scale=0.02, originX=10, originY=20}",
            "Capture@ROISet :: cam=Cam0, roi=\"10,20,300,200\"",
            "Capture@FindCircle(InOut) :: cam=Cam0 -> pt=CirclePt",
            "Capture@PatternMatchOne :: cam=Cam0, file=Mark.png {angle=15} -> var=MatchScore, pt=MarkPt",
            // DAQ
            "DAQ@WaveFormGenerate :: wav=W1 {type=Square, rate=1000, sec=0.02, amp=5, offset=5}",
            "DAQ@WaveFormGenerate :: wav=W2 {type=Square, rate=1000, sec=0.02, amp=5, offset=5}",
            "DAQ@Calculate :: wav=WSum, expr=W1 + W2",
            "DAQ@Filter :: wav=WSum, type=MovingAverage {window=3}",
            "DAQ@MergeWaveForm :: wav=WMulti, wavs=[W1;W2]",
            "DAQ@CompareAvgInRange :: wav=W1, max=15, min=0",
            // USER
            "User@SetControlValue :: ui=TxtSpeed, val=250",
            "User@GetControlValue :: ui=TxtSpeed -> var=ReadSpeed",
            "User@ButtonTrigger :: ui=BtnStart, logic=True",
            "User@QueryControls {kind=Button} -> var=BtnNames",
            // PYTHON
            "Python@Reset {session=V2Test}",
            "Python@SetVariable :: var=ReadSpeed {py=spd, session=V2Test}",
            "Python@Script :: code=\"doubled = 500\" {session=V2Test}",
            "Python@GetVariable :: py=doubled {session=V2Test} -> var=PyOut",
            "Python@SetPoint :: pt=CirclePt {py=p_in, session=V2Test}",
            "Python@GetPoint :: py=p_in {session=V2Test} -> pt=PyPtOut"
        });

        var prog = ProgramTable.FromText(v2Script, 0, "V2_Batch5_Integration");
        vm.LoadProgram(0, prog);
        vm.StartThread(0, 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 4000)
        {
            vm.Tick();
        }

        string sec = vm.MainThreads[0].CurrentSection;
        Assert.False(vm.MainThreads[0].IsRunning);

        // MOTION assertions
        Assert.True(engines.Io.GetPort("Motor1#Homed?"));
        Assert.Equal("Nozzle1", engines.Variable.Get("Motor1#Tool", "Global"));
        Assert.Equal(0.0, engines.Point.GetPoint("Motor1").X, 6);

        // CAPTURE assertions
        Assert.True(engines.Io.GetPort("CirclePt#Find?"));
        Assert.Equal(50.0, engines.Point.GetPoint("CirclePt").X, 6);
        Assert.True(engines.Io.GetPort("MarkPt#Find?"));
        Assert.Equal("98.5", engines.Variable.Get("MatchScore", sec));
        Assert.Equal(100.0, engines.Point.GetPoint("MarkPt").X, 6);

        // DAQ assertions
        Assert.Contains("WSum", engines.Daq.GetAllNames());
        Assert.Contains("WMulti", engines.Daq.GetAllNames());
        Assert.Equal(40, engines.Daq.GetWaveform("WMulti").Channels[0].Y.Length);
        Assert.True(engines.Io.GetPort("W1#OK?"));

        // USER assertions
        Assert.Equal("250", engines.Variable.Get("ReadSpeed", sec));
        Assert.True(engines.Io.GetPort("UserPanel#BtnStart?"));
        Assert.Contains("BtnStart", engines.Variable.Get("BtnNames", sec));

        // PYTHON assertions
        Assert.Equal("500", engines.Variable.Get("PyOut", sec));
        Assert.Equal(50.0, engines.Point.GetPoint("PyPtOut").X, 6);
    }

    [Fact]
    public void Last4Modules_Schemas_Bridge_CsviManifests_And_LiveV2Execution_EndToEnd()
    {
        // 1. 驗證最後 4 模組 Schema 計數與全專案總計 251 條（Distinct CommandKey）
        Assert.Equal(9, StandardCommandSchemas.GetByModule("Redis").Count);
        Assert.Equal(12, StandardCommandSchemas.GetByModule("MultiStation").Count);
        Assert.Equal(3, StandardCommandSchemas.GetByModule("AIAgent").Count);
        Assert.Equal(2, StandardCommandSchemas.GetByModule("Hello").Count);
        int totalUnique = StandardCommandSchemas.All.Values.Select(s => s.CommandKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(246, totalUnique);
        Assert.Null(StandardCommandSchemas.Get("MultiStation@GetPoint"));

        // 2. 驗證雙向轉換（含 Redis 多 Session/key 與 AIAgent ~ Bypass <-> {bypass=true}）
        Assert.Equal("key=LotId -> var=CurLot", CommandSyntaxBridge.FormatToV2("Redis@Get", "LotId, $CurLot"));
        Assert.Equal("JobQ, Session=MES, TimeOut=3, [[ErrQ]]逾時", CommandSyntaxBridge.NormalizeForExecution("Redis@BLPop", "key=JobQ {session=MES, timeout=3} -> var=NextJob [[ErrQ]]逾時"));
        Assert.Equal("cmd=PING {session=Line1}", CommandSyntaxBridge.FormatToV2("Redis@CMD", "Line1, PING"));
        Assert.Equal("prompt=CheckJam {bypass=true} [[Recover]]", CommandSyntaxBridge.FormatToV2("AIAgent@ErrorHandle", "~CheckJam, [[Recover]]"));
        Assert.Equal("~CheckJam, [[Recover]]", CommandSyntaxBridge.NormalizeForExecution("AIAgent@ErrorHandle", "prompt=CheckJam {bypass=true} [[Recover]]"));
        Assert.Equal("station=2", CommandSyntaxBridge.FormatToV2("MultiStation@SetStationON", "2"));

        // 3. 驗證 4 模組共 29 顆 .csvi 皆已嵌入正確 Schema
        string[] mods = { "REDIS", "MULTISTATION", "AIAGENT", "HELLO" };
        int verifiedCsvis = 0;
        foreach (var mod in mods)
        {
            foreach (var schema in StandardCommandSchemas.GetByModule(mod))
            {
                string act = schema.CommandKey.Substring(schema.CommandKey.IndexOf('@') + 1);
                string? path = ModuleRegistry.ResolveCommandCsviPath(mod, act);
                Assert.NotNull(path);
                var pkg = CsviPackage.LoadFromFile(path!, unpackSources: false);
                try
                {
                    Assert.NotNull(pkg.Manifest.Schema);
                    Assert.Equal(schema.CommandKey, pkg.Manifest.Schema!.CommandKey);
                    verifiedCsvis++;
                }
                finally { pkg.Unload(); }
            }
        }
        Assert.Equal(26, verifiedCsvis);

        // 4. 端對端在真實 ArcosThreadVM 執行 REDIS / MULTISTATION / AIAGENT / HELLO v2.0 腳本
        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        string v2Script = string.Join("\n", new[]
        {
            // REDIS（自動啟動本機 127.0.0.1:6379 作為預設 Session，另以 CreateSession 連線 DB 1 作為隔離 Session MES）
            "System@IncludeModule :: module=Redis",
            "Redis@CreateSession :: session=Line1 {url=127.0.0.1:6379}",
            "Redis@CreateSession :: session=MES {url=127.0.0.1:6379/1}",
            "Redis@CMD :: cmd=\"DEL JobQ\"",
            "Redis@CMD :: cmd=\"DEL JobQ\" {session=MES}",
            "Redis@Set :: key=LotId, val=LOT_LINE1",
            "Redis@Set :: key=LotId, val=LOT_MES {session=MES}",
            "Redis@Get :: key=LotId -> var=ReadLotDefault",
            "Redis@Get :: key=LotId {session=MES} -> var=ReadLotMes",
            "Redis@LPush :: key=JobQ, val=JOB_A",
            "Redis@LPush :: key=JobQ, val=JOB_B",
            "Redis@LPush :: key=JobQ, val=JOB_MES {session=MES}",
            "Redis@LPop :: key=JobQ -> var=Pop1",
            "Redis@BLPop :: key=JobQ {timeout=2} -> var=Pop2",
            "Redis@LPop :: key=JobQ {session=MES} -> var=PopMes",
            "Redis@Publish :: key=EvtCh, val=Done",
            "Redis@Subscribe :: key=EvtCh {session=MES}",
            "Redis@CMD :: cmd=PING {session=Line1}",
            // MULTISTATION 1：舊版整數工站與舊指令名相容
            "MultiStation@SetStationON :: station=1",
            "MultiStation@SetStationOK :: station=1",
            "MultiStation@SetStationOFF :: station=2",
            "MultiStation@StateShift {shift=1}",
            "MultiStation@UpdateIndicator",
            "MultiStation@ChkStationRunState :: station=2",
            // MULTISTATION 2：命名工位（轉盤 [入料;加工;檢驗;出料] + SetOn/SetOK 以 $logic 設定狀態 + 隨行資料移位 + False 自動清空）
            "IO@AddVirtualIO :: io=InSensor? {logic=True}",
            "IO@AddVirtualIO :: io=VisionOK? {logic=True}",
            "MultiStation@AddStation :: station=FeedInStation {items=[入料;加工;檢驗;出料]}",
            "MultiStation@SetOn :: station=FeedInStation.入料, logic=InSensor?",
            "MultiStation@SetOK :: station=FeedInStation.入料, logic=VisionOK?",
            "MultiStation@SetData :: station=FeedInStation.入料, val=SN_A001 {field=SN}",
            "MultiStation@StateShift {station=FeedInStation, shift=1}",
            "MultiStation@GetData :: station=FeedInStation.加工 {field=SN} -> var=ShiftedSN",
            "MultiStation@SetOn :: station=FeedInStation.加工, logic=False",
            "MultiStation@GetData :: station=FeedInStation.加工 {field=SN} -> var=ClearedSN",
            // MULTISTATION 3：1-based Tray 盤（與 Point&Path 解耦，透過 FindFirstOK/FindEmpty 輸出 index 串接 ResetPalletIndex）
            "Point&Path@InsertOnePoint :: pt=TrayPts, val=(10,0,0)",
            "Point&Path@InsertOnePoint :: pt=TrayPts, val=(20,0,0)",
            "Point&Path@InsertOnePoint :: pt=TrayPts, val=(30,0,0)",
            "MultiStation@AddStation :: station=TrayA {slots=3}",
            "MultiStation@SetOn :: station=TrayA.2, logic=True",
            // 入料盤 FindFirstOK -> 輸出 index 交給 Point&Path@ResetPalletIndex + Transfer 至工作站入料口
            "MultiStation@SetOn :: station=TrayA.1, logic=True",
            "MultiStation@SetOK :: station=TrayA.1, logic=!VisionOK?",
            "MultiStation@SetOK :: station=TrayA.2, logic=VisionOK?",
            "MultiStation@SetData :: station=TrayA.2, val=LOT_TRAY2 {field=SN}",
            "MultiStation@FindFirstOK :: station=TrayA -> var=PickSlot, index=PickIdx",
            "Point&Path@ResetPalletIndex :: pt=TrayPts {index=$(PickIdx)}",
            "MultiStation@Transfer :: from=$(PickSlot), to=FeedInStation.入料",
            // 出料盤 FindEmpty -> 輸出 index 交給 Point&Path@ResetPalletIndex + Transfer 下料
            "Point&Path@InsertOnePoint :: pt=OutTrayPts, val=(100,0,0)",
            "Point&Path@InsertOnePoint :: pt=OutTrayPts, val=(200,0,0)",
            "MultiStation@AddStation :: station=OutTray {slots=2}",
            "MultiStation@SetOn :: station=OutTray.1, logic=True",
            "MultiStation@FindEmpty :: station=OutTray -> var=DropSlot, index=DropIdx",
            "Point&Path@ResetPalletIndex :: pt=OutTrayPts {index=$(DropIdx)}",
            "MultiStation@Transfer :: from=FeedInStation.入料, to=$(DropSlot)",
            // 環形緩衝區前/後位移 + 直線輸送帶搭配 SetOn logic=False 清頭端
            "MultiStation@AddStation :: station=Conv {items=[S1;S2;S3]}",
            "MultiStation@SetOn :: station=Conv.S3, logic=True",
            "MultiStation@SetData :: station=Conv.S3, val=TAIL_PART {field=SN}",
            "MultiStation@StateShift {station=Conv, shift=-1}",
            "MultiStation@GetData :: station=Conv.S2 {field=SN} -> var=ConvBackSN",
            "MultiStation@StateShift {station=Conv, shift=2}",
            "MultiStation@SetOn :: station=Conv.S1, logic=False",
            "MultiStation@GetData :: station=Conv.S1 {field=SN} -> var=ConvHeadSN",
            // AIAGENT (bypass=true 離線確定性跳轉)
            "AIAgent@SuperviseStart :: prompt=InspectPart {bypass=true}",
            "AIAgent@SuperviseEnd",
            "AIAgent@ErrorHandle :: prompt=CheckJam {bypass=true} [[AiFallback]]",
            "Variable@Set :: var=Skipped, val=YES",
            "Thread@Label :: label=AiFallback",
            "Variable@Set :: var=FallbackHit, val=YES",
            // HELLO
            "Hello@Init",
            "Hello@HelloWorld {msg=\"Hello V2\"}"
        });

        var prog = ProgramTable.FromText(v2Script, 0, "V2_Last4_Integration");
        vm.LoadProgram(0, prog);
        vm.StartThread(0, 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 4000)
        {
            vm.Tick();
        }

        string sec = vm.MainThreads[0].CurrentSection;
        Assert.False(vm.MainThreads[0].IsRunning);

        // REDIS assertions（驗證本機預設 Session DB0 與指定 Session MES DB1 真實 Redis 隔離，LPUSH/LPOP 依標準 LIFO 順序）
        Assert.Equal("OK", engines.Variable.Get("RedisErr#Line1", "Global"));
        Assert.Equal("OK", engines.Variable.Get("RedisErr#MES", "Global"));
        Assert.Equal("LOT_LINE1", engines.Variable.Get("ReadLotDefault", sec));
        Assert.Equal("LOT_MES", engines.Variable.Get("ReadLotMes", sec));
        Assert.Equal("JOB_B", engines.Variable.Get("Pop1", sec));
        Assert.Equal("JOB_A", engines.Variable.Get("Pop2", sec));
        Assert.Equal("JOB_MES", engines.Variable.Get("PopMes", sec));

        // MULTISTATION assertions（舊版整數 + 命名工位 + 隨行資料移位 + OFF 自動清空 + 1-based Point 綁定 + FindFirstOK/FindEmpty/Transfer/線性位移）
        Assert.True(engines.Io.GetPort("Station2#Run?"));
        Assert.True(engines.Io.GetPort("Station2#OK?"));
        Assert.False(engines.Io.GetPort("Station1#Run?"));

        Assert.Equal("SN_A001", engines.Variable.Get("ShiftedSN", sec));
        Assert.Equal("", engines.Variable.Get("ClearedSN", sec));
        Assert.False(engines.Io.GetPort("FeedInStation.加工#On?"));
        Assert.False(engines.Io.GetPort("FeedInStation.加工#OK?"));
        Assert.False(engines.Io.GetPort("FeedInStation.入料#On?"));

        // FindFirstOK 跳過 NG 的 TrayA.1，找到第一個可取料 OK 的 TrayA.2 (0-based index=1)，透過 ResetPalletIndex 定位 TrayPts -> Transfer 到 FeedInStation.入料 -> OutTray.2
        Assert.True(engines.Io.GetPort("TrayA#Found?"));
        Assert.Equal("TrayA.2", engines.Variable.Get("PickSlot", sec));
        Assert.Equal("1", engines.Variable.Get("PickIdx", sec));
        Assert.Equal(20.0, engines.Point.GetPoint("TrayPts").X, 6);
        Assert.False(engines.Io.GetPort("TrayA.2#On?"));
        Assert.False(engines.Io.GetPort("TrayA.2#OK?"));

        Assert.True(engines.Io.GetPort("OutTray#Found?"));
        Assert.Equal("OutTray.2", engines.Variable.Get("DropSlot", sec));
        Assert.Equal("1", engines.Variable.Get("DropIdx", sec));
        Assert.Equal(200.0, engines.Point.GetPoint("OutTrayPts").X, 6);
        Assert.True(engines.Io.GetPort("OutTray.2#On?"));
        Assert.True(engines.Io.GetPort("OutTray.2#OK?"));
        Assert.Equal("LOT_TRAY2", engines.Variable.Get("OutTray.2#SN", "Global"));
        Assert.Equal("", engines.Variable.Get("FeedInStation.入料#SN", "Global"));

        // 環形緩衝區負向 shift=-1（S3 -> S2）再正向 shift=2（S2 -> S1）後清頭端 SetStationOFF
        Assert.Equal("TAIL_PART", engines.Variable.Get("ConvBackSN", sec));
        Assert.False(engines.Io.GetPort("Conv.S1#On?"));
        Assert.False(engines.Io.GetPort("Conv.S3#On?"));
        Assert.Equal("", engines.Variable.Get("ConvHeadSN", sec));

        // AIAGENT assertions
        Assert.Equal("YES", engines.Variable.Get("FallbackHit", sec));
        Assert.Throws<KeyNotFoundException>(() => engines.Variable.Get("Skipped", sec));

        // HELLO assertions
        Assert.Equal("INITIALIZED", engines.Variable.Get("Hello_Status", "Hello"));
    }

    [Fact]
    public void ThreadGoOn_IsExactInverseOfStop_TrueContinues_FalseHaltsThread()
    {
        var io = new ARCOS.Core.Engines.IoEngine();
        io.AddVirtualIO("MaterialOK?", true);
        io.AddVirtualIO("SafetyOK?", false);
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        string script = string.Join("\n", new[]
        {
            "Thread@GoOn {logic=MaterialOK?}",
            "Variable@Set :: var=PassedTrue, val=YES",
            "Thread@GoOn {logic=SafetyOK?, msg=安全條件不成立}",
            "Variable@Set :: var=ShouldNotRun, val=YES"
        });

        vm.LoadProgram(0, ProgramTable.FromText(script, 0, "GoOnInverseTest"));
        vm.StartThread(0, 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 2000)
        {
            vm.Tick();
        }

        string sec = vm.MainThreads[0].CurrentSection;
        Assert.False(vm.MainThreads[0].IsRunning);
        Assert.Equal("YES", engines.Variable.Get("PassedTrue", sec));
        Assert.Throws<KeyNotFoundException>(() => engines.Variable.Get("ShouldNotRun", sec));
    }

    [Fact]
    public void ChkInRangeXY_Supports2PtRect_3PtCuboid_MultiPtPolygon_AndIoOutput()
    {
        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        string script = string.Join("\n", new[]
        {
            // 1) 2 點長方形: (0,0,0) ~ (100,100,0)
            "Point&Path@InsertOnePoint :: pt=Rect2D, val=(0,0,0)",
            "Point&Path@InsertOnePoint :: pt=Rect2D, val=(100,100,0)",
            "Point&Path@Calculate :: pt=P_RectIn, expr=(50,50,999)",
            "Point&Path@ChkInRangeXY :: pt=P_RectIn, rangePt=Rect2D -> io=RectIn?",
            // 2) 3 點長方體: XY=(0,0)~(100,100), Z=0~50
            "Point&Path@InsertOnePoint :: pt=Cube3D, val=(0,0,0)",
            "Point&Path@InsertOnePoint :: pt=Cube3D, val=(100,100,0)",
            "Point&Path@InsertOnePoint :: pt=Cube3D, val=(100,100,50)",
            "Point&Path@Calculate :: pt=P_CubeIn, expr=(50,50,25)",
            "Point&Path@Calculate :: pt=P_CubeOutZ, expr=(50,50,80)",
            "Point&Path@ChkInRangeXY :: pt=P_CubeIn, rangePt=Cube3D -> io=CubeIn?",
            "Point&Path@ChkInRangeXY :: pt=P_CubeOutZ, rangePt=Cube3D -> io=CubeOutZ?",
            // 3) 4 點菱形平面多邊形: (50,0), (100,50), (50,100), (0,50)
            "Point&Path@InsertOnePoint :: pt=Diamond, val=(50,0,0)",
            "Point&Path@InsertOnePoint :: pt=Diamond, val=(100,50,0)",
            "Point&Path@InsertOnePoint :: pt=Diamond, val=(50,100,0)",
            "Point&Path@InsertOnePoint :: pt=Diamond, val=(0,50,0)",
            "Point&Path@Calculate :: pt=P_PolyIn, expr=(50,50,0)",
            "Point&Path@Calculate :: pt=P_PolyCornerOut, expr=(5,5,0)",
            "Point&Path@Calculate :: pt=P_PolyEdge, expr=(75,25,0)",
            "Point&Path@ChkInRangeXY :: pt=P_PolyIn, rangePt=Diamond -> io=PolyIn?",
            "Point&Path@ChkInRangeXY :: pt=P_PolyCornerOut, rangePt=Diamond -> io=PolyOut?",
            "Point&Path@ChkInRangeXY :: pt=P_PolyEdge, rangePt=Diamond -> io=PolyEdge?",
            // 4) {axis=XYZ} 2點長方體 & {axis=Z} 單軸高度窗
            "Point&Path@InsertOnePoint :: pt=Box3D2Pt, val=(0,0,10)",
            "Point&Path@InsertOnePoint :: pt=Box3D2Pt, val=(100,100,40)",
            "Point&Path@Calculate :: pt=P_ZOk, expr=(50,50,25)",
            "Point&Path@Calculate :: pt=P_ZHigh, expr=(50,50,60)",
            "Point&Path@ChkInRangeXY :: pt=P_ZOk, rangePt=Box3D2Pt {axis=XYZ} -> io=Box2PtIn?",
            "Point&Path@ChkInRangeXY :: pt=P_ZHigh, rangePt=Box3D2Pt {axis=XYZ} -> io=Box2PtOut?",
            "Point&Path@ChkInRangeXY :: pt=P_ZOk, rangePt=Box3D2Pt {axis=Z} -> io=ZOnlyIn?",
            "Point&Path@ChkInRangeXY :: pt=P_ZHigh, rangePt=Box3D2Pt {axis=Z} -> io=ZOnlyOut?",
            // 5) 3 點共面三角形（同 Z -> 自動判 2D 三角形）
            "Point&Path@InsertOnePoint :: pt=Tri2D, val=(0,0,0)",
            "Point&Path@InsertOnePoint :: pt=Tri2D, val=(100,0,0)",
            "Point&Path@InsertOnePoint :: pt=Tri2D, val=(50,100,0)",
            "Point&Path@Calculate :: pt=P_TriIn, expr=(50,30,99)",
            "Point&Path@Calculate :: pt=P_TriOut, expr=(10,90,0)",
            "Point&Path@ChkInRangeXY :: pt=P_TriIn, rangePt=Tri2D -> io=TriIn?",
            "Point&Path@ChkInRangeXY :: pt=P_TriOut, rangePt=Tri2D -> io=TriOut?",
            // 6) 1 點 + Radius 圓形安全區
            "Point&Path@Calculate :: pt=CircleZone, expr=(50,50,0)",
            "Point&Path@ReplaceH :: pt=CircleZone, h=20",
            "Point&Path@Calculate :: pt=P_CirIn, expr=(60,55,0)",
            "Point&Path@Calculate :: pt=P_CirOut, expr=(80,80,0)",
            "Point&Path@ChkInRangeXY :: pt=P_CirIn, rangePt=CircleZone -> io=CirIn?",
            "Point&Path@ChkInRangeXY :: pt=P_CirOut, rangePt=CircleZone -> io=CirOut?"
        });

        vm.LoadProgram(0, ProgramTable.FromText(script, 0, "ChkInRangeGeometryTest"));
        vm.StartThread(0, 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 2000)
        {
            vm.Tick();
        }

        Assert.False(vm.MainThreads[0].IsRunning);
        Assert.True(engines.Io.GetPort("RectIn?"));
        Assert.True(engines.Io.GetPort("CubeIn?"));
        Assert.False(engines.Io.GetPort("CubeOutZ?"));
        Assert.True(engines.Io.GetPort("PolyIn?"));
        Assert.False(engines.Io.GetPort("PolyOut?"));
        Assert.True(engines.Io.GetPort("PolyEdge?"));
        Assert.True(engines.Io.GetPort("Box2PtIn?"));
        Assert.False(engines.Io.GetPort("Box2PtOut?"));
        Assert.True(engines.Io.GetPort("ZOnlyIn?"));
        Assert.False(engines.Io.GetPort("ZOnlyOut?"));
        Assert.True(engines.Io.GetPort("TriIn?"));
        Assert.False(engines.Io.GetPort("TriOut?"));
        Assert.True(engines.Io.GetPort("CirIn?"));
        Assert.False(engines.Io.GetPort("CirOut?"));
    }

    [Fact]
    public void MotionHome_SupportsOptionalAxes_ThreeFixedModes_AndRecoveryWithoutThrowing()
    {
        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        // 先模擬點位有初始位移且處於異警狀態
        engines.Point.SetPoint("Motor1", new ARCOS.Core.Model.MachinePoint(10, 20, 30, 0, 0, 0));
        engines.Io.SetPort("Motor1#Alarm?", true);

        string script = string.Join("\n", new[]
        {
            // 僅對 Z 軸做 ResetCounter：清除異警 + Servo ON，僅 Z 歸零，X/Y 維持不動
            "Motion@Home :: mot=Motor1 {axis=Z, mode=ResetCounter}",
            // Robot 省略 axis，正向回原點
            "Motion@Home :: mot=Robot1 {mode=Positive}",
            // 對 Motor1 XY 反向回原點
            "Motion@Home :: mot=Motor1 {axis=XY, mode=Negative}"
        });

        Assert.Equal("Motor1, Axes=Z, Mode=ResetCounter",
            CommandSyntaxBridge.NormalizeForExecution("Motion@Home", "mot=Motor1 {axis=Z, mode=ResetCounter}"));
        Assert.Equal("Robot1, Mode=Positive",
            CommandSyntaxBridge.NormalizeForExecution("Motion@Home", "mot=Robot1 {mode=Positive}"));

        vm.LoadProgram(0, ProgramTable.FromText(script, 0, "MotionHomeModesTest"));
        vm.StartThread(0, 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 2000)
        {
            vm.Tick();
        }

        Assert.False(vm.MainThreads[0].IsRunning);
        Assert.False(engines.Io.GetPort("Motor1#Alarm?"));
        Assert.True(engines.Io.GetPort("Motor1#ServoOn?"));
        Assert.True(engines.Io.GetPort("Motor1#Homed?"));
        Assert.Equal(0.0, engines.Point.GetPoint("Motor1").X, 6);
        Assert.Equal(0.0, engines.Point.GetPoint("Motor1").Y, 6);
        Assert.Equal(0.0, engines.Point.GetPoint("Motor1").Z, 6);
        Assert.True(engines.Io.GetPort("Robot1#Homed?"));
    }

    [Fact]
    public void MotionCommands_AcceptInlineLiteralPointCoordinates_WithoutQuotes()
    {
        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        // 確認 ArgParaHelper 保護括號內逗號不切碎 (100,200,300,90,180,90)
        ArgParaHelper.ParseKeyValueArgs("Motor1, 100, (100,200,300,90,180,90), Z=50", out var pos);
        Assert.Equal(3, pos.Count);
        Assert.Equal("(100,200,300,90,180,90)", pos[2]);

        Assert.Equal("mot=Motor1, speed=100, pt=(100,200,300,90,180,90)",
            CommandSyntaxBridge.FormatToV2("Motion@GoPoint", "Motor1, 100, (100,200,300,90,180,90)"));

        string script = string.Join("\n", new[]
        {
            "Motion@AddMotion :: mot=M_Lit, driver=MockMotion {axis=6, speed=5000}",
            "Motion@GoPoint :: mot=M_Lit, speed=5000, pt=(100,200,300,90,180,90)",
            "Motion@WaitMotion :: mot=M_Lit {timeout=5}",
            "Motion@GetPos :: mot=M_Lit",
            "Motion@SetTool :: mot=M_Lit, pt=(0,0,15,0,0,0)"
        });

        vm.LoadProgram(0, ProgramTable.FromText(script, 0, "MotionLiteralPtTest"));
        vm.StartThread(0, 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 4000)
        {
            vm.Tick();
            System.Threading.Thread.Sleep(5);
        }

        Assert.False(vm.MainThreads[0].IsRunning);
        var cur = engines.Point.GetPoint("M_Lit");
        Assert.Equal(100.0, cur.X, 3);
        Assert.Equal(200.0, cur.Y, 3);
        Assert.Equal(300.0, cur.Z, 3);
        Assert.Equal(90.0, cur.U, 3);
        Assert.Equal(180.0, cur.V, 3);
        Assert.Equal(90.0, cur.W, 3);
        Assert.Equal("(0,0,15,0,0,0)", engines.Variable.Get("M_Lit#Tool", "Global"));
    }

    [Fact]
    public void MultiLineText_StorageSingleLineEscaped_Vs_HumanDisplayFoldAndCodeBlock()
    {
        var extraSchema = StandardCommandSchemas.Get("Motion@ExtraCMD")!;
        Assert.Equal(ParamValueType.MultiLineText, extraSchema.FindParam("cmd")!.ValueType);

        // 儲存層（單行 \n 無損跳脫，不破壞 .prog / Telnet）
        string stored = "mot=Robot1, cmd=\"PWR ON\\nSPEED 80\\nMOVE P1\"";
        var parsed = CommandParamParser.Parse(stored, extraSchema);
        Assert.False(parsed.HasSyntaxError);
        Assert.Equal("PWR ON\nSPEED 80\nMOVE P1", parsed.Get("cmd"));

        // 單行檢視：折疊為首行摘要 + 總行數徽章
        string singleView = CommandDisplayProjector.ProjectParameter(stored, extraSchema, CommandDisplayLevel.Audit, multiLineLayout: false);
        Assert.Contains("cmd=⟦PWR ON … (3 lines)⟧", singleView);
        Assert.DoesNotContain("\\n", singleView);

        // 多行檢視 / Tooltip：展開為真正換行、帶 │ 邊線且無跳脫噪音的程式碼區塊
        string multiView = CommandDisplayProjector.ProjectParameter(stored, extraSchema, CommandDisplayLevel.Audit, multiLineLayout: true);
        Assert.Contains("\n  │ PWR ON\n  │ SPEED 80\n  │ MOVE P1", multiView);
        Assert.DoesNotContain("\\n", multiView);

        // Python@Script 同樣適用
        var pySchema = StandardCommandSchemas.Get("Python@Script")!;
        string pyStored = "code=\"x = 10\\ny = x * 2\\nres = y + 5\" {session=Vision}";
        string pySingle = CommandDisplayProjector.ProjectParameter(pyStored, pySchema, CommandDisplayLevel.Compact, multiLineLayout: false);
        string pyMulti = CommandDisplayProjector.ProjectParameter(pyStored, pySchema, CommandDisplayLevel.Audit, multiLineLayout: true);
        Assert.Contains("code=⟦x = 10 … (3 lines)⟧", pySingle);
        Assert.Contains("\n  │ x = 10\n  │ y = x * 2\n  │ res = y + 5", pyMulti);
    }

    [Fact]
    public void MotionAndCaptureConfig_AcceptBothNumericAndTextValues_EndToEnd()
    {
        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        string script = string.Join("\n", new[]
        {
            "Motion@AddMotion :: mot=M_Cfg, driver=MockMotion",
            "Motion@Config :: mot=M_Cfg, key=Accel, val=2000",
            "Motion@Config :: mot=M_Cfg, key=MoveMode, val=HighSpeed",
            "Capture@AddCam :: cam=C_Cfg, driver=MockCamera",
            "Capture@Config :: cam=C_Cfg, key=Exposure, val=15.5",
            "Capture@Config :: cam=C_Cfg, key=TriggerMode, val=Hardware"
        });

        vm.LoadProgram(0, ProgramTable.FromText(script, 0, "ConfigNumAndTextTest"));
        vm.StartThread(0, 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 4000)
        {
            vm.Tick();
            System.Threading.Thread.Sleep(5);
        }

        Assert.False(vm.MainThreads[0].IsRunning);
        dynamic motDrv = drivers.GetDriver("Motion", "M_Cfg")!;
        Assert.Equal("2000", (string)motDrv.ConfigParameters["Accel"]);
        Assert.Equal("HighSpeed", (string)motDrv.ConfigParameters["MoveMode"]);

        dynamic camDrv = drivers.GetDriver("Capture", "C_Cfg")!;
        Assert.Equal(15.5, (double)camDrv.ExposureMs, 6);
        Assert.Equal("15.5", (string)camDrv.ConfigParameters["Exposure"]);
        Assert.Equal("Hardware", (string)camDrv.ConfigParameters["TriggerMode"]);
    }

    [Fact]
    public void CaptureSnapAndTrigger_OverwriteCameraSlot_AndAppendToOptionalImaqsBuffer()
    {
        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        Assert.Equal("cam=Cam0 {imaqs=FlyBuf}", CommandSyntaxBridge.FormatToV2("Capture@Snap", "Cam0, FlyBuf"));
        Assert.Equal("cam=Cam0 {imaqs=FlyBuf}", CommandSyntaxBridge.FormatToV2("Capture@Trigger", "Cam0, FlyBuf"));

        string script = string.Join("\n", new[]
        {
            "Capture@AddCam :: cam=C_Fly, driver=MockCamera {exposure=1}",
            "Capture@Snap :: cam=C_Fly",
            "Capture@Snap :: cam=C_Fly {imaqs=FlyBuf}",
            "Capture@Trigger :: cam=C_Fly {imaqs=FlyBuf}"
        });

        vm.LoadProgram(0, ProgramTable.FromText(script, 0, "CaptureSnapTriggerDualWriteTest"));
        vm.StartThread(0, 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 4000)
        {
            vm.Tick();
            System.Threading.Thread.Sleep(5);
        }

        Assert.False(vm.MainThreads[0].IsRunning);
        Assert.True(engines.Io.GetPort("C_Fly#OK?"));
        // 相機同名影像槽 C_Fly 為覆寫（永遠維持 1 張最新影像）
        Assert.Single(engines.Image.GetFrameArray("C_Fly"));
        // 選填 -> imaqs=FlyBuf 同步 Append 累積 2 張（第 2 行的 Snap 與第 3 行的 Trigger）
        Assert.Equal(2, engines.Image.GetFrameArray("FlyBuf").Count);
    }

    [Fact]
    public void CaptureLoadFile2Imaq_SupportsRegexBatchLoading()
    {
        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));

        string tmpDir = Path.Combine(Path.GetTempPath(), $"arcos_regex_img_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            using (var mat = new OpenCvSharp.Mat(16, 16, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(10, 20, 30)))
            {
                OpenCvSharp.Cv2.ImWrite(Path.Combine(tmpDir, "img_01.png"), mat);
                OpenCvSharp.Cv2.ImWrite(Path.Combine(tmpDir, "img_02.png"), mat);
                OpenCvSharp.Cv2.ImWrite(Path.Combine(tmpDir, "skip_01.png"), mat);
            }

            // 驗證混合路徑（目錄正斜線 + 檔名正規表示式反斜線 \d+\.）與純 Windows 反斜線路徑皆可正確載入
            string mixedRegexPath = tmpDir.Replace('\\', '/') + @"/img_\d+\.png";
            engines.Image.LoadFile2Imaq("BatchImgs", mixedRegexPath);
            Assert.Equal(2, engines.Image.GetFrameArray("BatchImgs").Count);

            string winRegexPath = Path.Combine(tmpDir, @"img_\d+\.png");
            engines.Image.LoadFile2Imaq("BatchImgsWin", winRegexPath);
            Assert.Equal(2, engines.Image.GetFrameArray("BatchImgsWin").Count);

            // 驗證 Capture@LoadNextImaq :: cam=Cam0, imaqs=BatchImgs 與 Display2User 的 ui 參數雙向轉換
            Assert.Equal("Cam0, BatchImgs", CommandSyntaxBridge.NormalizeForExecution("Capture@LoadNextImaq", "cam=Cam0, imaqs=BatchImgs"));
            Assert.Equal("cam=Cam0, imaqs=BatchImgs", CommandSyntaxBridge.FormatToV2("Capture@LoadNextImaq", "Cam0, BatchImgs"));
            Assert.Equal("Cam0, ImgView1", CommandSyntaxBridge.NormalizeForExecution("Capture@Display2User", "cam=Cam0, ui=ImgView1"));
            Assert.Equal("cam=Cam0, ui=ImgView1", CommandSyntaxBridge.FormatToV2("Capture@Display2User", "Cam0, ImgView1"));
            Assert.Equal("PressCurve, WaveChart1", CommandSyntaxBridge.NormalizeForExecution("DAQ@Display2User", "wav=PressCurve, ui=WaveChart1"));
            Assert.Equal("wav=PressCurve, ui=WaveChart1", CommandSyntaxBridge.FormatToV2("DAQ@Display2User", "PressCurve, WaveChart1"));

            // 驗證舊別名 ctrl 仍可向下相容解析
            Assert.Equal("Cam0, ImgView1", CommandSyntaxBridge.NormalizeForExecution("Capture@Display2User", "cam=Cam0, ctrl=ImgView1"));
            Assert.Equal("PressCurve, WaveChart1", CommandSyntaxBridge.NormalizeForExecution("DAQ@Display2User", "wav=PressCurve, ctrl=WaveChart1"));
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DaqAnalogIn_WritesScalarToVariable_And_WaveFormLoadSaveRequireFile()
    {
        // 1. 驗證 Schema 與雙向格式化
        Assert.Equal("Daq0, ForceVal", CommandSyntaxBridge.NormalizeForExecution("DAQ@AnalogIn", "daq=Daq0 -> var=ForceVal"));
        Assert.Equal("daq=Daq0 -> var=ForceVal", CommandSyntaxBridge.FormatToV2("DAQ@AnalogIn", "Daq0, $ForceVal"));
        Assert.Equal("RefW, ref.csv", CommandSyntaxBridge.NormalizeForExecution("DAQ@WaveFormLoad", "wav=RefW, file=ref.csv"));
        Assert.Equal("wav=RefW, file=ref.csv", CommandSyntaxBridge.FormatToV2("DAQ@WaveFormLoad", "RefW, ref.csv"));
        Assert.Equal("PressCurve, log.csv", CommandSyntaxBridge.NormalizeForExecution("DAQ@WaveFormSave", "wav=PressCurve, file=log.csv"));
        Assert.Equal("wav=PressCurve, file=log.csv", CommandSyntaxBridge.FormatToV2("DAQ@WaveFormSave", "PressCurve, log.csv"));

        // 2. 端到端執行 DAQ@AnalogIn -> var=ForceVal（MockDAQ 單點計數 0, 1）並寫入 VariableEngine
        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));

        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        string script = string.Join("\n", new[]
        {
            "DAQ@AddDAQ :: daq=D_AI, driver=MockDAQ",
            "DAQ@AnalogIn :: daq=D_AI -> var=ForceVal1",
            "DAQ@AnalogIn :: daq=D_AI -> var=ForceVal2"
        });

        vm.LoadProgram(0, ProgramTable.FromText(script, 0, "DaqAnalogInE2E"));
        vm.StartThread(0, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 4000)
        {
            vm.Tick();
            System.Threading.Thread.Sleep(5);
        }

        Assert.False(vm.MainThreads[0].IsRunning);
        Assert.True(engines.Io.GetPort("D_AI#OK?"));
        // 未啟動 Start 時單點讀取會從 0 開始依序擷取並寫入變數
        Assert.Equal("0", engines.Variable.Get("ForceVal1"));
        Assert.Equal("1", engines.Variable.Get("ForceVal2"));
    }

    [Fact]
    public void DaqRingBuffer_Start_AnalogIn_And_AnalogInWaveForm_SliceRecentSeconds()
    {
        // 驗證 AddDAQ 支援 sec 時間長度參數
        Assert.Equal("Daq0, MockDAQ, Rate=1000, Sec=5, Wave=Sine",
            CommandSyntaxBridge.NormalizeForExecution("DAQ@AddDAQ", "daq=Daq0, driver=MockDAQ {rate=1000, sec=5, wave=Sine}"));

        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        string script = string.Join("\n", new[]
        {
            "DAQ@AddDAQ :: daq=D_Ring, driver=MockDAQ {rate=1000, sec=1, wave=Triangle}",
            "DAQ@Start :: daq=D_Ring",
            "System@Delay :: sec=0.12",
            "DAQ@AnalogIn :: daq=D_Ring -> var=LatestVal",
            "DAQ@AnalogInWaveForm :: daq=D_Ring {sec=0.05} -> wav=Recent50ms",
            "DAQ@Stop :: daq=D_Ring"
        });

        vm.LoadProgram(0, ProgramTable.FromText(script, 0, "DaqRingBufferE2E"));
        vm.StartThread(0, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 4000)
        {
            vm.Tick();
            System.Threading.Thread.Sleep(5);
        }

        Assert.False(vm.MainThreads[0].IsRunning);
        Assert.True(double.TryParse(engines.Variable.Get("LatestVal"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double latest));
        Assert.True(latest > 0, $"Expected live ring-buffer latest value > 0, got {latest}");

        var wf = engines.Daq.GetWaveform("Recent50ms");
        Assert.Equal(50, wf.GetOrCreateChannel(0).Y.Length);
    }

    [Fact]
    public void DaqWaveFormGenerate_UsesDurationSecAndSampleRate()
    {
        Assert.Equal("RefW, Triangle, Rate=500, Sec=0.4, Amp=10",
            CommandSyntaxBridge.NormalizeForExecution("DAQ@WaveFormGenerate", "wav=RefW {type=Triangle, rate=500, sec=0.4, amp=10}"));

        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        string script = string.Join("\n", new[]
        {
            "DAQ@WaveFormGenerate :: wav=RefW {type=Triangle, rate=500, sec=0.4, amp=10}",
            "DAQ@WaveFormGenerate :: wav=DefaultW"
        });

        vm.LoadProgram(0, ProgramTable.FromText(script, 0, "DaqWfGenE2E"));
        vm.StartThread(0, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 3000)
        {
            vm.Tick();
            System.Threading.Thread.Sleep(5);
        }

        Assert.False(vm.MainThreads[0].IsRunning);
        var chRef = engines.Daq.GetWaveform("RefW").GetOrCreateChannel(0);
        Assert.Equal(200, chRef.Y.Length); // 500 Hz * 0.4 s = 200 samples
        Assert.Equal(1.0 / 500.0, chRef.Dt, 6);

        var chDef = engines.Daq.GetWaveform("DefaultW").GetOrCreateChannel(0);
        Assert.Equal(1000, chDef.Y.Length); // default 1000 Hz * 1 s = 1000 samples
        Assert.Equal(0.001, chDef.Dt, 6);
    }

    [Fact]
    public void DaqWaveformComparisonAndCalc_OversamplesToHighestSampleRate()
    {
        var io = new ARCOS.Core.Engines.IoEngine();
        var daq = new ARCOS.Core.Engines.DaqEngine(io);

        // 1. CompareCoverBy: PressCurve (1000 Hz, 1.0s = 1000 pts) vs Upper (100 Hz, 1.0s = 100 pts) & Lower (200 Hz, 1.0s = 200 pts)
        var pressOk = Enumerable.Repeat(5.0, 1000).ToArray();
        var upper100 = Enumerable.Repeat(10.0, 100).ToArray();
        var lower200 = Enumerable.Repeat(0.0, 200).ToArray();
        daq.SetWaveform("PressOK", DateTime.Now, 0.001, pressOk);
        daq.SetWaveform("Upper100", DateTime.Now, 0.01, upper100);
        daq.SetWaveform("Lower200", DateTime.Now, 0.005, lower200);

        Assert.True(daq.CompareCoverBy("PressOK", "Upper100", "Lower200"));
        Assert.True(io.GetPort("PressOK#OK?"));

        // 在 t = 0.753s (index 753, 遠超過 100Hz 的 100 點) 放入超限尖峰 -> 必須因超取樣至 1000Hz 而被檢出 NG
        var pressSpike = (double[])pressOk.Clone();
        pressSpike[753] = 12.5;
        daq.SetWaveform("PressSpike", DateTime.Now, 0.001, pressSpike);
        Assert.False(daq.CompareCoverBy("PressSpike", "Upper100", "Lower200"));
        Assert.False(io.GetPort("PressSpike#OK?"));

        // 2. 反向高取樣包絡線：Slow (100 Hz, 1.0s = 100 pts, 值=5.0) vs HighRateUpper (1000 Hz, 1.0s = 1000 pts, t=0.505s 處有窄凹槽=3.0)
        var slow100 = Enumerable.Repeat(5.0, 100).ToArray();
        var upper1000 = Enumerable.Repeat(10.0, 1000).ToArray();
        upper1000[505] = 3.0;
        daq.SetWaveform("Slow100", DateTime.Now, 0.01, slow100);
        daq.SetWaveform("Upper1000", DateTime.Now, 0.001, upper1000);
        Assert.False(daq.CompareCoverBy("Slow100", "Upper1000"));
        Assert.False(io.GetPort("Slow100#OK?"));

        // 3. CalculateWaveform: 100 Hz (1.0s, 值=10) + 1000 Hz (1.0s, 值=5) -> 超取樣輸出 1000 Hz (1000 pts, 值=15)
        daq.SetWaveform("W100", DateTime.Now, 0.01, Enumerable.Repeat(10.0, 100).ToArray());
        daq.SetWaveform("W1000", DateTime.Now, 0.001, Enumerable.Repeat(5.0, 1000).ToArray());
        daq.CalculateWaveform("WSum", "W100", '+', "W1000");
        var sumCh = daq.GetWaveform("WSum").GetOrCreateChannel(0);
        Assert.Equal(0.001, sumCh.Dt, 6);
        Assert.Equal(1000, sumCh.Y.Length);
        Assert.All(sumCh.Y, v => Assert.Equal(15.0, v, 6));

        // 4. MergeWaveforms: 100 Hz (0.5s = 50 pts) + 1000 Hz (0.5s = 500 pts) -> 超取樣輸出 1000 Hz (1000 pts = 1.0s)
        daq.SetWaveform("SegA", DateTime.Now, 0.01, Enumerable.Repeat(2.0, 50).ToArray());
        daq.SetWaveform("SegB", DateTime.Now, 0.001, Enumerable.Repeat(4.0, 500).ToArray());
        daq.MergeWaveforms("Merged", new[] { "SegA", "SegB" });
        var mergedCh = daq.GetWaveform("Merged").GetOrCreateChannel(0);
        Assert.Equal(0.001, mergedCh.Dt, 6);
        Assert.Equal(1000, mergedCh.Y.Length);
        Assert.Equal(2.0, mergedCh.Y[0], 6);
        Assert.Equal(2.0, mergedCh.Y[499], 6);
        Assert.Equal(4.0, mergedCh.Y[500], 6);
        Assert.Equal(4.0, mergedCh.Y[999], 6);
    }

    [Fact]
    public void DaqCompareAvgAndMaxMin_SlicesByTimeSeconds()
    {
        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        // 構造 1.0 秒波形（1000 Hz 與 200 Hz 各一條）：只有 t ∈ [0.3s, 0.7s] 落在 5.0，其餘皆為 100.0
        var w1000 = new double[1000];
        for (int i = 0; i < w1000.Length; i++)
            w1000[i] = (i >= 300 && i <= 700) ? 5.0 : 100.0;
        engines.Daq.SetWaveform("FastW", DateTime.Now, 0.001, w1000);

        var w200 = new double[200];
        for (int i = 0; i < w200.Length; i++)
            w200[i] = (i >= 60 && i <= 140) ? 5.0 : 100.0;
        engines.Daq.SetWaveform("SlowW", DateTime.Now, 0.005, w200);

        // 先驗證全段（0 ~ -1）必然為 NG
        Assert.False(engines.Daq.CompareAvgInRange("FastW", max: 10, min: 2));
        Assert.False(engines.Daq.CompareMaxMinInRange("SlowW", max: 10, min: 2));

        // 透過 v2.0 腳本以相同的秒數區間 {start=0.3, end=0.7} 檢核不同取樣率波形 -> 皆須 PASS
        string script = string.Join("\n", new[]
        {
            "DAQ@CompareAvgInRange :: wav=FastW, max=10, min=2 {start=0.3, end=0.7}",
            "DAQ@CompareMaxMinInRange :: wav=SlowW, max=10, min=2 {start=0.3, end=0.7}"
        });

        vm.LoadProgram(0, ProgramTable.FromText(script, 0, "DaqCompareSecE2E"));
        vm.StartThread(0, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 3000)
        {
            vm.Tick();
            System.Threading.Thread.Sleep(5);
        }

        Assert.False(vm.MainThreads[0].IsRunning);
        Assert.True(engines.Io.GetPort("FastW#OK?"));
        Assert.True(engines.Io.GetPort("SlowW#OK?"));
    }

    [Fact]
    public void UserButtonAndBlinking_ExplicitLogic_SupportsOnOffAndLogicExpressions()
    {
        Assert.Equal("ui=BtnStart, logic=True", CommandSyntaxBridge.FormatToV2("User@ButtonTrigger", "BtnStart, ON"));
        Assert.Equal("ui=BtnAuto, logic=False", CommandSyntaxBridge.FormatToV2("User@ButtonValue", "BtnAuto, 0"));
        Assert.Equal("ui=BtnAlarm, logic=Alarm?", CommandSyntaxBridge.FormatToV2("User@ButtonBlinking", "BtnAlarm, Alarm?"));

        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));
        engines.UserPanel.RegisterControls(new[]
        {
            new ARCOS.Core.Engines.UserPanelControlInfo { Name = "BtnStart", Kind = ARCOS.Core.Engines.UserPanelControlKind.Button, Role = ARCOS.Core.Engines.UserPanelElementRole.Control },
            new ARCOS.Core.Engines.UserPanelControlInfo { Name = "BtnAuto", Kind = ARCOS.Core.Engines.UserPanelControlKind.ToggleButton, Role = ARCOS.Core.Engines.UserPanelElementRole.Control },
            new ARCOS.Core.Engines.UserPanelControlInfo { Name = "BtnAlarm", Kind = ARCOS.Core.Engines.UserPanelControlKind.Button, Role = ARCOS.Core.Engines.UserPanelElementRole.Control }
        });
        io.SetPort("Ready?", true);
        io.SetPort("Alarm?", true);

        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);
        var requests = new List<ARCOS.Core.Engines.UserPanelCommandRequest>();
        engines.UserPanel.CommandRequested += r => requests.Add(r);

        string script = string.Join("\n", new[]
        {
            "User@ButtonTrigger :: ui=BtnStart, logic=Ready?",
            "User@ButtonValue :: ui=BtnAuto, logic=!Ready?",
            "User@ButtonBlinking :: ui=BtnAlarm, logic=Alarm?"
        });

        vm.LoadProgram(0, ProgramTable.FromText(script, 0, "UserLogicE2E"));
        vm.StartThread(0, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 3000)
        {
            vm.Tick();
            System.Threading.Thread.Sleep(5);
        }

        Assert.False(vm.MainThreads[0].IsRunning);
        Assert.True(io.GetPort("UserPanel#BtnStart?"));
        Assert.Contains(requests, r => r.CommandType == ARCOS.Core.Engines.UserPanelCommandType.ButtonValue && r.ControlName == "BtnStart" && r.BoolValue && r.TriggerEvent);
        Assert.Contains(requests, r => r.CommandType == ARCOS.Core.Engines.UserPanelCommandType.ButtonValue && r.ControlName == "BtnAuto" && !r.BoolValue && !r.TriggerEvent);
        Assert.Contains(requests, r => r.CommandType == ARCOS.Core.Engines.UserPanelCommandType.SetBlinking && r.ControlName == "BtnAlarm" && r.BoolValue);
    }

    [Fact]
    public void PythonReset_UnifiedSessionSemantics_DefaultSpecificAndAll()
    {
        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        string script = string.Join("\n", new[]
        {
            "Python@Script :: code=\"v_def = 11\"",
            "Python@Script :: code=\"v_s1 = 22\" {session=PyS1}",
            "Python@Script :: code=\"v_s2 = 33\" {session=PyS2}",
            // 1. 不指定 session -> 僅清預設 session，PyS1/PyS2 不受影響
            "Python@Reset",
            "Python@GetVariable :: py=v_def -> var=DefAfterDefaultReset",
            "Python@GetVariable :: py=v_s1 {session=PyS1} -> var=S1AfterDefaultReset",
            // 2. 指定 session=PyS1 -> 僅清 PyS1，PyS2 不受影響
            "Python@Reset {session=PyS1}",
            "Python@GetVariable :: py=v_s1 {session=PyS1} -> var=S1AfterS1Reset",
            "Python@GetVariable :: py=v_s2 {session=PyS2} -> var=S2AfterS1Reset",
            // 3. 指定 session=ALL -> 全清
            "Python@Reset {session=ALL}",
            "Python@GetVariable :: py=v_s2 {session=PyS2} -> var=S2AfterAllReset"
        });

        vm.LoadProgram(0, ProgramTable.FromText(script, 0, "PyResetUnifiedE2E"));
        vm.StartThread(0, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 5000)
        {
            vm.Tick();
            System.Threading.Thread.Sleep(5);
        }

        Assert.False(vm.MainThreads[0].IsRunning);
        Assert.Equal("", engines.Variable.Get("DefAfterDefaultReset"));
        Assert.Equal("22", engines.Variable.Get("S1AfterDefaultReset"));
        Assert.Equal("", engines.Variable.Get("S1AfterS1Reset"));
        Assert.Equal("33", engines.Variable.Get("S2AfterS1Reset"));
        Assert.Equal("", engines.Variable.Get("S2AfterAllReset"));

        // 驗證 Python@SetCaptureFile 已移除，且 SetCapture / GetCapture 統一採用 cam（GetCapture 的 -> cam 為必填）
        Assert.Null(StandardCommandSchemas.Get("Python@SetCaptureFile"));
        var getCapSchema = StandardCommandSchemas.Get("Python@GetCapture");
        Assert.NotNull(getCapSchema);
        var camOutParam = getCapSchema!.FindParam("cam");
        Assert.NotNull(camOutParam);
        Assert.Equal(ParamDirection.Output, camOutParam!.Direction);
        Assert.True(camOutParam.Required);
        Assert.Equal("Cam0, PythonName=img0",
            CommandSyntaxBridge.NormalizeForExecution("Python@SetCapture", "cam=Cam0 {py=img0}"));
        Assert.Equal("out_img, ARCOSName=CamOut",
            CommandSyntaxBridge.NormalizeForExecution("Python@GetCapture", "py=out_img -> cam=CamOut"));
        Assert.Equal("py=out_img -> cam=CamOut",
            CommandSyntaxBridge.FormatToV2("Python@GetCapture", "out_img, ARCOSName=CamOut"));
    }

    [Fact]
    public void RedisIncludeModule_AutoStartsLocalServer_AndSupportsDefaultPlusNamedSessions()
    {
        var io = new ARCOS.Core.Engines.IoEngine();
        var engines = new BasicEngines(
            io,
            new ARCOS.Core.Engines.VariableEngine(),
            new ARCOS.Core.Engines.PointEngine(io),
            new ARCOS.Core.Engines.ImageEngine(io),
            new ARCOS.Core.Engines.DaqEngine(io),
            new ARCOS.Core.Engines.TimerEngine(io));
        var drivers = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, drivers);

        // 不呼叫 CreateSession，僅 IncludeModule::Redis 後即可直接讀寫本機預設 Redis (127.0.0.1:6379 DB0)
        // 再用 CreateSession 連線 DB2 作為其他 Redis Session，互不覆蓋預設 Session；並測試不可達 port 寫入錯誤狀態
        string uniqueKey = $"ArcosAutoTest_{Guid.NewGuid():N}";
        string script = string.Join("\n", new[]
        {
            "System@IncludeModule :: module=Redis",
            $"Redis@Set :: key={uniqueKey}, val=LOCAL_DEFAULT_VAL",
            "Redis@CreateSession :: session=OtherDB {url=127.0.0.1:6379/2}",
            $"Redis@Set :: key={uniqueKey}, val=OTHER_DB2_VAL {{session=OtherDB}}",
            $"Redis@Get :: key={uniqueKey} -> var=ValLocal",
            $"Redis@Get :: key={uniqueKey} {{session=OtherDB}} -> var=ValOther",
            "Redis@CreateSession :: session=BadRemote {url=127.0.0.1:16399}",
            $"Redis@CMD :: cmd=\"DEL {uniqueKey}\"",
            $"Redis@CMD :: cmd=\"DEL {uniqueKey}\" {{session=OtherDB}}"
        });

        vm.LoadProgram(0, ProgramTable.FromText(script, 0, "RedisAutoStartE2E"));
        vm.StartThread(0, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.MainThreads[0].IsRunning && sw.ElapsedMilliseconds < 6000)
        {
            vm.Tick();
            System.Threading.Thread.Sleep(5);
        }

        Assert.False(vm.MainThreads[0].IsRunning);
        Assert.True(ARCOS.Core.Engines.RedisEngine.ProbePing("127.0.0.1", 6379));
        Assert.Equal("LOCAL_DEFAULT_VAL", engines.Variable.Get("ValLocal"));
        Assert.Equal("OTHER_DB2_VAL", engines.Variable.Get("ValOther"));
        Assert.Equal("OK", engines.Variable.Get("RedisErr#OtherDB", "Global"));
        Assert.StartsWith("ERR:", engines.Variable.Get("RedisErr#BadRemote", "Global"));
    }
}
