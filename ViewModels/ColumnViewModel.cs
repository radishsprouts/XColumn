using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json.Serialization;
using XColumn.Helpers;
using XColumn.Models;
using XColumn.Services;

namespace XColumn.ViewModels
{
    /// <summary>
    /// カラムのUI状態、コマンド、およびタイマー/レート制限との連携を司る ViewModel。
    /// 以前の ColumnData から肥大化したロジックと WebView への強参照を完全に排除し、
    /// View への操作要求はデリゲート/イベント経由で行うことで循環参照とメモリリークを根絶します。
    /// </summary>
    public partial class ColumnViewModel : ViewModelBase, IColumnTimerTarget, IRateLimitTarget
    {
        private static readonly Random _jitterRandom = new();

        #region Identity

        /// <summary>
        /// カラムの一意識別子。
        /// </summary>
        public Guid Id { get; } = Guid.NewGuid();

        #endregion

        #region Observable Properties (UI バインド対象)

        /// <summary>
        /// アクティブ状態（選択中かどうか）。
        /// </summary>
        [ObservableProperty]
        private bool isActive;

        /// <summary>
        /// カラム幅（px）。
        /// </summary>
        [ObservableProperty]
        private double width = 380;

        /// <summary>
        /// Webページ内で入力フォームにフォーカスがあるかどうか。
        /// </summary>
        [ObservableProperty]
        private bool isInputActive;

        partial void OnIsInputActiveChanged(bool value)
        {
            if (value) LastInputTime = DateTime.Now;
        }

        /// <summary>
        /// スクロール位置がトップ（またはそれに近い）かどうか。
        /// </summary>
        [ObservableProperty]
        private bool isAtTop = true;

        /// <summary>
        /// メモリ解放のための休止状態かどうか。
        /// </summary>
        [ObservableProperty]
        private bool isSuspended = false;

        /// <summary>
        /// レート制限ステータス（Normal / Caution / Stopped）。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsRateLimitCaution))]
        [NotifyPropertyChangedFor(nameof(IsRateLimitStopped))]
        private RateLimitStatus rateLimitStatus = RateLimitStatus.Normal;

        /// <summary>
        /// 注意モード（残数僅少）かどうか。
        /// </summary>
        public bool IsRateLimitCaution => RateLimitStatus == RateLimitStatus.Caution;

        /// <summary>
        /// 更新停止モードかどうか。
        /// </summary>
        public bool IsRateLimitStopped => RateLimitStatus == RateLimitStatus.Stopped;

        /// <summary>
        /// カラムヘッダーにAPI残数を表示するかどうか。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsRateLimitRemainingVisible))]
        private bool showRateLimitRemaining;

        /// <summary>
        /// 主タイムラインのAPI残数（未取得時は -1）。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RateLimitRemainingText))]
        [NotifyPropertyChangedFor(nameof(IsRateLimitRemainingVisible))]
        private int rateLimitRemaining = -1;

        /// <summary>
        /// 残数表示テキスト。
        /// </summary>
        public string RateLimitRemainingText => RateLimitRemaining >= 0 ? RateLimitRemaining.ToString() : "";

        /// <summary>
        /// 残数表示の表示可否（設定ON かつ 取得済み）。
        /// </summary>
        public bool IsRateLimitRemainingVisible => ShowRateLimitRemaining && RateLimitRemaining >= 0;

        /// <summary>
        /// レート制限(429)による休止中かどうか。
        /// </summary>
        [ObservableProperty]
        private bool isRateLimited;

        /// <summary>
        /// 現在のカラムURL。
        /// </summary>
        [ObservableProperty]
        private string url = "https://x.com/home";

        /// <summary>
        /// 自動更新の間隔（秒）。
        /// </summary>
        [ObservableProperty]
        private int refreshIntervalSeconds = 300;

        partial void OnRefreshIntervalSecondsChanged(int value) => ResetCountdown();

        /// <summary>
        /// 自動更新が有効かどうか。
        /// </summary>
        [ObservableProperty]
        private bool isAutoRefreshEnabled = false;

        partial void OnIsAutoRefreshEnabledChanged(bool value) => ResetCountdown();

        /// <summary>
        /// リポスト非表示設定。
        /// </summary>
        [ObservableProperty]
        private bool isRetweetHidden = false;

        /// <summary>
        /// リプライ（返信）非表示設定。
        /// </summary>
        [ObservableProperty]
        private bool isReplyHidden = false;

        /// <summary>
        /// カラムの削除や戻る操作をロックする設定。
        /// </summary>
        [ObservableProperty]
        private bool isLocked = false;

