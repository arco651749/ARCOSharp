using System;
using System.Collections.Generic;
using System.Linq;
using ARCOS.Core.Model;

namespace ARCOS.Core.Engines;

/// <summary>
/// 單一工位狀態模型（同時映射至 IO 旗標、Variable 隨行資料與綁定 Point&Path 座標）
/// </summary>
public sealed class StationSlotState
{
    public string Name { get; }
    public int Index { get; }
    public bool IsOn { get; set; }
    public string Result { get; set; } = "NONE"; // NONE / OK / NG
    public bool IsBusy { get; set; }
    public Dictionary<string, string> Data { get; } = new(StringComparer.OrdinalIgnoreCase);

    public StationSlotState(string name, int index)
    {
        Name = name;
        Index = index;
    }
}

/// <summary>
/// 多工站群組實例（例如轉盤 FeedInStation 或料盤 TrayA）
/// </summary>
public sealed class StationGroup
{
    public string Name { get; }
    public bool IsLegacyNumeric { get; }
    public string? BoundPointName { get; set; }
    public List<StationSlotState> Slots { get; } = new();

    public StationGroup(string name, bool isLegacyNumeric = false)
    {
        Name = name;
        IsLegacyNumeric = isLegacyNumeric;
    }

    public string GetSlotPrefix(StationSlotState slot)
        => IsLegacyNumeric ? $"Station{slot.Name}" : $"{Name}.{slot.Name}";
}

/// <summary>
/// 多工站協同核心引擎（MultiStationEngine）
/// - 支援命名工位清單（如 station=FeedInStation {items=[入料;加工;檢驗;出料]}）
/// - 支援 1-based 料盤/數量工位與 Point&Path 點位陣列綁定（如 station=TrayA {slots=20, pt=Tray2D}）
/// - 支援「工站.工位」定址（如 FeedInStation.入料、TrayA.3），並 100% 相容舊版純整數工站（Station1#Run?）
/// - 有料/無料/OK/NG/Busy 自動映射至 IoEngine；工位隨行資料自動映射至 VariableEngine 並隨 StateShift 同步搬移
/// </summary>
public class MultiStationEngine
{
    private const string LegacyGroupKey = "__LegacyNumeric";
    private readonly object _lock = new();
    private readonly IoEngine _io;
    private readonly VariableEngine _variable;
    private readonly PointEngine _point;
    private readonly Dictionary<string, StationGroup> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _groupOrder = new();

    public string? DefaultStationName { get; private set; }

    public MultiStationEngine(IoEngine io, VariableEngine variable, PointEngine point)
    {
        _io = io ?? throw new ArgumentNullException(nameof(io));
        _variable = variable ?? throw new ArgumentNullException(nameof(variable));
        _point = point ?? throw new ArgumentNullException(nameof(point));
    }

    public IReadOnlyList<string> GetStationNames()
    {
        lock (_lock)
        {
            return _groupOrder.ToList();
        }
    }

    public StationGroup? GetStation(string stationName)
    {
        if (string.IsNullOrWhiteSpace(stationName)) return null;
        lock (_lock)
        {
            return _groups.TryGetValue(stationName.Trim(), out var g) ? g : null;
        }
    }

    /// <summary>
    /// 建立或重新定義多工站群組（支援命名工位清單 items、1-based 數量 slots、或由綁定 Point&Path 點位陣列自動推斷數量）
    /// </summary>
    public StationGroup AddStation(
        string stationName,
        IReadOnlyList<string>? items = null,
        int? slotCount = null,
        string? pointArrayName = null)
    {
        if (string.IsNullOrWhiteSpace(stationName))
            throw new ArgumentException("Station name cannot be empty.", nameof(stationName));

        string cleanName = stationName.Trim();
        string? boundPt = string.IsNullOrWhiteSpace(pointArrayName) ? null : pointArrayName.Trim().TrimStart('$');

        var slotNames = new List<string>();
        if (items != null && items.Count > 0)
        {
            foreach (var raw in items)
            {
                string s = raw?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(s)) slotNames.Add(s);
            }
        }

        if (slotNames.Count == 0)
        {
            int count = slotCount.GetValueOrDefault(0);
            if (count <= 0 && !string.IsNullOrEmpty(boundPt))
            {
                try
                {
                    count = _point.GetPointArray(boundPt).Count;
                }
                catch
                {
                    count = 0;
                }
            }

            if (count <= 0) count = 1;
            for (int i = 1; i <= count; i++)
            {
                slotNames.Add(i.ToString());
            }
        }

