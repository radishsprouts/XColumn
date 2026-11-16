using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using Application = System.Windows.Application;
using XColumn.Helpers;

namespace XColumn.Services
{
    /// <summary>
    /// IUpdateService の既定実装。
    /// </summary>
    public class UpdateService : IUpdateService
    {
        private const string DefaultReleasesApiUrl = "https://api.github.com/repos/radishsprouts/XColumn/releases/latest";
        private readonly HttpClient _httpClient;

        public UpdateService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
        }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion, string skippedVersion, CancellationToken ct = default)
        {
            var result = new UpdateCheckResult();
            try
            {
                Version current = new Version(string.IsNullOrEmpty(currentVersion) ? "1.0.0" : currentVersion);

                using var request = new HttpRequestMessage(HttpMethod.Get, DefaultReleasesApiUrl);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("XColumn", currentVersion));

                var res = await _httpClient.SendAsync(request, ct);
                if (!res.IsSuccessStatusCode) return result;

                string jsonStr = await res.Content.ReadAsStringAsync(ct);
                var json = JsonNode.Parse(jsonStr);
                if (json == null) return result;

                string tag = json["tag_name"]?.GetValue<string>() ?? "v0.0.0";
                result.ReleaseUrl = json["html_url"]?.GetValue<string>() ?? "";
                result.ReleaseNotes = json["body"]?.GetValue<string>() ?? "";

                string remoteVerStr = tag.TrimStart('v');
                result.RemoteVersion = remoteVerStr;
                Version remote = new Version(remoteVerStr);

                if (remote > current && remoteVerStr != skippedVersion)
                {
                    result.HasUpdate = true;
                    var (assetName, assetUrl) = SelectUpdateAsset(json);
                    result.AssetName = assetName;
                    result.AssetDownloadUrl = assetUrl;

                    if (assetName != null)
                    {
                        result.Sha256DownloadUrl = FindAssetSha256Url(json, assetName);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Update check failed: {ex.Message}");
            }

            return result;
        }

        public async Task ApplyUpdateAsync(UpdateCheckResult updateInfo, string activeProfileName, Action<string>? onStatusUpdate = null, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(updateInfo.AssetDownloadUrl) || string.IsNullOrEmpty(updateInfo.AssetName))
                throw new InvalidOperationException("Asset information is missing.");

            string tempZip = Path.Combine(Path.GetTempPath(), updateInfo.AssetName);
            string stageDir = Path.Combine(Path.GetTempPath(), "XColumn_update_" + Guid.NewGuid().ToString("N"));

            try
            {
                onStatusUpdate?.Invoke("ダウンロード中...");
                using (var res = await _httpClient.GetAsync(updateInfo.AssetDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    res.EnsureSuccessStatusCode();
                    await using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);
                    await res.Content.CopyToAsync(fs, ct);
                }

                // SHA256 検証
                if (!string.IsNullOrEmpty(updateInfo.Sha256DownloadUrl))
                {
                    onStatusUpdate?.Invoke("整合性チェック中...");
                    string shaText = await _httpClient.GetStringAsync(updateInfo.Sha256DownloadUrl, ct);
                    string expected = new string(shaText.Trim().TakeWhile(Uri.IsHexDigit).ToArray());
                    if (expected.Length == 64)
                    {
                        string actual = ComputeSha256Hex(tempZip);
                        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("SHA-256 hash mismatch.");
                    }
                }

                // ZIP展開 & Zip Slip防止
                onStatusUpdate?.Invoke("展開中...");
                Directory.CreateDirectory(stageDir);
                string newExePath = ExtractSingleExe(tempZip, stageDir);

                // リネーム方式で適用
                string currentExe = Process.GetCurrentProcess().MainModule?.FileName
                                    ?? throw new InvalidOperationException("Cannot resolve current executable path.");
                string oldExe = currentExe + ".old";

                if (File.Exists(oldExe))
                {
                    try { File.SetAttributes(oldExe, FileAttributes.Normal); } catch { }
                    try { File.Delete(oldExe); } catch { }
                }

                File.Move(currentExe, oldExe, overwrite: true);
                try
                {
                    File.Copy(newExePath, currentExe, overwrite: true);
                }
                catch
                {
                    try { if (File.Exists(currentExe)) File.Delete(currentExe); File.Move(oldExe, currentExe); } catch { }
                    throw;
                }

                // 新exe起動 → 自己終了（バグ#4修正: --wait-pid を確実に付与）
                Process.Start(new ProcessStartInfo
                {
                    FileName = currentExe,
                    Arguments = $"--wait-pid {Environment.ProcessId} --profile \"{activeProfileName}\"",
                    UseShellExecute = true
                });

                Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { }
            }
        }

        private static bool IsSelfContainedBuild()
        {
            var attr = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => string.Equals(a.Key, "BuildType", StringComparison.OrdinalIgnoreCase));
            return string.Equals(attr?.Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static (string? name, string? url) SelectUpdateAsset(JsonNode releaseJson)
        {
            bool wantContained = IsSelfContainedBuild();
            var assets = releaseJson["assets"]?.AsArray();
            if (assets == null) return (null, null);

            foreach (var a in assets)
            {
                string name = a?["name"]?.GetValue<string>() ?? "";
                string dl = a?["browser_download_url"]?.GetValue<string>() ?? "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                bool isContained = name.Contains("_Contained", StringComparison.OrdinalIgnoreCase);
                if (isContained != wantContained) continue;
                if (!dl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;

                return (name, dl);
            }
            return (null, null);
        }

        private static string? FindAssetSha256Url(JsonNode releaseJson, string zipAssetName)
        {
            var assets = releaseJson["assets"]?.AsArray();
            if (assets == null) return null;

            string target = zipAssetName + ".sha256";
            foreach (var a in assets)
            {
                string name = a?["name"]?.GetValue<string>() ?? "";
                if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                    return a?["browser_download_url"]?.GetValue<string>();
            }
            return null;
        }

        private static string ExtractSingleExe(string zipPath, string stageDir)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            string fullStage = Path.GetFullPath(stageDir);

            foreach (var e in archive.Entries)
            {
                string full = Path.GetFullPath(Path.Combine(fullStage, e.FullName));
                if (!full.StartsWith(fullStage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Zip Slip detected: " + e.FullName);
            }

            var exeEntries = archive.Entries
                .Where(e => string.Equals(e.Name, "XColumn.exe", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exeEntries.Count != 1)
                throw new InvalidOperationException($"Expected exactly one XColumn.exe in zip, found {exeEntries.Count}.");

            string destPath = Path.Combine(fullStage, "XColumn.exe");
            exeEntries[0].ExtractToFile(destPath, overwrite: true);
            return destPath;
        }

        private static string ComputeSha256Hex(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        }
    }
}
