using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MVCON.Core.Csvi;
using MVCON.CsviStudio.Services;

namespace MVCON.CsviStudio.Mcp;

/// <summary>
/// Model Context Protocol (MCP) Server 實作 (JSON-RPC 2.0 over Stdio)
/// 提供 AI Agent 對 CSVI 虛擬儀表的內省、原始碼提取、編譯打包與沙盒測試能力。
/// </summary>
public class McpServer
{
    private readonly CsviCompilerService _compiler = new();
    private readonly SandboxExecutor _sandbox = new();

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), Console.InputEncoding);
        using var writer = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true };

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

                var response = HandleRequest(method, @params, id);
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

    private JsonObject? HandleRequest(string? method, JsonNode? @params, JsonNode? id)
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
                response["result"] = ExecuteTool(toolName, arguments);
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
                        ["sdkVersion"] = "MVCON-SDK-8.0.1-Avalonia-11.0.10",
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
                        ["csviPath"] = new JsonObject { ["type"] = "string", ["description"] = ".csvi 檔案完整或相對路徑" }
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
                        ["commandKey"] = new JsonObject { ["type"] = "string", ["description"] = "指令 Key，例如 IO@SetPort" },
                        ["sourceFiles"] = new JsonObject { ["type"] = "object", ["description"] = "原始碼字典：檔名 -> 代碼字串" },
                        ["packagingMode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "PortableOpen", "PasswordLocked", "StrippedRelease" } },
                        ["password"] = new JsonObject { ["type"] = "string", ["description"] = "密碼 (若是 PasswordLocked)" },
                        ["author"] = new JsonObject { ["type"] = "string", ["description"] = "修訂作者" },
                        ["revisionSummary"] = new JsonObject { ["type"] = "string", ["description"] = "修訂變更摘要" }
                    },
                    ["required"] = new JsonArray { "outputPath", "commandKey", "sourceFiles" }
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
            }
        };
    }

    private JsonObject ExecuteTool(string name, JsonObject args)
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
                        // 嘗試在當前目錄或 src/MODULES 尋找
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
                    if (!File.Exists(path)) throw new FileNotFoundException("CSVI file not found", path);

                    byte[] bytes = File.ReadAllBytes(path);
                    var package = CsviPackage.LoadFromBytes(bytes);

                    var ret = new JsonObject
                    {
                        ["filePath"] = Path.GetFullPath(path),
                        ["fileSize"] = bytes.Length,
                        ["manifest"] = JsonSerializer.SerializeToNode(package.Manifest),
                        ["hasSourceBundle"] = package.EmbeddedSourceFiles.Count > 0,
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
                    var package = CsviPackage.LoadFromBytes(bytes, pass);

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

                    string asmName = Path.GetFileNameWithoutExtension(outPath);
                    var manifest = new CsviManifest
                    {
                        CommandKey = cmdKey,
                        PackagingMode = mode,
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

                    var ret = new JsonObject
                    {
                        ["success"] = true,
                        ["outputPath"] = Path.GetFullPath(outPath),
                        ["fileSize"] = compRes.AssemblyBytes.Length,
                        ["elapsedMs"] = compRes.ElapsedMilliseconds,
                        ["warnings"] = new JsonArray(compRes.Warnings.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray())
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
                    var pkg = CsviPackage.LoadFromBytes(bytes);
                    string cmdKey = !string.IsNullOrEmpty(pkg.Manifest.CommandKey) ? pkg.Manifest.CommandKey : "Test@Command";

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
}
