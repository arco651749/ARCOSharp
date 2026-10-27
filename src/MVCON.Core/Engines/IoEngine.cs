using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MVCON.Core.Model;

namespace MVCON.Core.Engines;

/// <summary>
/// 動態無限 IO 池引擎 (IoEngine)
/// 管理實體點位 (Pn.m)、別名對應、系統虛擬 IO (如 Cam0#OK?) 與常數 (True/False)
/// 具備執行緒安全、硬體批次搬運與狀態變更廣播
/// </summary>
public class IoEngine
{
    private readonly object _lock = new();

    // 實體點位池：以 (PortIndex, BitIndex) 索引，預設 P0.0 ~ P63.7，超過自動動態擴充
    private readonly ConcurrentDictionary<(int Port, int Bit), bool> _physicalPorts = new();

    // 實體點與別名的雙向映射
    private readonly ConcurrentDictionary<string, string> _aliasToPhysical = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _physicalToAlias = new(StringComparer.OrdinalIgnoreCase);

    // 虛擬 IO 字典 (包含 # 系統旗標，如 ThreadMain#Run?)
    private readonly ConcurrentDictionary<string, bool> _virtualIos = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex PhysicalPortRegex = new(@"^[pP](\d+)\.(\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// 當點位狀態實際改變 (old != new) 時觸發廣播：(Port, Alias, NewValue)
    /// </summary>
    public event Action<string, string, bool>? IoChanged;

    public IoEngine()
    {
        // 預先初始化 512 個實體 IO 點位 (P0.0 ~ P63.7，共 64 Ports x 8 Bits)
        for (int port = 0; port < 64; port++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                _physicalPorts[(port, bit)] = false;
            }
        }

        // 預先初始化常數虛擬 IO 與系統旗標
        _virtualIos["TRUE"] = true;
        _virtualIos["FALSE"] = false;
        _virtualIos["System#Pause?"] = false;
        _virtualIos["System#Run?"] = false;
    }

    /// <summary>
    /// 單點讀取：支援 True/False 常數、別名、實體點位與虛擬 IO
    /// </summary>
    public bool GetPort(string nameOrPort)
    {
        if (string.IsNullOrWhiteSpace(nameOrPort))
            throw new ArgumentException("IO name cannot be null or empty.", nameof(nameOrPort));

        string key = nameOrPort.Trim();

        // 1. 常數判斷
        if (string.Equals(key, "True", StringComparison.OrdinalIgnoreCase) || key == "1") return true;
        if (string.Equals(key, "False", StringComparison.OrdinalIgnoreCase) || key == "0") return false;

        // 2. 虛擬 IO 優先判斷（含 # 或已登記的虛擬 IO）
        if (_virtualIos.TryGetValue(key, out bool vVal))
        {
            return vVal;
        }

        // 3. 別名轉實體點
        if (_aliasToPhysical.TryGetValue(key, out string? phys))
        {
            return ReadPhysical(phys);
        }

        // 4. 直接作為實體點位
        if (PhysicalPortRegex.IsMatch(key))
        {
            return ReadPhysical(key);
        }

        // 5. 若是虛擬標誌但尚未初始化，或動態虛擬點位，預設為 false
        return false;
    }

