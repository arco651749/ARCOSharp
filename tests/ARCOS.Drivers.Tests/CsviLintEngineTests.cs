using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ARCOS.Core.Csvi;
using ARCOS.CsviStudio.Mcp;
using ARCOS.CsviStudio.Services;
using Xunit;

namespace ARCOS.Drivers.Tests;

public class CsviLintEngineTests
{
    [Fact]
    public void Compile_NonCompliantIoDriver_EmitsRuleWarningsWithoutBlockingBuild()
    {
        string badDriverCode = """
using System.Threading;
using System.Threading.Tasks;
using ARCOS.Core.Drivers;

namespace ARCOS.Driver.BadTestIo;

public class BadTestIoDriver : IDriver
{
    private BasicEngines? _engines;
    private CancellationTokenSource? _cts;
    public string DeviceName { get; private set; } = string.Empty;
    public string Category => "IO";
    public string DriverType => "BadTestIo";
    public string OutStart => "P0.0";
    public string InStart => "P1.0";

    public void Init(string deviceName, string param, BasicEngines engines)
    {
        DeviceName = deviceName;
        _engines = engines;
        _cts = new CancellationTokenSource();
        Task.Run(() => WorkerLoop(_cts.Token));
    }

    private async Task WorkerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            _engines?.Io.SetBatch(OutStart, new bool[8]);
            await Task.Delay(25, token);
        }
    }

    public void PostTicket(DriverTicket ticket) => ticket.SetCompleted();
    public object? CreateDriverView() => null;
    public void Shutdown() { }
    public void Dispose() { }
}
""";

        var compiler = new CsviCompilerService();
        var sources = new Dictionary<string, string> { ["Logic.cs"] = badDriverCode };
        var manifest = new CsviManifest { CommandKey = "IO@AddIODriver::BadTestIo" };

        var res = compiler.Compile("BadTestIo", sources, manifest);

        // Must NEVER block compilation — outputs valid .csvi bytes while raising clear rule Warnings.
        Assert.True(res.Success);
        Assert.NotEmpty(res.AssemblyBytes);
        Assert.Contains(res.Warnings, w => w.Contains("[IO-01]"));
        Assert.Contains(res.Warnings, w => w.Contains("[IO-02]"));
        Assert.Contains(res.Warnings, w => w.Contains("[IO-03]"));
        Assert.Contains(res.Warnings, w => w.Contains("[IO-05]"));
        Assert.Contains(res.Warnings, w => w.Contains("[DRV-01]"));
    }

    [Fact]
    public void Compile_CompliantMockIoDriver_ProducesZeroDynamicRuleWarnings()
    {
        string mockIoPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MODULES", "IO", "DRIVER", "MockIO", "MockIoDriver.cs"));
        if (!File.Exists(mockIoPath)) return;

        var compiler = new CsviCompilerService();
        var sources = new Dictionary<string, string>
        {
            ["GlobalUsings.g.cs"] = """
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
""",
            ["Logic.cs"] = File.ReadAllText(mockIoPath),
            ["MockUiStub.cs"] = """
using Avalonia.Controls;
using ARCOS.Core.Drivers;

namespace ARCOS.Driver.MockIO;

public class MockIoView : UserControl
{
    public MockIoView(MockIoDriver driver) { }
}

public class MockIoGuidanceDialog : Window
{
    public MockIoGuidanceDialog(MockIoDriver driver, string currentParam, BasicEngines engines) { }
}
"""
        };
        var manifest = new CsviManifest { CommandKey = "IO@AddIODriver::MockIO" };

        var res = compiler.Compile("MockIOCheck", sources, manifest);
        Assert.True(res.Success, string.Join("\n", res.Errors));
        Assert.DoesNotContain(res.Warnings, w => w.StartsWith("[LINT-WARN]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LlmReviewClient_OllamaAndOpenAiEndpoints_ParseWarningsFromCustomRulesConfig()
    {
        // Spin up tiny local loopback HTTP server simulating Ollama /api/chat JSON response
        int port = 19842;
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        string? capturedRequestBody = null;
        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            capturedRequestBody = await sr.ReadToEndAsync();

            string ollamaEnvelope = """
            {
              "message": {
                "role": "assistant",
                "content": "{\"summary\":\"發現 1 項潛在問題\",\"results\":[{\"rule_id\":\"IO-01\",\"status\":\"FAIL\",\"evidence_file\":\"Logic.cs\",\"evidence_lines\":\"L12-L18\",\"reasoning\":\"Init 未讀取硬體 DO 初值\"}]}"
              }
            }
            """;
            byte[] buf = Encoding.UTF8.GetBytes(ollamaEnvelope);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = buf.Length;
            await ctx.Response.OutputStream.WriteAsync(buf);
            ctx.Response.Close();
        });

        var cfg = new CsviLintConfig
        {
            LlmReviewer = new LlmReviewerConfig
            {
                Enabled = true,
                Provider = "Ollama",
                EndpointOrCommand = $"http://127.0.0.1:{port}/api/chat",
                Model = "qwen2.5-coder:7b",
                TimeoutSeconds = 10
            },
            Rules = new List<CsviLintRuleDefinition>
            {
                new()
                {
                    Id = "IO-01",
                    Enabled = true,
                    Category = "IO",
                    RequiresInterface = "IDriver",
                    Title = "Init 必須先讀 DO 初值",
                    PromptInstruction = "檢查 Init 有無讀取 DO 初值"
                }
            }
        };

        var compiler = new CsviCompilerService();
        var sources = new Dictionary<string, string>
        {
            ["Logic.cs"] = "using ARCOS.Core.Drivers; public class DemoIo : IDriver { public string DeviceName => \"D\"; public string Category => \"IO\"; public string DriverType => \"Demo\"; public void Init(string d, string p, BasicEngines e){} public void Shutdown(){} public void PostTicket(DriverTicket t){} public object? CreateDriverView() => null; public void Dispose(){} }"
        };

        var review = await compiler.ReviewWithConfiguredLlmAsync("DemoIo", sources, new CsviManifest { CommandKey = "IO@AddIODriver" }, cfg);
        await serverTask;

        Assert.True(review.Executed);
        Assert.True(review.Succeeded);
        Assert.NotNull(capturedRequestBody);
        Assert.Contains("IO-01", capturedRequestBody);
        Assert.Single(review.Warnings);
        Assert.Contains("[AI-LINT-WARN][IO-01]", review.Warnings[0]);
        Assert.Contains("Init 未讀取硬體 DO 初值", review.Warnings[0]);
    }

    [Fact]
    public void LlmReviewClient_AgyCliEnvelope_UnwrapsResponseAndPropagatesErrors()
    {
        string okEnvelope = """
        {"conversation_id":"abc","status":"SUCCESS","response":"{\"summary\":\"OK\",\"results\":[]}\n"}
        """;
        string unwrapped = CsviLlmReviewClient.UnwrapAgyJsonResponse(okEnvelope, string.Empty);
        Assert.Contains("\"summary\":\"OK\"", unwrapped);

        string errEnvelope = """
        {"conversation_id":"abc","status":"ERROR","response":"","error":"model quota exceeded"}
        """;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CsviLlmReviewClient.UnwrapAgyJsonResponse(errEnvelope, string.Empty));
        Assert.Contains("model quota exceeded", ex.Message);
    }

