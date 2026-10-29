namespace ARCOS.Core.Execution;

/// <summary>
/// 模組指令直譯處理器介面
/// 各模組 (System@, Thread@, IO@, Motion@, Point&Path@, Variable@, Capture@, DAQ@, User@, Call@) 實作此介面
/// </summary>
public interface IModuleHandler
{
    /// <summary>
    /// 模組名稱 (如 "IO", "Motion", "System", "Thread")
    /// </summary>
    string ModuleName { get; }

    /// <summary>
    /// 取得該模組支援的所有指令動作清單 (供 *@getCMD 自動探索與 IDE 自動補全)
    /// </summary>
    IReadOnlyList<string> GetSupportedCommands();

    /// <summary>
    /// 執行單一指令動作
    /// </summary>
    /// <param name="action">動作名稱 (如 "SetPort", "GoPoint", "Delay")</param>
    /// <param name="filteredParam">已完成變數替換 (Param Filter) 的參數字串</param>
    /// <param name="ctx">執行緒即時上下文 (包含 PC、微步 State、Engine 參照)</param>
    /// <returns>執行結果 (Completed, Waiting, Jump, Error 等)</returns>
    CommandResult Execute(string action, string filteredParam, ExecutionContext ctx);
}
