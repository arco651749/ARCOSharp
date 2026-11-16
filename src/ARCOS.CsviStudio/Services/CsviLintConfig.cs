using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ARCOS.CsviStudio.Services;

public sealed class CsviLintConfig
{
    public int SchemaVersion { get; set; } = 1;
    public LintBehaviorConfig LintBehavior { get; set; } = new();
    public LlmReviewerConfig LlmReviewer { get; set; } = new();
    public List<CsviLintRuleDefinition> Rules { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string? FindConfigFilePath()
    {
        var roots = new List<string>
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        try
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; i < 6 && dir != null; i++)
            {
                roots.Add(dir.FullName);
                dir = dir.Parent;
            }
            var appDir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && appDir != null; i++)
            {
                roots.Add(appDir.FullName);
                appDir = appDir.Parent;
            }
        }
        catch { }

        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(root, "RULES", "CsviLintRules.json");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public static string GetWritableConfigFilePath()
    {
        return FindConfigFilePath()
               ?? Path.Combine(AppContext.BaseDirectory, "RULES", "CsviLintRules.json");
    }

    public static CsviLintConfig Load(string? explicitPath = null)
    {
        string? path = !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath : FindConfigFilePath();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<CsviLintConfig>(File.ReadAllText(path), JsonOpts);
                if (cfg != null)
                {
                    cfg.LintBehavior ??= new();
                    cfg.LlmReviewer ??= new();
                    cfg.Rules ??= new();
                    return cfg;
                }
            }
            catch { }
        }
        return CreateBuiltInFallback();
    }

    public void Save(string? explicitPath = null)
    {
        string path = !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath : GetWritableConfigFilePath();
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static (string EndpointOrCommand, string Model, string ApiKeyHint) GetProviderPreset(string provider)
    {
        return provider?.Trim() switch
        {
            "Ollama" => ("http://localhost:11434/api/chat", "qwen2.5-coder:7b", ""),
            "OpenAiCompatible" => ("https://api.openai.com/v1/chat/completions", "gpt-4o-mini", "$OPENAI_API_KEY"),
            "GeminiHttp" => ("https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent", "gemini-2.5-flash", "$GOOGLE_API_KEY"),
            "ClaudeCli" => ("claude", "", ""),
            "CodexCli" => ("codex", "", ""),
            "AgyCli" => ("agy", "gemini-3.8-flash-low", ""),
            "CustomCli" => ("my-llm-cli --prompt-stdin", "", ""),
            _ => ("", "", "")
        };
    }

    private static CsviLintConfig CreateBuiltInFallback() => new()
    {
        LlmReviewer = new LlmReviewerConfig { Enabled = false, Provider = "Ollama" },
        Rules = new List<CsviLintRuleDefinition>()
    };
}

public sealed class LintBehaviorConfig
{
    public bool WarningOnly { get; set; } = true;
    public bool EmitLocalStaticWarningsOnCompile { get; set; } = true;
}

public sealed class LlmReviewerConfig
{
    public bool Enabled { get; set; } = false;
    public bool RunAfterBuild { get; set; } = true;
    public string Provider { get; set; } = "Ollama";
    public string EndpointOrCommand { get; set; } = "http://localhost:11434/api/chat";
    public string Model { get; set; } = "qwen2.5-coder:7b";
    public string ApiKeyEnvOrLiteral { get; set; } = string.Empty;
    public string CliArgsTemplate { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 45;
    public string PromptTemplate { get; set; } =
        "你是 ARCOSharp 工控系統的架構審查員。請只依據以下規則審查這份 `{driver_name}` (Category={category}) 原始碼。\n" +
        "注意：各廠牌硬體 SDK 函式命名不同，請從實際資料流向與生命週期語意判斷。\n\n" +
        "## 審查規則清單\n{rules_text}\n\n" +
        "## 回覆格式（必須為純 JSON）\n" +
        "{\"summary\":\"一句話總結\",\"results\":[{\"rule_id\":\"IO-01\",\"status\":\"PASS 或 FAIL\",\"evidence_file\":\"Logic.cs\",\"evidence_lines\":\"L10-L30\",\"reasoning\":\"原因\"}]}\n\n" +
        "## 程式碼\n{numbered_source}";

    [JsonIgnore]
    public static IReadOnlyList<string> SupportedProviders { get; } = new[]
    {
        "Disabled",
        "Ollama",
        "OpenAiCompatible",
        "GeminiHttp",
        "ClaudeCli",
        "CodexCli",
        "AgyCli",
        "CustomCli"
    };
}

public sealed class CsviLintRuleDefinition
{
    public string Id { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Category { get; set; } = "*";
    public string RequiresInterface { get; set; } = "*";
    public string Title { get; set; } = string.Empty;
    public string PromptInstruction { get; set; } = string.Empty;
    public LocalStaticCheckConfig? LocalStaticCheck { get; set; }
}

public sealed class LocalStaticCheckConfig
{
    public string Kind { get; set; } = "None";
    public bool FollowPrivateCalls { get; set; } = true;
    public List<string> EntryMethods { get; set; } = new();
    public List<string> RequiredMethodTokens { get; set; } = new();
    public List<string> RequiredArgumentTokens { get; set; } = new();
    public List<string> ForbiddenMethodTokens { get; set; } = new();
    public List<string> ForbiddenArgumentTokens { get; set; } = new();
    public List<string> RequiredTokens { get; set; } = new();
    public List<string> TriggerIfSourceContains { get; set; } = new();
    public string Pattern { get; set; } = string.Empty;
}

public sealed record CsviTargetContext(string DriverName, string Category, bool ImplementsIDriver, bool ImplementsGuidance, bool ImplementsModuleHandler);
