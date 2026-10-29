using System.Text.Json.Serialization;

namespace ARCOS.Core.Csvi;

/// <summary>
/// CSVI 3.0 組件封裝模式
/// </summary>
public enum CsviPackagingMode
{
    /// <summary>
    /// 開放攜帶模式：UTF-8 明文壓縮封裝 .cs 與 .axaml 原始碼
    /// </summary>
    PortableOpen = 0,

    /// <summary>
    /// 密碼保護模式：AES-256-GCM 加密封裝原始碼，需密碼才可解出
    /// </summary>
    PasswordLocked = 1,

    /// <summary>
    /// 純執行剝離模式：完全移除原始碼資源區段（0 bytes）
    /// </summary>
    StrippedRelease = 2
}

/// <summary>
/// 組件類型
/// </summary>
public enum CsviComponentType
{
    Command = 0,
    Driver = 1,
    Library = 2
}

/// <summary>
/// 單次修訂變更記錄
/// </summary>
public class CsviRevisionEntry
{
    public int Revision { get; set; } = 1;
    public string TimestampUtc { get; set; } = DateTime.UtcNow.ToString("o");
    public string Author { get; set; } = "IPC-STATION\\Engineer";
    public string Summary { get; set; } = string.Empty;
    public string UnifiedDiff { get; set; } = string.Empty;
}

/// <summary>
/// CSVI 3.0 標準 Manifest 元數據結構 (存於 CSVI.Manifest.json 資源中)
/// </summary>
public class CsviManifest
{
    public string FormatVersion { get; set; } = "3.0";
    public string Module { get; set; } = string.Empty;
    public string CommandKey { get; set; } = string.Empty;
    
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CsviComponentType ComponentType { get; set; } = CsviComponentType.Command;

    public string SdkSignature { get; set; } = "ARCOS-SDK-8.0.1-Avalonia-11.0.10";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CsviPackagingMode PackagingMode { get; set; } = CsviPackagingMode.PortableOpen;

    public bool SourceEncrypted { get; set; } = false;
    public string SourceSha256 { get; set; } = string.Empty;
    public string LastCompiledUtc { get; set; } = DateTime.UtcNow.ToString("o");
    public string LastAuthor { get; set; } = "IPC-STATION\\Engineer";

    public List<CsviRevisionEntry> RevisionHistory { get; set; } = new();
}
