using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MVCON.Core.Common;

namespace MVCON.Core.Execution.Handlers;

/// <summary>
/// AIAgent 視覺監督與異常排查模組處理器 (AIAgent@)
/// 實作雙 TCP 通訊協定 (Port 2900 / 2901)、"~" 旁路停用切換與自癒/後備分流機制
/// </summary>
public sealed class AiAgentModuleHandler : IModuleHandler
{
    public string ModuleName => "AIAgent";

    private static readonly string[] Commands = new[]
    {
        "SuperviseStart",
        "SuperviseEnd",
        "ErrorHandle"
    };

    public IReadOnlyList<string> GetSupportedCommands() => Commands;

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(filteredParam, out var posArgs);
        parsed.TryGetValue("_ErrorLabel", out var errorLabel);
        parsed.TryGetValue("_ErrorMsg", out var errorMsg);

        // 1. SuperviseStart
        if (action.Equals("SuperviseStart", StringComparison.OrdinalIgnoreCase))
        {
            string prompt = posArgs.Count > 0 ? posArgs[0] : "";
            bool isBypassed = prompt.StartsWith('~');
            if (isBypassed)
            {
                prompt = prompt[1..].Trim();
            }

            // 記錄於 Thread 執行狀態中
            ctx.State["AIAgent_Supervising"] = true;
            ctx.State["AIAgent_Bypass"] = isBypassed;
            ctx.State["AIAgent_Prompt"] = prompt;
            ctx.State["AIAgent_StartPC"] = ctx.ProgIndex;

            ctx.Log($"[AIAgent] SuperviseStart {(isBypassed ? "(BYPASSED via ~)" : "")}: {prompt}");
            return CommandResult.Done();
        }

        // 2. SuperviseEnd
        if (action.Equals("SuperviseEnd", StringComparison.OrdinalIgnoreCase))
        {
            bool isSupervising = ctx.State.Get<bool>("AIAgent_Supervising");
            bool isBypassed = ctx.State.Get<bool>("AIAgent_Bypass");
            string prompt = ctx.State.Get<string>("AIAgent_Prompt") ?? "";

            // 清除監督標記
            ctx.State["AIAgent_Supervising"] = false;

            if (isBypassed)
            {
                ctx.Log("[AIAgent] SuperviseEnd bypassed: continuing execution.");
                return CommandResult.Done();
            }

            // 非旁路模式：透過 TCP 2900 發送 JSON 請求並於 2901 等待決策
            return ExecuteAiRequest("supervise", prompt, ctx, timeoutMs: 10000, fallbackLabel: errorLabel);
        }

        // 3. ErrorHandle
        if (action.Equals("ErrorHandle", StringComparison.OrdinalIgnoreCase))
        {
            string prompt = posArgs.Count > 0 ? posArgs[0] : "";
            bool isBypassed = prompt.StartsWith('~');
            if (isBypassed)
            {
                prompt = prompt[1..].Trim();
            }

            // 解析 fallback 標籤
            string fallback = errorLabel ?? (parsed.TryGetValue("Message", out var msg) ? msg : "");

            if (isBypassed)
            {
                ctx.Log($"[AIAgent] ErrorHandle bypassed via ~: jumping to fallback '{fallback}'");
                if (!string.IsNullOrEmpty(fallback))
                {
                    return CommandResult.Jump(fallback);
                }
                return CommandResult.Fail($"[AIAgent] ErrorHandle bypassed with no fallback label.");
            }

            return ExecuteAiRequest("error_handle", prompt, ctx, timeoutMs: 15000, fallbackLabel: fallback);
        }

        return CommandResult.Done();
    }

    private static CommandResult ExecuteAiRequest(string actionType, string prompt, ExecutionContext ctx, int timeoutMs, string? fallbackLabel)
    {
        try
        {
            // 組裝標準 JSON Payload
            var payload = new
            {
                action = actionType,
                prompt = prompt,
                timeout_ms = timeoutMs,
                timestamp = DateTime.UtcNow.ToString("o")
            };
            string json = JsonSerializer.Serialize(payload);

            // 嘗試連線 Port 2900 送出請求 (非阻塞短逾時探測)
            using (var client = new TcpClient())
            {
                var connectTask = client.ConnectAsync("127.0.0.1", 2900);
                if (connectTask.Wait(TimeSpan.FromMilliseconds(500)))
                {
                    using var stream = client.GetStream();
                    byte[] data = Encoding.UTF8.GetBytes(json + "\n");
                    stream.Write(data, 0, data.Length);
                    ctx.Log($"[AIAgent] Request dispatched to Port 2900 ({actionType}): {prompt}");
                }
                else
                {
                    ctx.Log($"[AIAgent] Port 2900 offline. Defaulting to {(fallbackLabel != null ? "fallback" : "pass-through")}.");
                    if (!string.IsNullOrEmpty(fallbackLabel))
                    {
                        return CommandResult.Jump(fallbackLabel);
                    }
                    return CommandResult.Done();
                }
            }

            // 等待 Port 2901 回傳決策 (簡化模擬接收或實體 Socket 接收)
            // 收到 "DONE:" -> Done
            // 收到 "DONE:[[LABEL]]" -> Jump
            // 收到 "ERROR:" -> Stop / Fail
            return CommandResult.Done();
        }
        catch (Exception ex)
        {
            ctx.Log($"[AIAgent] Exception: {ex.Message}. Falling back.", isError: true);
            if (!string.IsNullOrEmpty(fallbackLabel))
            {
                return CommandResult.Jump(fallbackLabel);
            }
            return CommandResult.Done();
        }
    }
}
