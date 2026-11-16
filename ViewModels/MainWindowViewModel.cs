using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using XColumn.Helpers;
using XColumn.Messages;
using XColumn.Models;
using XColumn.Properties;
using XColumn.Services;

namespace XColumn.ViewModels
{
    /// <summary>
    /// MainWindow のコア状態管理およびメニュー・全体操作を司るメイン ViewModel。
    /// 以前の MainWindow からビジネスロジック・タイマー管理・状態保持を完全移譲し、
    /// View 側を純粋な UI 描画層へスリム化します。
    /// </summary>
    public partial class MainWindowViewModel : ViewModelBase
    {
        #region Constants

        private const string DefaultHomeUrl = "https://x.com/home";
        private const string DefaultNotifyUrl = "https://x.com/notifications";
        private const string DefaultTrendUrl = "https://x.com/explore/tabs/trending";
        private const string DefaultGlobalTrendUrl = "https://x.com/i/jf/global-trending/home";
        private const string DefaultGrokUrl = "https://x.com/i/grok";
        private const string SearchUrlFormat = "https://x.com/search?q={0}";
        private const string ListUrlFormat = "https://x.com/i/lists/{0}";

        #endregion

        #region Services

        private readonly ISettingsService _settingsService;
        private readonly IProfileService _profileService;
        private readonly IDialogService _dialogService;
        private readonly IAppTimerService _timerService;
        private readonly IRateLimitService _rateLimitService;
        private readonly IAudioService _audioService;
        private readonly IUpdateService _updateService;
        private readonly IStatusService _statusService;
        private readonly IWebViewEnvironmentService _environmentService;

        #endregion

        #region Observable Collections & Properties

        /// <summary>
        /// 現在開かれているカラムのコレクション。
        /// </summary>
        public ObservableCollection<ColumnViewModel> Columns { get; } = new();

        /// <summary>
        /// 現在アクティブなプロファイル名。
        /// </summary>
        [ObservableProperty]
        private string activeProfile = "";

        /// <summary>
        /// 全体音量（0〜100）。
        /// </summary>
        [ObservableProperty]
        private int appVolume = 100;

        partial void OnAppVolumeChanged(int value)
        {
            // 音量変更時は IAudioService を通じて自プロセスPIDにのみ適用
            _audioService.SetVolume(value / 100.0, Array.Empty<int>());
        }

        /// <summary>
        /// アプリのテーマ ("System", "Light", "Dark")。
        /// </summary>
        [ObservableProperty]
        private string currentTheme = "System";

        /// <summary>
        /// テーマが変更されたときに View へ通知するイベント。
        /// </summary>
        public event Action<string>? ThemeChanged;

        partial void OnCurrentThemeChanged(string value)
        {
            ThemeChanged?.Invoke(value);
        }

        /// <summary>
        /// アプリ言語 ("ja-JP", "en-US")。
        /// </summary>
        [ObservableProperty]
        private string currentLanguage = "ja-JP";

        /// <summary>
        /// カラムを均等幅で配置するかどうか。
        /// </summary>
        [ObservableProperty]
        private bool isUniformGrid = false;

        /// <summary>
        /// 2段グリッドレイアウトを使用するかどうか。
        /// </summary>
        [ObservableProperty]
        private bool useTwoTierLayout = false;

        /// <summary>
        /// カラムヘッダーに URL を表示するかどうか。
        /// </summary>
        [ObservableProperty]
        private bool showColumnUrl = true;

        /// <summary>
        /// ウィンドウアクティブ時に自動更新タイマーを一時停止するかどうか。
        /// </summary>
        [ObservableProperty]
        private bool stopTimerWhenActive = false;

        partial void OnStopTimerWhenActiveChanged(bool value)
        {
            _timerService.StopTimerWhenActive = value;
        }

        /// <summary>
        /// サーバー接続状態。
        /// </summary>
        [ObservableProperty]
        private ConnectionStatus connectionStatus = ConnectionStatus.Normal;

        /// <summary>
        /// レート制限到達推定メッセージ。
        /// </summary>
        [ObservableProperty]
        private string rateLimitEstimateText = "";

        #endregion

        #region Constructor

        public MainWindowViewModel(
            ISettingsService settingsService,
            IProfileService profileService,
            IDialogService dialogService,
            IAppTimerService timerService,
            IRateLimitService rateLimitService,
            IAudioService audioService,
            IUpdateService updateService,
            IStatusService statusService,
            IWebViewEnvironmentService environmentService)
        {
            _settingsService = settingsService;
            _profileService = profileService;
            _dialogService = dialogService;
            _timerService = timerService;
            _rateLimitService = rateLimitService;
            _audioService = audioService;
            _updateService = updateService;
            _statusService = statusService;
            _environmentService = environmentService;

            // 接続状態監視の購読
            _statusService.StatusChanged += (s, e) =>
            {
                ConnectionStatus = e.NewStatus;
            };

            // 自動シャットダウンコールバック
            _timerService.AutoShutdownRequested += () =>
            {
                Logger.Log("[MainWindowViewModel] Auto shutdown triggered.");
                WeakReferenceMessenger.Default.Send(new CloseAppRequest());
            };
        }

        #endregion

        #region Column Management

        /// <summary>
        /// カラムを追加し、タイマーサービスへ登録します。
        /// </summary>
        public void AddColumn(ColumnViewModel column)
        {
            Columns.Add(column);
            _timerService.RegisterColumn(column);
            column.Closed += OnColumnClosed;
        }