        /// <summary>
        /// WebViewのズーム倍率（1.0 = 100%）。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ZoomPercentage))]
        private double zoomFactor = 1.0;

        /// <summary>
        /// サムネイル画像・動画の表示倍率（％）。
        /// </summary>
        [ObservableProperty]
        private int mediaScalePercentage = 100;

        /// <summary>
        /// 次の更新までの残り秒数。
        /// </summary>
        [ObservableProperty]
        private int remainingSeconds;

        partial void OnRemainingSecondsChanged(int value) => UpdateCountdownText();

        /// <summary>
        /// カウントダウン表示文字列（例: "(4:59)"）。
        /// </summary>
        [ObservableProperty]
        private string countdownText = "";

        /// <summary>
        /// このカラムが使用するプロファイル名。未指定(null)の場合はウィンドウのメインプロファイルを使用します。
        /// </summary>
        [ObservableProperty]
        private string? profileName;

        #endregion

        #region Non-Observable & Auxiliary Properties

        /// <summary>
        /// 外部サイトかどうか。
        /// </summary>
        public bool IsExternalSite { get; set; } = false;

        /// <summary>
        /// リスト自動遷移フラグ。
        /// </summary>
        public bool IsListAutoNav { get; set; } = false;

        /// <summary>
        /// 直前の有効なURL。
        /// </summary>
        public string LastValidUrl { get; set; } = "https://x.com/home";

        /// <summary>
        /// 最終入力時刻。
        /// </summary>
        public DateTime LastInputTime { get; set; } = DateTime.MinValue;

        /// <summary>
        /// ソフト更新（ピリオドキー）を使用するかどうか。
        /// </summary>
        public bool UseSoftRefresh { get; set; } = true;

        /// <summary>
        /// 更新タイミングのジッター（分散）を適用するかどうか。
        /// </summary>
        public bool UseRefreshJitter { get; set; } = false;

        /// <summary>
        /// 未読位置保持（スクロール中は更新しない）設定。
        /// </summary>
        public bool KeepUnreadPosition { get; set; } = false;

        /// <summary>
        /// レート制限リセット予定時刻。
        /// </summary>
        public DateTimeOffset RateLimitResetTime { get; set; } = DateTimeOffset.MinValue;

        /// <summary>
        /// 429休止画面を実際に表示中かどうか。
        /// </summary>
        public bool DidShowRateLimitScreen { get; set; } = false;

        /// <summary>
        /// UI表示用のズーム率（％）。
        /// </summary>
        public int ZoomPercentage
        {
            get => (int)Math.Round(ZoomFactor * 100);
            set
            {
                int val = Math.Clamp(value, 10, 500);
                ZoomFactor = val / 100.0;
            }
        }

        #endregion

        #region Events (View への委譲要求)

        /// <summary>「戻る」操作を View に要求するイベント。</summary>
        public event Action<ColumnViewModel>? GoBackRequested;

        /// <summary>「基点URLに戻る」操作を View に要求するイベント。</summary>
        public event Action<ColumnViewModel>? NavigateHomeRequested;

        /// <summary>休止/復帰の画面切り替えを View に要求するイベント。</summary>
        public event Action<ColumnViewModel, bool>? SuspendRequested;

        /// <summary>429レート制限専用画面の表示を View に要求するイベント。</summary>
        public event Action<ColumnViewModel, DateTimeOffset>? RateLimitSuspendRequested;

        /// <summary>リロード実行を View に要求する非同期デリゲート (forceReload, isUserInitiated)。</summary>
        public event Func<ColumnViewModel, bool, bool, Task>? ReloadRequested;

        /// <summary>ユーザーが読書中またはマウスホバー中かを View に問い合わせるデリゲート。</summary>
        public event Func<ColumnViewModel, bool>? IsReadingOrHoveredQuery;

        /// <summary>カラムの破棄・終了を通知するイベント。</summary>
        public event Action<ColumnViewModel>? Closed;

        #endregion

        #region Commands

        /// <summary>
        /// 手動更新（強制リロード）。
        /// </summary>
        [RelayCommand]
        public async Task ManualRefreshAsync()
        {
            if (IsSuspended) return;
            if (RateLimitStatus == RateLimitStatus.Stopped) return;

            if (ReloadRequested != null)
            {
                await ReloadRequested.Invoke(this, true, true);
            }
            ResetCountdown();
        }

        /// <summary>
        /// 「戻る」操作。
        /// </summary>
        [RelayCommand]
        public void GoBack()
        {
            if (IsLocked) return;
            GoBackRequested?.Invoke(this);
        }

        /// <summary>
        /// 「基点URLに戻る」操作。
        /// </summary>
        [RelayCommand]
        public void NavigateHome()
        {
            if (IsSuspended) return;
            NavigateHomeRequested?.Invoke(this);
        }

