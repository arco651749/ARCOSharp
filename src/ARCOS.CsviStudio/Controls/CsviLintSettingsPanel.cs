using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ARCOS.Core.Csvi;
using ARCOS.CsviStudio.Services;

namespace ARCOS.CsviStudio.Controls;

/// <summary>
/// 共用 AI &amp; 動態 Lint 規則設定面板（同時內嵌於 ARCOS.App CSVI Studio 分頁與獨立 CSVI Studio 視窗）。
/// 支援使用者設定 Ollama / OpenAI-Compatible / Gemini / Claude CLI / Codex CLI / AGY CLI 端點並僅輸出 Warning。
/// </summary>
public sealed class CsviLintSettingsPanel : UserControl
{
    private readonly CheckBox _chkEnabled;
    private readonly CheckBox _chkAfterBuild;
    private readonly ComboBox _cmbProvider;
    private readonly TextBox _txtEndpoint;
    private readonly TextBox _txtModel;
    private readonly TextBox _txtApiKey;
    private readonly TextBox _txtCliArgs;
    private readonly TextBox _txtTimeout;
    private readonly TextBlock _txtRulesSummary;
    private readonly TextBox _txtRulesPreview;
    private bool _suppressPresetFill;

    public event Action? RunManualReviewRequested;
    public event Action<string>? LogRequested;

    public CsviLintSettingsPanel()
    {
        _chkEnabled = new CheckBox { Content = "啟用 LLM 語意審查 (僅顯示 Warning，不擋編譯)", FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#1E293B") };
        _chkAfterBuild = new CheckBox { Content = "編譯 .csvi 後於背景自動審查", FontSize = 11.5, Foreground = Brush.Parse("#334155") };

        _cmbProvider = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11.5, ItemsSource = LlmReviewerConfig.SupportedProviders };
        _txtEndpoint = new TextBox { FontSize = 11.5, FontFamily = new FontFamily("Consolas"), Watermark = "HTTP URL 或 CLI 命令（如 claude / codex / agy）" };
        _txtModel = new TextBox { FontSize = 11.5, FontFamily = new FontFamily("Consolas"), Watermark = "qwen2.5-coder:7b / gemini-2.5-flash / 留空用預設" };
        _txtApiKey = new TextBox { FontSize = 11.5, FontFamily = new FontFamily("Consolas"), Watermark = "選填：$GOOGLE_API_KEY / $OPENAI_API_KEY 或留白" };
        _txtCliArgs = new TextBox { FontSize = 11.5, FontFamily = new FontFamily("Consolas"), Watermark = "選填 CLI 額外參數，如 -p 或 {prompt_file}" };
        _txtTimeout = new TextBox { FontSize = 11.5, FontFamily = new FontFamily("Consolas"), Width = 70 };

        _txtRulesSummary = new TextBlock { FontSize = 11, Foreground = Brush.Parse("#0F766E"), TextWrapping = TextWrapping.Wrap };
        _txtRulesPreview = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            FontSize = 11,
            Height = 125,
            Background = Brush.Parse("#F8FAFC"),
            Foreground = Brush.Parse("#334155")
        };

        var btnSave = new Button { Content = "💾 儲存設定", Background = Brush.Parse("#0284C7"), Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, FontSize = 11.5, Padding = new Thickness(10, 5) };
        var btnReload = new Button { Content = "🔄 重載規則檔", Background = Brush.Parse("#E2E8F0"), Foreground = Brush.Parse("#1E293B"), FontSize = 11.5, Padding = new Thickness(10, 5) };
        var btnReviewNow = new Button { Content = "🔍 立即 AI 審查目前程式碼", Background = Brush.Parse("#059669"), Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, FontSize = 11.5, Padding = new Thickness(10, 5) };

        _cmbProvider.SelectionChanged += (_, _) =>
        {
            if (_suppressPresetFill || _cmbProvider.SelectedItem is not string prov) return;
            var (ep, model, keyHint) = CsviLintConfig.GetProviderPreset(prov);
            _txtEndpoint.Text = ep;
            _txtModel.Text = model;
            if (string.IsNullOrWhiteSpace(_txtApiKey.Text)) _txtApiKey.Text = keyHint;
        };

