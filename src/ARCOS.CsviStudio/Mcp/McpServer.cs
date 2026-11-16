using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ARCOS.Core.Csvi;
using ARCOS.CsviStudio.Services;

namespace ARCOS.CsviStudio.Mcp;

/// <summary>
/// Model Context Protocol (MCP) Server 實作 (JSON-RPC 2.0 over Stdio)
/// 提供 AI Agent 對 CSVI 虛擬儀表的內省、原始碼提取、編譯打包與沙盒測試能力。
/// </summary>
public class McpServer
{
    private readonly CsviCompilerService _compiler = new();
    private readonly SandboxExecutor _sandbox = new();

    /// <summary>
    /// 當 MCP 請求載入或編譯 CSVI 時，通知 UI 主執行緒載入至 Studio 編輯器
    /// </summary>
    public Action<string>? OnLoadFileRequested { get; set; }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = utf8NoBom;
        Console.OutputEncoding = utf8NoBom;

        using var reader = new StreamReader(Console.OpenStandardInput(), utf8NoBom);
        using var writer = new StreamWriter(Console.OpenStandardOutput(), utf8NoBom) { AutoFlush = true };

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var request = JsonNode.Parse(line);
                if (request == null) continue;

                var id = request["id"];
                var method = request["method"]?.GetValue<string>();
                var @params = request["params"];

