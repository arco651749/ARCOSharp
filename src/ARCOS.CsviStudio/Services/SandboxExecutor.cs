using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;
using ARCOS.Core.Execution;
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
/// </summary>
public class SandboxExecutor
{
    private class CollectibleAssemblyLoadContext : AssemblyLoadContext
    {
        public CollectibleAssemblyLoadContext() : base(isCollectible: true) { }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 優先委派給預設上下文以共享 ARCOS.Core 核心型別
            return null;
        }
    }

    /// <summary>
    /// 在隔離記憶體沙盒中執行指定 CSVI 指令的 Execute()
    /// </summary>
    public SandboxExecutionResult Execute(byte[] assemblyBytes, string commandKey, string parameter)
    {
        var result = new SandboxExecutionResult();
        var sw = Stopwatch.StartNew();

        var alc = new CollectibleAssemblyLoadContext();
        try
        {
            using var ms = new MemoryStream(assemblyBytes);
            var asm = alc.LoadFromStream(ms);

            // 尋找實作 IModuleHandler 的型別
            var handlerType = asm.GetTypes().FirstOrDefault(t => typeof(IModuleHandler).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

            if (handlerType == null)
            {
                sw.Stop();
                result.Success = false;
                result.Error = $"No implementation of IModuleHandler found in assembly '{asm.FullName}'.";
                result.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
                return result;
            }

            var handler = (IModuleHandler?)Activator.CreateInstance(handlerType);
            if (handler == null)
            {
                sw.Stop();
                result.Success = false;
                result.Error = $"Failed to instantiate module handler '{handlerType.FullName}'.";
                result.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
                return result;
            }

            // 準備沙盒環境
            var engines = new BasicEngines();
            var drivers = new DriverManager();
            var program = new ProgramTable();
            var context = new CoreExecutionContext(0, "SandboxThread", program, engines, drivers);

            string action = commandKey.Contains('@') ? commandKey.Split('@')[1] : commandKey;

            // 執行微秒級 Execute
            var subSw = Stopwatch.StartNew();
            CommandResult cmdRes = handler.Execute(action, parameter, context);
            subSw.Stop();

            sw.Stop();
            result.Success = cmdRes.Status == CommandStatus.Completed || cmdRes.Status == CommandStatus.Jump;
            result.ElapsedMilliseconds = subSw.Elapsed.TotalMilliseconds;
            result.Output = $"Action: {action}, Param: '{parameter}', Status: {cmdRes.Status}";
            if (cmdRes.Status == CommandStatus.Error)
            {
                result.Error = cmdRes.ErrorMessage;
            }
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
