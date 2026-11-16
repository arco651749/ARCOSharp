using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ARCOS.Core.Csvi;
using Microsoft.CodeAnalysis.CSharp;

namespace ARCOS.CsviStudio.Services;

public sealed class CsviLlmReviewResult
{
    public bool Executed { get; set; }
    public bool Succeeded { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
    public List<string> PassedRuleIds { get; set; } = new();
    public string InfoMessage { get; set; } = string.Empty;
    public string RenderedPrompt { get; set; } = string.Empty;
}

public sealed class CsviReviewSnapshot
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;
    public string CommandKey { get; set; } = string.Empty;
    public List<string> StaticWarnings { get; set; } = new();
    public bool AiPending { get; set; }
    public bool AiExecuted { get; set; }
    public bool AiSucceeded { get; set; }
    public string AiProvider { get; set; } = string.Empty;
    public string AiSummary { get; set; } = string.Empty;
    public string AiInfoMessage { get; set; } = string.Empty;
    public List<string> AiWarnings { get; set; } = new();
    public List<string> AiPassedRuleIds { get; set; } = new();
}

/// <summary>
/// 跨介面/CLI/MCP 共用的最近一次 Lint 與 LLM 審查快照儲存區。
/// 同步寫入記憶體與 %LOCALAPPDATA%\ARCOS\csvi_last_review.json，讓外部 Stdio MCP Client 也能讀取 UI 剛跑完的 AI 審查訊息。
/// </summary>
public static class CsviReviewHistoryStore
{
    private static readonly object SyncLock = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static CsviReviewSnapshot? _latestInMemory;

