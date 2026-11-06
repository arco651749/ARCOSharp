using System.Threading.Tasks;
using ARCOS.Core.Drivers;
using ARCOS.Core.Guidance;

namespace ARCOS.Driver.MockIO;

/// <summary>
/// MockIO 專屬參數導引插件 (IParameterGuidance)
/// 遵循大一統三元架構：
/// 1. 維度 1 (C# Logic): 透過 Execute / Init 配置驅動
/// 2. 維度 2 (UI FrontPanel): 透過 GetFrontPanelControl() 傳回 MockIoView
/// 3. 維度 3 (Parameter Guidance UI): 透過 ShowGuidanceDialogAsync 彈出 MockIoGuidanceDialog 供視覺化配置
/// </summary>
public class MockIoGuidance : IParameterGuidance
{
    public string CommandKey => "IO@AddIODriver::MockIO";
    public string ParameterFormat => "$io, MockIO, Device=com1, OutStart=P0.0, OutPorts=32, InputStart=P4.0, InPorts=32";
    public string Description => "MockIO 虛擬硬體驅動參數配置精靈 (Driver Parameter Wizard)";

    public Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
    {
        var driver = new MockIoDriver();
        driver.Init("IO1", currentParam, engines);
        return driver.ShowGuidanceDialogAsync(currentParam, engines, owner);
    }

    public object? GetFrontPanelControl()
    {
        var driver = new MockIoDriver();
        return driver.CreateDriverView();
    }
}
