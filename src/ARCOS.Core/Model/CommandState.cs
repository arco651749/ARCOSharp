using System.Text.Json.Nodes;

namespace ARCOS.Core.Model;

/// <summary>
/// 雙模態指令微步狀態機與暫存上下文 (Command Execution Micro-State &amp; Transient Scratchpad)
/// 平時作為 4-byte i32 (Value) 記錄毫秒數或步序，零記憶體分配；
/// 需時透過索引子存取 JSON 字典暫存多步交握上下文，並在指令完成時自動 Reset 釋放。
/// </summary>
public class CommandState
{
    /// <summary>
    /// 原生 i32 數值：通常承載經過之毫秒數 (ms) 或狀態機步驟序號 (Step)
    /// </summary>
    public int Value { get; set; } = 0;

    /// <summary>
    /// 惰性初始化的 JSON 暫存字典
    /// </summary>
    private JsonObject? _transientData;

    /// <summary>
    /// 取得暫存 JSON 物件（若為 null 則按需實例化）
    /// </summary>
    public JsonObject Data => _transientData ??= new JsonObject();

    /// <summary>
    /// 是否包含非空的暫存資料
    /// </summary>
    public bool HasData => _transientData != null && _transientData.Count > 0;

    /// <summary>
    /// 隱式轉換為 int，支援像 int 一樣直接參與數值比較或賦值
    /// </summary>
    public static implicit operator int(CommandState? s) => s?.Value ?? 0;

    /// <summary>
    /// 隱式由 int 建立 CommandState
    /// </summary>
    public static implicit operator CommandState(int val) => new() { Value = val };

    /// <summary>
    /// 索引子存取暫存 Key-Value 資料
    /// </summary>
    public JsonNode? this[string key]
    {
        get => _transientData?[key];
        set => Data[key] = value;
    }

    /// <summary>
    /// 取得指定型別的暫存資料
    /// </summary>
    public T? Get<T>(string key, T? defaultValue = default)
    {
        if (_transientData != null && _transientData.TryGetPropertyValue(key, out var node) && node != null)
        {
            try
            {
                return node.GetValue<T>();
            }
            catch
            {
                return defaultValue;
            }
        }
        return defaultValue;
    }

    /// <summary>
    /// 重設狀態：數值歸 0 且釋放單步微步暫存（保留跨行工作階段標記，如 AIAgent_）
    /// </summary>
    public void Reset()
    {
        Value = 0;
        if (_transientData != null)
        {
            var persistentEntries = _transientData
                .Where(kvp => kvp.Key.StartsWith("AIAgent_", StringComparison.OrdinalIgnoreCase))
                .Select(kvp => (kvp.Key, Value: kvp.Value?.DeepClone()))
                .ToList();

            _transientData.Clear();

            if (persistentEntries.Count > 0)
            {
                foreach (var entry in persistentEntries)
                {
                    _transientData[entry.Key] = entry.Value;
                }
            }
            else
            {
                _transientData = null;
            }
        }
    }

    public override string ToString()
    {
        return HasData ? $"{Value} | {_transientData!.ToJsonString()}" : Value.ToString();
    }
}
