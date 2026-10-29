namespace ARCOS.Core.Execution.Handlers;

/// <summary>
/// User 模組直譯處理器 (User@)
/// </summary>
public sealed class UserModuleHandler : IModuleHandler
{
    public string ModuleName => "User";

    private static readonly string[] Commands = new[]
    {
        "ButtonValue",
        "ButtonTrigger",
        "ButtonBlinking",
        "ButtonText",
        "TextBoxValue",
        "TabValue",
        "TabTrigger",
        "Param2UserPanel"
    };

    public IReadOnlyList<string> GetSupportedCommands() => Commands;

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        // User 模組主要用於向 UI 前面板同步狀態，在 Headless 核心中記錄事件
        ctx.Log($"[User HMI] {action}::{filteredParam}");
        return CommandResult.Done();
    }
}
