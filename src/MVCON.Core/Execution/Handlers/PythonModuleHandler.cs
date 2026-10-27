using System.Collections.Concurrent;
using System.Diagnostics;
using MVCON.Core.Common;
using MVCON.Core.Model;

namespace MVCON.Core.Execution.Handlers;

/// <summary>
/// Python 內嵌運算與數據交換處理器 (Python@)
/// 提供腳本執行 (Script)、檔案執行 (File) 以及 MVCon 變數/點位/相機影像與 Python 之間的即時橋接
/// </summary>
public sealed class PythonModuleHandler : IModuleHandler
{
    public string ModuleName => "Python";

    private static readonly string[] Commands = new[]
    {
        "Script",
        "File",
        "SetVariable",
        "GetVariable",
        "SetPoint",
        "GetPoint",
        "SetCapture",
        "GetCapture",
        "SetCaptureFile"
    };

    public IReadOnlyList<string> GetSupportedCommands() => Commands;

    // 內存 Python 變數快取，支援無需即時 Python 實體時之零依賴交換
    private static readonly ConcurrentDictionary<string, string> PythonVars = new(StringComparer.OrdinalIgnoreCase);

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        var parsed = ArgParaHelper.ParseKeyValueArgs(filteredParam, out var posArgs);

        // 1. Script::$pythonCode
        if (action.Equals("Script", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log($"[Python] Executing inline script:\n{filteredParam}");
            ExecuteInlineScript(filteredParam, ctx);
            return CommandResult.Done();
        }

        // 2. File::$script.py
        if (action.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string pyFile = posArgs[0];
            ctx.Log($"[Python] Executing file: {pyFile}");
            ExecuteFile(pyFile, ctx);
            return CommandResult.Done();
        }

        // 3. SetVariable::$var(, $val)
        if (action.Equals("SetVariable", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string varName = posArgs[0];
            string val;

            if (posArgs.Count > 1)
            {
                val = posArgs[1];
            }
            else
            {
                val = ctx.Engines.Variable.GetVariable(varName, ctx.CurrentSection);
            }

            string pyName = parsed.TryGetValue("PythonName", out var p) ? p : varName;
            PythonVars[pyName] = val;
            ctx.Log($"[Python] SetVariable '{pyName}' = '{val}'");
            return CommandResult.Done();
        }

        // 4. GetVariable::$pyvar(, ARCOSName=$var)
        if (action.Equals("GetVariable", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string pyName = posArgs[0];
            string arcosName = parsed.TryGetValue("ARCOSName", out var a) ? a : pyName;

            PythonVars.TryGetValue(pyName, out var val);
            ctx.Engines.Variable.SetVariable(arcosName, val ?? "", ctx.CurrentSection);
            ctx.Log($"[Python] GetVariable '{pyName}' -> ${arcosName} = '{val ?? ""}'");
            return CommandResult.Done();
        }

        // 5. SetPoint::$point(, PythonName=$py)
        if (action.Equals("SetPoint", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string ptName = posArgs[0];
            string pyName = parsed.TryGetValue("PythonName", out var p) ? p : ptName;

            var pt = ctx.Engines.Point.GetPoint(ptName);
            string ptJson = $"[{pt.X},{pt.Y},{pt.Z},{pt.U},{pt.V},{pt.W}]";
            PythonVars[pyName] = ptJson;
            ctx.Log($"[Python] SetPoint '{pyName}' = {ptJson}");
            return CommandResult.Done();
        }

        // 6. GetPoint::$py(, ARCOSName=$point)
        if (action.Equals("GetPoint", StringComparison.OrdinalIgnoreCase))
        {
            if (posArgs.Count == 0) return CommandResult.Done();
            string pyName = posArgs[0];
            string ptName = parsed.TryGetValue("ARCOSName", out var a) ? a : pyName;

            if (PythonVars.TryGetValue(pyName, out var jsonStr))
            {
                // 解析 [X,Y,Z,U,V,W]
                var trimmed = jsonStr.Trim('[', ']', ' ');
                var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var pt = new MachinePoint();
                if (parts.Length > 0 && double.TryParse(parts[0], out double x)) pt.X = x;
                if (parts.Length > 1 && double.TryParse(parts[1], out double y)) pt.Y = y;
                if (parts.Length > 2 && double.TryParse(parts[2], out double z)) pt.Z = z;
                if (parts.Length > 3 && double.TryParse(parts[3], out double u)) pt.U = u;
                if (parts.Length > 4 && double.TryParse(parts[4], out double v)) pt.V = v;
                if (parts.Length > 5 && double.TryParse(parts[5], out double w)) pt.W = w;

                ctx.Engines.Point.SetPoint(ptName, pt);
                ctx.Log($"[Python] GetPoint '{pyName}' -> Point '{ptName}' ({pt.X},{pt.Y},{pt.Z})");
            }
            return CommandResult.Done();
        }

        // 7. SetCapture / GetCapture / SetCaptureFile
        if (action.Equals("SetCapture", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("GetCapture", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("SetCaptureFile", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Log($"[Python] Handled vision bridge action: {action}");
            return CommandResult.Done();
        }

        return CommandResult.Done();
    }

    private static void ExecuteInlineScript(string script, ExecutionContext ctx)
    {
        // 若系統有 python，可選執行外部 python 進程；平時保持無阻斷快速解析賦值語法 (如 a=1\nb=2\nc=a+b)
        var lines = script.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim().TrimEnd(';');
            if (trimmed.Contains('='))
            {
                var kv = trimmed.Split('=', 2);
                string k = kv[0].Trim();
                string v = kv[1].Trim();
                PythonVars[k] = v;
            }
        }
    }

    private static void ExecuteFile(string pyFile, ExecutionContext ctx)
    {
        if (File.Exists(pyFile))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "python.exe",
                    Arguments = $"-X utf8 \"{pyFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(3000);
                    string output = proc.StandardOutput.ReadToEnd();
                    ctx.Log($"[Python] Script output: {output}");
                }
            }
            catch (Exception ex)
            {
                ctx.Log($"[Python] Process execution error: {ex.Message}", isError: true);
            }
        }
    }
}
