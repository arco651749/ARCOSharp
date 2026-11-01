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
        get => _customRoot ?? Directory.GetCurrentDirectory();
        set => _customRoot = value;
    }

    public static string ScriptDirectory => Path.Combine(RootDirectory, "SCRIPT");
    public static string LogsDirectory => Path.Combine(RootDirectory, "LOGS");
    public static string PictureDirectory => Path.Combine(RootDirectory, "PICTURE");
    public static string WaveformDirectory => Path.Combine(RootDirectory, "WAVEFORM");
    public static string PointAndPathDirectory => Path.Combine(RootDirectory, "POINT&PATH");
    public static string ToolsDirectory => Path.Combine(RootDirectory, "tools");

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
        if (Path.IsPathRooted(csviFileName)) return csviFileName;
        string rootCandidate = Path.Combine(RootDirectory, csviFileName);
        if (File.Exists(rootCandidate)) return rootCandidate;
        string modulesCandidate = Path.Combine(RootDirectory, "MODULES", csviFileName);
        if (File.Exists(modulesCandidate)) return modulesCandidate;
        string srcModulesCandidate = Path.Combine(RootDirectory, "src", "MODULES", csviFileName);
        if (File.Exists(srcModulesCandidate)) return srcModulesCandidate;

        try
        {
            string modulesDir = Path.Combine(RootDirectory, "MODULES");
            if (Directory.Exists(modulesDir))
            {
                var match = Directory.GetFiles(modulesDir, csviFileName, SearchOption.AllDirectories).FirstOrDefault();
                if (match != null) return match;
            }
            string srcModulesDir = Path.Combine(RootDirectory, "src", "MODULES");
            if (Directory.Exists(srcModulesDir))
            {
                var match = Directory.GetFiles(srcModulesDir, csviFileName, SearchOption.AllDirectories).FirstOrDefault();
                if (match != null) return match;
            }
            string distModulesDir = Path.Combine(RootDirectory, "dist", "ARCOS_Runtime", "MODULES");
            if (Directory.Exists(distModulesDir))
            {
                var match = Directory.GetFiles(distModulesDir, csviFileName, SearchOption.AllDirectories).FirstOrDefault();
                if (match != null) return match;
            }
        }
        catch { }

        return rootCandidate;
    }
}
