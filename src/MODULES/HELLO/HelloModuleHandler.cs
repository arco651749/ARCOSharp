using System;
using System.Collections.Generic;
using ARCOS.Core.Execution;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Module.Hello;

public class HelloModuleHandler : IModuleHandler
{
    public string ModuleName => "Hello";

    public IReadOnlyList<string> GetSupportedCommands() =>
        ModuleRegistry.GetCsviCommandsForModule("Hello") ?? new[] { "HelloWorld", "Init" };

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        if (action.Equals("Init", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log("[Hello] Initializing Hello module...");
            ctx.Engines.Variable.Set("Hello_Status", "INITIALIZED", "Hello");
            ctx.Engines.Record.Record("SYSTEMLOG", "[Hello] Module initialized.");

            string? initCsvi = ModuleRegistry.ResolveCommandCsviPath("Hello", "Init");
            if (initCsvi != null)
            {
                var h = (ctx.Registry ?? new ModuleRegistry()).LoadCommandCsviHandler(initCsvi);
                if (h != null) return h.Execute(action, filteredParam, ctx);
            }
            return CommandResult.Done();
        }

        string? csviPath = ModuleRegistry.ResolveCommandCsviPath("Hello", action);
        if (csviPath == null)
        {
            return CommandResult.Fail($"Command CSVI file not found: MODULES/Hello/{action}.csvi");
        }

        var handler = (ctx.Registry ?? new ModuleRegistry()).LoadCommandCsviHandler(csviPath);
        if (handler == null)
        {
            return CommandResult.Fail($"Failed to load command CSVI handler from: {csviPath}");
        }

        return handler.Execute(action, filteredParam, ctx);
    }
}
