namespace XColumn.Services
{
    /// <summary>
    /// タイマーによる自動更新やカウントダウンの対象となるカラムの契約。
    /// </summary>
    public interface IColumnTimerTarget
    {
        /// <summary>自動更新が有効かどうか。</summary>
        bool IsAutoRefreshEnabled { get; }

        /// <summary>自動更新の間隔（秒）。</summary>
        int RefreshIntervalSeconds { get; }

        /// <summary>次の更新までの残り秒数。</summary>
        int RemainingSeconds { get; set; }

        /// <summary>更新タイミングのジッター（分散）を適用するかどうか。</summary>
        bool UseRefreshJitter { get; }

        /// <summary>休止状態かどうか。</summary>
        bool IsSuspended { get; }

        /// <summary>レート制限による更新停止中かどうか。</summary>
        bool IsRateLimitStopped { get; }

        /// <summary>レート制限リセット予定時刻。</summary>
        DateTimeOffset RateLimitResetTime { get; }

        /// <summary>タイマーによって自動更新を実行する要求を発火します。</summary>
        Task TriggerAutoRefreshAsync();

        /// <summary>レート制限による停止から通常状態へ復帰させます。</summary>
        Task ResumeFromRateLimitAsync();
    }

    /// <summary>
    /// アプリケーション全体のタイマー駆動を一元管理する中央サービス。
    /// カラムごとの DispatcherTimer 乱立を排除し、単一のタイマーで全カラムのカウントダウン・更新・シャットダウンを統括します。
    /// </summary>
    public interface IAppTimerService
    {
        /// <summary>タイマー対象のカラムを登録します。</summary>
        void RegisterColumn(IColumnTimerTarget column);

        /// <summary>タイマー対象のカラムを登録解除します。</summary>
        void UnregisterColumn(IColumnTimerTarget column);

        /// <summary>登録されているすべてのカラムの登録を解除します。</summary>
        void ClearColumns();

        /// <summary>ウィンドウアクティブ時にタイマーを一時停止するかどうか。</summary>
        bool StopTimerWhenActive { get; set; }

        /// <summary>中央タイマーを開始します。</summary>
        void Start();

        /// <summary>中央タイマーを停止します。</summary>
        void Stop();

        /// <summary>アプリがアクティブになった際のタイマー一時停止（設定に基づく）。</summary>
        void PauseForActive();

        /// <summary>アプリが非アクティブになった際のタイマー再開。</summary>
        void ResumeFromActive();

        /// <summary>自動シャットダウンの設定を更新します。</summary>
        /// <param name="enabled">有効かどうか。</param>
        /// <param name="minutes">非アクティブ待機時間（分）。</param>
        void ConfigureAutoShutdown(bool enabled, int minutes);

        /// <summary>アプリのアクティブ状態の変更を通知します（シャットダウン判定用）。</summary>
        void SetAppActive(bool isActive);

        /// <summary>自動シャットダウン条件に達した際に発火するイベント。</summary>
        event Action? AutoShutdownRequested;
    }
}
