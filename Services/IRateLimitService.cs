using XColumn.Models;

namespace XColumn.Services
{
    /// <summary>
    /// レート制限の監視対象となるカラムの契約。
    /// </summary>
    public interface IRateLimitTarget
    {
        /// <summary>カラムのURL。</summary>
        string Url { get; }

        /// <summary>プロファイル名。</summary>
        string? ProfileName { get; }

        /// <summary>X以外の外部サイトかどうか。</summary>
        bool IsExternalSite { get; }

        /// <summary>自動更新が有効かどうか。</summary>
        bool IsAutoRefreshEnabled { get; }

        /// <summary>自動更新間隔（秒）。</summary>
        int RefreshIntervalSeconds { get; }

        /// <summary>現在のレート制限ステータス。</summary>
        RateLimitStatus RateLimitStatus { get; set; }

        /// <summary>最新の観測残数。</summary>
        int RateLimitRemaining { get; set; }

        /// <summary>レート制限リセット予定時刻。</summary>
        DateTimeOffset RateLimitResetTime { get; set; }

        /// <summary>手動またはレート制限による休止中かどうか。</summary>
        bool IsSuspended { get; }

        /// <summary>残数とリセット時刻が観測された際のステータス更新。</summary>
        void UpdateRateLimit(int remaining, DateTimeOffset resetTime, bool ignoreProtection);

        /// <summary>429エラーが発生した際の通知。</summary>
        void OnRateLimitTriggered(DateTimeOffset? resetTime, bool hasResetHeader);
    }

    /// <summary>
    /// X/Twitter のレート制限（429）監視、残数追跡、バックオフ計算、および到達推定時間を担当するサービス。
    /// </summary>
    public interface IRateLimitService
    {
        /// <summary>
        /// HTTPレスポンスヘッダーを解析し、必要に応じてカラムのレート制限状態を更新します。
        /// </summary>
        void ProcessResponse(string requestUri, int statusCode, IReadOnlyDictionary<string, string> headers, IRateLimitTarget target, bool ignoreProtection);

        /// <summary>
        /// カラムURLから主タイムラインの GraphQL Operation 名を分類・判定します。
        /// </summary>
        string ClassifyOperation(string? url);

        /// <summary>
        /// 観測したOperationが対象カラムの主タイムライン取得操作かどうかを判定します。
        /// </summary>
        bool IsPrimaryTimelineOperation(string columnUrl, string operation);

        /// <summary>
        /// 現在のカラム構成から、無操作時に 429 到達までの推定時間を算出します。
        /// </summary>
        string BuildRateLimitEstimate(IEnumerable<IRateLimitTarget> columns, string activeProfileName);
    }
}
