using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using ARCOS.App.Dialogs;
using ARCOS.Core.Drivers;
using ARCOS.Core.Guidance;

namespace ARCOS.App.Guidance;

/// <summary>
/// 指令參數導引視窗動態解析管理器
/// 優先搜尋 MODULES/{Module}/USERPARAMETEREDITOR/{Command}.exe 與 EXTRACMD/USERPARAMETEREDITOR/{Command}.exe
/// 透過 --param 傳遞參數，讀取 stdout 輸出並等待視窗自動關閉 (Auto-Close)，達成 ARCOS 骨架與導引視窗 100% 動態解耦
/// </summary>
public sealed class ParameterGuidanceManager
{
    private static readonly Lazy<ParameterGuidanceManager> _lazy = new(() => new ParameterGuidanceManager());
    public static ParameterGuidanceManager Instance => _lazy.Value;

    private readonly Dictionary<string, IParameterGuidance> _guidances = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterGuidance(IParameterGuidance guidance)
    {
        _guidances[guidance.CommandKey] = guidance;
    }

    /// <summary>
    /// 動態解析並取得指定模組或指令的 Front Panel / SubPanel 視覺控制項 (CSVI 三位一體之支柱 3)
    /// </summary>
    public object? GetFrontPanel(string commandOrModuleKey)
    {
        string trimmed = commandOrModuleKey?.Trim() ?? "";
        string module = trimmed;
        string action = "SetPort";
        if (trimmed.Contains('@'))
        {
            var parts = trimmed.Split('@', 2);
            module = parts[0].Trim();
            action = parts[1].Trim();
        }

        string? dllPath = FindGuidanceDll(module, action, trimmed);
        if (!string.IsNullOrEmpty(dllPath) && File.Exists(dllPath))
        {
            var plugin = LoadDynamicPlugin(dllPath, trimmed);
            if (plugin != null)
            {
                return plugin.GetFrontPanelControl();
            }
        }

        if (_guidances.TryGetValue(trimmed, out var g))
        {
            return g.GetFrontPanelControl();
        }

        return null;
    }