    [Fact]
    public async Task McpServer_ToolsListCompileAndLastReview_ExposesIo04AndAiMessages()
    {
        var cfg = CsviLintConfig.Load();
        Assert.Contains(cfg.Rules, r => r.Id == "IO-04" && r.Enabled);

        var mcp = new McpServer();
        var listResp = await mcp.HandleRequestDirectAsync("tools/list", new JsonObject(), JsonValue.Create(1));
        string listJson = listResp!.ToJsonString();
        Assert.Contains("csvi_ai_review", listJson);
        Assert.Contains("csvi_get_last_review", listJson);

        string tempCsvi = Path.Combine(Path.GetTempPath(), $"mcp_test_{Guid.NewGuid():N}.csvi");
        try
        {
            var compileReq = new JsonObject
            {
                ["name"] = "csvi_compile_and_pack",
                ["arguments"] = new JsonObject
                {
                    ["outputPath"] = tempCsvi,
                    ["commandKey"] = "IO@AddIODriver::McpDemo",
                    ["sourceFiles"] = new JsonObject
                    {
                        ["Logic.cs"] = "using ARCOS.Core.Drivers; public class McpDemoIo : IDriver { public string DeviceName => \"D\"; public string Category => \"IO\"; public string DriverType => \"McpDemo\"; public void Init(string d, string p, BasicEngines e){} public void Shutdown(){} public void PostTicket(DriverTicket t){} public object? CreateDriverView() => null; public void Dispose(){} }"
                    }
                }
            };

            var compResp = await mcp.HandleRequestDirectAsync("tools/call", compileReq, JsonValue.Create(2));
            string compText = compResp!["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("[LINT-WARN][IO-01]", compText);

            var aiRes = new CsviLlmReviewResult
            {
                Executed = true,
                Succeeded = true,
                Provider = "AgyCli",
                Summary = "偵測到 1 項 PostTicket 同步風險",
                Warnings = { "[AI-LINT-WARN][IO-04] PostTicket 未非阻塞入隊 (Logic.cs:L1)" }
            };
            CsviReviewHistoryStore.Record(CsviReviewHistoryStore.CreateSnapshot(
                "MCP-Test", "McpDemo", "IO@AddIODriver::McpDemo",
                new[] { "[LINT-WARN][IO-01] Missing DO readback" }, aiRes));

            var lastResp = await mcp.HandleRequestDirectAsync(
                "tools/call",
                new JsonObject { ["name"] = "csvi_get_last_review", ["arguments"] = new JsonObject() },
                JsonValue.Create(3));
            string lastText = lastResp!["result"]!["content"]![0]!["text"]!.GetValue<string>();
            var parsedLast = JsonNode.Parse(lastText)!;
            Assert.True(parsedLast["hasSnapshot"]!.GetValue<bool>());
            Assert.Contains("PostTicket 同步風險", parsedLast["snapshot"]!["AiSummary"]!.GetValue<string>());
            Assert.Contains("[AI-LINT-WARN][IO-04]", parsedLast["snapshot"]!["AiWarnings"]![0]!.GetValue<string>());
        }
        finally
        {
            if (File.Exists(tempCsvi)) File.Delete(tempCsvi);
        }
    }
}
