using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XColumn.Helpers;
using XColumn.Models;

namespace XColumn.Services
{
    /// <summary>
    /// ISettingsService の既定実装。
    /// DPAPI(ProtectedData)でプロファイル設定を暗号化し、ファイルへ永続化します。
    /// </summary>
    public class SettingsService : ISettingsService
    {
        // ===== Constants =====

        /// <summary>
        /// DPAPI暗号化に使用するエントロピー（追加のソルトのようなもの）。
        /// 旧 MainWindow._entropy と同一の値を維持すること。
        /// これを変えると既存ユーザーの設定が復号できなくなります。
        /// </summary>
        private static readonly byte[] _entropy = { 0x1A, 0x2B, 0x3C, 0x4D, 0x5E };

        // ===== Properties =====

        /// <inheritdoc/>
        public string UserDataFolder { get; }

        /// <inheritdoc/>
        public string ProfilesFolder { get; }

        /// <inheritdoc/>
        public string AppConfigPath { get; }

        // ===== Constructor =====

        /// <summary>
        /// 各種パスを解決し、必要なフォルダを作成します。
        /// </summary>
        /// <param name="customUserDataFolder">テスト時などに指定するカスタムユーザーデータフォルダ（省略時は %AppData%\XColumn）。</param>
        public SettingsService(string? customUserDataFolder = null)
        {
            UserDataFolder = !string.IsNullOrEmpty(customUserDataFolder)
                ? customUserDataFolder
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XColumn");
            ProfilesFolder = Path.Combine(UserDataFolder, "Profiles");
            AppConfigPath = Path.Combine(UserDataFolder, "app_config.json");

            // 念のため必要なフォルダを確保しておく
            Directory.CreateDirectory(ProfilesFolder);
        }

        // ===== Public Methods (Path) =====

        /// <inheritdoc/>
        public string GetProfilePath(string profileName)
            => Path.Combine(ProfilesFolder, $"{profileName}.dat");

        // ===== Public Methods (App Config I/O) =====

        /// <inheritdoc/>
        public AppConfig LoadAppConfig()
        {
            if (!File.Exists(AppConfigPath)) return new AppConfig();

            try
            {
                string json = File.ReadAllText(AppConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
            catch
            {
                return new AppConfig();
            }
        }

        /// <inheritdoc/>
        public void SaveAppConfig(AppConfig config)
        {
            try
            {
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(AppConfigPath, json);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save app config: {ex.Message}");
            }
        }

        // ===== Public Methods (Profile Settings I/O) =====

        /// <inheritdoc/>
        public AppSettings ReadSettings(string profileName)
        {
            string path = GetProfilePath(profileName);
            if (!File.Exists(path)) return new AppSettings();

            try
            {
                byte[] encrypted = File.ReadAllBytes(path);
                byte[] jsonBytes = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
                var settings = JsonSerializer.Deserialize<AppSettings>(Encoding.UTF8.GetString(jsonBytes)) ?? new AppSettings();

                // マイグレーション処理（古いバージョンの設定を補正）
                MigrateSettingsIfNeeded(settings);

                return settings;
            }
            catch
            {
                // 復号またはデシリアライズに失敗した場合はデフォルト設定を返す
                return new AppSettings();
            }
        }

        private static void MigrateSettingsIfNeeded(AppSettings settings)
        {
            if (settings.ConfigVersion < 1)
            {
                settings.ConfigVersion = 1;
            }
            // 画面外座標の安全ガード（バグ#2補正）
            if (settings.WindowLeft < -10000 || settings.WindowTop < -10000)
            {
                settings.WindowLeft = 100;
                settings.WindowTop = 100;
            }
        }

        /// <inheritdoc/>
        public void SaveSettings(string profileName, AppSettings settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings);
                byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), _entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(GetProfilePath(profileName), encrypted);
            }
            catch (Exception ex)
            {
                Logger.Log($"Save failed: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        public bool ClearBrowserCache(string profileName)
        {
            try
            {
                if (string.IsNullOrEmpty(profileName)) profileName = "Default";
                string browserDataFolder = Path.Combine(UserDataFolder, "BrowserData", profileName, "EBWebView", "Default");
                if (!Directory.Exists(browserDataFolder)) return true;

                // キャッシュ系ディレクトリを特定して削除
                string[] cacheDirs = { "Cache", "Code Cache", "GPUCache", "DawnCache", "Service Worker" };
                foreach (var dirName in cacheDirs)
                {
                    string target = Path.Combine(browserDataFolder, dirName);
                    if (Directory.Exists(target))
                    {
                        try { Directory.Delete(target, true); } catch { /* ロック中のファイルはスキップ */ }
                    }
                }
                Logger.Log($"[SettingsService] Browser cache cleared for profile: {profileName}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[SettingsService] ClearBrowserCache failed: {ex.Message}");
                return false;
            }
        }
    }
}