using System.Windows.Threading;
using XColumn.Helpers;

namespace XColumn.Services
{
    /// <summary>
    /// IAppTimerService の既定実装。
    /// 単一の DispatcherTimer（1秒間隔）で全カラムのカウントダウン・更新・レート制限復帰・自動終了を駆動します。
    /// </summary>
    public class AppTimerService : IAppTimerService
    {
        private readonly DispatcherTimer _timer;
        private readonly List<IColumnTimerTarget> _columns = new();
        private readonly object _lock = new();
        private static readonly Random _jitterRandom = new();

        private bool _isPausedByActive = false;
        private bool _isAppActive = true;
        private bool _autoShutdownEnabled = false;
        private int _autoShutdownMinutes = 30;
        private DateTime? _lastDeactivatedTime = null;

        public event Action? AutoShutdownRequested;

        /// <summary>ウィンドウアクティブ時にタイマーを一時停止するかどうか。</summary>
        public bool StopTimerWhenActive { get; set; } = false;

        public AppTimerService()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Timer_Tick;
        }

        public void RegisterColumn(IColumnTimerTarget column)
        {
            lock (_lock)
            {
                if (!_columns.Contains(column))
                {
                    _columns.Add(column);
                    ResetColumnCountdown(column);
                }
            }
        }

        public void UnregisterColumn(IColumnTimerTarget column)
        {
            lock (_lock)
            {
                _columns.Remove(column);
            }
        }

        public void ClearColumns()
        {
            lock (_lock)
            {
                _columns.Clear();
            }
        }

        public void Start()
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
                Logger.Log("[AppTimerService] Central timer started.");
            }
        }

        public void Stop()
        {
            if (_timer.IsEnabled)
            {
                _timer.Stop();
                Logger.Log("[AppTimerService] Central timer stopped.");
            }
        }

        public void PauseForActive()
        {
            _isPausedByActive = true;
        }

        public void ResumeFromActive()
        {
            _isPausedByActive = false;
        }

        public void ConfigureAutoShutdown(bool enabled, int minutes)
        {
            _autoShutdownEnabled = enabled;
            _autoShutdownMinutes = Math.Max(1, minutes);
        }

        public void SetAppActive(bool isActive)
        {
            _isAppActive = isActive;
            if (!isActive)
            {
                _lastDeactivatedTime = DateTime.Now;
            }
            else
            {
                _lastDeactivatedTime = null;
            }
        }

        private async void Timer_Tick(object? sender, EventArgs e)
        {
            List<IColumnTimerTarget> targets;
            lock (_lock)
            {
                targets = _columns.ToList();
            }

            // 1. 各カラムのカウントダウン・更新処理
            foreach (var col in targets)
            {
                // 休止中はスキップ
                if (col.IsSuspended) continue;

                // レート制限中（更新停止）の自動復帰判定
                if (col.IsRateLimitStopped)
                {
                    if (DateTimeOffset.Now >= col.RateLimitResetTime)
                    {
                        try
                        {
                            await col.ResumeFromRateLimitAsync();
                            ResetColumnCountdown(col);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[AppTimerService] ResumeFromRateLimit failed: {ex.Message}");
                        }
                    }
                    continue;
                }

                // アクティブ時一時停止中はカウントダウンを進行させない
                if (_isPausedByActive) continue;

                // 自動更新が有効な場合のカウントダウン
                if (col.IsAutoRefreshEnabled && col.RefreshIntervalSeconds > 0)
                {
                    if (col.RemainingSeconds > 0)
                    {
                        col.RemainingSeconds--;
                    }

                    if (col.RemainingSeconds <= 0)
                    {
                        // 更新発火
                        try
                        {
                            await col.TriggerAutoRefreshAsync();
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[AppTimerService] TriggerAutoRefresh failed: {ex.Message}");
                        }
                        finally
                        {
                            ResetColumnCountdown(col);
                        }
                    }
                }
            }

            // 2. 自動シャットダウン判定
            if (_autoShutdownEnabled && !_isAppActive && _lastDeactivatedTime.HasValue)
            {
                var elapsed = DateTime.Now - _lastDeactivatedTime.Value;
                if (elapsed.TotalMinutes >= _autoShutdownMinutes)
                {
                    Stop();
                    AutoShutdownRequested?.Invoke();
                }
            }
        }

        /// <summary>
        /// カラムの次回更新秒数をジッター付きで再設定します。
        /// </summary>
        public static void ResetColumnCountdown(IColumnTimerTarget col)
        {
            if (!col.IsAutoRefreshEnabled || col.RefreshIntervalSeconds <= 0)
            {
                col.RemainingSeconds = 0;
                return;
            }

            int jitter = 0;
            if (col.UseRefreshJitter)
            {
                int maxJitter = Math.Min((int)(col.RefreshIntervalSeconds * 0.2), 60);
                if (maxJitter > 0)
                {
                    lock (_jitterRandom)
                    {
                        jitter = _jitterRandom.Next(0, maxJitter + 1);
                    }
                }
            }

            col.RemainingSeconds = col.RefreshIntervalSeconds + jitter;
        }
    }
}
