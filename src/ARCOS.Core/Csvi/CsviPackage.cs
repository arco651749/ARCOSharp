using System.IO;
using System.Reflection;
using System.Text.Json;
using ARCOS.Core.Common;

namespace ARCOS.Core.Csvi;

/// <summary>
/// CSVI 3.0 套件封裝與解讀輔助類別 (Package Inspector & Reader)
/// </summary>
public class CsviPackage
{
    public CsviManifest Manifest { get; set; } = new();
    public Dictionary<string, string> EmbeddedSourceFiles { get; set; } = new();
    public Dictionary<string, string> EmbeddedRuntimeXaml { get; set; } = new();
    public byte[] AssemblyRawBytes { get; set; } = Array.Empty<byte>();
    public Assembly? LoadedAssembly { get; set; }
    public CollectiblePluginContext? Context { get; set; }
    public bool HasSourceBundleResource { get; set; }

    /// <summary>
    /// 卸載此套件所屬之 Collectible Assembly 上下文，釋放記憶體
    /// </summary>
    public void Unload()
    {
        Context?.Unload();
        Context = null;
        LoadedAssembly = null;
    }

    public static CsviPackage LoadFromBytes(byte[] csviBytes, string? password = null, bool unpackSources = true, CollectiblePluginContext? customContext = null, byte[]? pdbBytes = null)
    {
        var package = new CsviPackage
        {
            AssemblyRawBytes = csviBytes
        };
        var context = customContext ?? new CollectiblePluginContext($"CsviPackage_{Guid.NewGuid():N}");
        var asm = context.LoadAssemblyBytes(csviBytes, pdbBytes);
        package.Context = context;
        package.LoadedAssembly = asm;

        // 載入 Assembly 檢查 ManifestResource

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

        // 3. 檢查並解包 Source Bundle
        using (var bundleStream = asm.GetManifestResourceStream("CSVI.Source.Bundle"))
        {
            if (bundleStream != null)
            {
                package.HasSourceBundleResource = true;
                if (unpackSources)
                {
                    using var ms = new MemoryStream();
                    bundleStream.CopyTo(ms);
                    byte[] bundleBytes = ms.ToArray();
                    package.EmbeddedSourceFiles = CsviSourceBundle.Unpack(bundleBytes, package.Manifest.PackagingMode, password);
                }
            }
        }

        return package;
    }

    public static CsviPackage Load(string filePath) => LoadFromFile(filePath);

    public static CsviPackage LoadFromFile(string filePath, string? password = null, bool unpackSources = true)
    {
        byte[] bytes = File.ReadAllBytes(filePath);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        var context = new CollectiblePluginContext($"Csvi_{Path.GetFileNameWithoutExtension(filePath)}_{Guid.NewGuid():N}", dir);
        
        byte[]? pdbBytes = null;
        string pdbPath = Path.ChangeExtension(filePath, ".pdb");
        if (File.Exists(pdbPath))
        {
            try { pdbBytes = File.ReadAllBytes(pdbPath); } catch { }
        }

        return LoadFromBytes(bytes, password, unpackSources, context, pdbBytes);
    }
}
