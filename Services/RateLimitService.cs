using System.Text;
using System.Windows;
using Application = System.Windows.Application;
using XColumn.Helpers;
using XColumn.Models;

namespace XColumn.Services
{
    /// <summary>
    /// IRateLimitService の既定実装。
    /// </summary>
    public class RateLimitService : IRateLimitService
    {
        private const int RateLimitCautionThreshold = 10;
        private const int RateLimitStopThreshold = 5;
        private const int RateLimitMinCooldownSeconds = 60;
        private const int RateLimitMaxCooldownSeconds = 900;
        private const int RL429_WindowSeconds = 900;

        // 連続トリップ追跡用（カラムUrl単位）
        private readonly Dictionary<string, (int streak, DateTime lastTrip)> _tripStreaks = new();

        public void ProcessResponse(string requestUri, int statusCode, IReadOnlyDictionary<string, string> headers, IRateLimitTarget target, bool ignoreProtection)
        {
            if (target.IsExternalSite || target.IsSuspended) return;

            string op = ExtractGraphqlOperation(requestUri);

            // 1. レート制限ヘッダー監視 (200 OK 含む)
            if (headers.TryGetValue("x-rate-limit-remaining", out string? remainingStr) &&
                int.TryParse(remainingStr, out int remaining))
            {
                if (IsPrimaryTimelineOperation(target.Url, op))
                {
                    DateTimeOffset resetTime = DateTimeOffset.Now.AddMinutes(15);
                    if (headers.TryGetValue("x-rate-limit-reset", out string? resetStr) &&
                        long.TryParse(resetStr, out long unix))
                    {
                        resetTime = DateTimeOffset.FromUnixTimeSeconds(unix);
                    }

                    // UIスレッドへの非同期ディスパッチ（バグ#6対策: 同期Invokeを排除）
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        target.UpdateRateLimit(remaining, resetTime, ignoreProtection);
                    });
                }
            }

            // 2. 429 エラー検知
            if (statusCode == 429 && !ignoreProtection)
            {
                if (IsPrimaryTimelineOperation(target.Url, op))
                {
                    bool hasReset = false;
                    DateTimeOffset resetTime = DateTimeOffset.Now.AddMinutes(15);

                    if (headers.TryGetValue("x-rate-limit-reset", out string? resetStr) &&
                        long.TryParse(resetStr, out long unix))
                    {
                        resetTime = DateTimeOffset.FromUnixTimeSeconds(unix);
                        hasReset = true;
                    }

                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        target.OnRateLimitTriggered(hasReset ? resetTime : null, hasReset);
                    });
                }
            }
        }

        public string ClassifyOperation(string? url)
        {
            url ??= "";
            if (url.Contains("/search")) return "SearchTimeline";
            if (url.Contains("/lists/") || url.Contains("/i/lists/")) return "ListLatestTweetsTimeline";
            if (url.Contains("/notifications")) return "NotificationsTimeline";
            if (url.Contains("/home")) return "HomeTimeline";
            return "UserTweets";
        }

        public bool IsPrimaryTimelineOperation(string columnUrl, string operation)
        {
            string url = columnUrl ?? "";
            if (url.Contains("/search")) return operation == "SearchTimeline";
            if (url.Contains("/lists/") || url.Contains("/i/lists/")) return operation == "ListLatestTweetsTimeline";
            if (url.Contains("/notifications")) return operation == "NotificationsTimeline";
            if (url.Contains("/home")) return operation == "HomeTimeline" || operation == "HomeLatestTimeline";
            return operation == "UserTweets" || operation == "UserTweetsAndReplies" || operation == "UserMedia";
        }

        public static string ExtractGraphqlOperation(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            try
            {
                var uri = new Uri(url);
                string path = uri.AbsolutePath;
                int idx = path.IndexOf("/graphql/", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return "";

                string sub = path.Substring(idx + "/graphql/".Length);
                string[] parts = sub.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) return parts[1]; // /graphql/{queryId}/{OperationName}
                if (parts.Length == 1) return parts[0];
                return "";
            }
            catch { return ""; }
        }

        public string BuildRateLimitEstimate(IEnumerable<IRateLimitTarget> columns, string activeProfileName)
        {
            var targets = columns
                .Where(c => !c.IsExternalSite && c.IsAutoRefreshEnabled && c.RefreshIntervalSeconds > 0)
                .ToList();

            if (targets.Count == 0) return "自動更新が有効な対象カラムがありません。";

            var groups = targets.GroupBy(c =>
            {
                string profile = string.IsNullOrEmpty(c.ProfileName) ? activeProfileName : c.ProfileName!;
                string op = ClassifyOperation(c.Url);
                return (profile, op);
            });

            var sb = new StringBuilder();
            double? shortest = null;

            foreach (var g in groups)
            {
                double requestsPerSec = g.Sum(c => 1.0 / c.RefreshIntervalSeconds);
                if (requestsPerSec <= 0) continue;

                int budget = BudgetFor(g.Key.op);
                double naturalConsumptionInWindow = requestsPerSec * RL429_WindowSeconds;
                double secondsTo429 = double.PositiveInfinity;

                if (naturalConsumptionInWindow > budget)
                {
                    secondsTo429 = budget / requestsPerSec;
                    if (!shortest.HasValue || secondsTo429 < shortest.Value)
                    {
                        shortest = secondsTo429;
                    }
                }

                sb.AppendLine($"・[{g.Key.profile}] {g.Key.op}:");
                sb.AppendLine($"    カラム数: {g.Count()} / 合計レート: {requestsPerSec * 60:F1} 回/分 (枠: {budget}回/15分)");
                sb.AppendLine($"    推定429到達: {(double.IsInfinity(secondsTo429) ? "到達しない（安全）" : FormatHms(secondsTo429))}");
            }

            string summary = shortest.HasValue
                ? $"【最短到達目安: 約 {FormatHms(shortest.Value)}】\n\n"
                : "【現在の設定では無操作で429に到達するカラム群はありません】\n\n";

            return summary + sb.ToString();
        }

        private static int BudgetFor(string op) => op switch
        {
            "SearchTimeline" => 50,
            "NotificationsTimeline" => 1500,
            _ => 500
        };

        private static string FormatHms(double seconds)
        {
            if (double.IsInfinity(seconds) || double.IsNaN(seconds)) return "—";
            var ts = TimeSpan.FromSeconds(Math.Max(seconds, 0));
            return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
                                      : $"{ts.Minutes}:{ts.Seconds:00}";
        }
    }
}
