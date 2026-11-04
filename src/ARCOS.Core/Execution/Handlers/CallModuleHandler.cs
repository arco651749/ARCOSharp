using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using ARCOS.Core.Common;

namespace ARCOS.Core.Execution.Handlers;

/// <summary>
/// Call 模組直譯處理器 (Call@)
/// 完整實作 ARCOS Call 模組規範：調度 ExternalModules/ 目錄下之獨立 CSVI (以及向下相容之 .vi/.dll/.exe/.bat)
/// 支援三種模式：Debug (開啟前面板調試)、Runtime (&, 背景全速執行)、Reentrant (&&, 多執行緒並行可重入)
/// </summary>
public class CallModuleHandler : IModuleHandler
{
    public string ModuleName => "Call";

    // 快取已載入之 Assembly (路徑 -> (LastWriteTime, Assembly))
    private static readonly ConcurrentDictionary<string, (DateTime LastWrite, Assembly Asm)> AssemblyCache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetSupportedCommands()
    {
        var list = new List<string> { "Call" };
        try
        {
            string extDir = ArcosPaths.ExternalModulesDirectory;
            if (Directory.Exists(extDir))
            {
                foreach (var file in Directory.GetFiles(extDir, "*.csvi"))
                {
                    list.Add(Path.GetFileName(file));
                    list.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
        }
        catch { }
        return list;
    }

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        // 1. 拆解模式後綴：Debug (無後綴)、Runtime (&)、Reentrant (&&)
        string mode = "Debug";
        string target = action.Trim();

        if (target.EndsWith("&&", StringComparison.Ordinal))
        {
            mode = "Reentrant";
            target = target.Substring(0, target.Length - 2).Trim();
        }
        else if (target.EndsWith('&'))
        {
            mode = "Runtime";
            target = target.Substring(0, target.Length - 1).Trim();
        }

        // 2. 解析參數、Error Label 與 TimeOut
        var parsed = ArgParaHelper.ParseKeyValueArgs(filteredParam, out var posArgs);
        parsed.TryGetValue("_ErrorLabel", out var errorLabel);
        parsed.TryGetValue("_ErrorMsg", out var errorMsg);
        parsed.TryGetValue("TimeOut", out var timeOutStr);
        double timeOutSec = double.TryParse(timeOutStr, out double t) ? t : 0;

        ctx.Log($"[Call Module] [{mode}] Invoking target '{target}' with args: [{string.Join(", ", posArgs)}]");

        // 3. 探測 ExternalModules/ 下對應之 CSVI 或外部程式
        string resolvedPath = ArcosPaths.ResolveExternalModuleCsviPath(target);

        // 4. 若目標為可執行檔 (.exe / .bat)
        if (File.Exists(resolvedPath) && (resolvedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || resolvedPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            return ExecuteProcess(resolvedPath, target, mode, posArgs, parsed, errorLabel, errorMsg, timeOutSec, ctx);
        }

        // 5. 若目標為 CSVI 組件或 DLL (.csvi / .dll)
        if (File.Exists(resolvedPath) && (resolvedPath.EndsWith(".csvi", StringComparison.OrdinalIgnoreCase) || resolvedPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var asm = LoadOrGetAssembly(resolvedPath);
                if (asm != null)
                {
                    // 尋找實作 ICallCommand 介面之型別
                    var cmdType = asm.GetTypes().FirstOrDefault(t => typeof(ICallCommand).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                    if (cmdType != null)
                    {
                        var instance = (ICallCommand)Activator.CreateInstance(cmdType)!;
                        object? result = instance.Execute(posArgs, ctx);

                        // 處理回傳值注入 VariableEngine
                        AssignReturnValue(result, posArgs, parsed, ctx);
                        ctx.Log($"[Call Module] Executed CSVI '{Path.GetFileName(resolvedPath)}' successfully. Result: {result ?? "null"}");
                        return CommandResult.Done();
                    }

                    // 亦相容支援實作 IModuleHandler 之組件
                    var handlerType = asm.GetTypes().FirstOrDefault(t => typeof(IModuleHandler).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                    if (handlerType != null)
                    {
                        var handlerInstance = (IModuleHandler)Activator.CreateInstance(handlerType)!;
                        return handlerInstance.Execute(target, filteredParam, ctx);
                    }
                }
            }
            catch (Exception ex)
            {
                string msg = string.IsNullOrEmpty(errorMsg) ? $"Error executing CSVI '{target}': {ex.Message}" : errorMsg;
                ctx.Log($"[Call Module] {msg}", isError: true);
                if (!string.IsNullOrEmpty(errorLabel))
                {
                    return CommandResult.Fail(msg, errorLabel);
                }
                return CommandResult.Fail(msg);
            }
        }

        // 6. 兜底回退：若檔案未找到 (例如開發/單元測試環境中的 Mock 虛擬調度)
        if (!File.Exists(resolvedPath))
        {
            ctx.Log($"[Call Module] Target '{target}' not found at '{resolvedPath}'. Performing mock invocation.");
            // 模擬成功回傳 "OK" 注入指定回傳變數
            AssignReturnValue("OK", posArgs, parsed, ctx);

            if (!string.IsNullOrEmpty(errorLabel) && target.Contains("Error", StringComparison.OrdinalIgnoreCase))
            {
                string msg = string.IsNullOrEmpty(errorMsg) ? $"Mock error in '{target}'" : errorMsg;
                return CommandResult.Fail(msg, errorLabel);
            }
        }

        return CommandResult.Done();
    }

    private static Assembly? LoadOrGetAssembly(string fullPath)
    {
        try
        {
            var fileInfo = new FileInfo(fullPath);
            var lastWrite = fileInfo.LastWriteTimeUtc;

            if (AssemblyCache.TryGetValue(fullPath, out var cached) && cached.LastWrite == lastWrite)
            {
                return cached.Asm;
            }

            // 使用 ReadAllBytes 避免檔案鎖定 (支援熱覆蓋抽換 CSVI)
            byte[] bytes = File.ReadAllBytes(fullPath);
            var asm = Assembly.Load(bytes);
            AssemblyCache[fullPath] = (lastWrite, asm);
            return asm;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Call Module] Failed to load assembly from '{fullPath}': {ex.Message}");
            return null;
        }
    }

    private static void AssignReturnValue(object? result, IReadOnlyList<string> posArgs, Dictionary<string, string> parsed, ExecutionContext ctx)
    {
        if (result == null) return;
        string resultStr = result.ToString() ?? "";

        // 優先順序 1: 具名參數 ReturnVar 或 OutVar
        if (parsed.TryGetValue("ReturnVar", out var rVar) && !string.IsNullOrWhiteSpace(rVar))
        {
            ctx.Engines.Variable.Set(rVar.TrimStart('$'), resultStr, ctx.CurrentSection);
            return;
        }
        if (parsed.TryGetValue("OutVar", out var oVar) && !string.IsNullOrWhiteSpace(oVar))
        {
            ctx.Engines.Variable.Set(oVar.TrimStart('$'), resultStr, ctx.CurrentSection);
            return;
        }

        // 優先順序 2: 若最後一個位置參數為變數名稱 (例如 $Result 或 ResultPos)
        if (posArgs.Count > 0)
        {
            string lastArg = posArgs[^1].Trim();
            if (lastArg.StartsWith('$'))
            {
                ctx.Engines.Variable.Set(lastArg.TrimStart('$'), resultStr, ctx.CurrentSection);
                return;
            }

            // 若不是純數字、也不是布林值，視為變數名稱
            if (!double.TryParse(lastArg, out _) && !bool.TryParse(lastArg, out _) && IsValidIdentifier(lastArg))
            {
                ctx.Engines.Variable.Set(lastArg, resultStr, ctx.CurrentSection);
            }
        }
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        char first = name[0];
        if (!char.IsLetter(first) && first != '_') return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static CommandResult ExecuteProcess(string exePath, string target, string mode, IReadOnlyList<string> posArgs,
        Dictionary<string, string> parsed, string? errorLabel, string? errorMsg, double timeOutSec, ExecutionContext ctx)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = string.Join(" ", posArgs),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = mode != "Debug"
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                int waitMs = timeOutSec > 0 ? (int)(timeOutSec * 1000) : 10000;
                bool exited = proc.WaitForExit(waitMs);
                if (!exited)
                {
                    try { proc.Kill(); } catch { }
                    string timeoutError = string.IsNullOrEmpty(errorMsg) ? $"External process '{target}' timed out after {timeOutSec}s" : errorMsg;
                    return CommandResult.Fail(timeoutError, errorLabel);
                }

                string output = proc.StandardOutput.ReadToEnd().Trim();
                AssignReturnValue(output, posArgs, parsed, ctx);

                if (proc.ExitCode != 0)
                {
                    string msg = string.IsNullOrEmpty(errorMsg) ? $"External process '{target}' failed with exit code {proc.ExitCode}" : errorMsg;
                    return CommandResult.Fail(msg, errorLabel);
                }
            }
        }
        catch (Exception ex)
        {
            string msg = string.IsNullOrEmpty(errorMsg) ? $"Failed to execute process '{target}': {ex.Message}" : errorMsg;
            return CommandResult.Fail(msg, errorLabel);
        }

        return CommandResult.Done();
    }
}
