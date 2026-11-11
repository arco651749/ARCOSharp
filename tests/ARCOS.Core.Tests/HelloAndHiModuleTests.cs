using System;
using System.Linq;
using ARCOS.Core.Drivers;
using ARCOS.Core.Execution;
using ARCOS.Core.Model;
using Xunit;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Core.Tests;

public class HelloAndHiModuleTests
{
    [Fact]
    public void HelloModule_GetCsviCommands_ListsHelloWorldAndExcludesMaster()
    {
        var cmds = ModuleRegistry.GetCsviCommandsForModule("Hello");
        Assert.NotNull(cmds);
        Assert.Contains("HelloWorld", cmds);
        Assert.DoesNotContain("Hello", cmds);
    }

    [Fact]
    public void HelloModule_HelloWorldCommand_WritesToSystemLog()
    {
        var registry = ModuleRegistry.CreateDefault();
        registry.LoadExternalModules();

        var engines = new BasicEngines();
        var drivers = new DriverManager(engines);
        var executor = new CommandExecutor(registry);

        var ctx = new ExecutionContext(0, "ThreadMain", new ProgramTable(), engines, drivers)
        {
            Registry = registry
        };

        var result = executor.ExecuteCommandLine(ctx, "Hello@HelloWorld::");
        Assert.Equal(CommandStatus.Completed, result.Status);

        var channel = engines.Record.GetChannel("SYSTEMLOG");
        Assert.NotNull(channel);
        Assert.True(channel.Entries.Take(channel.Index).Any(e => e.Message.Contains("hello world", StringComparison.OrdinalIgnoreCase)),
            "SYSTEMLOG should contain 'hello world'");
    }

    [Fact]
    public void HelloModule_HelloWorldCommand_CustomParam_WritesToSystemLog()
    {
        var registry = ModuleRegistry.CreateDefault();
        registry.LoadExternalModules();

        var engines = new BasicEngines();
        var drivers = new DriverManager(engines);
        var executor = new CommandExecutor(registry);

        var ctx = new ExecutionContext(0, "ThreadMain", new ProgramTable(), engines, drivers)
        {
            Registry = registry
        };

        var result = executor.ExecuteCommandLine(ctx, "Hello@HelloWorld::Custom Greeting");
        Assert.Equal(CommandStatus.Completed, result.Status);

        var channel = engines.Record.GetChannel("SYSTEMLOG");
        Assert.NotNull(channel);
        Assert.True(channel.Entries.Take(channel.Index).Any(e => e.Message.Contains("Custom Greeting", StringComparison.OrdinalIgnoreCase)),
            "SYSTEMLOG should contain 'Custom Greeting'");
    }

    [Fact]
    public void ExternalCall_HiCsvi_WritesToSystemLog()
    {
        var registry = ModuleRegistry.CreateDefault();
        var engines = new BasicEngines();
        var drivers = new DriverManager(engines);
        var executor = new CommandExecutor(registry);

        var ctx = new ExecutionContext(0, "ThreadMain", new ProgramTable(), engines, drivers)
        {
            Registry = registry
        };

        var result = executor.ExecuteCommandLine(ctx, "Call@hi.csvi&::");
        Assert.Equal(CommandStatus.Completed, result.Status);

        var channel = engines.Record.GetChannel("SYSTEMLOG");
        Assert.NotNull(channel);
        Assert.True(channel.Entries.Take(channel.Index).Any(e => e.Message.Contains("hi", StringComparison.OrdinalIgnoreCase)),
            "SYSTEMLOG should contain 'hi'");
    }

    [Fact]
    public void HelloAndHi_FullScriptStepExecution_OnDemandLazyLoad_And_IncludeModule_Works()
    {
        // 不預先呼叫 LoadExternalModules()，直接測試真實腳本單步執行 (ExecuteStep) 的隨選載入與 Init.csvi
        var registry = ModuleRegistry.CreateDefault();
        var engines = new BasicEngines();
        var drivers = new DriverManager(engines);
        var executor = new CommandExecutor(registry);

        string script = @"
System@IncludeModule::Hello;
Hello@HelloWorld::;
Call@hi.csvi&::;
";
        var prog = ProgramTable.FromText(script, 0, "Main");
        var ctx = new ExecutionContext(0, "ThreadMain", prog, engines, drivers)
        {
            Registry = registry,
            IsRunning = true
        };

        int safety = 0;
        while (ctx.IsRunning && ctx.ProgIndex <= ctx.Program.Count && safety++ < 20)
        {
            var res = executor.ExecuteStep(ctx);
            Assert.NotEqual(CommandStatus.Error, res.Status);
        }

        // 驗證 Init.csvi 已設定 Hello_Status = INITIALIZED
        Assert.Equal("INITIALIZED", engines.Variable.Get("Hello_Status", "Hello"));

        // 驗證 SYSTEMLOG 同時有 "hello world" 與 "hi"
        var channel = engines.Record.GetChannel("SYSTEMLOG");
        Assert.NotNull(channel);
        var messages = channel.Entries.Take(channel.Index).Select(e => e.Message).ToList();
        Assert.Contains(messages, m => m.Contains("hello world", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(messages, m => string.Equals(m.Trim(), "hi", StringComparison.OrdinalIgnoreCase));
    }
}
