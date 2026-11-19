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
        Assert.Equal(5.0, waitParsed.GetDouble("timeoutSec"), 6);
        Assert.Equal("Err1", waitParsed.ErrorLabel);
        Assert.Equal("等待啟動訊號逾時", waitParsed.ErrorMessage);
        Assert.Equal("io=Start? {timeoutSec=5} [[Err1]]等待啟動訊號逾時", CommandFormatter.FormatParameter(waitParsed, waitSchema));

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
        Assert.Equal("System@TimerReset :: timer=T_Heat {timeoutSec=30}", timerLine);
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
        Assert.Equal("msg=\"First line … (+2 lines)\"", l1MultiData);
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
            p0.Add(CommandItem.Parse("System@ProgramLoad :: path=\"C:\\\\Data\\\\Master.prog\"", 2));
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
            Assert.Equal(@"C:\Data\Master.prog", parsedLoad.Get("path"));

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
            "System@TimerReset :: timer=T_Run {timeoutSec=10}",
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
            "IO@WaitPortOn :: io=NeverOn? {timeoutSec=0.05} [[ErrTimeout]]V2逾時自癒",
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
        Assert.Equal("timer=T_Heat {timeoutSec=30}", CommandSyntaxBridge.FormatToV2("System@TimerReset", "T_Heat, 30"));
        Assert.Equal("counter=C_Retry {target=3}", CommandSyntaxBridge.FormatToV2("System@CounterReset", "C_Retry, 3"));
        Assert.Equal("rec=CT_Pick {msg=Start}", CommandSyntaxBridge.FormatToV2("System@Record", "CT_Pick, Start"));
        Assert.Equal("msg=單純警告", CommandSyntaxBridge.FormatToV2("System@WarningBox", "單純警告"));
        Assert.Equal("msg=料盤不足 {logic=LowTray?}", CommandSyntaxBridge.FormatToV2("System@WarningBox", "LowTray?, 料盤不足"));
        Assert.Equal("enabled=true", CommandSyntaxBridge.FormatToV2("Thread@MutePopupMessage", "1"));
        Assert.Equal("label=ErrCatch", CommandSyntaxBridge.FormatToV2("Thread@ErrorHandler", "[[ErrCatch]]"));
        Assert.Equal("subs=[Sub1;Sub2] {skipNotFound=true}", CommandSyntaxBridge.FormatToV2("Thread@SubThreadWait", "Sub1, Sub2, SKIPNOTFIND=1"));
        Assert.Equal("lock=GantryZone {timeoutSec=10}", CommandSyntaxBridge.FormatToV2("Thread@KeySectionLock", "GantryZone, 10"));

        // VARIABLE & POINT&PATH 舊式特殊語法升級（顯式目標置左，附加可選輸出才用 ->）
        Assert.Equal("var=Total, expr=$(A)+$(B)", CommandSyntaxBridge.FormatToV2("Variable@Calculate", "$Total=$(A)+$(B)"));
        Assert.Equal("var=Res, logic=Pass?, true=OK, false=NG", CommandSyntaxBridge.FormatToV2("Variable@Select", "Res=Pass??OK:NG"));
        Assert.Equal("var=PickX, pt=PickPos, axis=X", CommandSyntaxBridge.FormatToV2("Variable@GetPointElement", "$PickX=X(PickPos)"));
        Assert.Equal("var=OutSet, varA=SetA, varB=SetB", CommandSyntaxBridge.FormatToV2("Variable@SUnion", "OutSet, SetA, SetB"));
        Assert.Equal("pt=P_Out, expr=P1+P2", CommandSyntaxBridge.FormatToV2("Point&Path@Calculate", "$P_Out=P1+P2"));
        Assert.Equal("pt=Tray, expr=\"(10,20,30)\" {append=true}", CommandSyntaxBridge.FormatToV2("Point&Path@Calculate", "Tray<=(10,20,30)"));
        Assert.Equal("pt=P_Dst, logic=Left?, true=P_L, false=P_R", CommandSyntaxBridge.FormatToV2("Point&Path@Select", "P_Dst=Left??P_L:P_R"));
        Assert.Equal("pt=P1, delta=\"(10,20,0)\"", CommandSyntaxBridge.FormatToV2("Point&Path@AddXYZ", "P1, (10,20,0)"));
        Assert.Equal("pt=P1, rangePt=SafeBox -> var=IsSafe", CommandSyntaxBridge.FormatToV2("Point&Path@ChkInRangeXY", "P1, SafeBox, IsSafe"));
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
            "Variable@Calculate :: var=Total, expr=$(A) + $(B)",
            "Variable@Select :: var=Status, logic=$(Total)==40, true=PASS, false=FAIL",
            "Variable@Set :: var=Queue, val=[Item1;Item2;Item3]",
            "Variable@LPop :: var=Queue -> out=HeadVal",
            "Variable@Set :: var=Set1, val=[A;B]",
            "Variable@Set :: var=Set2, val=[B;C]",
            "Variable@SIntersection :: var=InterOut, varA=Set1, varB=Set2",
            "Point&Path@Calculate :: pt=P1, expr=\"(10, 20, 30)\"",
            "Point&Path@AddXYZ :: pt=P1, delta=\"(5, 10, 0)\"",
            "Point&Path@Calculate :: pt=P2, expr=\"(100, 100, 0)\"",
            "Point&Path@Select :: pt=P_Sel, logic=$(Total)==40, true=P1, false=P2",
            "Variable@GetPointElement :: var=SelX, pt=P_Sel, axis=X",
            "Point&Path@InsertOnePoint :: pt=Tray1D, val=\"(0,0,0)\"",
            "Point&Path@Calculate :: pt=Tray1D, expr=\"(100,0,0)\" {append=true}",
            "Point&Path@Pallet1D :: pt=Tray1D, count=5",
            "Point&Path@InsertOnePoint :: pt=Box, val=\"(0,0,0)\"",
            "Point&Path@InsertOnePoint :: pt=Box, val=\"(50,50,0)\"",
            "Point&Path@ChkInRangeXY :: pt=P_Sel, rangePt=Box -> var=InSafeBox"
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
        Assert.Equal("1", engines.Variable.Get("InSafeBox", sec));
        Assert.True(engines.Io.GetPort("P_Sel#InRange?"));
    }

    [Fact]
    public void StandardCommandSchemas_Batch5Modules_All100RegisteredWithDomainTypedKeys()
    {
        Assert.Equal(21, StandardCommandSchemas.GetByModule("Motion").Count);
        Assert.Equal(28, StandardCommandSchemas.GetByModule("Capture").Count);
        Assert.Equal(18, StandardCommandSchemas.GetByModule("DAQ").Count);
        Assert.Equal(21, StandardCommandSchemas.GetByModule("User").Count);
        Assert.Equal(12, StandardCommandSchemas.GetByModule("Python").Count);

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
        Assert.Equal("card=IO1, driver=MockIO {port=COM1}",
            CommandSyntaxBridge.FormatToV2("IO@AddIODriver", "IO1, MockIO, DEVICE=COM1"));

        var patMatch = StandardCommandSchemas.Get("Capture@PatternMatchOne")!;
        Assert.Equal("imaq", patMatch.Parameters[0].Name);
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
        Assert.Equal("mot=Motor1, axes=XY, speed=100, coords=\"10,20\"",
            CommandSyntaxBridge.FormatToV2("Motion@Go", "Motor1, XY, 100, 10, 20"));
        Assert.Equal("Motor1, XY, 100, 10, 20",
            CommandSyntaxBridge.NormalizeForExecution("Motion@Go", "mot=Motor1, axes=XY, speed=100, coords=\"(10, 20)\""));
        Assert.Equal("mot=Motor1, speed=100, pt=PickPos {safePos=25}",
            CommandSyntaxBridge.FormatToV2("Motion@GoSafe", "Motor1, 100, PickPos, Z=25"));
        Assert.Equal("Motor1, 100, PickPos, X=15, [[ErrM]]逾時",
            CommandSyntaxBridge.NormalizeForExecution("Motion@GoSafe", "mot=Motor1, speed=100, pt=PickPos {safeAxis=X, safePos=15} [[ErrM]]逾時"));

        // CAPTURE
        Assert.Equal("imaq=Cam0, roi=\"10,20,300,200\"",
            CommandSyntaxBridge.FormatToV2("Capture@ROISet", "Cam0, 10, 20, 300, 200"));
        Assert.Equal("imaq=Cam0, template=Mark.png {angle=15} -> var=Score, pt=MarkPt",
            CommandSyntaxBridge.FormatToV2("Capture@PatternMatchOne", "Cam0, Mark.png, $Score, MarkPt, 15"));
        Assert.Equal("Cam0, Mark.png, Score, MarkPt, 15",
            CommandSyntaxBridge.NormalizeForExecution("Capture@PatternMatchOne", "imaq=Cam0, template=Mark.png {angle=15} -> var=Score, pt=MarkPt"));

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
        Assert.Equal("ctrl=TxtSpeed -> var=CurSpeed",
            CommandSyntaxBridge.FormatToV2("User@GetControlValue", "TxtSpeed, $CurSpeed"));
        Assert.Equal("BtnList, Panel=StationA, Kind=Button",
            CommandSyntaxBridge.NormalizeForExecution("User@QueryControls", "{panel=StationA, kind=Button} -> var=BtnList"));
        Assert.Equal("ctrl=BtnStart, text=\"Hello, World\"",
            CommandSyntaxBridge.FormatToV2("User@ButtonText", "BtnStart, Hello, World"));

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
        Assert.Equal(100, verifiedCsvis);

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
            "Capture@CalibrateManual :: imaq=Cam0 {scale=0.02, originX=10, originY=20}",
            "Capture@ROISet :: imaq=Cam0, roi=\"10,20,300,200\"",
            "Capture@FindCircle(InOut) :: imaq=Cam0 -> pt=CirclePt",
            "Capture@PatternMatchOne :: imaq=Cam0, template=Mark.png {angle=15} -> var=MatchScore, pt=MarkPt",
            // DAQ
            "DAQ@WaveFormGenerate :: wav=W1 {type=Square, count=20, amp=5, offset=5}",
            "DAQ@WaveFormGenerate :: wav=W2 {type=Square, count=20, amp=5, offset=5}",
            "DAQ@Calculate :: wav=WSum, expr=W1 + W2",
            "DAQ@Filter :: wav=WSum, type=MovingAverage {window=3}",
            "DAQ@MergeWaveForm :: wav=WMulti, wavs=[W1;W2]",
            "DAQ@CompareAvgInRange :: wav=W1, max=15, min=0",
            // USER
            "User@SetControlValue :: ctrl=TxtSpeed, val=250",
            "User@GetControlValue :: ctrl=TxtSpeed -> var=ReadSpeed",
            "User@ButtonTrigger :: ctrl=BtnStart {on=true}",
            "User@QueryControls {kind=Button} -> var=BtnNames",
            // PYTHON
            "Python@ResetSession {session=V2Test}",
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
        Assert.Equal(15, StandardCommandSchemas.GetByModule("MultiStation").Count);
        Assert.Equal(3, StandardCommandSchemas.GetByModule("AIAgent").Count);
        Assert.Equal(2, StandardCommandSchemas.GetByModule("Hello").Count);
        int totalUnique = StandardCommandSchemas.All.Values.Select(s => s.CommandKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(251, totalUnique);

        // 2. 驗證雙向轉換（含 Redis 多 Session/key 與 AIAgent ~ Bypass <-> {bypass=true}）
        Assert.Equal("key=LotId -> var=CurLot", CommandSyntaxBridge.FormatToV2("Redis@Get", "LotId, $CurLot"));
        Assert.Equal("JobQ, Session=MES, TimeOut=3, [[ErrQ]]逾時", CommandSyntaxBridge.NormalizeForExecution("Redis@BLPop", "key=JobQ {session=MES, timeoutSec=3} -> var=NextJob [[ErrQ]]逾時"));
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
        Assert.Equal(29, verifiedCsvis);

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
            // REDIS（第一個 CreateSession=Line1 為預設 Session，MES 為第二個隔離 Session）
            "Redis@CreateSession :: session=Line1 {endpoint=127.0.0.1:6379}",
            "Redis@CreateSession :: session=MES {endpoint=192.168.1.20:6379}",
            "Redis@Set :: key=LotId, val=LOT_LINE1",
            "Redis@Set :: key=LotId, val=LOT_MES {session=MES}",
            "Redis@Get :: key=LotId -> var=ReadLotDefault",
            "Redis@Get :: key=LotId {session=MES} -> var=ReadLotMes",
            "Redis@LPush :: key=JobQ, val=JOB_A",
            "Redis@LPush :: key=JobQ, val=JOB_B",
            "Redis@LPush :: key=JobQ, val=JOB_MES {session=MES}",
            "Redis@LPop :: key=JobQ -> var=Pop1",
            "Redis@BLPop :: key=JobQ {timeoutSec=2} -> var=Pop2",
            "Redis@LPop :: key=JobQ {session=MES} -> var=PopMes",
            "Redis@Publish :: key=EvtCh, val=Done",
            "Redis@Subscribe :: key=EvtCh {session=MES}",
            "Redis@CMD :: cmd=PING {session=Line1}",
            // MULTISTATION 1：舊版整數工站向下相容
            "MultiStation@SetStationON :: station=1",
            "MultiStation@SetStationOK :: station=1",
            "MultiStation@SetStationOFF :: station=2",
            "MultiStation@StateShift {shift=1}",
            "MultiStation@UpdateIndicator",
            "MultiStation@ChkStationRunState :: station=2",
            // MULTISTATION 2：命名工位（轉盤 [入料;加工;檢驗;出料] + 隨行資料移位 + OFF 自動清空）
            "MultiStation@AddStation :: station=FeedInStation {items=[入料;加工;檢驗;出料]}",
            "MultiStation@SetStationON :: station=FeedInStation.入料",
            "MultiStation@SetStationOK :: station=FeedInStation.入料",
            "MultiStation@SetData :: station=FeedInStation.入料, val=SN_A001 {field=SN}",
            "MultiStation@StateShift {station=FeedInStation, shift=1}",
            "MultiStation@GetData :: station=FeedInStation.加工 {field=SN} -> var=ShiftedSN",
            "MultiStation@SetStationOFF :: station=FeedInStation.加工",
            "MultiStation@GetData :: station=FeedInStation.加工 {field=SN} -> var=ClearedSN",
            // MULTISTATION 3：1-based Tray 盤結合 Point&Path 點位陣列 + FindFirstOK / Transfer / FindEmpty / 線性輸送帶 wrap=false
            "Point&Path@InsertOnePoint :: pt=TrayPts, val=\"(10,0,0)\"",
            "Point&Path@InsertOnePoint :: pt=TrayPts, val=\"(20,0,0)\"",
            "Point&Path@InsertOnePoint :: pt=TrayPts, val=\"(30,0,0)\"",
            "MultiStation@AddStation :: station=TrayA {pt=TrayPts}",
            "MultiStation@SetStationON :: station=TrayA.2",
            "MultiStation@GetPoint :: station=TrayA.2 -> pt=Slot2Pt",
            // 入料盤 FindFirstOK + Transfer 至工作站入料口
            "MultiStation@SetStationON :: station=TrayA.1",
            "MultiStation@SetStationNG :: station=TrayA.1",
            "MultiStation@SetStationOK :: station=TrayA.2",
            "MultiStation@SetData :: station=TrayA.2, val=LOT_TRAY2 {field=SN}",
            "MultiStation@FindFirstOK :: station=TrayA -> var=PickSlot, pt=PickPt",
            "MultiStation@Transfer :: from=$(PickSlot), to=FeedInStation.入料",
            // 出料盤 FindEmpty + Transfer 下料
            "Point&Path@InsertOnePoint :: pt=OutTrayPts, val=\"(100,0,0)\"",
            "Point&Path@InsertOnePoint :: pt=OutTrayPts, val=\"(200,0,0)\"",
            "MultiStation@AddStation :: station=OutTray {pt=OutTrayPts}",
            "MultiStation@SetStationON :: station=OutTray.1",
            "MultiStation@FindEmpty :: station=OutTray -> var=DropSlot, pt=DropPt",
            "MultiStation@Transfer :: from=FeedInStation.入料, to=$(DropSlot)",
            // 環形緩衝區前/後位移 + 直線輸送帶搭配 SetStationOFF 清頭端
            "MultiStation@AddStation :: station=Conv {items=[S1;S2;S3]}",
            "MultiStation@SetStationON :: station=Conv.S3",
            "MultiStation@SetData :: station=Conv.S3, val=TAIL_PART {field=SN}",
            "MultiStation@StateShift {station=Conv, shift=-1}",
            "MultiStation@GetData :: station=Conv.S2 {field=SN} -> var=ConvBackSN",
            "MultiStation@StateShift {station=Conv, shift=2}",
            "MultiStation@SetStationOFF :: station=Conv.S1",
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

        // REDIS assertions（驗證預設 Session Line1 與指定 Session MES 完整隔離）
        Assert.Equal("OK", engines.Variable.Get("RedisErr#Line1", "Global"));
        Assert.Equal("OK", engines.Variable.Get("RedisErr#MES", "Global"));
        Assert.Equal("LOT_LINE1", engines.Variable.Get("ReadLotDefault", sec));
        Assert.Equal("LOT_MES", engines.Variable.Get("ReadLotMes", sec));
        Assert.Equal("JOB_A", engines.Variable.Get("Pop1", sec));
        Assert.Equal("JOB_B", engines.Variable.Get("Pop2", sec));
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

        Assert.Equal(20.0, engines.Point.GetPoint("Slot2Pt").X, 6);
        // FindFirstOK 跳過 NG 的 TrayA.1，找到第一個可取料 OK 的 TrayA.2，再 Transfer 到 FeedInStation.入料 -> OutTray.2
        Assert.True(engines.Io.GetPort("TrayA#Found?"));
        Assert.Equal("TrayA.2", engines.Variable.Get("PickSlot", sec));
        Assert.Equal(20.0, engines.Point.GetPoint("PickPt").X, 6);
        Assert.False(engines.Io.GetPort("TrayA.2#On?"));
        Assert.False(engines.Io.GetPort("TrayA.2#OK?"));

        Assert.True(engines.Io.GetPort("OutTray#Found?"));
        Assert.Equal("OutTray.2", engines.Variable.Get("DropSlot", sec));
        Assert.Equal(200.0, engines.Point.GetPoint("DropPt").X, 6);
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
}
