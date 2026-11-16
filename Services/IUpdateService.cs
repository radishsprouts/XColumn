using System.Text.Json.Nodes;

namespace XColumn.Services
{
    /// <summary>
    /// アップデート確認結果の情報。
    /// </summary>
    public class UpdateCheckResult
    {
        public bool HasUpdate { get; set; }
        public string RemoteVersion { get; set; } = "";
        public string ReleaseUrl { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public string? AssetName { get; set; }
        public string? AssetDownloadUrl { get; set; }
        public string? Sha256DownloadUrl { get; set; }
    }

    /// <summary>
    /// GitHub Releases からのアップデート確認・ダウンロード・自己更新を担当するサービス。
    /// </summary>
    public interface IUpdateService
    {
        /// <summary>最新リリースを確認します。</summary>
        Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion, string skippedVersion, CancellationToken ct = default);

        /// <summary>更新ZIPをダウンロード・検証し、リネーム方式で新バージョンを適用して再起動します。</summary>
        Task ApplyUpdateAsync(UpdateCheckResult updateInfo, string activeProfileName, Action<string>? onStatusUpdate = null, CancellationToken ct = default);
    }
}
