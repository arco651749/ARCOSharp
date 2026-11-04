using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ARCOS.Core.Drivers;
using ARCOS.Core.Guidance;

namespace ExternalModules.MyCsvi;

/// <summary>
/// mycsvi.csvi 專屬參數引導插件 (IParameterGuidance)
/// 供 ARCOSharp 在雙擊或選取 mycsvi.csvi 時彈出專屬參數調配 UI
/// </summary>
public class MyCsviGuidance : IParameterGuidance
{
    public string CommandKey => "Call@mycsvi.csvi";
    public string ParameterFormat => "$CameraOrVal1, $ThresholdOrVal2(, $MaxThreshold), $ReturnVar";
    public string Description => "專案客製 AOI 直方圖分析與數值計算工具 (mycsvi.csvi)";

    public async Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
    {
        var windowOwner = owner as Window;
        var dialog = new MyCsviGuidanceDialog(currentParam, engines);
        if (windowOwner != null)
        {
            return await dialog.ShowDialog<string?>(windowOwner);
        }
        else
        {
            dialog.Show();
            return null;
        }
    }

    public object? GetFrontPanelControl() => null;
}
