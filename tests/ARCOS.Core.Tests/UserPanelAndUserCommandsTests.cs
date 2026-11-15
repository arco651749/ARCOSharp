using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using ARCOS.Core.Common;
using ARCOS.Core.Csvi;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using ARCOS.Core.Execution;
using ARCOS.Core.Model;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;
using Xunit;

namespace ARCOS.Core.Tests;

public class UserPanelAndUserCommandsTests
{
    private static (ExecutionContext ctx, CommandExecutor exec, BasicEngines engines, List<UserPanelCommandRequest> uiReqs) CreateUserHarness(string script)
    {
        var registry = ModuleRegistry.CreateDefault();
        var executor = new CommandExecutor(registry);
        var engines = new BasicEngines();
        var driverMgr = new DriverManager(engines);
        var program = ProgramTable.FromText(script, 0, "UserTestProg");
        var ctx = new ExecutionContext(0, "ThreadMain", program, engines, driverMgr)
        {
            Registry = registry
        };

        var uiReqs = new List<UserPanelCommandRequest>();
        engines.UserPanel.CommandRequested += req => uiReqs.Add(req);
        return (ctx, executor, engines, uiReqs);
    }

    private static void RunAll(ExecutionContext ctx, CommandExecutor exec, int maxSteps = 100)
    {
        int guard = 0;
        while (ctx.IsRunning && ctx.ProgIndex <= ctx.Program.Count && guard++ < maxSteps)
        {
            var res = exec.ExecuteStep(ctx);
            Assert.NotEqual(CommandStatus.Error, res.Status);
        }
    }

    [Fact]
    public void UserPanelCsvi_ContainsIoVariableCaptureDaqAndUserControlsAndIndicators()
    {
        string path = ArcosPaths.ResolveUserPanelCsviPath("UserPanel.csvi");
        Assert.True(File.Exists(path), $"UserPanel.csvi not found: {path}");

        var pkg = CsviPackage.Load(path);
        Assert.NotNull(pkg.LoadedAssembly);

        var ctrlType = pkg.LoadedAssembly!.GetTypes().FirstOrDefault(t => t.Name == "UserPanelView");
        Assert.NotNull(ctrlType);

        var view = Activator.CreateInstance(ctrlType!) as Control;
        Assert.NotNull(view);

        var engines = new BasicEngines();
        engines.Variable.Set("$TotalCount", "42");
        engines.Variable.Set("$Recipe", "Recipe_Alpha");
        engines.Io.SetPort("P0.5", true);

        ctrlType!.GetMethod("Init")!.Invoke(view, new object[] { engines });

        // Verify named Controls & Indicators for IO, VARIABLE, CAPTURE, DAQ, USER exist inside UserPanel.csvi
        string[] expectedElements =
        {
            "MainTab", "BtnPartFeed", "BtnInspect", "BtnReject", "TogVacuum", "BtnConfirm", "BtnResetStats",
            "TxtRecipe", "TxtLotNo", "CmbRunMode", "TxtHmiMessage",
            "IndTotalParts", "IndOkParts", "IndNgParts", "IndCycleTime", "IndVarResult",
            "LedStationState", "LedP0_0", "LedP0_5", "LedP0_7", "LedP1_2",
            "ImgCam1", "PlotDaq1"
        };

        foreach (var name in expectedElements)
        {
            var found = view!.FindControl<Control>(name);
            Assert.NotNull(found);
        }

        pkg.Unload();
    }

