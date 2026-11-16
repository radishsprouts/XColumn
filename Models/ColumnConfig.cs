namespace XColumn.Models
{
    /// <summary>
    /// カラムの永続化設定データモデル（純粋なPOCO）。
    /// UIやWebViewコントロール、タイマーへの依存を一切持たず、
    /// 設定ファイル（.dat / JSON）との完全なシリアライズ互換性を保証します。
    /// </summary>
    public class ColumnConfig
    {
        /// <summary>表示するURL（基点URL）。</summary>
        public string Url { get; set; } = "https://x.com/home";

        /// <summary>カラム幅（px）。0の場合はグローバル設定が適用されます。</summary>
        public double Width { get; set; } = 380;

        /// <summary>自動更新間隔（秒）。</summary>
        public int RefreshIntervalSeconds { get; set; } = 300;

        /// <summary>自動更新が有効かどうか。</summary>
        public bool IsAutoRefreshEnabled { get; set; } = false;

        /// <summary>リポスト（RT）を非表示にするかどうか。</summary>
        public bool IsRetweetHidden { get; set; } = false;

        /// <summary>リプライ（返信）を非表示にするかどうか。</summary>
        public bool IsReplyHidden { get; set; } = false;

        /// <summary>カラムの削除や戻る操作をロックするかどうか。</summary>
        public bool IsLocked { get; set; } = false;

        /// <summary>ズーム倍率（1.0 = 100%）。</summary>
        public double ZoomFactor { get; set; } = 1.0;

        /// <summary>メディア（画像・動画）の表示倍率（％）。</summary>
        public int MediaScalePercentage { get; set; } = 100;

        /// <summary>このカラムが使用するプロファイル名（nullの場合はアクティブプロファイル）。</summary>
        public string? ProfileName { get; set; }

        /// <summary>X以外の外部サイトカラムかどうか。</summary>
        public bool IsExternalSite { get; set; } = false;

        /// <summary>リスト自動遷移カラムかどうか。</summary>
        public bool IsListAutoNav { get; set; } = false;

        /// <summary>直前の有効な基点URL（設定ページ等からの復帰用）。</summary>
        public string LastValidUrl { get; set; } = "https://x.com/home";

        /// <summary>最終入力時刻。</summary>
        public DateTime LastInputTime { get; set; } = DateTime.MinValue;
    }
}
