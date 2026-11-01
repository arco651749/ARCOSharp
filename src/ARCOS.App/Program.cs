using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Avalonia;
using ARCOS.App.Mcp;

namespace ARCOS.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 註冊全域組件解析器，確保 MODULES/ 與 DRIVER/ 載入的外掛可直接共用主目錄下的核心 DLL
        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            string candidatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{assemblyName.Name}.dll");
            if (File.Exists(candidatePath))
            {
                return context.LoadFromAssemblyPath(candidatePath);
            }
            return null;
        };

        bool isMcpMode = args.Any(a => a.Equals("--mcp", StringComparison.OrdinalIgnoreCase));
        if (isMcpMode)
        {
            // 若桌面上已有執行中的 ARCOS.App 實例，直接將 Stdio MCP 請求轉發至該實例
            if (ArcosMcpServer.TryForwardStdioToRunningInstance())
            {
                return;
            }

            // 否則在本進程啟動完整 Avalonia UI，並隔離標準輸出供 MCP JSON-RPC 專用
            var rawStdin = Console.OpenStandardInput();
            var rawStdout = Console.OpenStandardOutput();
            Console.SetOut(Console.Error);
            ArcosMcpServer.StartInProcessStdioMcpLoop(rawStdin, rawStdout);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
