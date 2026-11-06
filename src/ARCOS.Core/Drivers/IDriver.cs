namespace ARCOS.Core.Drivers;

/// <summary>
/// 通用硬體驅動外掛介面 (IDriver)
/// 遵循大一統三元架構 (Trinity Architecture)：
/// 1. 維度 1 (C# Code): 硬體搬運邏輯 (Init, Shutdown, PostTicket)
/// 2. 維度 2 (UI FrontPanel): 即時監控前面板調試視圖 (CreateDriverView)
/// 3. 維度 3 (Parameter Guidance UI): 驅動專屬參數配置導引視窗 (ShowGuidanceDialogAsync)
/// </summary>
public interface IDriver : IDisposable
{
    /// <summary>
    /// 設備自訂名稱 (如 IO1, Station1, Cam0)
    /// </summary>
    string DeviceName { get; }

    /// <summary>
    /// 驅動類別 ("IO", "Motion", "Capture", "DAQ", "Custom")
    /// </summary>
    string Category { get; }

    /// <summary>
    /// 驅動型態名稱 (如 "MockIO", "MockMotion", "ADLINK8164")
    /// </summary>
    string DriverType { get; }

    /// <summary>
    /// 初始化驅動並注入第一階段 Basic Engines
    /// </summary>
    void Init(string deviceName, string param, BasicEngines engines);

    /// <summary>
    /// 停止驅動並釋放硬體通訊與背景執行緒
    /// </summary>
    void Shutdown();

    /// <summary>
    /// 發送私有信號通訊票券至 Driver 的工作佇列
    /// </summary>
    void PostTicket(DriverTicket ticket);

    /// <summary>
    /// 【維度 2: UI 前面板】建立或取得該 Driver 的 WPF/Avalonia 前面板調試視圖 (UserControl)
    /// 核心庫保持 Headless，回傳 object 供宿主以 ContentControl 嵌入顯示
    /// </summary>
    object? CreateDriverView();

    /// <summary>
    /// 【維度 3: 驅動參數導引視窗 / Wizard】
    /// 彈出該 Driver 的專屬參數配置導引對話框 (供 IO@AddIODriver 等指令選取此驅動時視覺化調配參數)
    /// </summary>
    /// <param name="currentParam">目前的參數原始字串</param>
    /// <param name="engines">基礎引擎實體</param>
    /// <param name="owner">宿主視窗 (Avalonia Window 或父控制項)</param>
    /// <returns>使用者確認後組合之參數字串，若取消則回傳 null</returns>
    Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null) =>
        Task.FromResult<string?>(null);
}
