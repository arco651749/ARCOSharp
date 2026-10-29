using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ARCOS.Core.Engines;

public class VariableEntry
{
    public string Key { get; set; } = string.Empty;
    public string RawValue { get; set; } = string.Empty;
    public int CurrentIndex { get; set; } = 0;

    public VariableEntry() { }

    public VariableEntry(string key, string rawValue, int currentIndex = 0)
    {
        Key = key;
        RawValue = rawValue;
        CurrentIndex = currentIndex;
    }

    public List<string> ParseArrayElements()
    {
        if (RawValue.StartsWith('[') && RawValue.EndsWith(']'))
        {
            string content = RawValue.Substring(1, RawValue.Length - 2);
            if (string.IsNullOrEmpty(content)) return new List<string>();
            return content.Split(';').ToList();
        }
        return new List<string> { RawValue };
    }

    public void SetArrayElements(IEnumerable<string> elements)
    {
        RawValue = "[" + string.Join(';', elements) + "]";
    }
}

/// <summary>
/// 多層級動態符號表引擎 (VariableEngine)
/// 支援 Section 作用域 (Global / ThreadX / SubThread)、陣列游標、前置參數過濾與關聯索引搜尋
/// </summary>
public class VariableEngine
{
    private readonly object _lock = new();

    // 分區字典：SectionName -> (Key -> VariableEntry)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, VariableEntry>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    // 跨執行緒 Pub/Sub 事件通知字典
    private readonly ConcurrentDictionary<string, AutoResetEvent> _pubSubEvents = new(StringComparer.OrdinalIgnoreCase);

    // 用於匹配 $(Var@Sec)、$(Var)、$Var@Sec、$Var 的正則表達式
    private static readonly Regex ParamFilterRegex = new(
        @"\$\(([a-zA-Z0-9_#]+)(?:@([a-zA-Z0-9_#]+))?\)" + // $(Var@Sec) 或 $(Var)
        @"|" +
        @"\$([a-zA-Z0-9_#]+)(?:@([a-zA-Z0-9_#]+))?",     // $Var@Sec 或 $Var
        RegexOptions.Compiled);

