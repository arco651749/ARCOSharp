using Avalonia;
using MVCON.CsviStudio.Mcp;

namespace MVCON.CsviStudio;

internal class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        // 1. 檢查是否以 MCP Server 雙模啟動 (--mcp 旗標供 AI Agent 連線)
        if (args.Contains("--mcp", StringComparer.OrdinalIgnoreCase))
        {
            var mcpServer = new McpServer();
            await mcpServer.RunAsync();
            return 0;
        }

        // 2. 否則啟動 Avalonia 雙欄 GUI 工作站供人類工程師操作
        string? openFilePath = args.Select(a => a.Trim('"')).FirstOrDefault(a => !a.StartsWith("-") && a.EndsWith(".csvi", StringComparison.OrdinalIgnoreCase));
        BuildAvaloniaApp(openFilePath)
            .StartWithClassicDesktopLifetime(args);

        return 0;
    }

    public static AppBuilder BuildAvaloniaApp(string? openFilePath = null)
        => AppBuilder.Configure(() => new App(openFilePath))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