    /// <summary>
    /// 單點寫入：若為 # 系統旗標且不存在則自動建立；"True"/"False" 常數忽略寫入
    /// </summary>
    public void SetPort(string nameOrPort, bool value)
    {
        if (string.IsNullOrWhiteSpace(nameOrPort)) return;
        string key = nameOrPort.Trim();

        // 唯讀常數直接忽略
        if (string.Equals(key, "True", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "False", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_lock)
        {
            // 1. 虛擬 IO 或帶 # 的系統旗標
            if (key.Contains('#') || _virtualIos.ContainsKey(key))
            {
                bool oldVal = _virtualIos.TryGetValue(key, out bool cur) ? cur : false;
                _virtualIos[key] = value;
                if (oldVal != value)
                {
                    IoChanged?.Invoke(key, string.Empty, value);
                }
                return;
            }

            // 2. 別名轉換為實體點
            string targetPort = key;
            string targetAlias = string.Empty;

            if (_aliasToPhysical.TryGetValue(key, out string? mappedPort))
            {
                targetPort = mappedPort;
                targetAlias = key;
            }
            else if (_physicalToAlias.TryGetValue(key, out string? mappedAlias))
            {
                targetAlias = mappedAlias;
            }

            // 3. 寫入實體點位
            if (TryParsePhysicalPort(targetPort, out int port, out int bit))
            {
                var coord = (port, bit);
                bool oldVal = _physicalPorts.TryGetValue(coord, out bool cur) ? cur : false;
                _physicalPorts[coord] = value;

                if (oldVal != value)
                {
                    IoChanged?.Invoke(targetPort, targetAlias, value);
                }
            }
            else
            {
                // 若既不是合法的實體點，也不是已知的別名，作為動態虛擬 IO 存入
                bool oldVal = _virtualIos.TryGetValue(key, out bool cur) ? cur : false;
                _virtualIos[key] = value;
                if (oldVal != value)
                {
                    IoChanged?.Invoke(key, string.Empty, value);
                }
            }
        }
    }

    /// <summary>
    /// 綁定實體點位與別名 (對應 IO@SetPortName)
    /// </summary>
    public void SetPortName(string physicalPort, string aliasName)
    {
        if (string.IsNullOrWhiteSpace(physicalPort) || string.IsNullOrWhiteSpace(aliasName)) return;

        string port = physicalPort.Trim();
        string alias = aliasName.Trim();

        lock (_lock)
        {
            _aliasToPhysical[alias] = port;
            _physicalToAlias[port] = alias;
        }
    }

    /// <summary>
    /// 新增或覆寫虛擬 IO (對應 IO@AddVirtualIO)
    /// </summary>
    public void AddVirtualIO(string name, bool initialValue)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string key = name.Trim();

        lock (_lock)
        {
            bool oldVal = _virtualIos.TryGetValue(key, out bool cur) ? cur : !initialValue;
            _virtualIos[key] = initialValue;

            if (oldVal != initialValue)
            {
                IoChanged?.Invoke(key, string.Empty, initialValue);
            }
        }
    }

    /// <summary>
    /// 批次移位一組 IO 狀態，支援正負方向 (對應 IO@Shift)
    /// </summary>
    public void Shift(IReadOnlyList<string> ioNames, int shiftCount)
    {
        if (ioNames == null || ioNames.Count <= 1 || shiftCount == 0) return;

        lock (_lock)
        {
            int count = ioNames.Count;
            bool[] currentValues = new bool[count];
            for (int i = 0; i < count; i++)
            {
                currentValues[i] = GetPort(ioNames[i]);
            }

            bool[] newValues = new bool[count];
            for (int i = 0; i < count; i++)
            {
                int srcIdx = i - shiftCount;
                if (srcIdx >= 0 && srcIdx < count)
                {
                    newValues[i] = currentValues[srcIdx];
                }
                else
                {
                    newValues[i] = false; // 移出邊界補 false
                }
            }

            for (int i = 0; i < count; i++)
            {
                SetPort(ioNames[i], newValues[i]);
            }
        }
    }

    /// <summary>
    /// 【Driver Output 搬運】從起始點連續批次取出 bitCount 個布林值
    /// </summary>
    public bool[] GetBatch(string startPort, int bitCount)
    {
        if (bitCount <= 0) return Array.Empty<bool>();

        if (!TryParsePhysicalPort(startPort, out int startPortIdx, out int startBitIdx))
        {
            throw new ArgumentException($"Invalid physical start port format: '{startPort}'. Expected 'Pn.m'.");
        }

        bool[] result = new bool[bitCount];
        int currentPort = startPortIdx;
        int currentBit = startBitIdx;

        for (int i = 0; i < bitCount; i++)
        {
            result[i] = _physicalPorts.TryGetValue((currentPort, currentBit), out bool val) && val;

            currentBit++;
            if (currentBit >= 8)
            {
                currentBit = 0;
                currentPort++;
            }
        }

        return result;
    }