    public VariableEngine()
    {
        // 預設建立 Global 分區
        _sections["Global"] = new ConcurrentDictionary<string, VariableEntry>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 取得指定 Section 與 Global 登記之所有變數名稱（供導引視窗與 IDE 自動探索感知）
    /// </summary>
    public IReadOnlyList<string> GetAllVariableNames(string? section = null)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(section) && _sections.TryGetValue(section, out var secDict))
        {
            foreach (var k in secDict.Keys) keys.Add(k);
        }
        if (_sections.TryGetValue("Global", out var gDict))
        {
            foreach (var k in gDict.Keys) keys.Add(k);
        }
        return keys.ToList();
    }

    /// <summary>
    /// 讀取變數值（支援 Key@Section；若值為陣列 [A;B;C] 則回傳目前 CurrentIndex 指向的元素）
    /// </summary>
    public string Get(string keyExpr, string currentSection = "Global")
    {
        if (string.IsNullOrWhiteSpace(keyExpr)) return string.Empty;

        var entry = ResolveEntry(keyExpr, currentSection);
        if (entry == null)
        {
            throw new KeyNotFoundException($"Variable '{keyExpr}' was not found in section '{currentSection}' or 'Global'.");
        }

        // 若為陣列，取出目前游標指示的元素
        if (entry.RawValue.StartsWith('[') && entry.RawValue.EndsWith(']'))
        {
            var elements = entry.ParseArrayElements();
            if (elements.Count == 0) return string.Empty;
            int idx = Math.Clamp(entry.CurrentIndex, 0, elements.Count - 1);
            return elements[idx];
        }

        return entry.RawValue;
    }

    /// <summary>
    /// 讀取未拆解的原始字串（供 UI 表格或保存使用）
    /// </summary>
    public string GetRaw(string keyExpr, string currentSection = "Global")
    {
        var entry = ResolveEntry(keyExpr, currentSection);
        if (entry == null)
        {
            throw new KeyNotFoundException($"Variable '{keyExpr}' was not found.");
        }
        return entry.RawValue;
    }

    /// <summary>
    /// 讀取變數值別名方法 (供模組處理器調用)
    /// </summary>
    public string GetVariable(string key, string section = "Global") => Get(key, section);

    /// <summary>
    /// 設定變數值別名方法 (供模組處理器調用)
    /// </summary>
    public void SetVariable(string key, string value, string section = "Global") => Set(key, value, section);

    /// <summary>
    /// 設定變數值並將游標歸 0 (對應 Variable@Set)
    /// </summary>
    public void Set(string key, string value, string targetSection = "Global")
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        string targetSec = targetSection;
        string actualKey = key.Trim();

        // 支援傳入 Key@Section
        int atIdx = actualKey.IndexOf('@');
        if (atIdx > 0)
        {
            targetSec = actualKey.Substring(atIdx + 1).Trim();
            actualKey = actualKey.Substring(0, atIdx).Trim();
        }

        var secDict = _sections.GetOrAdd(targetSec, s => new ConcurrentDictionary<string, VariableEntry>(StringComparer.OrdinalIgnoreCase));
        secDict[actualKey] = new VariableEntry(actualKey, value ?? string.Empty, 0);
    }

    /// <summary>
    /// 刪除指定變數 (對應 Variable@Delete)
    /// </summary>
    public bool Delete(string key, string targetSection = "Global")
    {
        string targetSec = targetSection;
        string actualKey = key.Trim();

        int atIdx = actualKey.IndexOf('@');
        if (atIdx > 0)
        {
            targetSec = actualKey.Substring(atIdx + 1).Trim();
            actualKey = actualKey.Substring(0, atIdx).Trim();
        }

        if (_sections.TryGetValue(targetSec, out var secDict))
        {
            return secDict.TryRemove(actualKey, out _);
        }
        return false;
    }

    /// <summary>
    /// 重新命名變數 (對應 Variable@Rename)
    /// </summary>
    public void Rename(string oldKey, string newKey, string targetSection = "Global")
    {
        if (_sections.TryGetValue(targetSection, out var secDict))
        {
            if (secDict.TryRemove(oldKey, out var entry))
            {
                entry.Key = newKey;
                secDict[newKey] = entry;
            }
        }
    }

    /// <summary>
    /// 清空指定分區
    /// </summary>
    public void ClearSection(string section)
    {
        if (_sections.TryGetValue(section, out var secDict))
        {
            secDict.Clear();
        }
    }

    /// <summary>
    /// 【Param Filter 前置過濾器】將參數字串中的 $(Var@Sec)、$(Var)、$Var@Sec、$Var 查表替換為實際值
    /// </summary>
    public string FilterParameters(string rawParam, string currentSection = "Global")
    {
        if (string.IsNullOrEmpty(rawParam) || !rawParam.Contains('$'))
        {
            return rawParam;
        }

        return ParamFilterRegex.Replace(rawParam, match =>
        {
            string varName = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
            string secName = match.Groups[2].Success ? match.Groups[2].Value :
                            (match.Groups[4].Success ? match.Groups[4].Value : currentSection);

            try
            {
                return Get(varName, string.IsNullOrEmpty(secName) ? currentSection : secName);
            }
            catch
            {
                // 若變數查無此值，保持原樣避免誤替換
                return match.Value;
            }
        });
    }

    /// <summary>
    /// 數值累加與累減 (對應 Variable@Inc / Dec)
    /// </summary>
    public void Inc(string key, double step = 1, string section = "Global")
    {
        lock (_lock)
        {
            string curStr = "";
            try { curStr = Get(key, section); } catch { curStr = "0"; }
            double.TryParse(curStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double num);
            num += step;
            Set(key, num.ToString(CultureInfo.InvariantCulture), section);
        }
    }

    public void Dec(string key, double step = 1, string section = "Global")
    {
        Inc(key, -step, section);
    }

    /// <summary>
    /// 字串尾端拼接 (對應 Variable@Append)
    /// </summary>
    public void Append(string key, string suffix, string section = "Global")
    {
        lock (_lock)
        {
            string cur = "";
            try { cur = Get(key, section); } catch { }
            Set(key, cur + suffix, section);
        }
    }

    /// <summary>
    /// 子字串擷取 (對應 Variable@Trim)
    /// </summary>
    public void Trim(string key, int start, int length, string section = "Global")
    {
        lock (_lock)
        {
            string cur = Get(key, section);
            if (start < 0) start = 0;
            if (start >= cur.Length)
            {
                Set(key, "", section);
                return;
            }
            int actualLen = Math.Min(length, cur.Length - start);
            Set(key, cur.Substring(start, actualLen), section);
        }
    }

    /// <summary>
    /// 字串切割並轉為 [A;B;C] 陣列格式 (對應 Variable@Slice)
    /// </summary>
    public void Slice(string key, string separator, string section = "Global")
    {
        lock (_lock)
        {
            string cur = GetRaw(key, section);
            var parts = cur.Split(new[] { separator }, StringSplitOptions.None);
            Set(key, "[" + string.Join(';', parts) + "]", section);
        }
    }

    /// <summary>
    /// 陣列元素合併為單一字串 (對應 Variable@Joint)
    /// </summary>
    public void Joint(string key, string separator, string section = "Global")
    {
        lock (_lock)
        {
            var entry = ResolveEntry(key, section);
            if (entry != null)
            {
                var elements = entry.ParseArrayElements();
                Set(key, string.Join(separator, elements), section);
            }
        }
    }

    /// <summary>
    /// 設定陣列游標位置 (對應 Variable@LResetIndex)
    /// </summary>
    public void LResetIndex(string key, int index = 0, string section = "Global")
    {
        var entry = ResolveEntry(key, section);
        if (entry != null)
        {
            entry.CurrentIndex = Math.Max(0, index);
        }
    }

    /// <summary>
    /// 修改陣列指定索引元素 (對應 Variable@LSetIndexValue)
    /// </summary>
    public void LSetIndexValue(string key, int index, string val, string section = "Global")
    {
        lock (_lock)
        {
            var entry = ResolveEntry(key, section);
            if (entry != null)
            {
                var elements = entry.ParseArrayElements();
                if (index >= 0 && index < elements.Count)
                {
                    elements[index] = val;
                    entry.SetArrayElements(elements);
                }
            }
        }
    }

    /// <summary>
    /// 陣列游標 +1，未到底回傳 true (供 Thread@ForeachLoop 使用)
    /// </summary>
    public bool LMoveNext(string key, string section = "Global")
    {
        var entry = ResolveEntry(key, section);
        if (entry == null) return false;

        var elements = entry.ParseArrayElements();
        entry.CurrentIndex++;
        return entry.CurrentIndex < elements.Count;
    }

    public void LPush(string key, string val, string section = "Global")
    {
        lock (_lock)
        {
            var entry = ResolveEntry(key, section);
            var elements = entry?.ParseArrayElements() ?? new List<string>();
            elements.Insert(0, val);
            Set(key, "[" + string.Join(';', elements) + "]", section);
        }
    }

    public void RPush(string key, string val, string section = "Global")
    {
        lock (_lock)
        {
            var entry = ResolveEntry(key, section);
            var elements = entry?.ParseArrayElements() ?? new List<string>();
            elements.Add(val);
            Set(key, "[" + string.Join(';', elements) + "]", section);
        }
    }

    public string LPop(string key, string section = "Global")
    {
        lock (_lock)
        {
            var entry = ResolveEntry(key, section);
            if (entry == null) return string.Empty;
            var elements = entry.ParseArrayElements();
            if (elements.Count == 0) return string.Empty;

            string item = elements[0];
            elements.RemoveAt(0);
            entry.SetArrayElements(elements);
            return item;
        }
    }

    public string RPop(string key, string section = "Global")
    {
        lock (_lock)
        {
            var entry = ResolveEntry(key, section);
            if (entry == null) return string.Empty;
            var elements = entry.ParseArrayElements();
            if (elements.Count == 0) return string.Empty;

            int lastIdx = elements.Count - 1;
            string item = elements[lastIdx];
            elements.RemoveAt(lastIdx);
            entry.SetArrayElements(elements);
            return item;
        }
    }

    public void LRemove(string key, string targetVal, string section = "Global")
    {
        lock (_lock)
        {
            var entry = ResolveEntry(key, section);
            if (entry != null)
            {
                var elements = entry.ParseArrayElements();
                elements.RemoveAll(x => string.Equals(x, targetVal, StringComparison.Ordinal));
                entry.SetArrayElements(elements);
            }
        }
    }

    public void LReverse(string key, string section = "Global")
    {
        lock (_lock)
        {
            var entry = ResolveEntry(key, section);
            if (entry != null)
            {
                var elements = entry.ParseArrayElements();
                elements.Reverse();
                entry.SetArrayElements(elements);
            }
        }
    }

    public void LInsert(string key, string value, string condition, string section = "Global")
    {
        lock (_lock)
        {
            var entry = ResolveEntry(key, section);
            if (entry == null)
            {
                Set(key, $"[{value}]", section);
                return;
            }

            var elements = entry.ParseArrayElements();
            string cond = condition.Trim();
            int insertIdx = -1;

            if (cond.StartsWith("Before[", StringComparison.OrdinalIgnoreCase) && cond.EndsWith(']'))
            {
                string target = cond.Substring(7, cond.Length - 8);
                int found = elements.FindIndex(x => string.Equals(x, target, StringComparison.OrdinalIgnoreCase));
                insertIdx = found >= 0 ? found : elements.Count;
            }
            else if (cond.StartsWith("After[", StringComparison.OrdinalIgnoreCase) && cond.EndsWith(']'))
            {
                string target = cond.Substring(6, cond.Length - 7);
                int found = elements.FindIndex(x => string.Equals(x, target, StringComparison.OrdinalIgnoreCase));
                insertIdx = found >= 0 ? found + 1 : elements.Count;
            }
            else
            {
                insertIdx = elements.Count;
            }

            elements.Insert(Math.Clamp(insertIdx, 0, elements.Count), value);
            entry.SetArrayElements(elements);
        }
    }

    public void SAdd(string key, string value, string section = "Global")
    {
        lock (_lock)
        {
            var entry = ResolveEntry(key, section);
            if (entry == null)
            {
                Set(key, $"[{value}]", section);
                return;
            }

            var elements = entry.ParseArrayElements();
            if (!elements.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
            {
                elements.Add(value);
                entry.SetArrayElements(elements);
            }
        }
    }

    public void SUnion(string targetKey, string var1, string var2, string section = "Global")
    {
        lock (_lock)
        {
            var e1 = ResolveEntry(var1, section)?.ParseArrayElements() ?? new List<string>();
            var e2 = ResolveEntry(var2, section)?.ParseArrayElements() ?? new List<string>();
            var result = e1.Union(e2, StringComparer.OrdinalIgnoreCase).ToList();
            Set(targetKey, "[" + string.Join(';', result) + "]", section);
        }
    }

    public void SDiff(string targetKey, string var1, string var2, string section = "Global")
    {
        lock (_lock)
        {
            var e1 = ResolveEntry(var1, section)?.ParseArrayElements() ?? new List<string>();
            var e2 = ResolveEntry(var2, section)?.ParseArrayElements() ?? new List<string>();
            var result = e1.Except(e2, StringComparer.OrdinalIgnoreCase).ToList();
            Set(targetKey, "[" + string.Join(';', result) + "]", section);
        }
    }

    public void SIntersection(string targetKey, string var1, string var2, string section = "Global")
    {
        lock (_lock)
        {
            var e1 = ResolveEntry(var1, section)?.ParseArrayElements() ?? new List<string>();
            var e2 = ResolveEntry(var2, section)?.ParseArrayElements() ?? new List<string>();
            var result = e1.Intersect(e2, StringComparer.OrdinalIgnoreCase).ToList();
            Set(targetKey, "[" + string.Join(';', result) + "]", section);
        }
    }

    public void ShiftVariables(IReadOnlyList<string> vars, int shiftAmount, string section = "Global")
    {
        if (vars.Count <= 1 || shiftAmount == 0) return;

        lock (_lock)
        {
            int n = vars.Count;
            var values = vars.Select(v => Get(v, section)).ToList();
            var shifted = new string[n];

            for (int i = 0; i < n; i++)
            {
                int newIdx = (i + shiftAmount) % n;
                if (newIdx < 0) newIdx += n;
                shifted[newIdx] = values[i];
            }

            for (int i = 0; i < n; i++)
            {
                Set(vars[i], shifted[i], section);
            }
        }
    }

    public void Publish(string varName)
    {
        if (string.IsNullOrWhiteSpace(varName)) return;
        var ev = _pubSubEvents.GetOrAdd(varName.Trim(), _ => new AutoResetEvent(false));
        ev.Set();
    }

    public bool Subscribe(string varName, int timeoutMs = 10000)
    {
        if (string.IsNullOrWhiteSpace(varName)) return false;
        var ev = _pubSubEvents.GetOrAdd(varName.Trim(), _ => new AutoResetEvent(false));
        return ev.WaitOne(timeoutMs);
    }

    public void Flush(string varName)
    {
        if (string.IsNullOrWhiteSpace(varName)) return;
        if (_pubSubEvents.TryGetValue(varName.Trim(), out var ev))
        {
            ev.Reset();
        }
    }

    public (int Index, int Size) LGetIndexSize(string key, string section = "Global")
    {
        var entry = ResolveEntry(key, section);
        if (entry == null) return (0, 0);
        var elements = entry.ParseArrayElements();
        return (entry.CurrentIndex, elements.Count);
    }

    /// <summary>
    /// 【主鍵檢索連動】在 keyArr 陣列中尋找符合 targetVal 的元素索引，命中後同步切換所有 linkedArrs 的游標
    /// </summary>
    public bool LFindIndexSet(string keyArr, string targetVal, IEnumerable<string> linkedArrs, string section = "Global")
    {
        lock (_lock)
        {
            var masterEntry = ResolveEntry(keyArr, section);
            if (masterEntry == null) return false;

            var elements = masterEntry.ParseArrayElements();
            int foundIdx = elements.FindIndex(x => string.Equals(x, targetVal, StringComparison.OrdinalIgnoreCase));

            if (foundIdx >= 0)
            {
                masterEntry.CurrentIndex = foundIdx;
                foreach (var linked in linkedArrs)
                {
                    var childEntry = ResolveEntry(linked, section);
                    if (childEntry != null)
                    {
                        childEntry.CurrentIndex = foundIdx;
                    }
                }
                return true;
            }
            return false;
        }
    }

    public IReadOnlyDictionary<string, string> GetSectionSnapshot(string section)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_sections.TryGetValue(section, out var secDict))
        {
            foreach (var kvp in secDict)
            {
                result[kvp.Key] = kvp.Value.RawValue;
            }
        }
        return result;
    }

    public void SaveToFile(string filePath, IEnumerable<string>? keys = null)
    {
        var sb = new StringBuilder();
        foreach (var secKvp in _sections)
        {
            sb.AppendLine($"[{secKvp.Key}]");
            foreach (var varKvp in secKvp.Value)
            {
                if (keys == null || keys.Contains(varKvp.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"{varKvp.Key}={varKvp.Value.RawValue}");
                }
            }
            sb.AppendLine();
        }
        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    public void LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        string currentSec = "Global";
        foreach (var line in File.ReadAllLines(filePath, Encoding.UTF8))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
                continue;

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentSec = trimmed.Substring(1, trimmed.Length - 2).Trim();
                continue;
            }

            int eqIdx = trimmed.IndexOf('=');
            if (eqIdx > 0)
            {
                string key = trimmed.Substring(0, eqIdx).Trim();
                string val = trimmed.Substring(eqIdx + 1).Trim();
                Set(key, val, currentSec);
            }
        }
    }

    private VariableEntry? ResolveEntry(string keyExpr, string currentSection)
    {
        string key = keyExpr.Trim();

        // 系統預設全域時間變數：$DATE (yyyymmdd) 與 $TIME (hhmmss)
        if (key.Equals("DATE", StringComparison.OrdinalIgnoreCase))
        {
            return new VariableEntry("DATE", DateTime.Now.ToString("yyyyMMdd"));
        }
        if (key.Equals("TIME", StringComparison.OrdinalIgnoreCase))
        {
            return new VariableEntry("TIME", DateTime.Now.ToString("HHmmss"));
        }

        string targetSec = currentSection;

        int atIdx = key.IndexOf('@');
        if (atIdx > 0)
        {
            targetSec = key.Substring(atIdx + 1).Trim();
            key = key.Substring(0, atIdx).Trim();

            if (_sections.TryGetValue(targetSec, out var secDict) && secDict.TryGetValue(key, out var entry))
            {
                return entry;
            }
            return null;
        }

        // 1. 先查當前 Section
        if (!string.IsNullOrEmpty(currentSection) &&
            _sections.TryGetValue(currentSection, out var curSecDict) &&
            curSecDict.TryGetValue(key, out var curEntry))
        {
            return curEntry;
        }

        // 2. 回退查 Global
        if (_sections.TryGetValue("Global", out var globalSecDict) &&
            globalSecDict.TryGetValue(key, out var globalEntry))
        {
            return globalEntry;
        }

        return null;
    }
}
