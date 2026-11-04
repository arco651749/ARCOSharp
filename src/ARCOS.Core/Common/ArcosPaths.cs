using System;
using System.IO;

namespace ARCOS.Core.Common;

/// <summary>
/// ARCOSharp 系統標準目錄與路徑規範
/// - .pro 專案檔與 USER PANEL CSVI 位於 ARCOSharp 根目錄
/// - .init 程式檔位於 SCRIPT 目錄
/// - 系統日誌位於 LOGS 目錄
/// - 相機取像位於 PICTURE/<CameraName>/ 目錄
/// - DAQ 示波器波形位於 WAVEFORM 目錄
/// - 點位與運動軌跡位於 POINT&PATH 目錄
/// </summary>
public static class ArcosPaths
{
    private static string? _customRoot;

    public static string RootDirectory
    {
        get
        {
            if (_customRoot != null) return _customRoot;

            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ARCOS.sln")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }

            // 在發行環境 (無 ARCOS.sln) 則以 MODULES 目錄判定
            dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "MODULES")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }

            return Directory.GetCurrentDirectory();
        }
        set => _customRoot = value;
    }

    public static string ScriptDirectory => Path.Combine(RootDirectory, "SCRIPT");
    public static string LogsDirectory => Path.Combine(RootDirectory, "LOGS");
    public static string PictureDirectory => Path.Combine(RootDirectory, "PICTURE");
    public static string WaveformDirectory => Path.Combine(RootDirectory, "WAVEFORM");
    public static string PointAndPathDirectory => Path.Combine(RootDirectory, "POINT&PATH");
    public static string ToolsDirectory => Path.Combine(RootDirectory, "tools");
    public static string ExternalModulesDirectory => Path.Combine(RootDirectory, "ExternalModules");

    /// <summary>
    /// Meld 視覺化比對編輯器路徑 (優先使用 tools/MeldPortable/Meld.exe，找不到則 fallback 至系統 PATH 上的 meld)
    /// </summary>
    public static string MeldExecutable
    {
        get
        {
            string portable = Path.Combine(ToolsDirectory, "MeldPortable", "Meld.exe");
            if (File.Exists(portable)) return portable;
            return "meld";
        }
    }

    /// <summary>
    /// 初始化並確保所有標準工作目錄已建立
    /// </summary>
    public static void EnsureDirectoriesExist()
    {
        try
        {
            if (!Directory.Exists(ScriptDirectory)) Directory.CreateDirectory(ScriptDirectory);
            if (!Directory.Exists(LogsDirectory)) Directory.CreateDirectory(LogsDirectory);
            if (!Directory.Exists(PictureDirectory)) Directory.CreateDirectory(PictureDirectory);
            if (!Directory.Exists(WaveformDirectory)) Directory.CreateDirectory(WaveformDirectory);
            if (!Directory.Exists(PointAndPathDirectory)) Directory.CreateDirectory(PointAndPathDirectory);
            if (!Directory.Exists(ExternalModulesDirectory)) Directory.CreateDirectory(ExternalModulesDirectory);
        }
        catch { }
    }

    /// <summary>
    /// 取得特定相機專屬的影像存檔目錄：PICTURE/<CameraName>/
    /// </summary>
    public static string GetCameraPictureDirectory(string cameraName)
    {
        string safeName = string.IsNullOrWhiteSpace(cameraName) ? "DefaultCam" : cameraName.Trim();
        string dir = Path.Combine(PictureDirectory, safeName);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 取得今日系統日誌檔案完整路徑：LOGS/yyyyMMdd.log
    /// </summary>
    public static string GetTodayLogFilePath()
    {
        if (!Directory.Exists(LogsDirectory)) Directory.CreateDirectory(LogsDirectory);
        return Path.Combine(LogsDirectory, $"{DateTime.Now:yyyyMMdd}.log");
    }

    /// <summary>
    /// 解析 .pro 檔案路徑（預設在根目錄）
    /// </summary>
    public static string ResolveProPath(string proFileName)
    {
        if (Path.IsPathRooted(proFileName)) return proFileName;
        string rootCandidate = Path.Combine(RootDirectory, proFileName);
        if (File.Exists(rootCandidate)) return rootCandidate;
        string scriptCandidate = Path.Combine(ScriptDirectory, proFileName);
        if (File.Exists(scriptCandidate)) return scriptCandidate;
        return rootCandidate;
    }

    /// <summary>
    /// 解析 .init 檔案路徑（預設在 SCRIPT 目錄）
    /// </summary>
    public static string ResolveInitPath(string initFileName)
    {
        if (Path.IsPathRooted(initFileName)) return initFileName;
        string scriptCandidate = Path.Combine(ScriptDirectory, initFileName);
        if (File.Exists(scriptCandidate)) return scriptCandidate;
        string rootCandidate = Path.Combine(RootDirectory, initFileName);
        if (File.Exists(rootCandidate)) return rootCandidate;
        return scriptCandidate;
    }

    /// <summary>
    /// 解析 User Panel CSVI 路徑（預設在根目錄）
    /// </summary>
    public static string ResolveUserPanelCsviPath(string csviFileName)
    {
        if (string.IsNullOrWhiteSpace(csviFileName)) return string.Empty;
        if (Path.IsPathRooted(csviFileName)) return csviFileName;

        string withExt = csviFileName.EndsWith(".csvi", StringComparison.OrdinalIgnoreCase)
            ? csviFileName
            : $"{csviFileName}.csvi";

        var candidates = new[]
        {
            Path.Combine(RootDirectory, csviFileName),
            Path.Combine(RootDirectory, withExt),
            Path.Combine(RootDirectory, "MODULES", csviFileName),
            Path.Combine(RootDirectory, "MODULES", withExt),
            Path.Combine(RootDirectory, "MODULES", "SYSTEM", csviFileName),
            Path.Combine(RootDirectory, "MODULES", "SYSTEM", withExt),
            Path.Combine(RootDirectory, "dist", "ARCOS_Runtime", csviFileName),
            Path.Combine(RootDirectory, "dist", "ARCOS_Runtime", withExt),
            Path.Combine(RootDirectory, "dist", "ARCOS_Runtime", "MODULES", "SYSTEM", csviFileName),
            Path.Combine(RootDirectory, "dist", "ARCOS_Runtime", "MODULES", "SYSTEM", withExt),
            Path.Combine(RootDirectory, "src", "MODULES", csviFileName),
            Path.Combine(RootDirectory, "src", "MODULES", withExt),
            Path.Combine(RootDirectory, "src", "MODULES", "SYSTEM", csviFileName),
            Path.Combine(RootDirectory, "src", "MODULES", "SYSTEM", withExt),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        try
        {
            var searchNames = new[] { csviFileName, withExt }.Distinct();
            var searchDirs = new[]
            {
                Path.Combine(RootDirectory, "MODULES"),
                Path.Combine(RootDirectory, "dist", "ARCOS_Runtime"),
                Path.Combine(RootDirectory, "src", "MODULES")
            };

            foreach (var dir in searchDirs)
            {
                if (Directory.Exists(dir))
                {
                    foreach (var name in searchNames)
                    {
                        var match = Directory.GetFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
                        if (match != null) return match;
                    }
                }
            }
        }
        catch { }

        return Path.Combine(RootDirectory, withExt);
    }

    /// <summary>
    /// 解析 ExternalModules/ 目錄下之目標 CSVI 外部模組路徑 (支援 .vi 自動轉換至 .csvi)
    /// </summary>
    public static string ResolveExternalModuleCsviPath(string csviNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(csviNameOrPath)) return string.Empty;
        if (Path.IsPathRooted(csviNameOrPath)) return csviNameOrPath;

        string target = csviNameOrPath.Trim();
        // 自動替換 legacy .vi 為 .csvi
        if (target.EndsWith(".vi", StringComparison.OrdinalIgnoreCase))
        {
            target = target.Substring(0, target.Length - 3) + ".csvi";
        }
        else if (!target.EndsWith(".csvi", StringComparison.OrdinalIgnoreCase) &&
                 !target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                 !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                 !target.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            target += ".csvi";
        }

        var candidates = new[]
        {
            Path.Combine(ExternalModulesDirectory, target),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExternalModules", target),
            Path.Combine(Directory.GetCurrentDirectory(), "ExternalModules", target),
            Path.Combine(RootDirectory, "MODULES", "CALL", target),
            Path.Combine(RootDirectory, "dist", "ARCOS_Runtime", "ExternalModules", target),
            Path.Combine(RootDirectory, target)
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        return candidates[0];
    }
}
