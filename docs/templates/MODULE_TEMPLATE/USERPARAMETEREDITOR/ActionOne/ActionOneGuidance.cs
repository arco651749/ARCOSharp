using System.Threading.Tasks;
using Avalonia.Controls;
using ARCOS.Core.Drivers;

namespace ARCOS.Guidance.ActionOne;

/// <summary>
/// 雙模導引視窗進入點 (對標 LabVIEW 動態呼叫 VI：傳遞 live BasicEngines)
/// </summary>
public class ActionOneGuidance
{
    public string CommandKey => "Template@ActionOne";

    public async Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, Window owner)
    {
        // 1. 直接引用 Basic Engine 函數檢索動態資源
        var livePorts = engines.Io.GetAllPortNames();
        var liveVars = engines.Variable.GetAllVariableNames();

        // 2. 顯示視窗並回傳拼接參數
        var dialog = new ActionOneDialog(currentParam, livePorts);
        return await dialog.ShowDialog<string?>(owner);
    }
}
