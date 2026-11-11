using System;
using System.IO;
using System.Text.Json;
using ARCOS.Core.Common;

namespace ARCOS.App.Services;

/// <summary>
/// SUPERVISOR 權限帳戶管理器
/// 透過根目錄下的 Administrator.json 管理帳號與密碼；若未設定帳號密碼則允許自由切換 OPERATOR / SUPERVISOR。
/// </summary>
public sealed class AdministratorAccountManager
{
    public static AdministratorAccountManager Instance { get; } = new();

    public sealed class AdminConfig
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public string ConfigFilePath => Path.Combine(ArcosPaths.RootDirectory, "Administrator.json");

    public AdminConfig LoadConfig()
    {
        try
        {
            string path = ConfigFilePath;
            if (!File.Exists(path))
            {
                return new AdminConfig();
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AdminConfig>(json) ?? new AdminConfig();
        }
        catch
        {
            return new AdminConfig();
        }
    }

    public void SaveConfig(string username, string password)
    {
        try
        {
            string path = ConfigFilePath;
            if (string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(password))
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return;
            }

            var cfg = new AdminConfig
            {
                Username = username?.Trim() ?? "",
                Password = password ?? ""
            };
            string json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
        }
    }

    public bool HasCredentialsConfigured()
    {
        var cfg = LoadConfig();
        return !string.IsNullOrWhiteSpace(cfg.Username) || !string.IsNullOrWhiteSpace(cfg.Password);
    }

    public bool Verify(string username, string password)
    {
        var cfg = LoadConfig();
        if (string.IsNullOrWhiteSpace(cfg.Username) && string.IsNullOrWhiteSpace(cfg.Password))
        {
            return true;
        }

        bool userMatch = string.IsNullOrWhiteSpace(cfg.Username) ||
                         string.Equals(cfg.Username.Trim(), (username ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        bool passMatch = string.Equals(cfg.Password ?? "", password ?? "", StringComparison.Ordinal);
        return userMatch && passMatch;
    }
}