    [Fact]
    public void UserPanelEngine_QueryReadWrite_ControlsAndIndicators_WorksEndToEnd()
    {
        string script = @"
User@ButtonValue::TogVacuum,ON;
User@ButtonTrigger::BtnConfirm,ON;
User@ButtonBlinking::BtnConfirm,1;
User@ButtonText::BtnConfirm,Press Now;
User@TextBoxValue::TxtHmiMessage,Running Step 1;
User@TextBoxTrigger::TxtRecipe,Recipe_V2;
User@TextBoxBlinking::TxtHmiMessage,1;
User@TextRingValue::CmbRunMode,DRY_RUN;
User@TextRingTrigger::CmbRunMode,STEP;
User@TextRingBlinking::CmbRunMode,1;
User@DisableControl::BtnConfirm;
User@HideControl::TxtHmiMessage;
User@TabValue::MainTab,1;
User@TabTrigger::MainTab,2;
User@QueryControls::$Ctrls,Role=Control;
User@QueryControls::$Inds,Role=Indicator;
User@GetControlValue::TxtHmiMessage,$ReadMsg;
User@SetControlValue::IndVarResult,PASS;
User@GetControlValue::IndVarResult,$ReadInd;
User@Param2UserPanel::;
";

        var (ctx, exec, engines, uiReqs) = CreateUserHarness(script);

        engines.UserPanel.RegisterControls(new[]
        {
            new UserPanelControlInfo { Name = "TogVacuum", Kind = UserPanelControlKind.ToggleButton, Role = UserPanelElementRole.Control, BindingTarget = "IO:P0.5", IsMountedOnUi = true },
            new UserPanelControlInfo { Name = "BtnConfirm", Kind = UserPanelControlKind.Button, Role = UserPanelElementRole.Control, Text = "Confirm", IsMountedOnUi = true },
            new UserPanelControlInfo { Name = "TxtHmiMessage", Kind = UserPanelControlKind.TextBox, Role = UserPanelElementRole.Control, Value = "", IsMountedOnUi = true },
            new UserPanelControlInfo { Name = "TxtRecipe", Kind = UserPanelControlKind.TextBox, Role = UserPanelElementRole.Control, BindingTarget = "VAR:$Recipe", IsMountedOnUi = true },
            new UserPanelControlInfo { Name = "CmbRunMode", Kind = UserPanelControlKind.ComboBox, Role = UserPanelElementRole.Control, BindingTarget = "VAR:$RunMode", IsMountedOnUi = true },
            new UserPanelControlInfo { Name = "MainTab", Kind = UserPanelControlKind.TabControl, Role = UserPanelElementRole.Control, IsMountedOnUi = true },
            new UserPanelControlInfo { Name = "IndVarResult", Kind = UserPanelControlKind.TextBlock, Role = UserPanelElementRole.Indicator, BindingTarget = "VAR:$InspectionResult", IsMountedOnUi = true },
            new UserPanelControlInfo { Name = "ImgCam1", Kind = UserPanelControlKind.Image, Role = UserPanelElementRole.Indicator, BindingTarget = "CAPTURE:Cam1", IsMountedOnUi = true },
            new UserPanelControlInfo { Name = "PlotDaq1", Kind = UserPanelControlKind.Plot, Role = UserPanelElementRole.Indicator, BindingTarget = "DAQ:Wave1", IsMountedOnUi = true }
        });

        RunAll(ctx, exec);

        // 1. State verification in UserPanelEngine cache
        Assert.Equal("ON", engines.UserPanel.ReadControlValue("TogVacuum"));
        Assert.True(engines.Io.GetPort("UserPanel#BtnConfirm?"));
        Assert.Equal("Running Step 1", engines.Variable.Get("$ReadMsg"));
        Assert.Equal("Recipe_V2", engines.Variable.Get("$Recipe"));
        Assert.Equal("STEP", engines.Variable.Get("$RunMode"));
        Assert.Equal("PASS", engines.Variable.Get("$ReadInd"));
        Assert.Equal("2", engines.UserPanel.ReadControlValue("MainTab"));

        Assert.True(engines.UserPanel.TryGetControl("BtnConfirm", out var btnInfo));
        Assert.True(btnInfo!.IsBlinking);
        Assert.False(btnInfo.IsEnabled);
        Assert.Equal("Press Now", btnInfo.Text);

        Assert.True(engines.UserPanel.TryGetControl("TxtHmiMessage", out var msgInfo));
        Assert.False(msgInfo!.IsVisible);

        Assert.True(engines.UserPanel.TryGetControl("CmbRunMode", out var cmbInfo));
        Assert.True(cmbInfo!.IsBlinking);

        // Re-enable & Show verification (ProgIndex is 1-based)
        var reEnableProg = ProgramTable.FromText("User@EnableControl::BtnConfirm;\nUser@ShowControl::TxtHmiMessage;", 1, "ReEnableProg");
        ctx.Program = reEnableProg;
        ctx.ProgIndex = 1;
        ctx.IsRunning = true;
        RunAll(ctx, exec);
        Assert.True(engines.UserPanel.TryGetControl("BtnConfirm", out var btnReEnabled) && btnReEnabled!.IsEnabled);
        Assert.True(engines.UserPanel.TryGetControl("TxtHmiMessage", out var msgShown) && msgShown!.IsVisible);

        // 2. Query controls vs indicators separated properly
        string ctrls = engines.Variable.Get("$Ctrls");
        string inds = engines.Variable.Get("$Inds");
        Assert.Contains("BtnConfirm", ctrls);
        Assert.Contains("TxtRecipe", ctrls);
        Assert.Contains("MainTab", ctrls);
        Assert.Contains("ImgCam1", inds);
        Assert.Contains("PlotDaq1", inds);
        Assert.Contains("IndVarResult", inds);
        Assert.DoesNotContain("ImgCam1", ctrls);
        Assert.DoesNotContain("BtnConfirm", inds);

        // 3. UI command dispatch verification (Silent vs Trigger)
        Assert.Contains(uiReqs, r => r.CommandType == UserPanelCommandType.ButtonValue && r.ControlName == "TogVacuum" && !r.TriggerEvent);
        Assert.Contains(uiReqs, r => r.CommandType == UserPanelCommandType.ButtonValue && r.ControlName == "BtnConfirm" && r.TriggerEvent);
        Assert.Contains(uiReqs, r => r.CommandType == UserPanelCommandType.SelectTab && r.Value == "1" && !r.TriggerEvent);
        Assert.Contains(uiReqs, r => r.CommandType == UserPanelCommandType.SelectTab && r.Value == "2" && r.TriggerEvent);
        Assert.Contains(uiReqs, r => r.CommandType == UserPanelCommandType.ParamRefresh);
    }