                var response = await HandleRequestAsync(method, @params, id, cancellationToken);
                if (response != null)
                {
                    await writer.WriteLineAsync(response.ToJsonString());
                }
            }
            catch (Exception ex)
            {
                var errObj = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = null,
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32603,
                        ["message"] = ex.Message
                    }
                };
                await writer.WriteLineAsync(errObj.ToJsonString());
            }
        }
    }

    public JsonObject? HandleRequestDirect(string? method, JsonNode? @params, JsonNode? id)
    {
        return HandleRequestAsync(method, @params, id).GetAwaiter().GetResult();
    }

    public Task<JsonObject?> HandleRequestDirectAsync(string? method, JsonNode? @params, JsonNode? id, CancellationToken ct = default)
    {
        return HandleRequestAsync(method, @params, id, ct);
    }

    private async Task<JsonObject?> HandleRequestAsync(string? method, JsonNode? @params, JsonNode? id, CancellationToken ct = default)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone()
        };

        switch (method)
        {
            case "initialize":
                response["result"] = new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "CSVI_Studio_MCP",
                        ["version"] = "3.0.0"
                    },
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject
                        {
                            ["listChanged"] = true
                        },
                        ["resources"] = new JsonObject
                        {
                            ["subscribe"] = false,
                            ["listChanged"] = false
                        }
                    }
                };
                return response;

            case "tools/list":
                response["result"] = new JsonObject
                {
                    ["tools"] = GetToolDefinitions()
                };
                return response;

            case "tools/call":
                string toolName = @params?["name"]?.GetValue<string>() ?? string.Empty;
                var arguments = @params?["arguments"] as JsonObject ?? new JsonObject();
                response["result"] = await ExecuteToolAsync(toolName, arguments, ct);
                return response;

            case "resources/list":
                response["result"] = new JsonObject
                {
                    ["resources"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["uri"] = "csvi://system/sdk-info",
                            ["name"] = "SDK Info and ABI Alignment Matrix",
                            ["mimeType"] = "application/json"
                        }
                    }
                };
                return response;

            case "resources/read":
                string uri = @params?["uri"]?.GetValue<string>() ?? string.Empty;
                if (uri == "csvi://system/sdk-info")
                {
                    var info = new JsonObject
                    {
                        ["sdkVersion"] = "ARCOS-SDK-8.0.1-Avalonia-11.0.10",
                        ["dotNetVersion"] = Environment.Version.ToString(),
                        ["osVersion"] = Environment.OSVersion.ToString(),
                        ["supportedModes"] = new JsonArray { "PortableOpen", "PasswordLocked", "StrippedRelease" }
                    };
                    response["result"] = new JsonObject
                    {
                        ["contents"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["uri"] = uri,
                                ["mimeType"] = "application/json",
                                ["text"] = info.ToJsonString()
                            }
                        }
                    };
                    return response;
                }
                throw new ArgumentException($"Resource URI not found: {uri}");

            case "ping":
                response["result"] = new JsonObject();
                return response;

            default:
                response["error"] = new JsonObject
                {
                    ["code"] = -32601,
                    ["message"] = $"Method not found: {method}"
                };
                return response;
        }
    }

    private JsonArray GetToolDefinitions()
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["name"] = "csvi_list_modules",
                ["description"] = "掃描 MODULES/ 目錄下所有現存指令與硬體驅動清單，回傳模組分組、CommandKey 與檔案路徑",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["modulesRoot"] = new JsonObject { ["type"] = "string", ["description"] = "模組根目錄 (可選，預設當前 MODULES)" }
                    }
                }
            },
            new JsonObject
            {
                ["name"] = "csvi_inspect",
                ["description"] = "檢查指定 .csvi 組件的元數據 (Manifest)、保護模式、內嵌資源清單與歷史修訂",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["csviPath"] = new JsonObject { ["type"] = "string", ["description"] = ".csvi 檔案完整或相對路徑" },
                        ["password"] = new JsonObject { ["type"] = "string", ["description"] = "可選密碼 (若需檢視加密包內的檔案清單)" }
                    },
                    ["required"] = new JsonArray { "csviPath" }
                }
            },
            new JsonObject
            {
                ["name"] = "csvi_extract_source",
                ["description"] = "從可攜式 .csvi 提取內嵌的 .cs 與 .axaml 原始碼內容",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["csviPath"] = new JsonObject { ["type"] = "string", ["description"] = ".csvi 檔案路徑" },
                        ["password"] = new JsonObject { ["type"] = "string", ["description"] = "解鎖密碼 (若為 PasswordLocked 模式)" }
                    },
                    ["required"] = new JsonArray { "csviPath" }
                }
            },
            new JsonObject
            {
                ["name"] = "csvi_compile_and_pack",
                ["description"] = "透過內建 Roslyn 工具鏈將 C# 原始碼與 AXAML 編譯為 .csvi 二進位組件並自動封裝",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["outputPath"] = new JsonObject { ["type"] = "string", ["description"] = "輸出 .csvi 路徑" },
                        ["commandKey"] = new JsonObject { ["type"] = "string", ["description"] = "指令 Key，例如 IO@SetPort 或 MockIO" },
                        ["module"] = new JsonObject { ["type"] = "string", ["description"] = "所屬模組名稱，例如 IO" },
                        ["componentType"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "Command", "Driver", "Library" } },
                        ["sourceFiles"] = new JsonObject { ["type"] = "object", ["description"] = "原始碼字典：檔名 -> 代碼字串" },
                        ["packagingMode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "PortableOpen", "PasswordLocked", "StrippedRelease" } },
                        ["password"] = new JsonObject { ["type"] = "string", ["description"] = "密碼 (若是 PasswordLocked)" },
                        ["author"] = new JsonObject { ["type"] = "string", ["description"] = "修訂作者" },
                        ["revisionSummary"] = new JsonObject { ["type"] = "string", ["description"] = "修訂變更摘要" },
                        ["runAiReview"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否同時執行目前設定的 LLM 語意審查並回傳 aiReview 欄位 (預設 false；亦可改呼 csvi_ai_review)" }
                    },
                    ["required"] = new JsonArray { "outputPath", "commandKey", "sourceFiles" }
                }
            },
            new JsonObject
            {
                ["name"] = "csvi_ai_review",
                ["description"] = "對指定 sourceFiles 或 .csvi 執行本機動態規則檢查 ([LINT-WARN]) 與設定之 LLM 端點語意審查 ([AI-LINT-WARN])，回傳完整診斷與建議摘要",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["csviPath"] = new JsonObject { ["type"] = "string", ["description"] = ".csvi 檔案路徑 (若未傳 sourceFiles 時從中解包原始碼)" },
                        ["sourceFiles"] = new JsonObject { ["type"] = "object", ["description"] = "可選原始碼字典：檔名 -> 代碼字串" },
                        ["commandKey"] = new JsonObject { ["type"] = "string", ["description"] = "指令 Key，如 IO@AddIODriver::MockIO" },
                        ["password"] = new JsonObject { ["type"] = "string", ["description"] = "解鎖密碼 (若為 PasswordLocked .csvi)" }
                    }
                }
            },
            new JsonObject
            {
                ["name"] = "csvi_get_last_review",
                ["description"] = "讀取 CSVI Studio (GUI / CLI / MCP) 最近一次執行的靜態規則與 LLM 語意審查結果，包含 [LINT-WARN]、[AI-LINT-WARN]、Summary 與狀態",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                }
            },
            new JsonObject
            {
                ["name"] = "csvi_load_in_studio",
                ["description"] = "在 CSVI Studio 編輯器介面載入指定的 .csvi 檔案，立即更新代碼編輯器 (Logic.cs, Wizard.axaml, Panel.axaml) 與即時 XAML 預覽畫面",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["csviPath"] = new JsonObject { ["type"] = "string", ["description"] = ".csvi 檔案路徑" }
                    },
                    ["required"] = new JsonArray { "csviPath" }
                }
            },
            new JsonObject
            {
                ["name"] = "csvi_sandbox_execute",
                ["description"] = "在隔離沙盒中以微秒級速度執行指定 .csvi 的 Execute() 邏輯，回傳執行耗時與狀態",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["csviPath"] = new JsonObject { ["type"] = "string", ["description"] = ".csvi 檔案路徑" },
                        ["parameter"] = new JsonObject { ["type"] = "string", ["description"] = "傳入 Execute() 的參數字串" }
                    },
                    ["required"] = new JsonArray { "csviPath", "parameter" }
                }
            },
            new JsonObject
            {
                ["name"] = "csvi_capture_ui",
                ["description"] = "由 CSVI Studio 載入編譯後的 .dll 或 .csvi，渲染參數導引視窗 (Wizard) 或驅動前面板 (Panel) 並自動截圖回傳給 AI Agent 進行視覺版面校驗與反覆修改",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["csviPath"] = new JsonObject { ["type"] = "string", ["description"] = ".csvi 或 .dll 檔案路徑" },
                        ["target"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "Wizard", "Panel" }, ["description"] = "欲渲染與截圖的目標視窗 (預設 Wizard)" },
                        ["outputPath"] = new JsonObject { ["type"] = "string", ["description"] = "截圖輸出圖片檔案路徑 (可選)" },
                        ["width"] = new JsonObject { ["type"] = "integer", ["description"] = "視窗寬度 (預設 1000)" },
                        ["height"] = new JsonObject { ["type"] = "integer", ["description"] = "視窗高度 (預設 700)" }
                    },
                    ["required"] = new JsonArray { "csviPath" }
                }
            }
        };
    }

    private async Task<JsonObject> ExecuteToolAsync(string name, JsonObject args, CancellationToken ct = default)
    {
        var content = new JsonArray();

        try
        {
            switch (name)
            {
                case "csvi_list_modules":
                {
                    string root = args["modulesRoot"]?.GetValue<string>() ?? "MODULES";
                    if (!Directory.Exists(root))
                    {
                        if (Directory.Exists("src/MODULES")) root = "src/MODULES";
                    }

                    var list = new JsonArray();
                    if (Directory.Exists(root))
                    {
                        foreach (var file in Directory.GetFiles(root, "*.csvi", SearchOption.AllDirectories))
                        {
                            list.Add(new JsonObject
                            {
                                ["path"] = file,
                                ["fileName"] = Path.GetFileName(file),
                                ["directory"] = Path.GetDirectoryName(file)
                            });
                        }
                    }

                    var ret = new JsonObject
                    {
                        ["modulesRoot"] = Path.GetFullPath(root),
                        ["csviCount"] = list.Count,
                        ["items"] = list
                    };

                    content.Add(new JsonObject { ["type"] = "text", ["text"] = ret.ToJsonString() });
                    break;
                }

                case "csvi_inspect":
                {
                    string path = args["csviPath"]?.GetValue<string>() ?? throw new ArgumentException("Missing csviPath");
                    string? pass = args["password"]?.GetValue<string>();
                    if (!File.Exists(path)) throw new FileNotFoundException("CSVI file not found", path);

                    byte[] bytes = File.ReadAllBytes(path);
                    // 先讀取 Manifest 與 RuntimeXaml (不強制解包加密源碼以免未帶密碼時拋出例外)
                    var package = CsviPackage.LoadFromBytes(bytes, pass, unpackSources: false);
                    if (package.HasSourceBundleResource && (package.Manifest.PackagingMode == CsviPackagingMode.PortableOpen || !string.IsNullOrEmpty(pass)))
                    {
                        package = CsviPackage.LoadFromBytes(bytes, pass, unpackSources: true);
                    }

                    var ret = new JsonObject
                    {
                        ["filePath"] = Path.GetFullPath(path),
                        ["fileSize"] = bytes.Length,
                        ["manifest"] = JsonSerializer.SerializeToNode(package.Manifest),
                        ["hasSourceBundle"] = package.HasSourceBundleResource,
                        ["isSourceUnlocked"] = package.EmbeddedSourceFiles.Count > 0,
                        ["sourceFileNames"] = new JsonArray(package.EmbeddedSourceFiles.Keys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()),
                        ["runtimeXamlNames"] = new JsonArray(package.EmbeddedRuntimeXaml.Keys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray())
                    };

                    content.Add(new JsonObject { ["type"] = "text", ["text"] = ret.ToJsonString() });
                    break;
                }

                case "csvi_extract_source":
                {
                    string path = args["csviPath"]?.GetValue<string>() ?? throw new ArgumentException("Missing csviPath");
                    string? pass = args["password"]?.GetValue<string>();

                    if (!File.Exists(path)) throw new FileNotFoundException("CSVI file not found", path);

                    byte[] bytes = File.ReadAllBytes(path);
                    var package = CsviPackage.LoadFromBytes(bytes, pass, unpackSources: true);

                    var filesObj = new JsonObject();
                    foreach (var kvp in package.EmbeddedSourceFiles)
                    {
                        filesObj[kvp.Key] = kvp.Value;
                    }

                    var ret = new JsonObject
                    {
                        ["filePath"] = Path.GetFullPath(path),
                        ["packagingMode"] = package.Manifest.PackagingMode.ToString(),
                        ["filesCount"] = package.EmbeddedSourceFiles.Count,
                        ["sourceFiles"] = filesObj
                    };

                    content.Add(new JsonObject { ["type"] = "text", ["text"] = ret.ToJsonString() });
                    break;
                }

                case "csvi_compile_and_pack":
                {
                    string outPath = args["outputPath"]?.GetValue<string>() ?? throw new ArgumentException("Missing outputPath");
                    string cmdKey = args["commandKey"]?.GetValue<string>() ?? throw new ArgumentException("Missing commandKey");
                    var srcNode = args["sourceFiles"] as JsonObject ?? throw new ArgumentException("Missing sourceFiles object");

                    var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in srcNode)
                    {
                        if (prop.Value != null)
                        {
                            sources[prop.Key] = prop.Value.GetValue<string>();
                        }
                    }

                    string modeStr = args["packagingMode"]?.GetValue<string>() ?? "PortableOpen";
                    var mode = Enum.Parse<CsviPackagingMode>(modeStr, ignoreCase: true);
                    string? pass = args["password"]?.GetValue<string>();
                    string author = args["author"]?.GetValue<string>() ?? "AI Agent";
                    string summary = args["revisionSummary"]?.GetValue<string>() ?? "Compiled via CSVI Studio MCP";

                    string modName = args["module"]?.GetValue<string>() ?? (cmdKey.Contains('@') ? cmdKey.Split('@')[0] : "IO");
                    string compTypeStr = args["componentType"]?.GetValue<string>() ?? (cmdKey.Contains('@') ? "Command" : "Driver");
                    var compType = Enum.Parse<CsviComponentType>(compTypeStr, ignoreCase: true);

                    string asmName = Path.GetFileNameWithoutExtension(outPath);
                    var manifest = new CsviManifest
                    {
                        Module = modName,
                        CommandKey = cmdKey,
                        ComponentType = compType,
                        PackagingMode = mode,
                        SourceEncrypted = (mode == CsviPackagingMode.PasswordLocked),
                        LastAuthor = author,
                        RevisionHistory = new List<CsviRevisionEntry>
                        {
                            new() { Revision = 1, Author = author, Summary = summary }
                        }
                    };

                    var compRes = _compiler.Compile(asmName, sources, manifest, pass);
                    if (!compRes.Success)
                    {
                        return new JsonObject
                        {
                            ["isError"] = true,
                            ["content"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = "Compilation Failed:\n" + string.Join("\n", compRes.Errors)
                                }
                            }
                        };
                    }

                    string? dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.WriteAllBytes(outPath, compRes.AssemblyBytes);

                    try { OnLoadFileRequested?.Invoke(Path.GetFullPath(outPath)); } catch { }

                    bool runAiReview = args["runAiReview"]?.GetValue<bool>() ?? false;
                    CsviLlmReviewResult? aiRes = null;
                    if (runAiReview)
                    {
                        aiRes = await _compiler.ReviewWithConfiguredLlmAsync(asmName, sources, manifest, ct: ct);
                    }

                    CsviReviewHistoryStore.Record(CsviReviewHistoryStore.CreateSnapshot(
                        source: "MCP",
                        targetName: asmName,
                        commandKey: manifest.CommandKey,
                        staticWarnings: compRes.Warnings,
                        aiResult: aiRes,
                        aiPending: false));

                    var ret = new JsonObject
                    {
                        ["success"] = true,
                        ["outputPath"] = Path.GetFullPath(outPath),
                        ["fileSize"] = compRes.AssemblyBytes.Length,
                        ["elapsedMs"] = compRes.ElapsedMilliseconds,
                        ["warnings"] = new JsonArray(compRes.Warnings.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray())
                    };
                    if (aiRes != null)
                        ret["aiReview"] = BuildAiReviewJson(aiRes);

                    content.Add(new JsonObject { ["type"] = "text", ["text"] = SerializeMcpJson(ret) });
                    break;
                }

                case "csvi_ai_review":
                {
                    var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var manifest = new CsviManifest();
                    string asmName = "CsviReview";

                    if (args["sourceFiles"] is JsonObject srcObj && srcObj.Count > 0)
                    {
                        foreach (var prop in srcObj)
                        {
                            if (prop.Value != null)
                                sources[prop.Key] = prop.Value.GetValue<string>();
                        }
                    }
                    else
                    {
                        string path = args["csviPath"]?.GetValue<string>()
                            ?? throw new ArgumentException("Either sourceFiles or csviPath must be provided.");
                        if (!File.Exists(path)) throw new FileNotFoundException("CSVI file not found", path);
                        string? pass = args["password"]?.GetValue<string>();
                        var pkg = CsviPackage.LoadFromBytes(File.ReadAllBytes(path), pass, unpackSources: true);
                        foreach (var kvp in pkg.EmbeddedSourceFiles)
                            sources[kvp.Key] = kvp.Value;
                        manifest = pkg.Manifest;
                        asmName = Path.GetFileNameWithoutExtension(path);
                    }

                    string? cmdKeyOverride = args["commandKey"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(cmdKeyOverride))
                        manifest.CommandKey = cmdKeyOverride;

                    var compRes = _compiler.Compile(asmName, sources, manifest);
                    var aiRes = await _compiler.ReviewWithConfiguredLlmAsync(asmName, sources, manifest, ct: ct);

                    CsviReviewHistoryStore.Record(CsviReviewHistoryStore.CreateSnapshot(
                        source: "MCP",
                        targetName: asmName,
                        commandKey: manifest.CommandKey,
                        staticWarnings: compRes.Warnings,
                        aiResult: aiRes,
                        aiPending: false));

                    var ret = new JsonObject
                    {
                        ["targetName"] = asmName,
                        ["commandKey"] = manifest.CommandKey,
                        ["compilationSucceeded"] = compRes.Success,
                        ["staticWarnings"] = new JsonArray(compRes.Warnings.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray()),
                        ["aiReview"] = BuildAiReviewJson(aiRes)
                    };

                    content.Add(new JsonObject { ["type"] = "text", ["text"] = SerializeMcpJson(ret) });
                    break;
                }

                case "csvi_get_last_review":
                {
                    var snap = CsviReviewHistoryStore.GetLatest();
                    var ret = new JsonObject
                    {
                        ["hasSnapshot"] = snap != null,
                        ["snapshotFilePath"] = CsviReviewHistoryStore.SnapshotFilePath,
                        ["snapshot"] = snap == null ? null : JsonSerializer.SerializeToNode(snap)
                    };
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = SerializeMcpJson(ret) });
                    break;
                }

                case "csvi_capture_ui":
                {
                    string path = args["csviPath"]?.GetValue<string>() ?? throw new ArgumentException("Missing csviPath");
                    string target = args["target"]?.GetValue<string>() ?? "Wizard";
                    string? outPath = args["outputPath"]?.GetValue<string>();
                    int width = args["width"]?.GetValue<int>() ?? 1000;
                    int height = args["height"]?.GetValue<int>() ?? 700;

                    if (!File.Exists(path)) throw new FileNotFoundException("Target file not found", path);

                    if (string.IsNullOrEmpty(outPath))
                    {
                        string defaultDir = Path.Combine(Path.GetTempPath(), "csvi_previews");
                        Directory.CreateDirectory(defaultDir);
                        outPath = Path.Combine(defaultDir, $"{Path.GetFileNameWithoutExtension(path)}_{target}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                    }

                    string fullCsviPath = Path.GetFullPath(path);
                    string fullOutPath = Path.GetFullPath(outPath);
                    if (File.Exists(fullOutPath)) File.Delete(fullOutPath);

                    bool captureSuccess = false;
                    string captureError = string.Empty;

                    try
                    {
                        // 啟動 CSVI_Studio.exe 子行程載入該 .csvi，切換至指定 Wizard / Panel 頁籤並以 RenderTargetBitmap 直接渲染輸出圖片
                        string selfExe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CSVI_Studio.exe");
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = selfExe,
                            Arguments = $"\"{fullCsviPath}\" --capture-target \"{target}\" --capture-out \"{fullOutPath}\"",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using var proc = System.Diagnostics.Process.Start(psi);
                        proc?.WaitForExit(10000);
                        captureSuccess = File.Exists(fullOutPath) && new FileInfo(fullOutPath).Length > 0;
                    }
                    catch (Exception ex)
                    {
                        captureError = ex.Message;
                    }

                    var ret = new JsonObject
                    {
                        ["success"] = captureSuccess,
                        ["imagePath"] = fullOutPath,
                        ["target"] = target,
                        ["width"] = width,
                        ["height"] = height,
                        ["fileSize"] = File.Exists(fullOutPath) ? new FileInfo(fullOutPath).Length : 0,
                        ["error"] = captureError
                    };

                    content.Add(new JsonObject { ["type"] = "text", ["text"] = ret.ToJsonString() });
                    break;
                }

                case "csvi_load_in_studio":
                {
                    string path = args["csviPath"]?.GetValue<string>() ?? throw new ArgumentException("Missing csviPath");
                    if (!File.Exists(path)) throw new FileNotFoundException("CSVI file not found", path);
                    OnLoadFileRequested?.Invoke(Path.GetFullPath(path));
                    var ret = new JsonObject
                    {
                        ["success"] = true,
                        ["filePath"] = Path.GetFullPath(path),
                        ["message"] = $"Successfully loaded {Path.GetFileName(path)} into CSVI Studio."
                    };
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = ret.ToJsonString() });
                    break;
                }

                case "csvi_sandbox_execute":
                {
                    string path = args["csviPath"]?.GetValue<string>() ?? throw new ArgumentException("Missing csviPath");
                    string param = args["parameter"]?.GetValue<string>() ?? string.Empty;

                    if (!File.Exists(path)) throw new FileNotFoundException("CSVI file not found", path);

                    byte[] bytes = File.ReadAllBytes(path);
                    // 不強制解包 Source.Bundle，讓 PasswordLocked 與 StrippedRelease 皆可無需密碼直接執行 IL！
                    var pkg = CsviPackage.LoadFromBytes(bytes, unpackSources: false);
                    string cmdKey = !string.IsNullOrEmpty(pkg.Manifest.CommandKey) ? pkg.Manifest.CommandKey : Path.GetFileNameWithoutExtension(path);

                    var runRes = _sandbox.Execute(bytes, cmdKey, param);

                    var ret = new JsonObject
                    {
                        ["success"] = runRes.Success,
                        ["output"] = runRes.Output,
                        ["error"] = runRes.Error,
                        ["elapsedMs"] = runRes.ElapsedMilliseconds
                    };

                    content.Add(new JsonObject { ["type"] = "text", ["text"] = ret.ToJsonString() });
                    break;
                }

                default:
                    throw new ArgumentException($"Unknown tool: {name}");
            }
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["isError"] = true,
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = "Error: " + ex.Message
                    }
                }
            };
        }

        return new JsonObject
        {
            ["content"] = content
        };
    }

    private static readonly JsonSerializerOptions McpJsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string SerializeMcpJson(JsonNode node) => node.ToJsonString(McpJsonOpts);

    private static JsonObject BuildAiReviewJson(CsviLlmReviewResult aiRes)
    {
        return new JsonObject
        {
            ["executed"] = aiRes.Executed,
            ["succeeded"] = aiRes.Succeeded,
            ["provider"] = aiRes.Provider,
            ["summary"] = aiRes.Summary,
            ["infoMessage"] = aiRes.InfoMessage,
            ["warnings"] = new JsonArray(aiRes.Warnings.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray()),
            ["passedRuleIds"] = new JsonArray(aiRes.PassedRuleIds.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray())
        };
    }
}