    public static string SnapshotFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ARCOS",
        "csvi_last_review.json");

    public static CsviReviewSnapshot CreateSnapshot(
        string source,
        string targetName,
        string commandKey,
        IEnumerable<string>? staticWarnings,
        CsviLlmReviewResult? aiResult = null,
        bool aiPending = false)
    {
        return new CsviReviewSnapshot
        {
            TimestampUtc = DateTime.UtcNow,
            Source = source,
            TargetName = targetName,
            CommandKey = commandKey,
            StaticWarnings = staticWarnings?.ToList() ?? new List<string>(),
            AiPending = aiPending,
            AiExecuted = aiResult?.Executed ?? false,
            AiSucceeded = aiResult?.Succeeded ?? false,
            AiProvider = aiResult?.Provider ?? string.Empty,
            AiSummary = aiResult?.Summary ?? string.Empty,
            AiInfoMessage = aiResult?.InfoMessage ?? string.Empty,
            AiWarnings = aiResult?.Warnings.ToList() ?? new List<string>(),
            AiPassedRuleIds = aiResult?.PassedRuleIds.ToList() ?? new List<string>()
        };
    }

    public static void Record(CsviReviewSnapshot snapshot)
    {
        lock (SyncLock)
        {
            _latestInMemory = snapshot;
            try
            {
                string path = SnapshotFilePath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOpts), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    public static CsviReviewSnapshot? GetLatest()
    {
        lock (SyncLock)
        {
            CsviReviewSnapshot? diskSnapshot = null;
            try
            {
                if (File.Exists(SnapshotFilePath))
                    diskSnapshot = JsonSerializer.Deserialize<CsviReviewSnapshot>(File.ReadAllText(SnapshotFilePath, Encoding.UTF8), JsonOpts);
            }
            catch { }

            if (_latestInMemory == null) return diskSnapshot;
            if (diskSnapshot == null) return _latestInMemory;
            return diskSnapshot.TimestampUtc > _latestInMemory.TimestampUtc ? diskSnapshot : _latestInMemory;
        }
    }
}

/// <summary>
/// 統一的 CSVI Studio LLM 語意審查端點適配器：
/// 支援 Ollama (HTTP)、OpenAI-Compatible HTTP、Gemini HTTP、Claude CLI、Codex CLI、AGY CLI 與 Custom CLI。
/// 所有建議皆只轉化為 Warning，若外部端點離線、逾時或未設定金鑰亦僅記錄提示訊息，不阻斷 .csvi 產出。
/// </summary>
public static class CsviLlmReviewClient
{
    private static readonly HttpClient SharedHttp = new();

    public static string BuildPrompt(
        CsviLintConfig config,
        CsviTargetContext ctx,
        IReadOnlyList<CsviLintRuleDefinition> rules,
        Dictionary<string, string> sources)
    {
        var sbCode = new StringBuilder();
        foreach (var kv in sources.Where(k => k.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k.Key))
        {
            var lines = (kv.Value ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            sbCode.AppendLine($"=== FILE: {kv.Key} ({lines.Length} lines) ===");
            for (int i = 0; i < lines.Length; i++)
            {
                sbCode.AppendLine($"L{i + 1:D4}: {lines[i]}");
            }
            sbCode.AppendLine();
        }

        var sbRules = new StringBuilder();
        foreach (var r in rules)
        {
            sbRules.AppendLine($"- {r.Id} ({r.Title}): {r.PromptInstruction}");
        }

        string template = string.IsNullOrWhiteSpace(config.LlmReviewer.PromptTemplate)
            ? new LlmReviewerConfig().PromptTemplate
            : config.LlmReviewer.PromptTemplate;

        return template
            .Replace("{driver_name}", ctx.DriverName)
            .Replace("{category}", ctx.Category)
            .Replace("{rules_text}", sbRules.ToString().TrimEnd())
            .Replace("{numbered_source}", sbCode.ToString().TrimEnd());
    }

    public static async Task<CsviLlmReviewResult> ReviewAsync(
        CSharpCompilation compilation,
        CsviManifest manifest,
        Dictionary<string, string> sources,
        CsviLintConfig config,
        CancellationToken ct = default)
    {
        var rev = config.LlmReviewer;
        var res = new CsviLlmReviewResult { Provider = rev.Provider };

        if (!rev.Enabled || string.Equals(rev.Provider, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            res.InfoMessage = "AI 語意審查目前為關閉狀態。";
            return res;
        }

        var ctx = CsviDynamicRuleEngine.DetectContext(compilation, manifest, sources);
        var rules = CsviDynamicRuleEngine.FilterApplicableRules(config, ctx);
        if (rules.Count == 0)
        {
            res.InfoMessage = $"類別 '{ctx.Category}' 無啟用中之規則需要審查。";
            return res;
        }

        string prompt = BuildPrompt(config, ctx, rules, sources);
        res.RenderedPrompt = prompt;
        res.Executed = true;

        int timeoutSec = rev.TimeoutSeconds > 0 ? rev.TimeoutSeconds : 45;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        try
        {
            string rawResponse = rev.Provider?.Trim() switch
            {
                "Ollama" => await CallOllamaAsync(rev, prompt, timeoutCts.Token),
                "OpenAiCompatible" => await CallOpenAiCompatibleAsync(rev, prompt, timeoutCts.Token),
                "GeminiHttp" => await CallGeminiHttpAsync(rev, prompt, timeoutCts.Token),
                "ClaudeCli" => await CallCliAsync(ResolveCommand(rev.EndpointOrCommand, "claude"), "-p", prompt, rev.CliArgsTemplate, timeoutCts.Token),
                "CodexCli" => await CallCliAsync(ResolveCommand(rev.EndpointOrCommand, "codex"), "exec -", prompt, rev.CliArgsTemplate, timeoutCts.Token),
                "AgyCli" => await CallAgyCliAsync(rev, prompt, timeoutCts.Token),
                "CustomCli" => await CallCliAsync(rev.EndpointOrCommand, "", prompt, rev.CliArgsTemplate, timeoutCts.Token),
                _ => throw new NotSupportedException($"Unsupported LLM provider '{rev.Provider}'.")
            };

            ParseResponseIntoWarnings(rawResponse, rules, sources, res);
            res.Succeeded = true;
            return res;
        }
        catch (OperationCanceledException)
        {
            res.Succeeded = false;
            res.InfoMessage = $"AI 審查 ({rev.Provider}) 超過 {timeoutSec} 秒逾時，已安全略過（不影響編譯）。";
            return res;
        }
        catch (Exception ex)
        {
            res.Succeeded = false;
            res.InfoMessage = $"AI 審查 ({rev.Provider}) 無法連線或失敗：{ex.Message}（已安全略過，不影響編譯）。";
            return res;
        }
    }

    private static string ResolveCommand(string configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

    private static async Task<string> CallOllamaAsync(LlmReviewerConfig cfg, string prompt, CancellationToken ct)
    {
        string url = string.IsNullOrWhiteSpace(cfg.EndpointOrCommand) ? "http://localhost:11434/api/chat" : cfg.EndpointOrCommand.Trim();
        if (!url.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase) && !url.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase))
        {
            url = url.TrimEnd('/') + "/api/chat";
        }

        var body = new
        {
            model = string.IsNullOrWhiteSpace(cfg.Model) ? "qwen2.5-coder:7b" : cfg.Model.Trim(),
            messages = new[] { new { role = "user", content = prompt } },
            stream = false,
            format = "json",
            options = new { temperature = 0 }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var resp = await SharedHttp.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {text}");

        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
            return content.GetString() ?? string.Empty;
        if (doc.RootElement.TryGetProperty("response", out var r))
            return r.GetString() ?? string.Empty;
        return text;
    }

    private static async Task<string> CallOpenAiCompatibleAsync(LlmReviewerConfig cfg, string prompt, CancellationToken ct)
    {
        string url = string.IsNullOrWhiteSpace(cfg.EndpointOrCommand)
            ? "https://api.openai.com/v1/chat/completions"
            : cfg.EndpointOrCommand.Trim();
        string apiKey = ResolveApiKey(cfg.ApiKeyEnvOrLiteral);

        var body = new
        {
            model = string.IsNullOrWhiteSpace(cfg.Model) ? "gpt-4o-mini" : cfg.Model.Trim(),
            temperature = 0,
            messages = new[] { new { role = "user", content = prompt } }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await SharedHttp.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {text}");

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    private static async Task<string> CallGeminiHttpAsync(LlmReviewerConfig cfg, string prompt, CancellationToken ct)
    {
        string model = string.IsNullOrWhiteSpace(cfg.Model) ? "gemini-2.5-flash" : cfg.Model.Trim();
        string url = string.IsNullOrWhiteSpace(cfg.EndpointOrCommand)
            ? $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent"
            : cfg.EndpointOrCommand.Trim().Replace("{model}", model);

        string apiKey = ResolveApiKey(cfg.ApiKeyEnvOrLiteral);
        var body = new
        {
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0, responseMimeType = "application/json" }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(apiKey))
            req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);

        using var resp = await SharedHttp.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {text}");

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? string.Empty;
    }

    private static async Task<string> CallAgyCliAsync(LlmReviewerConfig cfg, string prompt, CancellationToken ct)
    {
        string cmd = ResolveAgyExecutable(cfg.EndpointOrCommand);
        if (!string.IsNullOrWhiteSpace(cfg.CliArgsTemplate))
            return await CallCliAsync(cmd, string.Empty, prompt, cfg.CliArgsTemplate, ct);

        string? tempPromptFile = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cmd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.ArgumentList.Add("--disable-slash-commands");
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("json");
            if (!string.IsNullOrWhiteSpace(cfg.Model))
            {
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(cfg.Model.Trim());
            }

            // Windows CreateProcess command-line limit is ~32,767 chars; keep direct -p under 24k chars,
            // and fall back to a temporary prompt file for oversized driver bundles.
            if (prompt.Length <= 24000)
            {
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(prompt);
            }
            else
            {
                tempPromptFile = Path.Combine(Path.GetTempPath(), $"csvi_lint_prompt_{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(tempPromptFile, prompt, new UTF8Encoding(false), ct);
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add($"Read and analyze the code review prompt in \"{tempPromptFile}\" and reply ONLY with the requested raw JSON.");
            }

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start AGY CLI '{cmd}'.");
            string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            string stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
                throw new InvalidOperationException($"AGY CLI exited with {proc.ExitCode}: {stderr.Trim()}");

            return UnwrapAgyJsonResponse(stdout, stderr);
        }
        finally
        {
            if (tempPromptFile != null && File.Exists(tempPromptFile))
            {
                try { File.Delete(tempPromptFile); } catch { }
            }
        }
    }

    private static string ResolveAgyExecutable(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && !string.Equals(configured.Trim(), "agy", StringComparison.OrdinalIgnoreCase))
            return configured.Trim();

        if (OperatingSystem.IsWindows())
        {
            string localAgy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "agy", "bin", "agy.exe");
            if (File.Exists(localAgy))
                return localAgy;
        }
        return ResolveCommand(configured, "agy");
    }

    public static string UnwrapAgyJsonResponse(string stdout, string stderr)
    {
        string raw = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        string trimmed = raw.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.TryGetProperty("status", out var stEl) &&
                    string.Equals(stEl.GetString(), "ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    string err = root.TryGetProperty("error", out var errEl) ? errEl.GetString() ?? trimmed : trimmed;
                    throw new InvalidOperationException(err);
                }
                if (root.TryGetProperty("response", out var respEl))
                    return respEl.GetString() ?? trimmed;
            }
            catch (JsonException) { }
        }
        return raw;
    }

    private static async Task<string> CallCliAsync(string command, string defaultArgs, string prompt, string overrideArgsTemplate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("CLI command is empty.");

        string args = string.IsNullOrWhiteSpace(overrideArgsTemplate) ? defaultArgs : overrideArgsTemplate.Trim();
        bool useTempFile = args.Contains("{prompt_file}", StringComparison.OrdinalIgnoreCase);
        string? tempPromptFile = null;

        try
        {
            if (useTempFile)
            {
                tempPromptFile = Path.Combine(Path.GetTempPath(), $"csvi_lint_prompt_{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(tempPromptFile, prompt, Encoding.UTF8, ct);
                args = args.Replace("{prompt_file}", $"\"{tempPromptFile}\"");
            }

            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardInput = !useTempFile,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = string.IsNullOrWhiteSpace(args) ? $"/c {command}" : $"/c {command} {args}";
            }
            else
            {
                psi.FileName = command;
                psi.Arguments = args;
            }

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start CLI '{command}'.");
            if (!useTempFile)
            {
                await proc.StandardInput.WriteAsync(prompt);
                proc.StandardInput.Close();
            }

            string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            string stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
                throw new InvalidOperationException($"CLI exited with {proc.ExitCode}: {stderr.Trim()}");

            return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        }
        finally
        {
            if (tempPromptFile != null && File.Exists(tempPromptFile))
            {
                try { File.Delete(tempPromptFile); } catch { }
            }
        }
    }

    public static string ResolveApiKey(string? envOrLiteral)
    {
        if (string.IsNullOrWhiteSpace(envOrLiteral)) return string.Empty;
        string trimmed = envOrLiteral.Trim();
        if (trimmed.StartsWith('$'))
        {
            string envName = trimmed[1..].Trim();
            string? val = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
            return string.Empty;
        }
        return trimmed;
    }

    internal static void ParseResponseIntoWarnings(
        string rawResponse,
        IReadOnlyList<CsviLintRuleDefinition> rules,
        Dictionary<string, string> sources,
        CsviLlmReviewResult result)
    {
        if (string.IsNullOrWhiteSpace(rawResponse)) return;

        string jsonSlice = ExtractJsonObject(rawResponse);
        if (string.IsNullOrWhiteSpace(jsonSlice))
        {
            result.Warnings.Add($"[AI-LINT] 模型未回傳標準 JSON，原文摘要: {rawResponse.Trim().Replace("\n", " ").Substring(0, Math.Min(180, rawResponse.Trim().Length))}");
            return;
        }

        using var doc = JsonDocument.Parse(jsonSlice);
        if (doc.RootElement.TryGetProperty("summary", out var sumEl))
            result.Summary = sumEl.GetString() ?? string.Empty;

        if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;

        var ruleMap = rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var item in arr.EnumerateArray())
        {
            string rid = item.TryGetProperty("rule_id", out var rEl) ? rEl.GetString() ?? "" : "";
            string status = item.TryGetProperty("status", out var sEl) ? sEl.GetString() ?? "" : "";
            string ef = item.TryGetProperty("evidence_file", out var fEl) ? fEl.GetString() ?? "" : "";
            string el = item.TryGetProperty("evidence_lines", out var lEl) ? lEl.GetString() ?? "" : "";
            string reason = item.TryGetProperty("reasoning", out var rsEl) ? rsEl.GetString() ?? "" : "";

            if (status.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
            {
                string title = ruleMap.TryGetValue(rid, out var def) ? def.Title : rid;
                string loc = string.IsNullOrWhiteSpace(ef) ? "" : $" ({ef}{(string.IsNullOrWhiteSpace(el) ? "" : ":" + el)})";
                result.Warnings.Add($"[AI-LINT-WARN][{rid}] {title}{loc} — {reason}");
            }
            else if (status.Equals("PASS", StringComparison.OrdinalIgnoreCase))
            {
                // Verify cited file actually exists when evidence_file is provided.
                if (!string.IsNullOrWhiteSpace(ef) && !sources.ContainsKey(ef))
                {
                    result.Warnings.Add($"[AI-LINT-WARN][{rid}] AI 判定 PASS 但引用不存在檔案 '{ef}'，請人工複核。");
                }
                else if (!string.IsNullOrWhiteSpace(rid))
                {
                    result.PassedRuleIds.Add(rid);
                }
            }
        }
    }

    private static string ExtractJsonObject(string raw)
    {
        string trimmed = raw.Trim();
        var fenceMatch = Regex.Match(trimmed, @"```(?:json)?\s*(\{[\s\S]*\})\s*```", RegexOptions.IgnoreCase);
        if (fenceMatch.Success) return fenceMatch.Groups[1].Value;

        int first = trimmed.IndexOf('{');
        int last = trimmed.LastIndexOf('}');
        if (first >= 0 && last > first) return trimmed[first..(last + 1)];
        return string.Empty;
    }
}
