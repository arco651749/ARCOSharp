using System.Threading.Tasks;
using MVCON.Core.Drivers;
using MVCON.Core.Execution;
using ExecutionContext = MVCON.Core.Execution.ExecutionContext;

namespace MVCON.Core.Guidance;

/// <summary>
/// 通用指令參數導引視窗插件介面 (IParameterGuidance)
/// 遵循同進程動態載入規範，直接傳入運行中的 BasicEngines 實例指標
/// </summary>
public interface IParameterGuidance
{
    /// <summary>
    /// 對應的模組指令名稱 (例如 "IO@SetPort", "IO@ClrPort", "Motion@GoPoint")
    /// </summary>
    string CommandKey { get; }

    /// <summary>
    /// 彈出導引對話框並回傳格式化之參數字串 (對標 LabVIEW Front Panel 前面板 UI)
    /// </summary>
    /// <param name="currentParam">目前的參數原始字串</param>
    /// <param name="engines">運行中的基礎引擎實體 (包含 IO, Variable, Point, Image, Daq, Timer)</param>
    /// <param name="owner">宿主視窗 (Avalonia Window 或父控制項)</param>
    /// <returns>使用者確認後組合之參數字串，若取消則回傳 null</returns>
    Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null);

    /// <summary>
    /// 輕量指令業務執行邏輯 (對標 LabVIEW Block Diagram 程式方塊圖)
    /// 供 GuidanceRunner 單獨測試或無微步需求的快速執行使用
    /// </summary>
    /// <param name="engines">基礎引擎實體</param>
    /// <param name="parameter">當前步驟參數</param>
    /// <returns>執行結果 (True 為成功，False 為失敗)</returns>
    bool Execute(BasicEngines engines, string parameter) => false;

    /// <summary>
    /// 全功能指令業務執行邏輯 (支援微步狀態機、逾時等待、錯誤標籤與完整 ExecutionContext)
    /// 預設自動轉發呼叫 Execute(ctx.Engines, parameter)
    /// </summary>
    /// <param name="ctx">主排程執行上下文</param>
    /// <param name="action">子指令名稱 (例如 "SetPort")</param>
    /// <param name="parameter">已過濾之參數字串</param>
    /// <returns>指令執行結果 (Done, Wait, Fail)</returns>
    CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        bool ok = Execute(ctx.Engines, parameter);
        return ok ? CommandResult.Done() : CommandResult.Fail($"Guidance Execute returned false for {action}");
    }

    /// <summary>
    /// 【支柱 3: User Panel / 即時前面板】
    /// 取得該指令/模組的即時前面板控制項 (Avalonia Control)，供主程式動態掛載於 SubPanel 容器
    /// 既可用於主畫面即時監控，亦可用於獨立除錯器或作為驅動人機面板
    /// </summary>
    /// <returns>Avalonia Control (如 UserControl)，若無提供則為 null</returns>
    object? GetFrontPanelControl() => null;
}
