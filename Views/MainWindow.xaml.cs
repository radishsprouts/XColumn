using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Wpf;
using ModernWpf.Controls.Primitives;
using XColumn.Helpers;
using XColumn.Messages;
using XColumn.Models;
using XColumn.Services;
using XColumn.ViewModels;

// 曖昧さ回避
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Keyboard = System.Windows.Input.Keyboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;
using R = XColumn.Properties.Resources;
using TextBox = System.Windows.Controls.TextBox;

namespace XColumn.Views
{
    /// <summary>
    /// アプリケーションのメインウィンドウ。
    /// 純粋な View として UI 描画、ウィンドウライフサイクル、および WebView2 の表示・破棄を担います。
    /// ビジネスロジックとタイマーは ViewModel およびサービス層に完全移譲されています。
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Dependency Properties

        public static readonly DependencyProperty UseTwoTierLayoutProperty =
            DependencyProperty.Register(nameof(UseTwoTierLayout), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

        public bool UseTwoTierLayout
        {
            get => (bool)GetValue(UseTwoTierLayoutProperty);
            set => SetValue(UseTwoTierLayoutProperty, value);
        }

        public static readonly DependencyProperty StopTimerWhenActiveProperty =
            DependencyProperty.Register(nameof(StopTimerWhenActive), typeof(bool), typeof(MainWindow),
                new PropertyMetadata(false, (d, e) =>
                {
                    if (d is MainWindow w && w.ViewModel != null)
                        w.ViewModel.StopTimerWhenActive = (bool)e.NewValue;
                }));

        public bool StopTimerWhenActive
        {
            get => (bool)GetValue(StopTimerWhenActiveProperty);
            set => SetValue(StopTimerWhenActiveProperty, value);
        }

        public static readonly DependencyProperty ShowColumnUrlProperty =
            DependencyProperty.Register(nameof(ShowColumnUrl), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));

        public bool ShowColumnUrl
        {
            get => (bool)GetValue(ShowColumnUrlProperty);
            set => SetValue(ShowColumnUrlProperty, value);
        }

