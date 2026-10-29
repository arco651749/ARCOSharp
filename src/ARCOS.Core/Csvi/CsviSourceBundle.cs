using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ARCOS.Core.Csvi;

/// <summary>
/// CSVI 3.0 原始碼打包器 (提供明文壓縮、AES-256-GCM 加密封裝與解密解包)
/// </summary>
public static class CsviSourceBundle
{
    private const int KeyDerivationIterations = 100_000;
    private const int KeySizeBytes = 32; // 256 bits
    private const int SaltSizeBytes = 16;
    private const int NonceSizeBytes = 12; // GCM standard nonce
    private const int TagSizeBytes = 16;  // GCM tag size

    /// <summary>
    /// 將原始碼字典封裝為 byte[]（支援明文 Zip 或 AES-256-GCM 加密）
    /// </summary>
    /// <param name="sourceFiles">檔名 -> 內容 字典 (如 "Logic.cs" => "...")</param>
    /// <param name="mode">封裝模式</param>
    /// <param name="password">密碼 (若為 PasswordLocked)</param>
    public static byte[] Pack(Dictionary<string, string> sourceFiles, CsviPackagingMode mode, string? password = null)
    {
        if (mode == CsviPackagingMode.StrippedRelease)
        {
            return Array.Empty<byte>();
        }

        // 1. 將多個原始碼檔案壓縮為 ZIP byte[]
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true, Encoding.UTF8))
        {
            foreach (var kvp in sourceFiles)
            {
                var entry = archive.CreateEntry(kvp.Key, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var writer = new StreamWriter(entryStream, Encoding.UTF8);
                writer.Write(kvp.Value);
            }
        }
        byte[] zipBytes = memoryStream.ToArray();

        if (mode == CsviPackagingMode.PortableOpen)
        {
            return zipBytes;
        }

        if (mode == CsviPackagingMode.PasswordLocked)
        {
            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("Password is required for PasswordLocked packaging mode.", nameof(password));
            }
            return EncryptAesGcm(zipBytes, password);
        }

        return Array.Empty<byte>();
    }

    /// <summary>
    /// 從 byte[] 解包出原始碼字典
    /// </summary>
    public static Dictionary<string, string> Unpack(byte[] bundleBytes, CsviPackagingMode mode, string? password = null)
    {
        if (bundleBytes == null || bundleBytes.Length == 0 || mode == CsviPackagingMode.StrippedRelease)
        {
            return new Dictionary<string, string>();
        }

        byte[] zipBytes;
        if (mode == CsviPackagingMode.PortableOpen)
        {
            zipBytes = bundleBytes;
        }
        else if (mode == CsviPackagingMode.PasswordLocked)
        {
            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("Password is required to unlock this CSVI bundle.", nameof(password));
            }
            zipBytes = DecryptAesGcm(bundleBytes, password);
        }
        else
        {
            return new Dictionary<string, string>();
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var memoryStream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8);
            result[entry.FullName] = reader.ReadToEnd();
        }

        return result;
    }

    /// <summary>
    /// 計算原始碼集合之 SHA-256 簽章
    /// </summary>
    public static string ComputeSha256(Dictionary<string, string> sourceFiles)
    {
        var sortedKeys = sourceFiles.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();
        foreach (var key in sortedKeys)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var valBytes = Encoding.UTF8.GetBytes(sourceFiles[key]);
            ms.Write(keyBytes);
            ms.WriteByte(0);
            ms.Write(valBytes);
            ms.WriteByte(0);
        }
        ms.Position = 0;
        byte[] hash = sha.ComputeHash(ms);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] EncryptAesGcm(byte[] plaintext, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);

        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, KeyDerivationIterations, HashAlgorithmName.SHA256);
        byte[] key = pbkdf2.GetBytes(KeySizeBytes);

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSizeBytes];

        using (var aesGcm = new AesGcm(key, TagSizeBytes))
        {
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        // Layout: [Salt (16)] + [Nonce (12)] + [Tag (16)] + [Ciphertext]
        using var outputMs = new MemoryStream();
        outputMs.Write(salt);
        outputMs.Write(nonce);
        outputMs.Write(tag);
        outputMs.Write(ciphertext);
        return outputMs.ToArray();
    }

    private static byte[] DecryptAesGcm(byte[] encryptedBundle, string password)
    {
        if (encryptedBundle.Length < SaltSizeBytes + NonceSizeBytes + TagSizeBytes)
        {
            throw new CryptographicException("Encrypted bundle payload is truncated or invalid.");
        }

        using var ms = new MemoryStream(encryptedBundle);
        byte[] salt = new byte[SaltSizeBytes];
        byte[] nonce = new byte[NonceSizeBytes];
        byte[] tag = new byte[TagSizeBytes];

        ms.ReadExactly(salt);
        ms.ReadExactly(nonce);
        ms.ReadExactly(tag);

        byte[] ciphertext = new byte[encryptedBundle.Length - (SaltSizeBytes + NonceSizeBytes + TagSizeBytes)];
        ms.ReadExactly(ciphertext);

        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, KeyDerivationIterations, HashAlgorithmName.SHA256);
        byte[] key = pbkdf2.GetBytes(KeySizeBytes);

        byte[] plaintext = new byte[ciphertext.Length];
        using (var aesGcm = new AesGcm(key, TagSizeBytes))
        {
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return plaintext;
    }
}