    [Fact]
    public void CustomTestUserPanels_TemplatesAndStationB_LoadAndContainAllControlTypes()
    {
        string templatesPath = ArcosPaths.ResolveUserPanelCsviPath("UserPanel_Templates.csvi");
        string stationBPath = ArcosPaths.ResolveUserPanelCsviPath("UserPanel_StationB.csvi");

        Assert.True(File.Exists(templatesPath), $"UserPanel_Templates.csvi not found: {templatesPath}");
        Assert.True(File.Exists(stationBPath), $"UserPanel_StationB.csvi not found: {stationBPath}");

        var emptyEngines = new BasicEngines();
        var engines = new BasicEngines();
        engines.Variable.Set("StepStatus", "READY_TEMPLATE");
        engines.Variable.Set("StationBCount", "88");

        var pkgT = CsviPackage.Load(templatesPath);
        Assert.NotNull(pkgT.LoadedAssembly);
        var typeT = pkgT.LoadedAssembly!.GetTypes().FirstOrDefault(t => typeof(Control).IsAssignableFrom(t) && !t.IsAbstract);
        Assert.NotNull(typeT);
        var viewT = Activator.CreateInstance(typeT!) as Control;
        Assert.NotNull(viewT);
        // 驗證空白環境下 Init 不應因變數未設定而拋錯
        typeT!.GetMethod("Init")?.Invoke(viewT, new object[] { emptyEngines });
        typeT!.GetMethod("Init")?.Invoke(viewT, new object[] { engines });

        string[] expectedTemplateControls =
        {
            "LedPort0", "IndStatus", "MainTab", "BtnDemo", "TogVacuum",
            "ChkBypass", "CmbRunMode", "TxtRecipe", "TxtHmiMessage",
            "ImgCam1", "PlotDaq1"
        };
        foreach (var name in expectedTemplateControls)
        {
            Assert.NotNull(viewT!.FindControl<Control>(name));
        }

        var pkgB = CsviPackage.Load(stationBPath);
        Assert.NotNull(pkgB.LoadedAssembly);
        var typeB = pkgB.LoadedAssembly!.GetTypes().FirstOrDefault(t => typeof(Control).IsAssignableFrom(t) && !t.IsAbstract);
        Assert.NotNull(typeB);
        var viewB = Activator.CreateInstance(typeB!) as Control;
        Assert.NotNull(viewB);
        // 驗證空白環境下 Init 不應因變數未設定而拋錯
        typeB!.GetMethod("Init")?.Invoke(viewB, new object[] { emptyEngines });
        typeB!.GetMethod("Init")?.Invoke(viewB, new object[] { engines });

        string[] expectedStationBControls =
        {
            "LedStationB", "IndInspectCount", "BtnConfirm", "TogClampB",
            "CmbFilterMode", "TxtHmiMessage", "TabStationB", "ImgCam1", "PlotDaq1"
        };
        foreach (var name in expectedStationBControls)
        {
            Assert.NotNull(viewB!.FindControl<Control>(name));
        }

        pkgT.Unload();
        pkgB.Unload();
    }