        public static readonly DependencyProperty ColumnWidthProperty =
            DependencyProperty.Register(nameof(ColumnWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(380.0));

        public double ColumnWidth
        {
            get => (double)GetValue(ColumnWidthProperty);
            set => SetValue(ColumnWidthProperty, value);
        }

        public static readonly DependencyProperty UseUniformGridProperty =
            DependencyProperty.Register(nameof(UseUniformGrid), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

        public bool UseUniformGrid
        {
            get => (bool)GetValue(UseUniformGridProperty);
            set => SetValue(UseUniformGridProperty, value);
        }

        #endregion

        #region Services & Fields

        private readonly ISettingsService _settingsService;
        private readonly IProfileService _profileService;
        private readonly IDialogService _dialogService;
        private readonly IAppTimerService _timerService;
        private readonly IRateLimitService _rateLimitService;
        private readonly IAudioService _audioService;
        private readonly IUpdateService _updateService;
        private readonly IStatusService _statusService;
        private readonly IWebViewEnvironmentService _environmentService;
        private readonly IScriptInjectionService _scriptService;

        public MainWindowViewModel ViewModel { get; }

        private string _activeProfileName = "default";
        private readonly string? _startupProfileName;
        private readonly bool _enableDevTools;
        private readonly bool _disableGpu;
        private AppSettings _currentSettings = new();
        private readonly ObservableCollection<ProfileItem> _profileNames = new();
        private CancellationTokenSource? _loadCts;
        private bool _isRestarting = false;
        private bool _isInitialized = false;
        private WindowSnapBehavior? _snapBehavior;

        // WebViewコントロールとViewModelの対応マップ（循環参照を防ぐためView側でのみ保持）
        private readonly Dictionary<ColumnViewModel, WebView2> _columnWebViewMap = new();

        #endregion

        #region Constructors

        public MainWindow() : this(null, false, false) { }

        public MainWindow(string? profileName, bool enableDevTools = false, bool disableGpu = false)
        {
            _startupProfileName = profileName;
            _enableDevTools = enableDevTools;
            _disableGpu = disableGpu;

            // DI コンテナからサービスを先に取得（XAML初期化中のイベント発火やバインディングによる null 参照を確実に防止）
            var sp = App.Current.Services;
            _settingsService = sp.GetRequiredService<ISettingsService>();
            _profileService = sp.GetRequiredService<IProfileService>();
            _dialogService = sp.GetRequiredService<IDialogService>();
            _timerService = sp.GetRequiredService<IAppTimerService>();
            _rateLimitService = sp.GetRequiredService<IRateLimitService>();
            _audioService = sp.GetRequiredService<IAudioService>();
            _updateService = sp.GetRequiredService<IUpdateService>();
            _statusService = sp.GetRequiredService<IStatusService>();
            _environmentService = sp.GetRequiredService<IWebViewEnvironmentService>();
            _scriptService = sp.GetRequiredService<IScriptInjectionService>();

            ViewModel = sp.GetRequiredService<MainWindowViewModel>();
            DataContext = ViewModel;

            // 起動プロファイル決定（コンストラクタ時点で早期確定させ、あらゆるUIイベント・バインディングで一貫性を保証）
            var startupConfig = _settingsService.LoadAppConfig();
            if (!string.IsNullOrEmpty(_startupProfileName))
            {
                _activeProfileName = _startupProfileName;
            }
            else if (!string.IsNullOrEmpty(startupConfig.StartupProfile))
            {
                _activeProfileName = startupConfig.StartupProfile;
            }
            else if (!string.IsNullOrEmpty(startupConfig.ActiveProfile))
            {
                _activeProfileName = startupConfig.ActiveProfile;
            }
            else
            {
                _activeProfileName = "default";
            }
            ViewModel.ActiveProfile = _activeProfileName;

            InitializeComponent();

            ColumnItemsControl.ItemsSource = ViewModel.Columns;

            // テーマ変更時の WebView2 連動
            ViewModel.ThemeChanged += OnThemeChanged;

            // メッセージ登録
            RegisterMessages();

            WindowHelper.SetUseModernWindowStyle(this, true);

            _snapBehavior = new WindowSnapBehavior(this);

            Closing += MainWindow_Closing;
            StateChanged += MainWindow_StateChanged;
            Activated += MainWindow_Activated;
            Deactivated += MainWindow_Deactivated;
        }

        #endregion

        #region Window Lifecycle & Resilience

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _loadCts = new CancellationTokenSource();

                // 起動プロファイル確認（コンストラクタで解決済み）
                ViewModel.ActiveProfile = _activeProfileName;

                // プロファイル一覧初期化
                InitializeProfilesUI();

                // 設定読み込み
                _currentSettings = _settingsService.ReadSettings(_activeProfileName);
                ApplySettingsToView(_currentSettings);

                // ウィンドウ位置・サイズの復元（バグ#2: 画面外座標保護）
                RestoreWindowBounds(_currentSettings);

                // スナップ機能の有効化（バグ#5対策: WindowSnapBehavior経由）
                _snapBehavior?.Attach(_currentSettings.EnableWindowSnap);

                // カラムの読み込み
                ViewModel.LoadColumnsFromSettings(_currentSettings);

                // 中央タイマーの開始
                _timerService.ConfigureAutoShutdown(_currentSettings.AutoShutdownEnabled, _currentSettings.AutoShutdownMinutes);
                _timerService.Start();

                // 起動時自動更新チェック
                if (_currentSettings.CheckForUpdates)
                {
                    _ = CheckForUpdatesSilentlyAsync();
                }

                _isInitialized = true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[MainWindow] Loaded error: {ex.Message}");
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isRestarting) return;

            try
            {
                _loadCts?.Cancel();

                // スナップフック解除（バグ#5対策: WndProcフックのみ解除）
                _snapBehavior?.Detach();

                // タイマー停止
                _timerService.Stop();

                // ウィンドウ状態と座標の安全な保存（バグ#2対策: 最小化時は RestoreBounds を採用）
                CollectWindowBounds(_currentSettings);

                // カラム構成を保存
                ViewModel.SaveColumnsToSettings(_currentSettings);
                _settingsService.SaveSettings(_activeProfileName, _currentSettings);

                // 全カラムの WebView2 破棄パイプライン実行（バグ#3対策: メモリ・プロセスリーク根絶）
                DisposeAllWebViews();
            }
            catch (Exception ex)
            {
                Logger.Log($"[MainWindow] Closing error: {ex.Message}");
            }
        }

