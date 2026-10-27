using System.Collections.Concurrent;
using MVCON.Core.Common;

namespace MVCON.Core.Execution.Handlers;

/// <summary>
/// 多工站流水線/旋轉盤狀態管理處理器 (MultiStation@)
/// 提供各站啟用/停用/良品(OK)/不良品(NG)標記、轉盤步進狀態移位 (StateShift) 與即時指示燈同步
/// </summary>
public sealed class MultiStationModuleHandler : IModuleHandler
{
    public string ModuleName => "MultiStation";

    private static readonly string[] Commands = new[]
    {
        "SetStationON",
        "SetStationOFF",
        "SetStationOK",
        "SetStationNG",
        "StateShift",
        "UpdateIndicator",
        "ResetDefault",
        "ChkStationRunState"
    };

    public IReadOnlyList<string> GetSupportedCommands() => Commands;

    // 工站狀態結構
    private sealed class StationState
    {
        public bool IsOn { get; set; }
        public string Result { get; set; } = "NONE"; // NONE, OK, NG
        public bool IsBusy { get; set; }
    }

    private static readonly ConcurrentDictionary<int, StationState> Stations = new();

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(filteredParam, out var posArgs);

        // 1. SetStationON::$stNo
        if (action.Equals("SetStationON", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0 || !int.TryParse(posArgs[0], out int stNo)) return CommandResult.Done();
            var st = Stations.GetOrAdd(stNo, _ => new StationState());
            st.IsOn = true;
            ctx.Engines.Io.SetPort($"Station{stNo}#Run?", true);
            ctx.Log($"[MultiStation] Station {stNo} set to ON");
            return CommandResult.Done();
        }

        // 2. SetStationOFF::$stNo
        if (action.Equals("SetStationOFF", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0 || !int.TryParse(posArgs[0], out int stNo)) return CommandResult.Done();
            var st = Stations.GetOrAdd(stNo, _ => new StationState());
            st.IsOn = false;
            ctx.Engines.Io.SetPort($"Station{stNo}#Run?", false);
            ctx.Log($"[MultiStation] Station {stNo} set to OFF");
            return CommandResult.Done();
        }

        // 3. SetStationOK::$stNo
        if (action.Equals("SetStationOK", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0 || !int.TryParse(posArgs[0], out int stNo)) return CommandResult.Done();
            var st = Stations.GetOrAdd(stNo, _ => new StationState());
            st.Result = "OK";
            ctx.Engines.Io.SetPort($"Station{stNo}#OK?", true);
            ctx.Engines.Io.SetPort($"Station{stNo}#NG?", false);
            ctx.Log($"[MultiStation] Station {stNo} quality marked as OK");
            return CommandResult.Done();
        }

        // 4. SetStationNG::$stNo
        if (action.Equals("SetStationNG", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0 || !int.TryParse(posArgs[0], out int stNo)) return CommandResult.Done();
            var st = Stations.GetOrAdd(stNo, _ => new StationState());
            st.Result = "NG";
            ctx.Engines.Io.SetPort($"Station{stNo}#OK?", false);
            ctx.Engines.Io.SetPort($"Station{stNo}#NG?", true);
            ctx.Log($"[MultiStation] Station {stNo} quality marked as NG");
            return CommandResult.Done();
        }

        // 5. StateShift::$direction
        if (action.Equals("StateShift", StringComparison.OrdinalIgnoreCase))
        {
            int shift = 1;
            if (posArgs.Count > 0)
            {
                string dir = posArgs[0];
                if (int.TryParse(dir, out int s)) shift = s;
                else if (dir.Equals("CCW", StringComparison.OrdinalIgnoreCase) || dir.Equals("Left", StringComparison.OrdinalIgnoreCase)) shift = -1;
            }

            ShiftStations(shift, ctx);
            ctx.Log($"[MultiStation] State shifted by {shift}");
            return CommandResult.Done();
        }

        // 6. UpdateIndicator::;
        if (action.Equals("UpdateIndicator", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var kvp in Stations)
            {
                ctx.Engines.Io.SetPort($"Station{kvp.Key}#Run?", kvp.Value.IsOn);
                ctx.Engines.Io.SetPort($"Station{kvp.Key}#OK?", kvp.Value.Result == "OK");
                ctx.Engines.Io.SetPort($"Station{kvp.Key}#NG?", kvp.Value.Result == "NG");
                ctx.Engines.Io.SetPort($"Station{kvp.Key}#Busy?", kvp.Value.IsBusy);
            }
            ctx.Log("[MultiStation] Indicators updated across all stations.");
            return CommandResult.Done();
        }

        // 7. ResetDefault::;
        if (action.Equals("ResetDefault", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var kvp in Stations)
            {
                kvp.Value.IsOn = false;
                kvp.Value.Result = "NONE";
                kvp.Value.IsBusy = false;
                ctx.Engines.Io.SetPort($"Station{kvp.Key}#Run?", false);
                ctx.Engines.Io.SetPort($"Station{kvp.Key}#OK?", false);
                ctx.Engines.Io.SetPort($"Station{kvp.Key}#NG?", false);
                ctx.Engines.Io.SetPort($"Station{kvp.Key}#Busy?", false);
            }
            ctx.Log("[MultiStation] Reset all stations to default.");
            return CommandResult.Done();
        }

        // 8. ChkStationRunState::$stNo
        if (action.Equals("ChkStationRunState", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0 || !int.TryParse(posArgs[0], out int stNo)) return CommandResult.Done();
            bool isBusy = Stations.TryGetValue(stNo, out var st) && st.IsBusy;
            ctx.Engines.Io.SetPort($"Station{stNo}#Busy?", isBusy);
            ctx.Log($"[MultiStation] Checked station {stNo} Busy={isBusy}");
            return CommandResult.Done();
        }

        return CommandResult.Done();
    }

    private static void ShiftStations(int shift, ExecutionContext ctx)
    {
        if (Stations.IsEmpty) return;
        var keys = Stations.Keys.OrderBy(k => k).ToList();
        if (keys.Count <= 1) return;

        // 依據鍵序建立環狀位移快照
        var snapshot = keys.Select(k => (k, Stations[k].IsOn, Stations[k].Result)).ToList();
        int n = snapshot.Count;

        for (int i = 0; i < n; i++)
        {
            int sourceIdx = (i - shift) % n;
            if (sourceIdx < 0) sourceIdx += n;

            var targetKey = snapshot[i].k;
            var sourceData = snapshot[sourceIdx];

            var targetState = Stations[targetKey];
            targetState.IsOn = sourceData.IsOn;
            targetState.Result = sourceData.Result;

            ctx.Engines.Io.SetPort($"Station{targetKey}#Run?", targetState.IsOn);
            ctx.Engines.Io.SetPort($"Station{targetKey}#OK?", targetState.Result == "OK");
            ctx.Engines.Io.SetPort($"Station{targetKey}#NG?", targetState.Result == "NG");
        }
    }
}