        /// <summary>
        /// 休止状態の切り替え。
        /// </summary>
        [RelayCommand]
        public void Suspend()
        {
            IsSuspended = !IsSuspended;

            if (IsSuspended)
            {
                RemainingSeconds = 0;
                SuspendRequested?.Invoke(this, true);
                Logger.Log($"[ColumnViewModel] Column suspended: {Url}");
            }
            else
            {
                IsRateLimited = false;
                SuspendRequested?.Invoke(this, false);
                ResetCountdown();
                Logger.Log($"[ColumnViewModel] Column resumed: {Url}");
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// カウントダウンおよび残り秒数を初期値にリセットします。
        /// </summary>
        public void ResetCountdown()
        {
            if (IsSuspended || RateLimitStatus == RateLimitStatus.Stopped || !IsAutoRefreshEnabled || RefreshIntervalSeconds <= 0)
            {
                RemainingSeconds = 0;
                CountdownText = "";
                return;
            }

            int jitter = 0;
            if (UseRefreshJitter && RefreshIntervalSeconds > 0)
            {
                int max = Math.Min((int)(RefreshIntervalSeconds * 0.2), 60);
                if (max > 0) jitter = _jitterRandom.Next(0, max + 1);
            }

            RemainingSeconds = RefreshIntervalSeconds + jitter;
            UpdateCountdownText();
        }

        /// <summary>
        /// カウントダウン表示文字列を更新します。
        /// </summary>
        public void UpdateCountdownText()
        {
            if (RateLimitStatus == RateLimitStatus.Stopped)
            {
                double sec = (RateLimitResetTime - DateTimeOffset.Now).TotalSeconds;
                if (sec < 0) sec = 0;
                CountdownText = $"({TimeSpan.FromSeconds(sec):m\\:ss})";
                return;
            }

            if (!IsAutoRefreshEnabled || RemainingSeconds <= 0)
            {
                CountdownText = "";
            }
            else
            {
                var ts = TimeSpan.FromSeconds(RemainingSeconds);
                CountdownText = ts.TotalHours >= 1
                    ? $"({(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2})"
                    : $"({ts.Minutes}:{ts.Seconds:D2})";
            }
        }

        /// <summary>
        /// タイマーサービス（IAppTimerService）からの自動更新要求を受け取って実行します。
        /// </summary>
        public async Task TriggerAutoRefreshAsync()
        {
            if (IsSuspended || RateLimitStatus == RateLimitStatus.Stopped) return;

            // 入力中または直近入力判定
            bool isRecentlyInput = (DateTime.Now - LastInputTime).TotalSeconds < 30;
            if (IsInputActive || isRecentlyInput)
            {
                RemainingSeconds = 30;
                UpdateCountdownText();
                return;
            }

            // View へのホバー・読書中問い合わせ
            if (IsReadingOrHoveredQuery != null && IsReadingOrHoveredQuery.Invoke(this))
            {
                Logger.Log($"[ColumnViewModel] Auto-refresh postponed (reading/hovered): {Url}");
                RemainingSeconds = 30;
                UpdateCountdownText();
                return;
            }

            if (ReloadRequested != null)
            {
                await ReloadRequested.Invoke(this, !UseSoftRefresh, false);
            }

            ResetCountdown();
        }

        /// <summary>
        /// レート制限による停止状態から復帰します。
        /// </summary>
        public async Task ResumeFromRateLimitAsync()
        {
            RateLimitStatus = RateLimitStatus.Normal;
            ResetCountdown();

            if (ReloadRequested != null)
            {
                await ReloadRequested.Invoke(this, !UseSoftRefresh, false);
            }
            Logger.Log($"[ColumnViewModel] Resumed from rate limit: {Url}");
        }

        /// <summary>
        /// レート制限情報の更新（IRateLimitTarget 実装）。
        /// </summary>
        public void UpdateRateLimit(int remaining, DateTimeOffset resetTime, bool ignoreProtection)
        {
            RateLimitRemaining = remaining;
            if (IsSuspended) return;

            if (ignoreProtection)
            {
                if (RateLimitStatus == RateLimitStatus.Caution)
                    RateLimitStatus = RateLimitStatus.Normal;
                return;
            }

            const int RateLimitStopThreshold = 5;
            const int RateLimitCautionThreshold = 10;

            if (remaining < RateLimitStopThreshold)
            {
                RateLimitResetTime = (resetTime - DateTimeOffset.Now).TotalSeconds > 1
                    ? resetTime
                    : DateTimeOffset.Now.AddSeconds(60);
                RateLimitStatus = RateLimitStatus.Stopped;
                UpdateCountdownText();
                Logger.Log($"[ColumnViewModel] Rate limit stop entered: remaining={remaining}, reset={RateLimitResetTime:HH:mm:ss}");
            }
            else if (remaining < RateLimitCautionThreshold)
            {
                if (RateLimitStatus != RateLimitStatus.Stopped)
                    RateLimitStatus = RateLimitStatus.Caution;
            }
            else
            {
                if (RateLimitStatus == RateLimitStatus.Caution)
                    RateLimitStatus = RateLimitStatus.Normal;
            }
        }

        /// <summary>
        /// 429エラー発生時の通知（IRateLimitTarget 実装）。
        /// </summary>
        public void OnRateLimitTriggered(DateTimeOffset? resetTime, bool hasResetHeader)
        {
            if (IsSuspended) return;

            DateTimeOffset resumeAt = resetTime ?? DateTimeOffset.Now.AddMinutes(15);
            if (resumeAt <= DateTimeOffset.Now)
                resumeAt = DateTimeOffset.Now.AddMinutes(15);

            RateLimitResetTime = resumeAt;
            IsRateLimited = true;
            IsSuspended = true;
            RemainingSeconds = 0;

            RateLimitSuspendRequested?.Invoke(this, resumeAt);
            Logger.Log($"[ColumnViewModel] 429 RateLimit triggered: resumeAt={resumeAt:HH:mm:ss}");
        }

        /// <summary>
        /// 保護設定変更時などにレート制限状態を全解除します。
        /// </summary>
        public void ClearRateLimitState()
        {
            bool wasStopped = RateLimitStatus == RateLimitStatus.Stopped;
            RateLimitStatus = RateLimitStatus.Normal;

            if (IsRateLimited)
            {
                IsRateLimited = false;
                IsSuspended = false;
                SuspendRequested?.Invoke(this, false);
                ResetCountdown();
                Logger.Log($"[ColumnViewModel] RateLimit cleared - resumed: {Url}");
            }
            else if (wasStopped)
            {
                ResetCountdown();
                _ = ManualRefreshAsync();
                Logger.Log($"[ColumnViewModel] RateLimit stopped cleared - refresh resumed: {Url}");
            }
        }

        /// <summary>
        /// カラムを安全に閉じてリソースを解放します。
        /// </summary>
        public void Close()
        {
            Closed?.Invoke(this);
            // イベントハンドラーをすべて解除
            GoBackRequested = null;
            NavigateHomeRequested = null;
            SuspendRequested = null;
            RateLimitSuspendRequested = null;
            ReloadRequested = null;
            IsReadingOrHoveredQuery = null;
            Closed = null;
        }

        #endregion

        #region Config Serialization

        /// <summary>
        /// 現在の状態から永続化用モデル ColumnConfig を生成します。
        /// </summary>
        public ColumnConfig ToConfig()
        {
            return new ColumnConfig
            {
                Url = this.Url,
                Width = this.Width,
                RefreshIntervalSeconds = this.RefreshIntervalSeconds,
                IsAutoRefreshEnabled = this.IsAutoRefreshEnabled,
                IsRetweetHidden = this.IsRetweetHidden,
                IsReplyHidden = this.IsReplyHidden,
                IsLocked = this.IsLocked,
                ZoomFactor = this.ZoomFactor,
                MediaScalePercentage = this.MediaScalePercentage,
                ProfileName = this.ProfileName,
                IsExternalSite = this.IsExternalSite,
                IsListAutoNav = this.IsListAutoNav,
                LastValidUrl = this.LastValidUrl,
                LastInputTime = this.LastInputTime
            };
        }

        /// <summary>
        /// ColumnConfig から ColumnViewModel を生成します。
        /// </summary>
        public static ColumnViewModel FromConfig(ColumnConfig config)
        {
            return new ColumnViewModel
            {
                Url = config.Url,
                Width = config.Width,
                RefreshIntervalSeconds = config.RefreshIntervalSeconds,
                IsAutoRefreshEnabled = config.IsAutoRefreshEnabled,
                IsRetweetHidden = config.IsRetweetHidden,
                IsReplyHidden = config.IsReplyHidden,
                IsLocked = config.IsLocked,
                ZoomFactor = config.ZoomFactor,
                MediaScalePercentage = config.MediaScalePercentage,
                ProfileName = config.ProfileName,
                IsExternalSite = config.IsExternalSite,
                IsListAutoNav = config.IsListAutoNav,
                LastValidUrl = config.LastValidUrl,
                LastInputTime = config.LastInputTime
            };
        }

        #endregion
    }
}
