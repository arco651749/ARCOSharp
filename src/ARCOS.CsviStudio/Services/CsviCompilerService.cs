using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ARCOS.Core.Csvi;

namespace ARCOS.CsviStudio.Services;

public class CompilationResult
{
    public bool Success { get; set; }
    public byte[] AssemblyBytes { get; set; } = Array.Empty<byte>();
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public long ElapsedMilliseconds { get; set; }
}

/// <summary>
/// 記憶體 Roslyn C# 編譯器與 CSVI 3.0 ManifestResource 封裝服務
/// </summary>
public class CsviCompilerService
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> CachedReferences = new(InitializeDefaultReferences);

    public static ImmutableArray<MetadataReference> DefaultReferences => CachedReferences.Value;

    private static ImmutableArray<MetadataReference> InitializeDefaultReferences()
    {
        var refs = new List<MetadataReference>();

        // 收集當前 AppDomain 核心組件
        var assemblies = new HashSet<Assembly>
        {
            typeof(object).Assembly,                         // System.Private.CoreLib
            typeof(Console).Assembly,                        // System.Console
            typeof(Enumerable).Assembly,                     // System.Linq
            typeof(List<>).Assembly,                         // System.Collections
            typeof(Task).Assembly,                           // System.Threading.Tasks
            typeof(ARCOS.Core.Execution.IModuleHandler).Assembly, // ARCOS.Core
            typeof(Avalonia.AvaloniaObject).Assembly,        // Avalonia.Base
            typeof(Avalonia.Controls.Control).Assembly,      // Avalonia.Controls
            typeof(Avalonia.Interactivity.RoutedEventArgs).Assembly // Avalonia.Interactivity
        };

        // 加入已載入組件的路徑
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location) && File.Exists(asm.Location))
            {
                assemblies.Add(asm);
            }
        }

        foreach (var asm in assemblies)
        {
            try
            {
                if (!string.IsNullOrEmpty(asm.Location) && File.Exists(asm.Location))
                {
                    refs.Add(MetadataReference.CreateFromFile(asm.Location));
                }
            }
            catch
            {
                // 忽略非檔案型組件
            }
        }

        return refs.ToImmutableArray();
    }

    /// <summary>
    /// 將原始碼字典編譯為包含 Manifest、Runtime XAML 與 Source Bundle 的 .csvi PE 二進位 byte[]
    /// </summary>
    public CompilationResult Compile(
        string assemblyName,
        Dictionary<string, string> sourceFiles,
        CsviManifest manifest,
        string? password = null,
        IEnumerable<MetadataReference>? extraReferences = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new CompilationResult();

        // 1. 解析 C# 語法樹
        var syntaxTrees = new List<SyntaxTree>();
        var xamlFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in sourceFiles)
        {
            if (kvp.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var tree = CSharpSyntaxTree.ParseText(kvp.Value, path: kvp.Key, encoding: Encoding.UTF8);
                syntaxTrees.Add(tree);
            }
            else if (kvp.Key.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || kvp.Key.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                xamlFiles[kvp.Key] = kvp.Value;
            }
        }

        // 2. 構建編譯引用
        var allRefs = DefaultReferences.ToList();
        if (extraReferences != null)
        {
            allRefs.AddRange(extraReferences);
        }

        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            platform: Platform.AnyCpu,
            nullableContextOptions: NullableContextOptions.Enable
        );

        var compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            allRefs,
            compilationOptions
        );

        // 3. 準備 ManifestResources
        var resourceDescriptions = new List<ResourceDescription>();

        // (a) Manifest JSON
        manifest.LastCompiledUtc = DateTime.UtcNow.ToString("o");
        manifest.SourceSha256 = CsviSourceBundle.ComputeSha256(sourceFiles);
        string manifestJson = System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        resourceDescriptions.Add(new ResourceDescription("CSVI.Manifest.json", () => new MemoryStream(manifestBytes), isPublic: true));

        // (b) Runtime XAML
        foreach (var xaml in xamlFiles)
        {
            string resName = "CSVI.RuntimeXaml." + Path.GetFileName(xaml.Key);
            byte[] xamlBytes = Encoding.UTF8.GetBytes(xaml.Value);
            resourceDescriptions.Add(new ResourceDescription(resName, () => new MemoryStream(xamlBytes), isPublic: true));
        }

        // (c) Source Bundle (依封裝模式決定明文、加密或剝離)
        byte[] bundleBytes = CsviSourceBundle.Pack(sourceFiles, manifest.PackagingMode, password);
        if (bundleBytes.Length > 0)
        {
            resourceDescriptions.Add(new ResourceDescription("CSVI.Source.Bundle", () => new MemoryStream(bundleBytes), isPublic: true));
        }

        // 4. Emit 編譯產生 PE 影像
        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream, manifestResources: resourceDescriptions);

        sw.Stop();
        result.ElapsedMilliseconds = sw.ElapsedMilliseconds;

        foreach (var diag in emitResult.Diagnostics)
        {
            if (diag.Severity == DiagnosticSeverity.Error)
            {
                result.Errors.Add(diag.ToString());
            }
            else if (diag.Severity == DiagnosticSeverity.Warning)
            {
                result.Warnings.Add(diag.ToString());
            }
        }

        if (emitResult.Success)
        {
            result.Success = true;
            result.AssemblyBytes = peStream.ToArray();
        }
        else
        {
            result.Success = false;
        }

        return result;
    }
}