        /// <summary>
        /// 最小化時の GPU / CPU 休止制御（バグ修正 / パフォーマンス最適化）。
        /// </summary>
        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            bool isMinimized = WindowState == WindowState.Minimized;
            foreach (var kvp in _columnWebViewMap)
            {
                var webView = kvp.Value;
                if (webView?.CoreWebView2 != null)
                {
                    try
                    {
                        // 最小化時は Chromium 描画パイプラインを休止させる
                        webView.Visibility = isMinimized ? Visibility.Collapsed : Visibility.Visible;
                    }
                    catch { }
                }
            }
        }

        private void MainWindow_Activated(object? sender, EventArgs e)
        {
            _timerService.SetAppActive(true);
            if (StopTimerWhenActive) _timerService.PauseForActive();
        }

        private void MainWindow_Deactivated(object? sender, EventArgs e)
        {
            _timerService.SetAppActive(false);
            if (StopTimerWhenActive) _timerService.ResumeFromActive();
        }

        #endregion

        #region Window Bounds Clipping Guard (バグ#2対策)

        private void RestoreWindowBounds(AppSettings settings)
        {
            double vLeft = SystemParameters.VirtualScreenLeft;
            double vTop = SystemParameters.VirtualScreenTop;
            double vWidth = SystemParameters.VirtualScreenWidth;
            double vHeight = SystemParameters.VirtualScreenHeight;

            if (settings.WindowLeft < vLeft - 100 || settings.WindowLeft > vLeft + vWidth - 100 ||
                settings.WindowTop < vTop - 100 || settings.WindowTop > vTop + vHeight - 100 ||
                settings.WindowWidth < 200 || settings.WindowHeight < 200)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Width = 1200;
                Height = 800;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = settings.WindowLeft;
                Top = settings.WindowTop;
                Width = settings.WindowWidth;
                Height = settings.WindowHeight;
            }

            if (settings.WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowState = WindowState.Normal;
            }
        }

        private void CollectWindowBounds(AppSettings settings)
        {
            if (WindowState == WindowState.Maximized)
            {
                settings.WindowState = WindowState.Maximized;
                settings.WindowTop = RestoreBounds.Top;
                settings.WindowLeft = RestoreBounds.Left;
                settings.WindowHeight = RestoreBounds.Height;
                settings.WindowWidth = RestoreBounds.Width;
            }
            else if (WindowState == WindowState.Minimized)
            {
                settings.WindowState = WindowState.Normal;
                settings.WindowTop = RestoreBounds.Top;
                settings.WindowLeft = RestoreBounds.Left;
                settings.WindowHeight = RestoreBounds.Height;
                settings.WindowWidth = RestoreBounds.Width;
            }
            else
            {
                settings.WindowState = WindowState.Normal;
                settings.WindowTop = Top;
                settings.WindowLeft = Left;
                settings.WindowHeight = Height;
                settings.WindowWidth = Width;
            }
        }

        #endregion

        #region Settings Application

        private void ApplySettingsToView(AppSettings s)
        {
            ColumnWidth = s.ColumnWidth > 0 ? s.ColumnWidth : 380;
            UseUniformGrid = s.UseUniformGrid;
            ShowColumnUrl = s.ShowColumnUrl;
            StopTimerWhenActive = s.StopTimerWhenActive;
            UseTwoTierLayout = s.UseTwoTierLayout;

            ViewModel.IsUniformGrid = s.UseUniformGrid;
            ViewModel.UseTwoTierLayout = s.UseTwoTierLayout;
            ViewModel.ShowColumnUrl = s.ShowColumnUrl;
            ViewModel.StopTimerWhenActive = s.StopTimerWhenActive;
            ViewModel.AppVolume = (int)Math.Round(s.AppVolume * 100);
            _audioService.SetVolume(s.AppVolume, GetActiveWebViewProcessIds());
            ViewModel.CurrentTheme = s.AppTheme ?? "System";

            if (!string.IsNullOrEmpty(s.AppFontFamily))
            {
                try { FontFamily = new System.Windows.Media.FontFamily(s.AppFontFamily); } catch { }
            }
            if (s.AppFontSize >= 9 && s.AppFontSize <= 24)
            {
                FontSize = s.AppFontSize;
            }
        }

        private void OnThemeChanged(string theme)
        {
            foreach (var kvp in _columnWebViewMap)
            {
                var webView = kvp.Value;
                if (webView?.CoreWebView2?.Profile != null)
                {
                    _environmentService.SetPreferredColorScheme(webView.CoreWebView2.Profile, theme);
                }
            }
        }

        #endregion

        #region WebView2 Lifecycle & Disposal Pipeline (バグ#3対策)

        private void DisposeColumnView(ColumnViewModel col, WebView2? webView)
        {
            if (webView != null)
            {
                try
                {
                    if (webView.CoreWebView2 != null)
                    {
                        webView.CoreWebView2.WebResourceResponseReceived -= CoreWebView2_WebResourceResponseReceived;
                        webView.CoreWebView2.Stop();
                        webView.CoreWebView2.Navigate("about:blank");
                    }
                    webView.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[MainWindow] WebView dispose error: {ex.Message}");
                }
            }
            _columnWebViewMap.Remove(col);
        }

        private void DisposeAllWebViews()
        {
            foreach (var kvp in _columnWebViewMap.ToList())
            {
                DisposeColumnView(kvp.Key, kvp.Value);
            }
            _columnWebViewMap.Clear();
        }

        /// <summary>
        /// 現在稼働中の全 WebView2（各カラム＋フォーカスビュー）のブラウザプロセスID（PID）を安全に収集します（バグ#1対策）。
        /// </summary>
        private IEnumerable<int> GetActiveWebViewProcessIds()
        {
            var pids = new HashSet<int>();
            foreach (var wv in _columnWebViewMap.Values)
            {
                if (wv?.CoreWebView2 != null)
                {
                    try
                    {
                        pids.Add((int)wv.CoreWebView2.BrowserProcessId);
                    }
                    catch { }
                }
            }
            if (FocusWebView?.CoreWebView2 != null)
            {
                try
                {
                    pids.Add((int)FocusWebView.CoreWebView2.BrowserProcessId);
                }
                catch { }
            }
            return pids;
        }

        #endregion

        #region Column Operations & UI Commands

        public void AddNewColumn(string url, bool isListAuto = false, bool isExternal = false)
        {
            var col = new ColumnViewModel
            {
                Url = url,
                Width = ColumnWidth,
                IsListAutoNav = isListAuto,
                IsExternalSite = isExternal,
                UseSoftRefresh = _currentSettings.UseSoftRefresh,
                UseRefreshJitter = _currentSettings.UseRefreshJitter,
                KeepUnreadPosition = _currentSettings.KeepUnreadPosition,
                ShowRateLimitRemaining = _currentSettings.ShowRateLimitRemaining
            };

            ViewModel.AddColumn(col);
        }

        private void DeleteColumn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is ColumnViewModel col)
            {
                if (col.IsLocked) return;

                if (_columnWebViewMap.TryGetValue(col, out var webView))
                {
                    DisposeColumnView(col, webView);
                }
                ViewModel.RemoveColumn(col);
            }
        }

        #endregion

        #region Profile Operations

        private void InitializeProfilesUI()
        {
            _profileNames.Clear();
            var config = _settingsService.LoadAppConfig();
            foreach (var p in config.ProfileNames)
            {
                _profileNames.Add(new ProfileItem { Name = p, IsActive = (p == _activeProfileName) });
            }
            ProfileComboBox.ItemsSource = _profileNames;
            var active = _profileNames.FirstOrDefault(p => p.IsActive);
            ProfileComboBox.SelectedItem = active ?? _profileNames.FirstOrDefault();
        }

        private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized || _currentSettings == null) return;
            if (ProfileComboBox.SelectedItem is ProfileItem item && item.Name != _activeProfileName)
            {
                SwitchProfile(item.Name);
            }
        }

        private void SwitchProfile(string newProfileName)
        {
            _isRestarting = true;
            CollectWindowBounds(_currentSettings);
            ViewModel.SaveColumnsToSettings(_currentSettings);
            _settingsService.SaveSettings(_activeProfileName, _currentSettings);

            // app_config.json の ActiveProfile を確実に更新
            var config = _settingsService.LoadAppConfig();
            config.ActiveProfile = newProfileName;
            _settingsService.SaveAppConfig(config);

            string exe = Environment.ProcessPath ?? "";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--wait-pid {Environment.ProcessId} --profile \"{newProfileName}\"",
                UseShellExecute = true
            });

            Application.Current.Shutdown();
        }

        private static string? GetTargetProfileName(object sender)
        {
            if (sender is FrameworkElement item && item.Tag is string name) return name;
            return null;
        }

        private void SwitchProfile_Click(object sender, RoutedEventArgs e)
        {
            string? target = GetTargetProfileName(sender);
            if (!string.IsNullOrEmpty(target) && target != _activeProfileName)
            {
                SwitchProfile(target);
            }
        }

        private void AddProfile_Click(object sender, RoutedEventArgs e)
        {
            var inputDlg = new InputWindow(R.Title_NewProfile, R.Prompt_NewProfile)
            {
                Owner = this
            };
            if (inputDlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(inputDlg.InputText))
            {
                string name = inputDlg.InputText.Trim();
                if (!IsValidProfileName(name)) return;

                try
                {
                    var config = _settingsService.LoadAppConfig();
                    if (config.ProfileNames.Contains(name))
                    {
                        _dialogService.ShowMessage(string.Format(R.Msg_Err_ProfileExists, name), R.Title_Error, MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    config.ProfileNames.Add(name);
                    _settingsService.SaveAppConfig(config);
                    InitializeProfilesUI();

                    if (_dialogService.ShowMessage(string.Format(R.Msg_ProfileCreated, name),
                        R.Title_Complete, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        SwitchProfile(name);
                    }
                }
                catch (Exception ex)
                {
                    _dialogService.ShowMessage(ex.Message, R.Title_Error, MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void RenameProfile_Click(object sender, RoutedEventArgs e)
        {
            string? targetProfile = GetTargetProfileName(sender);
            if (string.IsNullOrEmpty(targetProfile)) return;

            if (targetProfile == _activeProfileName)
            {
                CollectWindowBounds(_currentSettings);
                ViewModel.SaveColumnsToSettings(_currentSettings);
                _settingsService.SaveSettings(_activeProfileName, _currentSettings);
            }

            var inputDlg = new InputWindow(R.Title_RenameProfile, R.Prompt_RenameProfile, targetProfile)
            {
                Owner = this
            };

            if (inputDlg.ShowDialog() != true) return;

            string newName = inputDlg.InputText?.Trim() ?? "";
            if (string.IsNullOrEmpty(newName) || newName == targetProfile) return;
            if (!IsValidProfileName(newName)) return;

            try
            {
                var config = _settingsService.LoadAppConfig();
                if (config.ProfileNames.Contains(newName))
                {
                    _dialogService.ShowMessage(string.Format(R.Msg_Err_ProfileExists, newName), R.Title_Error, MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _profileService.RenameProfile(targetProfile, newName);

                int idx = config.ProfileNames.IndexOf(targetProfile);
                if (idx >= 0) config.ProfileNames[idx] = newName;

                if (targetProfile == _activeProfileName)
                {
                    _activeProfileName = newName;
                    config.ActiveProfile = newName;
                    ViewModel.ActiveProfile = newName;
                }

                _settingsService.SaveAppConfig(config);
                InitializeProfilesUI();

                _dialogService.ShowMessage(string.Format(R.Msg_ProfileRenamed, targetProfile, newName), R.Title_Complete);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage(string.Format(R.Msg_Err_RenameFailed, ex.Message), R.Title_Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
        {
            string? targetProfile = GetTargetProfileName(sender);
            if (string.IsNullOrEmpty(targetProfile)) return;

            var inputWin = new InputWindow(R.Title_DuplicateProfile, R.Prompt_NewProfile, $"Copy of {targetProfile}")
            {
                Owner = this
            };

            if (inputWin.ShowDialog() != true) return;

            string newName = inputWin.InputText?.Trim() ?? "";
            if (!IsValidProfileName(newName)) return;

            try
            {
                var config = _settingsService.LoadAppConfig();
                if (config.ProfileNames.Contains(newName))
                {
                    _dialogService.ShowMessage(string.Format(R.Msg_Err_ProfileExists, newName), R.Title_Error, MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                bool isActiveProfile = (targetProfile == _activeProfileName);

                if (isActiveProfile)
                {
                    CollectWindowBounds(_currentSettings);
                    ViewModel.SaveColumnsToSettings(_currentSettings);
                    _settingsService.SaveSettings(_activeProfileName, _currentSettings);
                }

                _profileService.DuplicateProfileSettings(targetProfile, newName);

                config.ProfileNames.Add(newName);
                _settingsService.SaveAppConfig(config);
                InitializeProfilesUI();

                if (isActiveProfile)
                {
                    // アクティブプロファイルはWebViewがデータフォルダをロックしているため、
                    // その場でのコピーは失敗しやすい。再起動前(App起動時)にコピーを実行するよう予約する。
                    string msgConfirm = string.Format(R.Msg_ConfirmCloneActiveProfile, targetProfile);

                    if (_dialogService.ShowMessage(msgConfirm, R.Title_RestartConfirm, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    {
                        // 起動時に処理されるコピー予約ファイルを書き出す
                        _profileService.SchedulePendingClone(targetProfile, newName);

                        SwitchProfile(newName);
                    }
                }
                else
                {
                    // 非アクティブプロファイルはロックされていないため、その場でコピー可能
                    _profileService.CopyBrowserData(targetProfile, newName);

                    string msg = string.Format(R.Msg_ProfileCreated, newName);
                    if (_dialogService.ShowMessage(msg, R.Title_Complete, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        SwitchProfile(newName);
                    }
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage(string.Format(R.Msg_Err_ProfileCloneFailed, ex.Message), R.Title_Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            string? targetProfile = GetTargetProfileName(sender);
            if (string.IsNullOrEmpty(targetProfile)) return;

            if (targetProfile == _activeProfileName)
            {
                string msg = string.Format(R.Msg_Err_CannotDeleteActive, targetProfile);
                _dialogService.ShowMessage(msg, R.Title_Error, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string confirmMsg = string.Format(R.Msg_ConfirmDeleteProfile, targetProfile);
            if (_dialogService.ShowMessage(confirmMsg, R.Title_ConfirmDelete, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                _profileService.DeleteProfile(targetProfile);

                var config = _settingsService.LoadAppConfig();
                config.ProfileNames.Remove(targetProfile);
                _settingsService.SaveAppConfig(config);
                InitializeProfilesUI();

                string doneMsg = string.Format(R.Msg_ProfileDeleted, targetProfile);
                _dialogService.ShowMessage(doneMsg, R.Title_Complete);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage(string.Format(R.Msg_Err_DeleteFailed, ex.Message), R.Title_Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LaunchNewWindow_Click(object sender, RoutedEventArgs e)
        {
            string? targetProfile = GetTargetProfileName(sender);
            if (string.IsNullOrEmpty(targetProfile)) return;

            try
            {
                string exe = Environment.ProcessPath ?? "";
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"--profile \"{targetProfile}\"",
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(processInfo);
            }
            catch (Exception ex)
            {
                _dialogService.ShowMessage(string.Format(R.Msg_Err_LaunchFailed, ex.Message), R.Title_Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool IsValidProfileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                _dialogService.ShowMessage(R.Msg_Err_ProfileNameInvalid, R.Title_Error);
                return false;
            }

            var config = _settingsService.LoadAppConfig();
            if (config.ProfileNames.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                _dialogService.ShowMessage(string.Format(R.Msg_Err_ProfileExists, name), R.Title_Error);
                return false;
            }

            return true;
        }

        #endregion

        #region Menu Handlers

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var config = _settingsService.LoadAppConfig();
            var dlg = new SettingsWindow(_currentSettings, config, _settingsService.AppConfigPath) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _currentSettings = dlg.ViewModel.Result;
                ApplySettingsToView(_currentSettings);
                _settingsService.SaveSettings(_activeProfileName, _currentSettings);
            }
        }

        private void ManageExtensions_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ExtensionWindow(_currentSettings.Extensions) { Owner = this };
            dlg.ShowDialog();
        }

        private void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            string confirmMsg = R.ResourceManager.GetString("Msg_ConfirmClearCache")
                ?? "ブラウザの一時キャッシュを削除しますか？\n（ログイン情報は維持されます）";
            string title = R.ResourceManager.GetString("Menu_ClearCache") ?? "キャッシュのクリア";
            if (_dialogService.ShowMessage(confirmMsg, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                bool success = _settingsService.ClearBrowserCache(_activeProfileName);
                if (success)
                {
                    string successMsg = R.ResourceManager.GetString("Msg_ClearCacheSuccess") ?? "キャッシュを正常に削除しました。";
                    _dialogService.ShowMessage(successMsg, title, MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    string failedMsg = R.ResourceManager.GetString("Msg_ClearCacheFailed") ?? "キャッシュの削除に失敗しました。";
                    _dialogService.ShowMessage(failedMsg, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            var result = await _updateService.CheckForUpdatesAsync(version, _currentSettings.SkippedVersion);
            if (result.HasUpdate)
            {
                string msg = string.Format(R.Msg_UpdateAvailable, result.RemoteVersion);
                if (_dialogService.ShowMessage(msg, R.Settings_UpdateCheck, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    await _updateService.ApplyUpdateAsync(result, _activeProfileName);
                }
            }
            else
            {
                _dialogService.ShowMessage(R.Update_UpToDate, R.Settings_UpdateCheck, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async Task CheckForUpdatesSilentlyAsync()
        {
            try
            {
                string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
                var result = await _updateService.CheckForUpdatesAsync(version, _currentSettings.SkippedVersion);
                if (result.HasUpdate)
                {
                    string msg = string.Format(R.Msg_UpdateAvailable, result.RemoteVersion);
                    if (_dialogService.ShowMessage(msg, R.Settings_UpdateCheck, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    {
                        await _updateService.ApplyUpdateAsync(result, _activeProfileName);
                    }
                }
            }
            catch { }
        }

        private void CheckRateLimit429_Click(object sender, RoutedEventArgs e)
        {
            string estimate = _rateLimitService.BuildRateLimitEstimate(ViewModel.Columns, _activeProfileName);
            _dialogService.ShowMessage(estimate, R.Menu_RateLimitEstimate, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Support_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SupportWindow { Owner = this };
            dlg.ShowDialog();
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            string message = string.Format(R.Msg_About_Body, Title);
            MessageWindow.Show(message, R.Title_About, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ExportDebugInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string info = $"XColumn Version: {Assembly.GetExecutingAssembly().GetName().Version}\nOS: {Environment.OSVersion}\nProfile: {_activeProfileName}\nColumns: {ViewModel.Columns.Count}";
                Clipboard.SetText(info);
                _dialogService.ShowMessage("デバッグ情報をクリップボードにコピーしました。", "Debug", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInitialized || _currentSettings == null || _audioService == null) return;
            _currentSettings.AppVolume = e.NewValue / 100.0;
            _audioService.SetVolume(_currentSettings.AppVolume, GetActiveWebViewProcessIds());
        }

        #endregion

        #region Keyboard & Mouse Wheel

        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                var scrollViewer = ColumnItemsControl.Template?.FindName("MainScrollViewer", ColumnItemsControl) as ScrollViewer;
                if (scrollViewer != null)
                {
                    scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset + e.Delta);
                    e.Handled = true;
                }
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox || e.OriginalSource is PasswordBox || e.OriginalSource is ModernWpf.Controls.NumberBox)
                return;

            if (_isFocusMode && e.Key == Key.Escape)
            {
                ExitFocusMode();
                e.Handled = true;
            }
        }

        public void PerformHorizontalScroll(double delta)
        {
            var scrollViewer = ColumnItemsControl.Template?.FindName("MainScrollViewer", ColumnItemsControl) as ScrollViewer;
            if (scrollViewer != null)
            {
                scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset + delta);
            }
        }

        #endregion

        #region UI Event Handlers & Context

        public void RestartApplication() => SwitchProfile(_activeProfileName);

        public void OpenFocusMode(string url, bool isVideo = false)
        {
            if (url.Contains("/video/"))
            {
                isVideo = true;
            }

            if (_currentSettings.AutoPipForVideo && isVideo)
            {
                var pip = new PipWindow(url, _settingsService.UserDataFolder) { Owner = this };
                pip.Show();
                return;
            }

            EnterFocusMode(url);
        }

        private void ColumnHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is ColumnViewModel col)
            {
                foreach (var c in ViewModel.Columns) c.IsActive = (c == col);
            }
        }

        private void ColumnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                btn.ContextMenu.IsOpen = true;
            }
        }

        private async void RetweetHidden_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.Tag is ColumnViewModel col)
            {
                if (_columnWebViewMap.TryGetValue(col, out var wv) && wv.CoreWebView2 != null)
                {
                    await _scriptService.ApplyCustomCssAsync(wv.CoreWebView2, col.Url, col, _currentSettings);
                }
            }
        }

        private async void ReplyHidden_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.Tag is ColumnViewModel col)
            {
                if (_columnWebViewMap.TryGetValue(col, out var wv) && wv.CoreWebView2 != null)
                {
                    await _scriptService.ApplyCustomCssAsync(wv.CoreWebView2, col.Url, col, _currentSettings);
                }
            }
        }

        private void ColumnSetting_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox txt)
            {
                var binding = txt.GetBindingExpression(TextBox.TextProperty);
                binding?.UpdateSource();
                Keyboard.ClearFocus();
            }
        }

        private void WebView_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is WebView2 webView && webView.DataContext is ColumnViewModel col)
            {
                foreach (var c in ViewModel.Columns) c.IsActive = (c == col);
            }
        }

        private void StatusPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://downdetector.jp/shougai/twitter/") { UseShellExecute = true });
            }
            catch { }
        }

        private void StatusPanel_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 即座にサーバー状態を更新
        }

        private void MenuProfile_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource != sender) return;

            // 静的アイテム（[0]新規作成, [1]セパレータ）以外をクリア
            while (MenuProfile.Items.Count > 2)
            {
                MenuProfile.Items.RemoveAt(2);
            }

            var config = _settingsService.LoadAppConfig();
            foreach (var profileName in config.ProfileNames)
            {
                bool isActive = profileName == _activeProfileName;
                var parentItem = new MenuItem
                {
                    Header = profileName
                };

                if (isActive)
                {
                    parentItem.FontWeight = FontWeights.Bold;
                }

                // 1. 切り替え
                var switchItem = new MenuItem
                {
                    Header = R.Menu_Profile_Switch,
                    Tag = profileName,
                    IsEnabled = !isActive
                };
                switchItem.Click += SwitchProfile_Click;
                parentItem.Items.Add(switchItem);

                parentItem.Items.Add(new Separator());

                // 2. 名前変更
                var renameItem = new MenuItem
                {
                    Header = R.Menu_Profile_Rename,
                    Tag = profileName,
                    IsEnabled = !isActive
                };
                renameItem.Click += RenameProfile_Click;
                parentItem.Items.Add(renameItem);

                // 3. 複製
                var dupItem = new MenuItem
                {
                    Header = R.Menu_Profile_Duplicate,
                    Tag = profileName
                };
                dupItem.Click += DuplicateProfile_Click;
                parentItem.Items.Add(dupItem);

                // 4. 別窓で起動
                var launchItem = new MenuItem
                {
                    Header = R.Menu_Profile_LaunchNew,
                    Tag = profileName
                };
                launchItem.Click += LaunchNewWindow_Click;
                parentItem.Items.Add(launchItem);

                parentItem.Items.Add(new Separator());

                // 5. 削除
                var deleteItem = new MenuItem
                {
                    Header = R.Menu_Profile_Delete,
                    Tag = profileName,
                    Foreground = System.Windows.Media.Brushes.Red,
                    IsEnabled = !isActive
                };
                deleteItem.Click += DeleteProfile_Click;
                parentItem.Items.Add(deleteItem);

                MenuProfile.Items.Add(parentItem);
            }
        }

        private void MenuOtherProfileTimeline_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menu) return;
            menu.Items.Clear();

            var config = _settingsService.LoadAppConfig();
            var profiles = config.ProfileNames.Where(p => p != _activeProfileName);
            foreach (var p in profiles)
            {
                var item = new MenuItem { Header = p };
                item.Click += (s, ev) =>
                {
                    var col = new ColumnViewModel
                    {
                        Url = "https://x.com/home",
                        ProfileName = p,
                        Width = ColumnWidth,
                        UseSoftRefresh = _currentSettings.UseSoftRefresh,
                        UseRefreshJitter = _currentSettings.UseRefreshJitter,
                        KeepUnreadPosition = _currentSettings.KeepUnreadPosition,
                        ShowRateLimitRemaining = _currentSettings.ShowRateLimitRemaining
                    };
                    ViewModel.AddColumn(col);
                };
                menu.Items.Add(item);
            }

            if (!menu.Items.IsEmpty) return;
            menu.Items.Add(new MenuItem { Header = R.Menu_Loading, IsEnabled = false });
        }

        #endregion

        #region Messaging

        private void RegisterMessages()
        {
            var messenger = WeakReferenceMessenger.Default;

            messenger.Register<AddColumnRequest>(this, (r, m) => AddNewColumn(m.Url));
            messenger.Register<AddListAutoColumnRequest>(this, (r, m) => AddNewColumn("https://x.com/home", isListAuto: true));
            messenger.Register<AddSiteColumnRequest>(this, (r, m) => AddNewColumn(m.Url, isExternal: true));
            messenger.Register<CloseAppRequest>(this, (r, m) => Close());
        }

        #endregion
    }
}
