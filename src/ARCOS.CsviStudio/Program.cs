using Avalonia;
using ARCOS.CsviStudio.Mcp;

namespace ARCOS.CsviStudio;

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

        // 2. 檢查是否以 CLI 命令列模式編譯 CSVI (--compile <sourceDir> <outputCsvi> [password])
        if (args.Length >= 3 && args[0].Equals("--compile", StringComparison.OrdinalIgnoreCase))
        {
            string sourceDir = args[1];
            string outputCsvi = args[2];
            string? password = args.Length >= 4 ? args[3] : null;

            if (!Directory.Exists(sourceDir))
            {
                Console.WriteLine($"Error: Source directory '{sourceDir}' not found.");
                return 1;
            }

            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(sourceDir, "*.*", SearchOption.TopDirectoryOnly))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".cs" or ".axaml" or ".xaml")
                {
                    sources[Path.GetFileName(file)] = File.ReadAllText(file);
                }
            }

            var manifest = new ARCOS.Core.Csvi.CsviManifest();
            string mfPath = Path.Combine(sourceDir, "CSVI.Manifest.json");
            if (!File.Exists(mfPath)) mfPath = Path.Combine(sourceDir, "manifest.json");
            if (File.Exists(mfPath))
            {
                manifest = System.Text.Json.JsonSerializer.Deserialize<ARCOS.Core.Csvi.CsviManifest>(File.ReadAllText(mfPath)) ?? manifest;
            }

            var compiler = new ARCOS.CsviStudio.Services.CsviCompilerService();
            string asmName = Path.GetFileNameWithoutExtension(outputCsvi);
            var result = compiler.Compile(asmName, sources, manifest, password);
            if (!result.Success)
            {
                Console.WriteLine($"Compilation failed with {result.Errors.Count} error(s):");
                foreach (var err in result.Errors) Console.WriteLine("  " + err);
                return 1;
            }

            string? outDir = Path.GetDirectoryName(outputCsvi);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }
            File.WriteAllBytes(outputCsvi, result.AssemblyBytes);
            Console.WriteLine($"SUCCESS: Compiled {outputCsvi} ({result.AssemblyBytes.Length} bytes) in {result.ElapsedMilliseconds} ms");
            return 0;
        }

        // 3. 否則啟動 Avalonia 雙欄 GUI 工作站供人類工程師操作
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