    /// <summary>
    /// 【Driver Input 搬運】從起始點一次整批寫入硬體讀回的布林陣列
    /// </summary>
    public void SetBatch(string startPort, ReadOnlySpan<bool> values)
    {
        if (values.IsEmpty) return;

        if (!TryParsePhysicalPort(startPort, out int startPortIdx, out int startBitIdx))
        {
            throw new ArgumentException($"Invalid physical start port format: '{startPort}'. Expected 'Pn.m'.");
        }

        int currentPort = startPortIdx;
        int currentBit = startBitIdx;

        lock (_lock)
        {
            for (int i = 0; i < values.Length; i++)
            {
                var coord = (currentPort, currentBit);
                bool newVal = values[i];
                bool oldVal = _physicalPorts.TryGetValue(coord, out bool cur) && cur;
                _physicalPorts[coord] = newVal;

                if (oldVal != newVal)
                {
                    string portName = $"P{currentPort}.{currentBit}";
                    _physicalToAlias.TryGetValue(portName, out string? alias);
                    IoChanged?.Invoke(portName, alias ?? string.Empty, newVal);
                }

                currentBit++;
                if (currentBit >= 8)
                {
                    currentBit = 0;
                    currentPort++;
                }
            }
        }
    }

    /// <summary>
    /// 依名稱清單批次讀取多個點位狀態
    /// </summary>
    public Dictionary<string, bool> GetPortsBatch(IEnumerable<string> names)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            try
            {
                result[name] = GetPort(name);
            }
            catch
            {
                result[name] = false;
            }
        }
        return result;
    }

    /// <summary>
    /// 取得所有可用的 PortName 清單 (包含使用者別名、系統虛擬點位與實體點位 P0.0~Pn.m)
    /// </summary>
    public IReadOnlyList<string> GetAllPortNames()
    {
        var names = new List<string>();

        // 1. 使用者自訂別名優先置頂 (例如: Cylinder_Extend, Vacuum_1)
        foreach (var alias in _aliasToPhysical.Keys.OrderBy(a => a))
        {
            names.Add(alias);
        }

        // 2. 常用實體點位 (P0.0 ~ P7.7，共 64 點常用標準點位)
        for (int p = 0; p < 8; p++)
        {
            for (int b = 0; b < 8; b++)
            {
                string portStr = $"P{p}.{b}";
                if (!names.Contains(portStr))
                {
                    names.Add(portStr);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// 取得所有已登記的點位快照（供 UI 列表與 Telnet $GetIO 使用）
    /// </summary>
    public IReadOnlyList<IoPortSnapshot> GetAllSnapshot()
    {
        var list = new List<IoPortSnapshot>();

        // 1. 常駐虛擬 IO (TRUE 永真, FALSE 永假) 與其他系統旗標
        list.Add(new IoPortSnapshot("TRUE", "Logic HIGH (1)", true, true));
        list.Add(new IoPortSnapshot("FALSE", "Logic LOW (0)", false, true));
        foreach (var kvp in _virtualIos)
        {
            if (string.Equals(kvp.Key, "TRUE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "FALSE", StringComparison.OrdinalIgnoreCase))
                continue;
            list.Add(new IoPortSnapshot(kvp.Key, string.Empty, kvp.Value, true));
        }

        // 2. 實體點位 (依 Port 與 Bit 由小到大排序輸出)
        var sortedKeys = _physicalPorts.Keys.OrderBy(k => k.Port).ThenBy(k => k.Bit);
        foreach (var key in sortedKeys)
        {
            string portStr = $"P{key.Port}.{key.Bit}";
            _physicalToAlias.TryGetValue(portStr, out string? alias);
            list.Add(new IoPortSnapshot(portStr, alias ?? string.Empty, _physicalPorts[key], false));
        }

        return list;
    }

    private bool ReadPhysical(string portStr)
    {
        if (TryParsePhysicalPort(portStr, out int port, out int bit))
        {
            return _physicalPorts.TryGetValue((port, bit), out bool val) && val;
        }
        return false;
    }

    public static bool TryParsePhysicalPort(string str, out int port, out int bit)
    {
        port = 0;
        bit = 0;
        var match = PhysicalPortRegex.Match(str);
        if (!match.Success) return false;

        port = int.Parse(match.Groups[1].Value);
        bit = int.Parse(match.Groups[2].Value);
        return true;
    }
}