    public async Task<string?> RequestGuidanceAsync(string commandKey, string currentParam, BasicEngines? engines, Window? owner)
    {
        string trimmedKey = commandKey?.Trim() ?? "";
        string module = "";
        string action = trimmedKey;
        if (trimmedKey.Contains('@'))
        {
            var parts = trimmedKey.Split('@', 2);
            module = parts[0].Trim();
            action = parts[1].Trim();
        }

        // 1. 優先搜尋動態 DLL 插件 (對標 LabVIEW 動態呼叫 VI：原生同進程載入，直接傳遞 BasicEngines 實例)
        string? dllPath = FindGuidanceDll(module, action, trimmedKey);
        if (!string.IsNullOrEmpty(dllPath) && File.Exists(dllPath) && engines != null && owner != null)
        {
            try
            {
                var plugin = LoadDynamicPlugin(dllPath, trimmedKey);
                if (plugin != null)
                {
                    return await plugin.ShowGuidanceDialogAsync(currentParam, engines, owner);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GUIDANCE ERROR] Failed to load dynamic DLL plugin '{dllPath}': {ex.Message}");
            }
        }

        // 2. 次之嘗試動態搜尋獨立 EXE 參數導引程式 (跨語言/隔離工具)
        string? exePath = FindGuidanceExecutable(module, action, trimmedKey);
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--cmd \"{trimmedKey}\" --param \"{currentParam.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = false
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync();

                    if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        return output.TrimEnd('\r', '\n');
                    }
                    return null; // 使用者按取消或未輸入變更
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GUIDANCE ERROR] Failed to launch standalone guidance '{exePath}': {ex.Message}");
            }
        }

        // 2. 搜尋記憶體中註冊的動態插件
        if (_guidances.TryGetValue(trimmedKey, out var g) && engines != null && owner != null)
        {
            return await g.ShowGuidanceDialogAsync(currentParam, engines, owner);
        }

        // 3. 兜底保護：若尚未提供專屬 EXE，呼叫內建通用對話框
        if (owner != null)
        {
            return await ParameterGuidanceWindow.ShowGuidanceAsync(owner, trimmedKey, currentParam, engines);
        }

        return null;
    }

    private IParameterGuidance? LoadDynamicPlugin(string dllPath, string targetCommandKey)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(dllPath);
            var asm = System.Reflection.Assembly.Load(bytes);
            IParameterGuidance? fallback = null;
            foreach (var t in asm.GetTypes())
            {
                if (typeof(IParameterGuidance).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                {
                    if (Activator.CreateInstance(t) is IParameterGuidance g)
                    {
                        if (g.CommandKey.Equals(targetCommandKey, StringComparison.OrdinalIgnoreCase) ||
                            g.CommandKey.Equals($"IO@{targetCommandKey}", StringComparison.OrdinalIgnoreCase))
                        {
                            return g;
                        }
                        fallback ??= g;
                    }
                }
            }
            return fallback;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DYNAMIC PLUGIN ERROR] Failed to instantiate IParameterGuidance from '{dllPath}': {ex.Message}");
        }
        return null;
    }

    private string? FindGuidanceDll(string module, string action, string fullKey)
    {
        var candidateRoots = new List<string>
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        try
        {
            var parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            if (Directory.Exists(parent)) candidateRoots.Add(parent);
        }
        catch { }

        foreach (var root in candidateRoots)
        {
            if (!string.IsNullOrEmpty(module))
            {
                // 1. 指令專屬 CSVI 虛擬儀表 (最高優先，直接位於 MODULES/<Module>/ 目錄下)
                string pCsvi1 = Path.Combine(root, "MODULES", module, $"{action}.csvi");
                if (File.Exists(pCsvi1)) return pCsvi1;

                string pCsvi2 = Path.Combine(root, "MODULES", module, $"{module}@{action}.csvi");
                if (File.Exists(pCsvi2)) return pCsvi2;

                string pCsviMod = Path.Combine(root, "MODULES", module, $"{module}.csvi");
                if (File.Exists(pCsviMod)) return pCsviMod;

                // 2. 舊式相容備援 (若有歷史遺留檔案才查閱)
                string pLegacy1 = Path.Combine(root, "MODULES", module, "USERPARAMETEREDITOR", $"{action}.csvi");
                if (File.Exists(pLegacy1)) return pLegacy1;

                string pLegacy2 = Path.Combine(root, "MODULES", module, "USERPARAMETEREDITOR", $"{action}.Guidance.dll");
                if (File.Exists(pLegacy2)) return pLegacy2;

                string pLegacy3 = Path.Combine(root, "MODULES", module, "USERPARAMETEREDITOR", $"{module}.Guidance.dll");
                if (File.Exists(pLegacy3)) return pLegacy3;
            }

            string pExtCsvi = Path.Combine(root, "EXTRACMD", $"{action}.csvi");
            if (File.Exists(pExtCsvi)) return pExtCsvi;

            string p4Csvi = Path.Combine(root, "EXTRACMD", "USERPARAMETEREDITOR", $"{action}.csvi");
            if (File.Exists(p4Csvi)) return p4Csvi;
        }

        return null;
    }

    private string? FindGuidanceExecutable(string module, string action, string fullKey)
    {
        // 搜尋候選根目錄：執行檔所在目錄、工作目錄、專案根目錄
        var candidateRoots = new List<string>
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        try
        {
            var parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            if (Directory.Exists(parent)) candidateRoots.Add(parent);
        }
        catch { }

        foreach (var root in candidateRoots)
        {
            if (!string.IsNullOrEmpty(module))
            {
                // 1. 指令專屬執行檔 (優先): MODULES/{Module}/USERPARAMETEREDITOR/{action}.Guidance.exe
                string p1 = Path.Combine(root, "MODULES", module, "USERPARAMETEREDITOR", $"{action}.Guidance.exe");
                if (File.Exists(p1)) return p1;

                string p1_2 = Path.Combine(root, "MODULES", module, "USERPARAMETEREDITOR", $"{action}.exe");
                if (File.Exists(p1_2)) return p1_2;

                string p2 = Path.Combine(root, "MODULES", module, "USERPARAMETEREDITOR", $"{fullKey}.exe");
                if (File.Exists(p2)) return p2;

                string pRunner = Path.Combine(root, "MODULES", module, "USERPARAMETEREDITOR", "GuidanceRunner.exe");
                if (File.Exists(pRunner)) return pRunner;

                // 2. 模組通用執行檔兜底: MODULES/{Module}/USERPARAMETEREDITOR/{module}.Guidance.exe
                string p0 = Path.Combine(root, "MODULES", module, "USERPARAMETEREDITOR", $"{module}.Guidance.exe");
                if (File.Exists(p0)) return p0;
            }

            // 候選路徑 2：EXTRACMD/USERPARAMETEREDITOR/{action}.exe
            string p3 = Path.Combine(root, "EXTRACMD", "USERPARAMETEREDITOR", $"{action}.exe");
            if (File.Exists(p3)) return p3;

            string p4 = Path.Combine(root, "EXTRACMD", "USERPARAMETEREDITOR", $"{fullKey}.exe");
            if (File.Exists(p4)) return p4;

            // 支援 Linux 無副檔名 ELF
            if (!string.IsNullOrEmpty(module))
            {
                string p1Linux = Path.Combine(root, "MODULES", module, "USERPARAMETEREDITOR", action);
                if (File.Exists(p1Linux)) return p1Linux;
            }
        }

        return null;
    }

    public Task<string?> ShowGuidanceDialogAsync(Window owner, string commandKey, string currentParam, BasicEngines engines)
    {
        return RequestGuidanceAsync(commandKey, currentParam, engines, owner);
    }
}
