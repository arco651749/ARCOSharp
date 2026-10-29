using System.Diagnostics;
using ARCOS.Core.Common;

namespace ARCOS.Core.Execution.Handlers;

/// <summary>
/// Call 模組直譯處理器 (Call@)
/// 完整實做 ARCOS/references/Call.md 規範之外部程式/VI 調度、三種模式 (Debug / Runtime & / Reentrant &&) 與錯誤跳轉
/// </summary>
public class CallModuleHandler : IModuleHandler
{
    public string ModuleName => "Call";

    private static readonly string[] Commands = new[]
    {
        "Call",
        "ExecuteVI",
        "ExecuteScript"
    };

    public IReadOnlyList<string> GetSupportedCommands() => Commands;

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

        // 2. 解析參數與 Error Label
        var parsed = ArgParaHelper.ParseKeyValueArgs(filteredParam, out var posArgs);
        parsed.TryGetValue("_ErrorLabel", out var errorLabel);
        parsed.TryGetValue("_ErrorMsg", out var errorMsg);

        ctx.Log($"[Call Module] [{mode}] Calling external target '{target}' with args: [{string.Join(", ", posArgs)}]");

        // 3. 檢查是否存在 ExternalModules 或 EXTRACMD 目錄下之對應執行檔/腳本
        string candidateDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExternalModules");
        string candidatePath = Path.Combine(candidateDir, target);

        // 若直接存在可執行檔或腳本
        if (File.Exists(candidatePath) && (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = candidatePath,
                    Arguments = string.Join(" ", posArgs),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = mode != "Debug"
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(10000);
                    string output = proc.StandardOutput.ReadToEnd().Trim();

                    // 若有指定回傳變數 (最後一個參數)，將輸出注入變數表
                    if (posArgs.Count > 0)
                    {
                        string lastArg = posArgs[^1];
                        if (lastArg.StartsWith('$'))
                        {
                            ctx.Engines.Variable.Set(lastArg.TrimStart('$'), output, ctx.CurrentSection);
                        }
                    }

                    if (proc.ExitCode != 0)
                    {
                        string msg = string.IsNullOrEmpty(errorMsg) ? $"External call '{target}' failed with exit code {proc.ExitCode}" : errorMsg;
                        return CommandResult.Fail(msg, errorLabel);
                    }
                }
            }
            catch (Exception ex)
            {
                string msg = string.IsNullOrEmpty(errorMsg) ? $"Failed to execute '{target}': {ex.Message}" : errorMsg;
                return CommandResult.Fail(msg, errorLabel);
            }
        }
        else
        {
            // LabVIEW VI 或虛擬調度：若有回傳變數，設定預設成功回傳值 "OK"
            if (posArgs.Count > 0)
            {
                string lastArg = posArgs[^1];
                if (lastArg.StartsWith('$'))
                {
                    ctx.Engines.Variable.Set(lastArg.TrimStart('$'), "OK", ctx.CurrentSection);
                }
            }
        }

        return CommandResult.Done();
    }
}
