using System.IO;
using Microsoft.Web.WebView2.Core;
using XColumn.Helpers;
using XColumn.Models;

namespace XColumn.Services
{
    /// <summary>
    /// IWebViewEnvironmentService の既定実装。
    /// </summary>
    public class WebViewEnvironmentService : IWebViewEnvironmentService
    {
        private readonly Dictionary<string, CoreWebView2Environment> _environments = new();
        private readonly HashSet<string> _loadedProfilesExtensions = new();
        private readonly string _userDataFolder;

        public WebViewEnvironmentService()
        {
            _userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XColumn");
        }

        public async Task<CoreWebView2Environment?> GetOrCreateEnvironmentAsync(string profileName, bool disablePopupBlocking = false)
        {
            if (string.IsNullOrEmpty(profileName)) profileName = "Default";

            if (_environments.TryGetValue(profileName, out var cachedEnv))
            {
                return cachedEnv;
            }

            try
            {
                string browserDataFolder = Path.Combine(_userDataFolder, "BrowserData", profileName);
                Directory.CreateDirectory(browserDataFolder);

                var options = new CoreWebView2EnvironmentOptions
                {
                    AreBrowserExtensionsEnabled = true,
                    AdditionalBrowserArguments = disablePopupBlocking ? "--disable-popup-blocking" : ""
                };

                var env = await CoreWebView2Environment.CreateAsync(null, browserDataFolder, options);
                _environments[profileName] = env;
                return env;
            }
            catch (Exception ex)
            {
                Logger.Log($"[WebViewEnvironmentService] Failed to create environment for {profileName}: {ex.Message}");
                return null;
            }
        }

        public async Task LoadExtensionsAsync(CoreWebView2Profile profile, string profileName, IEnumerable<ExtensionItem> extensions)
        {
            if (_loadedProfilesExtensions.Contains(profileName)) return;

            foreach (var ext in extensions)
            {
                if (ext.IsEnabled && Directory.Exists(ext.Path))
                {
                    try
                    {
                        var loaded = await profile.AddBrowserExtensionAsync(ext.Path);
                        ext.Id = loaded.Id;
                        ext.OptionsPage = GetOptionsPagePath(ext.Path);
                        Logger.Log($"[WebViewEnvironmentService] Extension loaded: {ext.Name} (ID: {ext.Id})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[WebViewEnvironmentService] Extension load error: {ext.Name} - {ex.Message}");
                    }
                }
            }

            _loadedProfilesExtensions.Add(profileName);
        }

        public void SetPreferredColorScheme(CoreWebView2Profile profile, string theme)
        {
            try
            {
                profile.PreferredColorScheme = theme switch
                {
                    "Light" => CoreWebView2PreferredColorScheme.Light,
                    "Dark" => CoreWebView2PreferredColorScheme.Dark,
                    _ => CoreWebView2PreferredColorScheme.Auto
                };
                Logger.Log($"[WebViewEnvironmentService] PreferredColorScheme set to: {profile.PreferredColorScheme}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[WebViewEnvironmentService] SetPreferredColorScheme failed: {ex.Message}");
            }
        }

        public async Task<bool> TryRecoverFromProcessFailureAsync(CoreWebView2ProcessFailedEventArgs args, Action onReloadRequested)
        {
            Logger.Log($"[WebViewEnvironmentService] Process failed: Kind={args.ProcessFailedKind}, Reason={args.Reason}");

            // レンダラープロセスまたはGPUプロセスの異常終了時の自動リカバリ
            if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
                args.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive ||
                args.ProcessFailedKind == CoreWebView2ProcessFailedKind.GpuProcessExited)
            {
                try
                {
                    await Task.Delay(500); // プロセス解放を少し待機
                    onReloadRequested();
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[WebViewEnvironmentService] Recovery reload failed: {ex.Message}");
                }
            }

            return false;
        }

        private static string GetOptionsPagePath(string extensionFolder)
        {
            try
            {
                string manifestPath = Path.Combine(extensionFolder, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    string json = File.ReadAllText(manifestPath);
                    var node = System.Text.Json.Nodes.JsonNode.Parse(json);
                    return node?["options_page"]?.GetValue<string>()
                           ?? node?["options_ui"]?["page"]?.GetValue<string>()
                           ?? "";
                }
            }
            catch { }
            return "";
        }
    }
}
