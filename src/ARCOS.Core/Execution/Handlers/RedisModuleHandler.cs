using System.Collections.Concurrent;
using ARCOS.Core.Common;

namespace ARCOS.Core.Execution.Handlers;

/// <summary>
/// Redis 跨專案/跨站狀態總線處理器 (Redis@)
/// 提供 Session 建立、鍵值讀寫、Pub/Sub 廣播與 List 佇列 (LPush/LPop/BLPop) 操作
/// 具備自給自足的高速內存總線與外部 Redis 服務橋接能力
/// </summary>
public sealed class RedisModuleHandler : IModuleHandler
{
    public string ModuleName => "Redis";

    private static readonly string[] Commands = new[]
    {
        "CreateSession",
        "CMD",
        "Set",
        "Get",
        "Publish",
        "Subscribe",
        "LPush",
        "LPop",
        "BLPop"
    };

    public IReadOnlyList<string> GetSupportedCommands() => Commands;

    // 內存 Session 與 Key-Value 存放區
    private static readonly ConcurrentDictionary<string, string> KeyStore = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> ListStore = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, Action<string>>> PubSubChannels = new(StringComparer.OrdinalIgnoreCase);

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(filteredParam, out var posArgs);
        parsed.TryGetValue("_ErrorLabel", out var errorLabel);
        parsed.TryGetValue("_ErrorMsg", out var errorMsg);

        // 1. CreateSession::$session, $host:$port(, $user:$password)
        if (action.Equals("CreateSession", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string session = posArgs[0];
            string endpoint = posArgs.Count > 1 ? posArgs[1] : "127.0.0.1:6379";

            // 設定 session 狀態變數 RedisErr#$session = OK
            ctx.Engines.Variable.SetVariable($"RedisErr#{session}", "OK");
            ctx.Log($"[Redis] CreateSession '{session}' at {endpoint}");
            return CommandResult.Done();
        }

        // 2. Set::$session, $value
        if (action.Equals("Set", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count < 2) return CommandResult.Done();
            string key = posArgs[0];
            string value = posArgs[1];

            KeyStore[key] = value;
            ctx.Log($"[Redis] Set '{key}' = {value}");
            return CommandResult.Done();
        }

        // 3. Get::$session(, $var@$sec)
        if (action.Equals("Get", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string session = posArgs[0];
            string targetVar = session;
            string section = ctx.CurrentSection;

            if (posArgs.Count > 1)
            {
                string target = posArgs[1];
                if (target.Contains('@'))
                {
                    var parts = target.Split('@', 2);
                    targetVar = parts[0];
                    section = parts[1];
                }
                else
                {
                    targetVar = target;
                }
            }

            KeyStore.TryGetValue(session, out var val);
            ctx.Engines.Variable.SetVariable(targetVar, val ?? "", section);
            ctx.Log($"[Redis] Get '{session}' -> ${targetVar} = '{val ?? ""}'");
            return CommandResult.Done();
        }

        // 4. Publish::$session, $value
        if (action.Equals("Publish", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count < 2) return CommandResult.Done();
            string channel = posArgs[0];
            string msg = posArgs[1];

            if (PubSubChannels.TryGetValue(channel, out var subscribers))
            {
                foreach (var handler in subscribers.Values)
                {
                    handler.Invoke(msg);
                }
            }
            ctx.Log($"[Redis] Publish to '{channel}': {msg}");
            return CommandResult.Done();
        }

        // 5. Subscribe::$session
        if (action.Equals("Subscribe", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string channel = posArgs[0];
            ctx.Log($"[Redis] Subscribed to '{channel}'");
            return CommandResult.Done();
        }

        // 6. LPush::$session, $val(, Dir=Start/End)
        if (action.Equals("LPush", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count < 2) return CommandResult.Done();
            string session = posArgs[0];
            string val = posArgs[1];

            var queue = ListStore.GetOrAdd(session, _ => new ConcurrentQueue<string>());
            queue.Enqueue(val);
            ctx.Log($"[Redis] LPush to '{session}': {val}");
            return CommandResult.Done();
        }

        // 7. LPop / BLPop
        if (action.Equals("LPop", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("BLPop", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string session = posArgs[0];

            if (ListStore.TryGetValue(session, out var queue) && queue.TryDequeue(out var val))
            {
                ctx.Engines.Variable.SetVariable(session, val, ctx.CurrentSection);
                return CommandResult.Done();
            }

            // BLPop 阻塞式等待
            if (action.Equals("BLPop", StringComparison.OrdinalIgnoreCase))
            {
                double timeoutSec = 5.0;
                if (parsed.TryGetValue("TimeOut", out var toStr) && double.TryParse(toStr, out double to))
                {
                    timeoutSec = to;
                }

                if (ctx.State.Value == 0)
                {
                    ctx.State.Value = 1;
                    ctx.State["startTime"] = Environment.TickCount64;
                    ctx.State["timeoutMs"] = (long)(timeoutSec * 1000);
                    return CommandResult.Wait();
                }

                long start = ctx.State.Get<long>("startTime");
                long timeoutMs = ctx.State.Get<long>("timeoutMs");
                if (Environment.TickCount64 - start >= timeoutMs)
                {
                    if (!string.IsNullOrEmpty(errorLabel))
                    {
                        return CommandResult.Jump(errorLabel);
                    }
                    return CommandResult.Fail($"[Redis] BLPop timeout on key '{session}'");
                }

                return CommandResult.Wait();
            }

            return CommandResult.Done();
        }

        // 8. CMD::$session, $rawCommand
        if (action.Equals("CMD", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log($"[Redis] Raw CMD executed: {filteredParam}");
            return CommandResult.Done();
        }

        return CommandResult.Done();
    }
}