        /// <summary>
        /// カラムを削除し、タイマーサービスから登録解除します。
        /// </summary>
        public void RemoveColumn(ColumnViewModel column)
        {
            column.Closed -= OnColumnClosed;
            _timerService.UnregisterColumn(column);
            Columns.Remove(column);
            column.Close();
        }

        /// <summary>
        /// すべてのカラムを登録解除してクリアします。
        /// </summary>
        public void ClearColumns()
        {
            foreach (var col in Columns.ToList())
            {
                col.Closed -= OnColumnClosed;
                _timerService.UnregisterColumn(col);
                col.Close();
            }
            Columns.Clear();
            _timerService.ClearColumns();
        }

        private void OnColumnClosed(ColumnViewModel column)
        {
            RemoveColumn(column);
        }

        /// <summary>
        /// AppSettings からカラムリストを復元します。
        /// </summary>
        public void LoadColumnsFromSettings(AppSettings settings)
        {
            ClearColumns();

            if (settings.Columns == null || settings.Columns.Count == 0)
            {
                // デフォルトのホームカラムを1つ追加
                var defaultCol = new ColumnViewModel
                {
                    Url = DefaultHomeUrl,
                    Width = settings.ColumnWidth > 0 ? settings.ColumnWidth : 380,
                    ShowRateLimitRemaining = settings.ShowRateLimitRemaining,
                    UseSoftRefresh = settings.UseSoftRefresh,
                    UseRefreshJitter = settings.UseRefreshJitter,
                    KeepUnreadPosition = settings.KeepUnreadPosition
                };
                AddColumn(defaultCol);
                return;
            }

            foreach (var cfg in settings.Columns)
            {
                var col = ColumnViewModel.FromConfig(cfg);
                col.ShowRateLimitRemaining = settings.ShowRateLimitRemaining;
                col.UseSoftRefresh = settings.UseSoftRefresh;
                col.UseRefreshJitter = settings.UseRefreshJitter;
                col.KeepUnreadPosition = settings.KeepUnreadPosition;
                AddColumn(col);
            }
        }

        /// <summary>
        /// 現在のカラム構成を AppSettings.Columns へ反映します（URL巻き戻り副作用なし）。
        /// </summary>
        public void SaveColumnsToSettings(AppSettings settings)
        {
            settings.Columns = Columns.Select(c => c.ToConfig()).ToList();
        }

        /// <summary>
        /// 429到達推定時間を更新します。
        /// </summary>
        public void UpdateRateLimitEstimate()
        {
            RateLimitEstimateText = _rateLimitService.BuildRateLimitEstimate(Columns, ActiveProfile);
        }

        /// <summary>
        /// 保護設定変更時などに全カラムのレート制限状態を一括解除します。
        /// </summary>
        public void ResetAllRateLimits()
        {
            foreach (var col in Columns)
            {
                col.ClearRateLimitState();
            }
            UpdateRateLimitEstimate();
        }

        #endregion

        #region Menu Commands

        [RelayCommand]
        private void AddHome() => RequestAddColumn(DefaultHomeUrl);

        [RelayCommand]
        private void AddNotify() => RequestAddColumn(DefaultNotifyUrl);

        [RelayCommand]
        private void AddTrend() => RequestAddColumn(DefaultTrendUrl);

        [RelayCommand]
        private void AddGlobalTrend() => RequestAddColumn(DefaultGlobalTrendUrl);

        [RelayCommand]
        private void AddGrok() => RequestAddColumn(DefaultGrokUrl);

        [RelayCommand]
        private void AddSearch()
        {
            var key = _dialogService.ShowInput(Resources.Prompt_Search, Resources.Prompt_SearchKeyword);
            if (!string.IsNullOrEmpty(key))
            {
                RequestAddColumn(string.Format(SearchUrlFormat, WebUtility.UrlEncode(key)));
            }
        }

        [RelayCommand]
        private void AddList()
        {
            var input = _dialogService.ShowInput(Resources.Prompt_AddList, Resources.Prompt_AddListInput);
            if (string.IsNullOrEmpty(input)) return;

            if (input.StartsWith("http"))
            {
                RequestAddColumn(input);
            }
            else if (long.TryParse(input, out _))
            {
                RequestAddColumn(string.Format(ListUrlFormat, input));
            }
            else
            {
                _dialogService.ShowMessage(Resources.Err_InputIdOrUrl, Resources.Common_Error);
            }
        }

        [RelayCommand]
        private void AddListAuto() => WeakReferenceMessenger.Default.Send(new AddListAutoColumnRequest());

        [RelayCommand]
        private void AddUser()
        {
            var input = _dialogService.ShowInput(Resources.AddUser, Resources.UserId);
            if (string.IsNullOrEmpty(input)) return;

            string userId = input.Trim().TrimStart('@');
            if (!string.IsNullOrEmpty(userId))
            {
                RequestAddColumn($"https://x.com/{userId}");
            }
        }

        [RelayCommand]
        private void AddSite()
        {
            var input = _dialogService.ShowInput(Resources.Prompt_AddSite, Resources.Prompt_AddSiteInput);
            if (string.IsNullOrWhiteSpace(input)) return;

            string url = input.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }
            WeakReferenceMessenger.Default.Send(new AddSiteColumnRequest(url));
        }

        [RelayCommand]
        private void Exit() => WeakReferenceMessenger.Default.Send(new CloseAppRequest());

        private void RequestAddColumn(string url)
            => WeakReferenceMessenger.Default.Send(new AddColumnRequest(url));

        #endregion
    }
}