        lock (_lock)
        {
            var group = new StationGroup(cleanName, isLegacyNumeric: false)
            {
                BoundPointName = boundPt
            };

            for (int i = 0; i < slotNames.Count; i++)
            {
                group.Slots.Add(new StationSlotState(slotNames[i], i));
            }

            if (!_groups.ContainsKey(cleanName))
            {
                _groupOrder.Add(cleanName);
            }
            _groups[cleanName] = group;
            if (string.IsNullOrEmpty(DefaultStationName))
            {
                DefaultStationName = cleanName;
            }

            SyncGroupToEngines(group);
            return group;
        }
    }

    public bool SetOn(string target)
    {
        lock (_lock)
        {
            if (!TryResolveSlotLocked(target, out var group, out var slot)) return false;
            slot.IsOn = true;
            SyncSlotToEngines(group, slot);
            return true;
        }
    }

    /// <summary>
    /// 依布林邏輯設定工位有料/無料（on=true 設為有料；on=false 設為無料，且 clearData=true 時清空 OK/NG 與隨行資料）
    /// </summary>
    public bool SetOn(string target, bool on, bool clearData = true)
        => on ? SetOn(target) : SetOff(target, clearData);

    /// <summary>
    /// 標記工位為無料/空位；clearData=true（預設）時一併清空 OK/NG 與該工位所有隨行 Variable 資料
    /// </summary>
    public bool SetOff(string target, bool clearData = true)
    {
        lock (_lock)
        {
            if (!TryResolveSlotLocked(target, out var group, out var slot)) return false;
            slot.IsOn = false;
            if (clearData)
            {
                slot.Result = "NONE";
                var keysToClear = slot.Data.Keys.ToList();
                slot.Data.Clear();
                string prefix = group.GetSlotPrefix(slot);
                foreach (var k in keysToClear)
                {
                    _variable.Set($"{prefix}#{k}", "", "Global");
                }
            }
            SyncSlotToEngines(group, slot);
            return true;
        }
    }

    public bool SetOk(string target)
    {
        lock (_lock)
        {
            if (!TryResolveSlotLocked(target, out var group, out var slot)) return false;
            slot.Result = "OK";
            SyncSlotToEngines(group, slot);
            return true;
        }
    }

    /// <summary>
    /// 依布林邏輯設定工位品質判定（isOk=true 設為 OK；isOk=false 設為 NG）
    /// </summary>
    public bool SetOk(string target, bool isOk)
        => isOk ? SetOk(target) : SetNg(target);

    public bool SetNg(string target)
    {
        lock (_lock)
        {
            if (!TryResolveSlotLocked(target, out var group, out var slot)) return false;
            slot.Result = "NG";
            SyncSlotToEngines(group, slot);
            return true;
        }
    }

    public bool SetBusy(string target, bool isBusy)
    {
        lock (_lock)
        {
            if (!TryResolveSlotLocked(target, out var group, out var slot)) return false;
            slot.IsBusy = isBusy;
            SyncSlotToEngines(group, slot);
            return true;
        }
    }

    public bool CheckRunState(string target, out bool isBusy)
    {
        lock (_lock)
        {
            if (!TryResolveSlotLocked(target, out var group, out var slot))
            {
                isBusy = false;
                return false;
            }
            isBusy = slot.IsBusy;
            string prefix = group.GetSlotPrefix(slot);
            _io.SetPort($"{prefix}#Busy?", isBusy);
            return true;
        }
    }

    public bool SetData(string target, string value, string field = "Data")
    {
        string cleanField = string.IsNullOrWhiteSpace(field) ? "Data" : field.Trim();
        lock (_lock)
        {
            if (!TryResolveSlotLocked(target, out var group, out var slot)) return false;
            slot.Data[cleanField] = value ?? string.Empty;
            string prefix = group.GetSlotPrefix(slot);
            _variable.Set($"{prefix}#{cleanField}", value ?? string.Empty, "Global");
            return true;
        }
    }

    public string GetData(string target, string field = "Data")
    {
        string cleanField = string.IsNullOrWhiteSpace(field) ? "Data" : field.Trim();
        lock (_lock)
        {
            if (!TryResolveSlotLocked(target, out _, out var slot)) return string.Empty;
            return slot.Data.TryGetValue(cleanField, out var val) ? val : string.Empty;
        }
    }

    /// <summary>
    /// 取出指定「工站.工位」對應於綁定 Point&Path 點位陣列之實體座標（依工位順序 0-based 對應）
    /// </summary>
    public bool TryGetSlotPoint(string target, out MachinePoint point)
    {
        point = new MachinePoint();
        lock (_lock)
        {
            if (!TryResolveSlotLocked(target, out var group, out var slot)) return false;
            if (string.IsNullOrWhiteSpace(group.BoundPointName)) return false;

            try
            {
                var pts = _point.GetPointArray(group.BoundPointName);
                if (slot.Index >= 0 && slot.Index < pts.Count)
                {
                    point = pts[slot.Index].Clone();
                    return true;
                }
            }
            catch
            {
                return false;
            }
            return false;
        }
    }

    /// <summary>
    /// 跨工站/跨料盤搬移交接：將來源工位之有料狀態、OK/NG 與全部隨行 Variable 資料整包轉移至目標工位，並可清空來源
    /// </summary>
    public bool Transfer(string fromTarget, string toTarget, bool clearSource = true)
    {
        lock (_lock)
        {
            if (!TryResolveSlotLocked(fromTarget, out var srcGroup, out var srcSlot)) return false;
            if (!TryResolveSlotLocked(toTarget, out var dstGroup, out var dstSlot)) return false;

            var srcFields = srcSlot.Data.Keys.ToList();
            var oldDstFields = dstSlot.Data.Keys.ToList();

            dstSlot.IsOn = srcSlot.IsOn;
            dstSlot.Result = srcSlot.Result;
            dstSlot.Data.Clear();
            foreach (var kv in srcSlot.Data)
            {
                dstSlot.Data[kv.Key] = kv.Value;
            }

            string dstPrefix = dstGroup.GetSlotPrefix(dstSlot);
            foreach (var oldKey in oldDstFields)
            {
                if (!dstSlot.Data.ContainsKey(oldKey))
                {
                    _variable.Set($"{dstPrefix}#{oldKey}", "", "Global");
                }
            }
            SyncSlotToEngines(dstGroup, dstSlot);

            if (clearSource)
            {
                srcSlot.IsOn = false;
                srcSlot.Result = "NONE";
                srcSlot.Data.Clear();
                string srcPrefix = srcGroup.GetSlotPrefix(srcSlot);
                foreach (var k in srcFields)
                {
                    _variable.Set($"{srcPrefix}#{k}", "", "Global");
                }
                SyncSlotToEngines(srcGroup, srcSlot);
            }

            return true;
        }
    }

    /// <summary>
    /// 尋找指定工站/料盤第一個可取料 OK 工位（預設 IsOn &amp;&amp; OK；allowUnchecked=true 時亦接受 IsOn &amp;&amp; !NG），同步更新 $(station)#Found?
    /// </summary>
    public bool TryFindFirstOk(string stationName, bool allowUnchecked, out string slotKey, out MachinePoint? point)
        => TryFindFirstOk(stationName, allowUnchecked, out slotKey, out _, out point);

    /// <summary>
    /// 尋找指定工站/料盤第一個可取料 OK 工位，回傳完整工位鍵與 0-based 索引（可直接供 Point&amp;Path@ResetPalletIndex 使用；未找到時 index=-1），同步更新 $(station)#Found?
    /// </summary>
    public bool TryFindFirstOk(string stationName, bool allowUnchecked, out string slotKey, out int slotIndex, out MachinePoint? point)
    {
        slotKey = string.Empty;
        slotIndex = -1;
        point = null;
        if (string.IsNullOrWhiteSpace(stationName)) return false;

        string cleanName = stationName.Trim();
        lock (_lock)
        {
            if (!_groups.TryGetValue(cleanName, out var group))
            {
                _io.SetPort($"{cleanName}#Found?", false);
                return false;
            }

            foreach (var slot in group.Slots)
            {
                bool match = allowUnchecked
                    ? (slot.IsOn && !slot.Result.Equals("NG", StringComparison.OrdinalIgnoreCase))
                    : (slot.IsOn && slot.Result.Equals("OK", StringComparison.OrdinalIgnoreCase));

                if (match)
                {
                    slotKey = group.GetSlotPrefix(slot);
                    slotIndex = slot.Index;
                    point = TryGetBoundPointLocked(group, slot);
                    _io.SetPort($"{group.Name}#Found?", true);
                    return true;
                }
            }

            _io.SetPort($"{group.Name}#Found?", false);
            return false;
        }
    }

    /// <summary>
    /// 尋找指定工站/出料料盤第一個空位（!IsOn），同步更新 $(station)#Found?
    /// </summary>
    public bool TryFindEmpty(string stationName, out string slotKey, out MachinePoint? point)
        => TryFindEmpty(stationName, out slotKey, out _, out point);

    /// <summary>
    /// 尋找指定工站/出料料盤第一個空位（!IsOn），回傳完整工位鍵與 0-based 索引（可直接供 Point&amp;Path@ResetPalletIndex 使用；未找到時 index=-1），同步更新 $(station)#Found?
    /// </summary>
    public bool TryFindEmpty(string stationName, out string slotKey, out int slotIndex, out MachinePoint? point)
    {
        slotKey = string.Empty;
        slotIndex = -1;
        point = null;
        if (string.IsNullOrWhiteSpace(stationName)) return false;

        string cleanName = stationName.Trim();
        lock (_lock)
        {
            if (!_groups.TryGetValue(cleanName, out var group))
            {
                _io.SetPort($"{cleanName}#Found?", false);
                return false;
            }

            foreach (var slot in group.Slots)
            {
                if (!slot.IsOn)
                {
                    slotKey = group.GetSlotPrefix(slot);
                    slotIndex = slot.Index;
                    point = TryGetBoundPointLocked(group, slot);
                    _io.SetPort($"{group.Name}#Found?", true);
                    return true;
                }
            }

            _io.SetPort($"{group.Name}#Found?", false);
            return false;
        }
    }

    private MachinePoint? TryGetBoundPointLocked(StationGroup group, StationSlotState slot)
    {
        if (string.IsNullOrWhiteSpace(group.BoundPointName)) return null;
        try
        {
            var pts = _point.GetPointArray(group.BoundPointName);
            if (slot.Index >= 0 && slot.Index < pts.Count)
            {
                return pts[slot.Index].Clone();
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 環形緩衝區狀態轉移（支援正數向前、負數向後位移指定格數），同步搬移 IO 狀態與全部隨行 Variable 資料。
    /// 直線輸送帶情境只需搭配 SetStationOFF 清除頭端或尾端工位即可。
    /// </summary>
    public void StateShift(string? stationName, int shift)
    {
        lock (_lock)
        {
            var targets = SelectTargetGroupsLocked(stationName);
            foreach (var group in targets)
            {
                int n = group.Slots.Count;
                if (n <= 1) continue;

                // 收集該工站所有使用過之隨行資料欄位，確保移位後原工位不再殘留舊資料
                var allFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var snapshot = new List<(bool IsOn, string Result, Dictionary<string, string> Data)>(n);
                foreach (var s in group.Slots)
                {
                    foreach (var k in s.Data.Keys) allFields.Add(k);
                    snapshot.Add((s.IsOn, s.Result, new Dictionary<string, string>(s.Data, StringComparer.OrdinalIgnoreCase)));
                }

                for (int i = 0; i < n; i++)
                {
                    int srcIdx = (i - shift) % n;
                    if (srcIdx < 0) srcIdx += n;
                    var src = snapshot[srcIdx];
                    var dst = group.Slots[i];

                    dst.IsOn = src.IsOn;
                    dst.Result = src.Result;
                    dst.Data.Clear();
                    foreach (var kv in src.Data)
                    {
                        dst.Data[kv.Key] = kv.Value;
                    }

                    SyncSlotToEngines(group, dst);

                    string prefix = group.GetSlotPrefix(dst);
                    foreach (var field in allFields)
                    {
                        string v = dst.Data.TryGetValue(field, out var fv) ? fv : string.Empty;
                        _variable.Set($"{prefix}#{field}", v, "Global");
                    }
                }
            }
        }
    }

    public void ResetDefault(string? stationName = null)
    {
        lock (_lock)
        {
            var targets = SelectTargetGroupsLocked(stationName);
            foreach (var group in targets)
            {
                foreach (var slot in group.Slots)
                {
                    slot.IsOn = false;
                    slot.Result = "NONE";
                    slot.IsBusy = false;
                    var fields = slot.Data.Keys.ToList();
                    slot.Data.Clear();
                    string prefix = group.GetSlotPrefix(slot);
                    foreach (var f in fields)
                    {
                        _variable.Set($"{prefix}#{f}", "", "Global");
                    }
                    SyncSlotToEngines(group, slot);
                }
            }
        }
    }

    public void UpdateIndicator(string? stationName = null)
    {
        lock (_lock)
        {
            var targets = SelectTargetGroupsLocked(stationName);
            foreach (var group in targets)
            {
                SyncGroupToEngines(group);
            }
        }
    }

    private List<StationGroup> SelectTargetGroupsLocked(string? stationName)
    {
        if (!string.IsNullOrWhiteSpace(stationName))
        {
            string clean = stationName.Trim();
            int dot = clean.IndexOf('.');
            if (dot > 0) clean = clean.Substring(0, dot).Trim();
            return _groups.TryGetValue(clean, out var g) ? new List<StationGroup> { g } : new List<StationGroup>();
        }
        return _groupOrder.Select(k => _groups[k]).ToList();
    }

    private bool TryResolveSlotLocked(string target, out StationGroup group, out StationSlotState slot)
    {
        group = null!;
        slot = null!;
        if (string.IsNullOrWhiteSpace(target)) return false;

        string t = target.Trim();
        int dot = t.IndexOf('.');
        if (dot > 0 && dot < t.Length - 1)
        {
            string stName = t.Substring(0, dot).Trim();
            string slotKey = t.Substring(dot + 1).Trim();
            if (!_groups.TryGetValue(stName, out group!))
            {
                group = new StationGroup(stName, isLegacyNumeric: false);
                _groups[stName] = group;
                _groupOrder.Add(stName);
                if (string.IsNullOrEmpty(DefaultStationName)) DefaultStationName = stName;
            }

            // 1. 先精準比對工位名稱（支援命名工位如 "入料" 或數字工位 "3"）
            var found = group.Slots.FirstOrDefault(s => s.Name.Equals(slotKey, StringComparison.OrdinalIgnoreCase));
            if (found != null)
            {
                slot = found;
                return true;
            }

            // 2. 若為正整數 1-based 索引，對應第 k 個工位
            if (int.TryParse(slotKey, out int idx1) && idx1 >= 1)
            {
                if (idx1 <= group.Slots.Count)
                {
                    slot = group.Slots[idx1 - 1];
                    return true;
                }
                // 尚未透過 AddStation 預建時自動補齊至 idx1（1-based）
                while (group.Slots.Count < idx1)
                {
                    int nextNum = group.Slots.Count + 1;
                    group.Slots.Add(new StationSlotState(nextNum.ToString(), nextNum - 1));
                }
                slot = group.Slots[idx1 - 1];
                return true;
            }

            // 3. 尚未註冊的具名工位動態附加
            slot = new StationSlotState(slotKey, group.Slots.Count);
            group.Slots.Add(slot);
            return true;
        }

        // 舊版相容：純整數工站編號（如 "1"、"2" -> Station1#Run?）
        if (int.TryParse(t, out int legacyNo))
        {
            if (!_groups.TryGetValue(LegacyGroupKey, out group!))
            {
                group = new StationGroup(LegacyGroupKey, isLegacyNumeric: true);
                _groups[LegacyGroupKey] = group;
                _groupOrder.Add(LegacyGroupKey);
            }

            string key = legacyNo.ToString();
            var existing = group.Slots.FirstOrDefault(s => s.Name == key);
            if (existing == null)
            {
                existing = new StationSlotState(key, group.Slots.Count);
                group.Slots.Add(existing);
                // 保持舊版依整數大小升冪排列，確保 StateShift 順序一致
                group.Slots.Sort((a, b) => int.Parse(a.Name).CompareTo(int.Parse(b.Name)));
            }
            slot = existing;
            return true;
        }

        return false;
    }

    private void SyncGroupToEngines(StationGroup group)
    {
        foreach (var slot in group.Slots)
        {
            SyncSlotToEngines(group, slot);
        }
    }

    private void SyncSlotToEngines(StationGroup group, StationSlotState slot)
    {
        string prefix = group.GetSlotPrefix(slot);
        if (!group.IsLegacyNumeric)
        {
            _io.SetPort($"{prefix}#On?", slot.IsOn);
        }
        _io.SetPort($"{prefix}#Run?", slot.IsOn);
        _io.SetPort($"{prefix}#OK?", slot.Result.Equals("OK", StringComparison.OrdinalIgnoreCase));
        _io.SetPort($"{prefix}#NG?", slot.Result.Equals("NG", StringComparison.OrdinalIgnoreCase));
        _io.SetPort($"{prefix}#Busy?", slot.IsBusy);

        foreach (var kv in slot.Data)
        {
            _variable.Set($"{prefix}#{kv.Key}", kv.Value, "Global");
        }
    }
}
