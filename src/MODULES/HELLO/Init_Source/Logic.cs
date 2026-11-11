using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ARCOS.Core.Drivers;
using ARCOS.Core.Execution;
using ARCOS.Core.Guidance;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Module.Hello;

public class HelloInitGuidance : IParameterGuidance, IModuleHandler
{
    public string CommandKey => "Hello@Init";
    public string ModuleName => "Hello";

    public IReadOnlyList<string> GetSupportedCommands() => new[] { "Init" };

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        ctx.Log("[Hello] Initializing Hello module...");
        ctx.Engines.Variable.Set("Hello_Status", "INITIALIZED", "Hello");
        ctx.Engines.Record.Record("SYSTEMLOG", "[Hello] Module initialized.");
        return CommandResult.Done();
    }

    public bool Execute(BasicEngines engines, string parameter)
    {
        engines.Variable.Set("Hello_Status", "INITIALIZED", "Hello");
        engines.Record.Record("SYSTEMLOG", "[Hello] Module initialized.");
        return true;
    }

    public CommandResult Execute(ExecutionContext ctx, string action, string parameter)
        => Execute(action, parameter, ctx);

    public async Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
        => currentParam;

    public object? GetFrontPanelControl() => null;
}
