using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace ARCOS.Core.Common;

/// <summary>
/// 具備可卸載能力 (isCollectible: true) 之組件載入上下文。
/// 支援從記憶體位元組流載入，絕對不鎖定 Windows 磁碟上的 .dll / .csvi 檔案，
/// 同時將核心共享組件委託給 Default 上下文以保證型別一致性。
/// </summary>
public class CollectiblePluginContext : AssemblyLoadContext
{
    private readonly string? _pluginDirectory;

    public CollectiblePluginContext(string? name = null, string? pluginDirectory = null) 
        : base(name ?? $"PluginContext_{Guid.NewGuid():N}", isCollectible: true)
    {
        _pluginDirectory = pluginDirectory;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? simpleName = assemblyName.Name;
        if (string.IsNullOrEmpty(simpleName)) return null;

        // 1. 若為宿主核心組件或已在 Default 上下文中載入者，優先委派以維持型別單一與介面相容性
        var existing = AssemblyLoadContext.Default.Assemblies
            .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            return existing;
        }

        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
        }
        catch
        {
            // 2. 若 Default 無法解析，且指定了外掛所在目錄，嘗試尋找外掛周邊的相依 DLL (以非鎖檔串流讀取)
            if (!string.IsNullOrEmpty(_pluginDirectory))
            {
                string candidate = Path.Combine(_pluginDirectory, $"{simpleName}.dll");
                if (File.Exists(candidate))
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(candidate);
                        using var ms = new MemoryStream(bytes);
                        string pdbPath = Path.Combine(_pluginDirectory, $"{simpleName}.pdb");
                        if (File.Exists(pdbPath))
                        {
                            byte[] pdbBytes = File.ReadAllBytes(pdbPath);
                            using var pdbMs = new MemoryStream(pdbBytes);
                            return LoadFromStream(ms, pdbMs);
                        }
                        return LoadFromStream(ms);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[CollectiblePluginContext] Failed to load dependent assembly '{simpleName}' from '{candidate}': {ex.Message}");
                    }
                }
            }
            return null;
        }
    }

    /// <summary>
    /// 從記憶體位元組串流載入組件與選擇性的 PDB 符號檔，避免磁碟鎖死
    /// </summary>
    public Assembly LoadAssemblyBytes(byte[] rawBytes, byte[]? pdbBytes = null)
    {
        using var ms = new MemoryStream(rawBytes);
        if (pdbBytes != null && pdbBytes.Length > 0)
        {
            using var pdbMs = new MemoryStream(pdbBytes);
            return LoadFromStream(ms, pdbMs);
        }
        return LoadFromStream(ms);
    }
}

/// <summary>
/// 動態外掛與 CSVI 組件之全域生命週期管理器 (支援熱更替換、動態 Unload 與零磁碟鎖定)
/// </summary>
public static class PluginLoadManager
{
    public class ManagedPluginEntry
    {
        public string Key { get; }
        public string? FilePath { get; }
        public DateTime LastWriteTimeUtc { get; set; }
        public CollectiblePluginContext Context { get; set; }
        public Assembly Assembly { get; set; }
        public WeakReference ContextWeakRef { get; }

        public ManagedPluginEntry(string key, string? filePath, DateTime lastWrite, CollectiblePluginContext context, Assembly assembly)
        {
            Key = key;
            FilePath = filePath;
            LastWriteTimeUtc = lastWrite;
            Context = context;
            Assembly = assembly;
            ContextWeakRef = new WeakReference(context, trackResurrection: false);
        }
    }

    private static readonly ConcurrentDictionary<string, ManagedPluginEntry> _plugins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 取得目前所有已載入之動態外掛項目
    /// </summary>
    public static IReadOnlyCollection<ManagedPluginEntry> ActivePlugins => _plugins.Values.ToList();