        btnSave.Click += (_, _) => SaveFromUi();
        btnReload.Click += (_, _) => LoadToUi(logNotice: true);
        btnReviewNow.Click += (_, _) =>
        {
            SaveFromUi(silent: true);
            RunManualReviewRequested?.Invoke();
        };

        var root = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 8,
                Children =
                {
                    _chkEnabled,
                    _chkAfterBuild,
                    MakeRow("LLM Provider:", _cmbProvider),
                    MakeRow("Endpoint / CLI:", _txtEndpoint),
                    MakeRow("Model:", _txtModel),
                    MakeRow("API Key / $Env:", _txtApiKey),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock { Text = "Timeout (s):", FontSize = 11.5, Foreground = Brush.Parse("#475569"), VerticalAlignment = VerticalAlignment.Center },
                            _txtTimeout,
                            new TextBlock { Text = "CLI Args:", FontSize = 11.5, Foreground = Brush.Parse("#475569"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) },
                            _txtCliArgs
                        }
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Margin = new Thickness(0, 4, 0, 0),
                        Children = { btnSave, btnReload, btnReviewNow }
                    },
                    _txtRulesSummary,
                    _txtRulesPreview
                }
            }
        };

        _txtCliArgs.Width = 175;
        Content = root;
        LoadToUi(logNotice: false);
    }

    private static Grid MakeRow(string label, Control input)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions { new(105, GridUnitType.Pixel), new(1, GridUnitType.Star) } };
        var lbl = new TextBlock { Text = label, FontSize = 11.5, Foreground = Brush.Parse("#475569"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(input, 1);
        g.Children.Add(lbl);
        g.Children.Add(input);
        return g;
    }

    public void LoadToUi(bool logNotice)
    {
        _suppressPresetFill = true;
        try
        {
            var cfg = CsviLintConfig.Load();
            _chkEnabled.IsChecked = cfg.LlmReviewer.Enabled;
            _chkAfterBuild.IsChecked = cfg.LlmReviewer.RunAfterBuild;
            _cmbProvider.SelectedItem = LlmReviewerConfig.SupportedProviders.Contains(cfg.LlmReviewer.Provider)
                ? cfg.LlmReviewer.Provider
                : "Ollama";
            _txtEndpoint.Text = cfg.LlmReviewer.EndpointOrCommand;
            _txtModel.Text = cfg.LlmReviewer.Model;
            _txtApiKey.Text = cfg.LlmReviewer.ApiKeyEnvOrLiteral;
            _txtCliArgs.Text = cfg.LlmReviewer.CliArgsTemplate;
            _txtTimeout.Text = cfg.LlmReviewer.TimeoutSeconds.ToString();

            string cfgPath = CsviLintConfig.FindConfigFilePath() ?? "(使用內建預設路徑: RULES/CsviLintRules.json)";
            int enabledCount = cfg.Rules.Count(r => r.Enabled);
            _txtRulesSummary.Text = $"已載入 {enabledCount}/{cfg.Rules.Count} 條動態規則（僅 Warning） | {cfgPath}";

            var sb = new StringBuilder();
            foreach (var r in cfg.Rules)
            {
                string mark = r.Enabled ? "ON " : "OFF";
                sb.AppendLine($"[{mark}] {r.Id} ({r.Category}) - {r.Title}");
            }
            _txtRulesPreview.Text = sb.ToString().TrimEnd();

            if (logNotice)
                LogRequested?.Invoke($"🔄 Reloaded {enabledCount} dynamic lint rules from {cfgPath}");
        }
        finally
        {
            _suppressPresetFill = false;
        }
    }

    public CsviLintConfig BuildUpdatedConfig()
    {
        var cfg = CsviLintConfig.Load();
        cfg.LlmReviewer.Enabled = _chkEnabled.IsChecked == true;
        cfg.LlmReviewer.RunAfterBuild = _chkAfterBuild.IsChecked == true;
        cfg.LlmReviewer.Provider = _cmbProvider.SelectedItem as string ?? "Ollama";
        cfg.LlmReviewer.EndpointOrCommand = _txtEndpoint.Text?.Trim() ?? string.Empty;
        cfg.LlmReviewer.Model = _txtModel.Text?.Trim() ?? string.Empty;
        cfg.LlmReviewer.ApiKeyEnvOrLiteral = _txtApiKey.Text?.Trim() ?? string.Empty;
        cfg.LlmReviewer.CliArgsTemplate = _txtCliArgs.Text?.Trim() ?? string.Empty;
        if (int.TryParse(_txtTimeout.Text?.Trim(), out int t) && t > 0)
            cfg.LlmReviewer.TimeoutSeconds = t;
        return cfg;
    }

    private void SaveFromUi(bool silent = false)
    {
        try
        {
            var cfg = BuildUpdatedConfig();
            string path = CsviLintConfig.GetWritableConfigFilePath();
            cfg.Save(path);
            LoadToUi(logNotice: false);
            if (!silent)
                LogRequested?.Invoke($"💾 Saved CSVI Lint & LLM Reviewer settings -> {path}");
        }
        catch (Exception ex)
        {
            LogRequested?.Invoke($"⚠ Failed to save Lint config: {ex.Message}");
        }
    }

    /// <summary>
    /// 供編譯成功後或手動點擊使用：把編譯產生的靜態 Warning 與背景 AI 審查 Warning 安全輸出至 Log（永不阻斷編譯）。
    /// </summary>
    public static void ReportWarningsAndMaybeRunLlmReview(
        CsviCompilerService compiler,
        string assemblyName,
        Dictionary<string, string> sources,
        CsviManifest manifest,
        CompilationResult compRes,
        Action<string> appendLog,
        bool forceRunLlm = false)
    {
        if (compRes.Warnings.Count > 0)
        {
            appendLog($"⚠ Diagnostics / Rule Warnings ({compRes.Warnings.Count}):\n  " + string.Join("\n  ", compRes.Warnings));
        }

        var cfg = CsviLintConfig.Load();
        bool willRunLlm = (forceRunLlm && cfg.LlmReviewer.Enabled && !string.Equals(cfg.LlmReviewer.Provider, "Disabled", StringComparison.OrdinalIgnoreCase))
                          || (!forceRunLlm && cfg.LlmReviewer.Enabled && cfg.LlmReviewer.RunAfterBuild && !string.Equals(cfg.LlmReviewer.Provider, "Disabled", StringComparison.OrdinalIgnoreCase));

        CsviReviewHistoryStore.Record(CsviReviewHistoryStore.CreateSnapshot(
            source: "GUI",
            targetName: assemblyName,
            commandKey: manifest.CommandKey,
            staticWarnings: compRes.Warnings,
            aiResult: null,
            aiPending: willRunLlm));

        if (!forceRunLlm && (!cfg.LlmReviewer.Enabled || !cfg.LlmReviewer.RunAfterBuild))
            return;

        if (forceRunLlm && (!cfg.LlmReviewer.Enabled || string.Equals(cfg.LlmReviewer.Provider, "Disabled", StringComparison.OrdinalIgnoreCase)))
        {
            appendLog("ℹ AI 語意審查尚未啟用或 Provider 設為 Disabled，請先於右側選擇 Provider 並勾選啟用。");
            return;
        }

        appendLog($"🤖 Launching background AI semantic lint ({cfg.LlmReviewer.Provider} / {cfg.LlmReviewer.Model})...");
        _ = Task.Run(async () =>
        {
            var aiRes = await compiler.ReviewWithConfiguredLlmAsync(assemblyName, sources, manifest, cfg);
            CsviReviewHistoryStore.Record(CsviReviewHistoryStore.CreateSnapshot(
                source: "GUI",
                targetName: assemblyName,
                commandKey: manifest.CommandKey,
                staticWarnings: compRes.Warnings,
                aiResult: aiRes,
                aiPending: false));

            Dispatcher.UIThread.Post(() =>
            {
                if (!aiRes.Succeeded)
                {
                    if (!string.IsNullOrWhiteSpace(aiRes.InfoMessage))
                        appendLog($"ℹ {aiRes.InfoMessage}");
                    return;
                }

                if (aiRes.Warnings.Count > 0)
                {
                    appendLog($"⚠ AI Semantic Warnings ({aiRes.Warnings.Count}):\n  " + string.Join("\n  ", aiRes.Warnings));
                }
                else
                {
                    appendLog($"✔ AI Semantic Lint Passed ({aiRes.Provider}): {aiRes.PassedRuleIds.Count} rules OK. {aiRes.Summary}");
                }
            });
        });
    }
}
