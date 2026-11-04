using System.Collections.Generic;

namespace ARCOS.Core.Execution;

/// <summary>
/// CSVI 外部自訂指令執行介面 (供 Call@<Target>.csvi 調用)
/// 任何放置於 ExternalModules/ 下的獨立 CSVI，實作此介面即可被 Call 模組直譯調度
/// </summary>
public interface ICallCommand
{
    /// <summary>
    /// 執行 CSVI 自訂演算法或外部邏輯
    /// </summary>
    /// <param name="args">輸入參數清單 (已完成 Param Filter)</param>
    /// <param name="ctx">執行緒即時上下文 (可訪問變數表、影像引擎、點位、IO 等)</param>
    /// <returns>回傳數值或物件 (若有指定接收變數，自動寫入 VariableEngine)</returns>
    object? Execute(IReadOnlyList<string> args, ExecutionContext ctx);
}
