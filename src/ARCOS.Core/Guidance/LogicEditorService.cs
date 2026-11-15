using System;
using System.Threading.Tasks;
using ARCOS.Core.Drivers;

namespace ARCOS.Core.Guidance;

/// <summary>
/// 跨模組共用的 $logic 視覺編輯器呼叫橋接服務。
/// 由宿主程式 (ARCOS.App) 於啟動時註冊 UI 視窗實作，讓所有動態載入的 .csvi Wizard 皆可直接調用。
/// </summary>
public static class LogicEditorService
{
    /// <summary>
    /// 由宿主應用程式註冊的邏輯編輯器彈窗委派：(currentExpr, engines, owner) -> updatedExpr?
    /// </summary>
    public static Func<string, BasicEngines, object?, Task<string?>>? ShowAsync { get; set; }

    public static bool IsAvailable => ShowAsync != null;

    /// <summary>
    /// 開啟共用邏輯編輯器視窗；若使用者取消或未註冊實作則回傳 null。
    /// </summary>
    public static Task<string?> OpenAsync(string? currentExpr, BasicEngines engines, object? owner = null)
    {
        if (ShowAsync == null)
        {
            return Task.FromResult<string?>(null);
        }
        return ShowAsync(currentExpr ?? string.Empty, engines, owner);
    }
}