    [Fact]
    public void MultiUserPanel_OpenSwitchScopedControlAndClose_WorksEndToEnd()
    {
        var registry = ModuleRegistry.CreateDefault();
        var executor = new CommandExecutor(registry);
        var engines = new BasicEngines();
        var driverMgr = new DriverManager(engines);
        var vm = new ArcosThreadVM(engines, driverMgr, executor);

        var openEvents = new List<string>();
        var closeEvents = new List<string>();
        vm.OnUserPanelOpenRequested += s => openEvents.Add(s);
        vm.OnUserPanelCloseRequested += s => closeEvents.Add(s);

        var uiReqs = new List<UserPanelCommandRequest>();
        engines.UserPanel.CommandRequested += r => uiReqs.Add(r);

        string script = @"
System@UserPanelOpen::UserPanel_Templates.csvi,Templates;
System@UserPanelOpen::UserPanel_StationB.csvi,StationB;
User@SwitchUserPanel::Templates;
User@TextBoxValue::TxtHmiMessage,Templates Msg;
User@ButtonText::BtnDemo,Template Go;
User@SetControlValue::StationB:TxtHmiMessage,StationB Background Msg;
User@ButtonText::StationB:BtnConfirm,B Armed;
User@TextRingValue::StationB:CmbFilterMode,MEDIAN;
User@QueryControls::$AllCtrls,Panel=*,Role=All;
User@SwitchUserPanel::StationB;
User@GetControlValue::TxtHmiMessage,$CurActiveMsg;
User@GetControlValue::Templates:TxtHmiMessage,$TemplateMsg;
User@GetControlValue::Templates:BtnDemo,$TemplateBtn;
System@UserPanelClose::StationB;
";

        var program = ProgramTable.FromText(script, 0, "MultiPanelTest");
        var ctx = new ExecutionContext(0, "ThreadMain", program, engines, driverMgr)
        {
            Registry = registry,
            VM = vm
        };

        RunAll(ctx, executor);

        Assert.Equal(2, openEvents.Count);
        Assert.Single(closeEvents);

        // StationB closed -> fallback active panel must revert to Templates
        Assert.Equal("Templates", engines.UserPanel.ActivePanelName);
        Assert.Equal("StationB Background Msg", engines.Variable.Get("$CurActiveMsg"));
        Assert.Equal("Templates Msg", engines.Variable.Get("$TemplateMsg"));
        Assert.Contains("Templates:TxtHmiMessage", engines.Variable.Get("$AllCtrls"));
        Assert.Contains("StationB:TxtHmiMessage", engines.Variable.Get("$AllCtrls"));

        // Verify SwitchPanel and ClosePanel requests emitted cleanly
        Assert.Contains(uiReqs, r => r.CommandType == UserPanelCommandType.SwitchPanel && r.ControlName == "Templates");
        Assert.Contains(uiReqs, r => r.CommandType == UserPanelCommandType.SwitchPanel && r.ControlName == "StationB");
        Assert.Contains(uiReqs, r => r.CommandType == UserPanelCommandType.ClosePanel && r.ControlName == "StationB");
        Assert.Contains(uiReqs, r => r.CommandType == UserPanelCommandType.TextValue && r.PanelName == "StationB" && r.ControlName == "TxtHmiMessage");
    }
}