    /// <summary>
    /// 從指定實體檔案載入或重新載入組件。
    /// 若檔案已在記憶體中且最後修改時間未變，則直接回傳快取之 Assembly；
    /// 若檔案已被重新編譯或時間更新，自動執行舊上下文的 Unload() 並以全新可收集上下文載入。
    /// </summary>
    public static Assembly? LoadOrReloadAssembly(string filePath, out bool reloaded)
    {
        reloaded = false;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(filePath);
        var fileInfo = new FileInfo(fullPath);
        var lastWrite = fileInfo.LastWriteTimeUtc;

        if (_plugins.TryGetValue(fullPath, out var entry))
        {
            if (entry.LastWriteTimeUtc == lastWrite && entry.ContextWeakRef.IsAlive)
            {
                return entry.Assembly;
            }

            // 檔案已修改或上下文已死，執行熱更卸載
            Unload(fullPath);
            reloaded = true;
        }

        try
        {
            string dir = Path.GetDirectoryName(fullPath) ?? "";
            string contextName = $"Plugin_{Path.GetFileNameWithoutExtension(fullPath)}_{Guid.NewGuid():N}";
            var context = new CollectiblePluginContext(contextName, dir);

            byte[] bytes = File.ReadAllBytes(fullPath);
            byte[]? pdbBytes = null;
            string pdbPath = Path.ChangeExtension(fullPath, ".pdb");
            if (File.Exists(pdbPath))
            {
                try { pdbBytes = File.ReadAllBytes(pdbPath); } catch { }
            }

            var asm = context.LoadAssemblyBytes(bytes, pdbBytes);
            var newEntry = new ManagedPluginEntry(fullPath, fullPath, lastWrite, context, asm);
            _plugins[fullPath] = newEntry;

            return asm;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PluginLoadManager] Error loading assembly from '{fullPath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 從原始位元組載入至獨立之 CollectiblePluginContext，若指定相同的 key 則會先卸載舊上下文
    /// </summary>
    public static Assembly LoadFromBytes(string key, byte[] bytes, byte[]? pdbBytes = null, string? pluginDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            key = $"BytesPlugin_{Guid.NewGuid():N}";

        if (_plugins.ContainsKey(key))
        {
            Unload(key);
        }

        string contextName = $"Plugin_{key}_{Guid.NewGuid():N}";
        var context = new CollectiblePluginContext(contextName, pluginDirectory);
        var asm = context.LoadAssemblyBytes(bytes, pdbBytes);

        var entry = new ManagedPluginEntry(key, null, DateTime.UtcNow, context, asm);
        _plugins[key] = entry;

        return asm;
    }

    /// <summary>
    /// 卸載指定 Key 或檔案路徑對應的外掛上下文，釋放記憶體
    /// </summary>
    public static bool Unload(string keyOrPath)
    {
        if (string.IsNullOrWhiteSpace(keyOrPath)) return false;

        string normalized = keyOrPath;
        if (File.Exists(keyOrPath))
        {
            normalized = Path.GetFullPath(keyOrPath);
        }

        if (_plugins.TryRemove(normalized, out var entry))
        {
            try
            {
                entry.Context.Unload();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PluginLoadManager] Exception during Unload of '{keyOrPath}': {ex.Message}");
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 卸載所有動態外掛上下文
    /// </summary>
    public static void UnloadAll()
    {
        foreach (var key in _plugins.Keys)
        {
            Unload(key);
        }
    }

    /// <summary>
    /// 檢查指定外掛是否已在載入狀態
    /// </summary>
    public static bool IsLoaded(string keyOrPath)
    {
        string normalized = File.Exists(keyOrPath) ? Path.GetFullPath(keyOrPath) : keyOrPath;
        return _plugins.TryGetValue(normalized, out var entry) && entry.ContextWeakRef.IsAlive;
    }

    /// <summary>
    /// 取得指定外掛目前快取的 Assembly
    /// </summary>
    public static Assembly? GetAssembly(string keyOrPath)
    {
        string normalized = File.Exists(keyOrPath) ? Path.GetFullPath(keyOrPath) : keyOrPath;
        if (_plugins.TryGetValue(normalized, out var entry) && entry.ContextWeakRef.IsAlive)
        {
            return entry.Assembly;
        }
        return null;
    }
}
