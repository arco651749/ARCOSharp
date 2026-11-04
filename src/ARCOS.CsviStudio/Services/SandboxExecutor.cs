using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using ARCOS.Core.Execution;
using ARCOS.Core.Guidance;
using ARCOS.Core.Model;
using CoreExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.CsviStudio.Services;

public class SandboxExecutionResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string? Error { get; set; }
    public double ElapsedMilliseconds { get; set; }
}

/// <summary>
/// 隔離可卸載之沙盒執行器 (使用 Collectible AssemblyLoadContext，非鎖檔熱測試)
/// 支援 IModuleHandler、IParameterGuidance 與 IDriver 三種 CSVI 組件類型
/// </summary>
public class SandboxExecutor
{
    private class CollectibleAssemblyLoadContext : AssemblyLoadContext
    {
        public CollectibleAssemblyLoadContext() : base(isCollectible: true) { }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            return null;
        }
    }

    public SandboxExecutionResult Execute(byte[] assemblyBytes, string commandKey, string parameter)
    {
        var result = new SandboxExecutionResult();
        var sw = Stopwatch.StartNew();

        var alc = new CollectibleAssemblyLoadContext();
        try
        {
            using var ms = new MemoryStream(assemblyBytes);
            var asm = alc.LoadFromStream(ms);

            var engines = new BasicEngines();
            var drivers = new DriverManager();
            var program = new ProgramTable();
            var context = new CoreExecutionContext(0, "SandboxThread", program, engines, drivers);
            string action = commandKey.Contains('@') ? commandKey.Split('@')[1] : commandKey;

            // 1. 優先尋找實作 IModuleHandler 的型別
            var handlerType = asm.GetTypes().FirstOrDefault(t => typeof(IModuleHandler).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
            if (handlerType != null && Activator.CreateInstance(handlerType) is IModuleHandler handler)
            {
                var subSw = Stopwatch.StartNew();
                CommandResult cmdRes = handler.Execute(action, parameter, context);
                subSw.Stop();

                sw.Stop();
                result.Success = cmdRes.Status == CommandStatus.Completed || cmdRes.Status == CommandStatus.Jump || cmdRes.Status == CommandStatus.Waiting;
                result.ElapsedMilliseconds = subSw.Elapsed.TotalMilliseconds;
                result.Output = $"[IModuleHandler] Action: {action}, Param: '{parameter}', Status: {cmdRes.Status}";
                if (cmdRes.Status == CommandStatus.Error) result.Error = cmdRes.ErrorMessage;
                return result;
            }

            // 2. 次之尋找實作 IParameterGuidance 的型別
            var guidanceTypes = asm.GetTypes().Where(t => typeof(IParameterGuidance).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract).ToList();
            if (guidanceTypes.Count > 0)
            {
                IParameterGuidance? targetGuidance = null;
                foreach (var gt in guidanceTypes)
                {
                    if (Activator.CreateInstance(gt) is IParameterGuidance g)
                    {
                        if (g.CommandKey.Equals(commandKey, StringComparison.OrdinalIgnoreCase) ||
                            g.CommandKey.EndsWith("@" + action, StringComparison.OrdinalIgnoreCase))
                        {
                            targetGuidance = g;
                            break;
                        }
                        targetGuidance ??= g;
                    }
                }

                if (targetGuidance != null)
                {
                    var subSw = Stopwatch.StartNew();
                    CommandResult cmdRes = targetGuidance.Execute(context, action, parameter);
                    subSw.Stop();

                    sw.Stop();
                    result.Success = cmdRes.Status == CommandStatus.Completed || cmdRes.Status == CommandStatus.Jump || cmdRes.Status == CommandStatus.Waiting;
                    result.ElapsedMilliseconds = subSw.Elapsed.TotalMilliseconds;
                    result.Output = $"[IParameterGuidance] Key: {targetGuidance.CommandKey}, Action: {action}, Param: '{parameter}', Status: {cmdRes.Status}";
                    if (cmdRes.Status == CommandStatus.Error) result.Error = cmdRes.ErrorMessage;
                    return result;
                }
            }

            // 3. 檢查是否為 IDriver 驅動組件 (如 MockIO.csvi)
            var driverType = asm.GetTypes().FirstOrDefault(t => typeof(IDriver).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
            if (driverType != null && Activator.CreateInstance(driverType) is IDriver drv)
            {
                var subSw = Stopwatch.StartNew();
                drv.Init("SandboxDev", string.IsNullOrWhiteSpace(parameter) ? "Device=com1,OutStart=P0.0,OutPorts=16,InStart=P1.0,InPorts=16" : parameter, engines);
                var ticket = new DriverTicket("FORCE_UPDATE");
                drv.PostTicket(ticket);
                var res = ticket.Wait(1000);
                drv.Shutdown();
                subSw.Stop();

                sw.Stop();
                if (!res.Success)
                {
                    result.Success = false;
                    result.Error = $"FORCE_UPDATE ticket failed: {res.Message}";
                    return result;
                }
                result.Success = true;
                result.ElapsedMilliseconds = subSw.Elapsed.TotalMilliseconds;
                result.Output = $"[IDriver] DriverType: {drv.DriverType}, Category: {drv.Category}, Init & FORCE_UPDATE Ticket Verified OK";
                return result;
            }

            sw.Stop();
            result.Success = false;
            result.Error = $"No implementation of IModuleHandler, IParameterGuidance, or IDriver found in assembly '{asm.FullName}'.";
            result.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.Success = false;
            result.Error = ex.ToString();
            result.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
        }
        finally
        {
            alc.Unload();
        }

        return result;
    }
}
