namespace ARCOS.Core.Drivers;

/// <summary>
/// 通用硬體驅動外掛介面 (IDriver)
/// 封裝「硬體搬運邏輯」與「可選之 WPF 前面板 UI (CreateDriverView)」
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
    /// 建立或取得該 Driver 的 WPF 前面板調試視圖 (UserControl)
    /// 核心庫保持 Headless，回傳 object 供 WPF 宿主以 ContentControl 嵌入顯示
    /// </summary>
    object? CreateDriverView();
}
