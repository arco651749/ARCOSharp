using System.IO;
using System.Reflection;
using System.Text.Json;

namespace MVCON.Core.Csvi;

/// <summary>
/// CSVI 3.0 套件封裝與解讀輔助類別 (Package Inspector & Reader)
/// </summary>
public class CsviPackage
{
    public CsviManifest Manifest { get; set; } = new();
    public Dictionary<string, string> EmbeddedSourceFiles { get; set; } = new();
    public Dictionary<string, string> EmbeddedRuntimeXaml { get; set; } = new();
    public byte[] AssemblyRawBytes { get; set; } = Array.Empty<byte>();

    public static CsviPackage LoadFromBytes(byte[] csviBytes, string? password = null)
    {
        var package = new CsviPackage
        {
            AssemblyRawBytes = csviBytes
        };

        // 載入 Assembly 檢查 ManifestResource
        var asm = Assembly.Load(csviBytes);

        // 1. 讀取 Manifest
        using (var manifestStream = asm.GetManifestResourceStream("CSVI.Manifest.json"))
        {
            if (manifestStream != null)
            {
                using var reader = new StreamReader(manifestStream);
                var json = reader.ReadToEnd();
                package.Manifest = JsonSerializer.Deserialize<CsviManifest>(json) ?? new CsviManifest();
            }
        }

        // 2. 讀取 Runtime XAML
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (name.StartsWith("CSVI.RuntimeXaml."))
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s != null)
                {
                    using var reader = new StreamReader(s);
                    package.EmbeddedRuntimeXaml[name] = reader.ReadToEnd();
                }
            }
        }

        // 3. 解包 Source Bundle
        using (var bundleStream = asm.GetManifestResourceStream("CSVI.Source.Bundle"))
        {
            if (bundleStream != null)
            {
                using var ms = new MemoryStream();
                bundleStream.CopyTo(ms);
                byte[] bundleBytes = ms.ToArray();
                package.EmbeddedSourceFiles = CsviSourceBundle.Unpack(bundleBytes, package.Manifest.PackagingMode, password);
            }
        }

        return package;
    }

    public static CsviPackage LoadFromFile(string filePath, string? password = null)
    {
        byte[] bytes = File.ReadAllBytes(filePath);
        return LoadFromBytes(bytes, password);
    }
}
