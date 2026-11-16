using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using XColumn.Helpers;
using XColumn.Models;
using XColumn.Scripts;
using XColumn.ViewModels;

// 曖昧さ回避
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;
using R = XColumn.Properties.Resources;

namespace XColumn.Views
{
    /// <summary>
    /// MainWindowのWebView関連ロジック（環境初期化、イベントハンドラ、CSS/スクリプト注入、
    /// フォーカスモード連携、コンテキストメニュー、エラー監視など）を管理する分割クラス。
    /// </summary>
    public partial class MainWindow
    {
        #region Fields

        /// <summary>フォーカスモードで表示中のカラムデータ。</summary>
        private ColumnViewModel? _focusedColumnData = null;

        /// <summary>現在フォーカスモード（単一ビュー）かどうか。</summary>
        private bool _isFocusMode = false;

        /// <summary>メディア（画像・動画）をクリックしてフォーカスモードに入ったかどうか。</summary>
        private bool _isMediaFocusIntent = false;

        /// <summary>最後にネットワークエラーを検知した時刻（短時間の大量通知を抑止するデバウンス用）。</summary>
        private DateTime _lastDetectedErrorTime = DateTime.MinValue;

        #endregion

        #region Public Methods

        /// <summary>
        /// 拡張機能のオプションページをフォーカスモードで開きます。
        /// </summary>
        public void OpenExtensionOptions(ExtensionItem ext)
        {
            if (string.IsNullOrEmpty(ext.Id) || string.IsNullOrEmpty(ext.OptionsPage))
            {
                MessageWindow.Show(this, R.Extension_NoOptionsPage, R.Title_Error, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string url = (ext.OptionsPage.StartsWith("http://") || ext.OptionsPage.StartsWith("https://"))
                ? ext.OptionsPage
                : $"chrome-extension://{ext.Id}/{ext.OptionsPage}";

            EnterFocusMode(url);
        }

        #endregion

        #region Focus Mode WebView Initialization

        private async Task InitializeFocusWebView()
        {
            if (FocusWebView.CoreWebView2 != null) return;

            try
            {
                var env = await _environmentService.GetOrCreateEnvironmentAsync(_activeProfileName, _currentSettings.DisablePopupBlocking);
                if (env != null)
                {
                    await FocusWebView.EnsureCoreWebView2Async(env);

                    if (FocusWebView.CoreWebView2 != null)
                    {
                        FocusWebView.CoreWebView2.Settings.IsScriptEnabled = true;
                        FocusWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                        FocusWebView.CoreWebView2.Settings.AreDevToolsEnabled = _enableDevTools;

                        FocusWebView.CoreWebView2.ContextMenuRequested += CoreWebView2_ContextMenuRequested;
                        FocusWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                        FocusWebView.CoreWebView2.NavigationStarting += (s, e) =>
                        {
                            string url = e.Uri;
                            if (url.StartsWith("chrome-extension://")) return;

                            if (!IsAllowedDomain(url, focus: true))
                            {
                                e.Cancel = true;
                                OpenExternalLinkByMode(url);
                            }
                        };

                        FocusWebView.CoreWebView2.NavigationCompleted += async (s, e) =>
                        {
                            if (e.IsSuccess && FocusWebView.CoreWebView2 != null)
                            {
                                string src = FocusWebView.CoreWebView2.Source;
                                await _scriptService.ApplyCustomCssAsync(FocusWebView.CoreWebView2, src, _focusedColumnData, _currentSettings);
                                await _scriptService.ApplyYouTubeClickScriptAsync(FocusWebView.CoreWebView2);
                                await _scriptService.ApplyTrendingClickScriptAsync(FocusWebView.CoreWebView2);
                                await _scriptService.ApplyMediaExpandAsync(FocusWebView.CoreWebView2, _isMediaFocusIntent);
                                await _scriptService.ApplyKeyDetectionAsync(FocusWebView.CoreWebView2);
                                await _scriptService.ApplyAbsoluteTimeAsync(FocusWebView.CoreWebView2, _currentSettings.ShowAbsoluteTime);
                            }
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[MainWindow] InitializeFocusWebView error: {ex.Message}");
            }
        }

        #endregion

        #region WebView2 Loaded & Setup

        private async void WebView_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is WebView2 webView && webView.CoreWebView2 == null && webView.DataContext is ColumnViewModel col)
            {
                try
                {
                    string targetProfile = string.IsNullOrEmpty(col.ProfileName) ? _activeProfileName : col.ProfileName;
                    var env = await _environmentService.GetOrCreateEnvironmentAsync(targetProfile, _currentSettings.DisablePopupBlocking);
                    if (env != null)
                    {
                        webView.CoreWebView2InitializationCompleted += WebView_CoreWebView2InitializationCompleted;
                        await webView.EnsureCoreWebView2Async(env);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[MainWindow] WebView_Loaded error: {ex.Message}");
                }
            }
        }

        private async void WebView_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (sender is WebView2 webView)
            {
                webView.CoreWebView2InitializationCompleted -= WebView_CoreWebView2InitializationCompleted;
                if (e.IsSuccess && webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                    webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                    string targetProfile = _activeProfileName;
                    if (webView.DataContext is ColumnViewModel colData && !string.IsNullOrEmpty(colData.ProfileName))
                    {
                        targetProfile = colData.ProfileName;
                    }

                    // 拡張機能ロード（サービスに委譲）
                    await _environmentService.LoadExtensionsAsync(webView.CoreWebView2.Profile, targetProfile, _currentSettings.Extensions);

                    // テーマの適用
                    _environmentService.SetPreferredColorScheme(webView.CoreWebView2.Profile, _currentSettings.AppTheme ?? "System");

                    if (webView.DataContext is ColumnViewModel col)
                    {
                        SetupWebView(webView, col);
                    }
                }
            }
        }

        private void SetupWebView(WebView2 webView, ColumnViewModel col)
        {
            if (webView.CoreWebView2 == null) return;

            // マップへ登録（View側でのみ保持、循環参照防止）
            _columnWebViewMap[col] = webView;

            webView.CoreWebView2.Settings.IsScriptEnabled = true;
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = _enableDevTools;

            // レスポンス監視イベント
            webView.CoreWebView2.WebResourceResponseReceived += CoreWebView2_WebResourceResponseReceived;

            // クラッシュ自動復旧（ProcessFailed）
            webView.CoreWebView2.ProcessFailed += async (s, args) =>
            {
                bool recovered = await _environmentService.TryRecoverFromProcessFailureAsync(args, () =>
                {
                    webView.CoreWebView2?.Reload();
                });
                if (recovered)
                {
                    Logger.Log($"[MainWindow] Auto-recovered WebView2 for {col.Url}");
                }
            };

            webView.ZoomFactor = col.ZoomFactor;

            // WebMessage 受信ハンドラ（ESCキー、入力状態、外部リンク、横スクロール等）
            webView.CoreWebView2.WebMessageReceived += (s, e) =>
            {
                try
                {
                    string jsonString = e.TryGetWebMessageAsString();
                    if (string.IsNullOrEmpty(jsonString)) return;
                    var json = JsonNode.Parse(jsonString);
                    string? type = json?["type"]?.GetValue<string>();

                    if (type == "openFocusMode")
                    {
                        string? url = json?["url"]?.GetValue<string>();
                        bool isVideo = json?["isVideo"]?.GetValue<bool>() ?? false;
                        if (!string.IsNullOrEmpty(url) && (IsAllowedDomain(url, true) || IsAllowedDomain(url, false)))
                        {
                            _isMediaFocusIntent = url.Contains("/photo/") || url.Contains("/video/");

                            // 別プロファイルの場合はモーダルを起動せず、そのままカラム内で遷移させる
                            if (!string.IsNullOrEmpty(col.ProfileName) && col.ProfileName != _activeProfileName)
                            {
                                if (_columnWebViewMap.TryGetValue(col, out var targetWv)) targetWv.CoreWebView2?.Navigate(url);
                                return;
                            }

                            _focusedColumnData = col;
                            Dispatcher.InvokeAsync(() => OpenFocusMode(url, isVideo));
                        }
                    }
                    else if (type == "openPipVideo")
                    {
                        string? url = json?["url"]?.GetValue<string>();
                        if (_currentSettings.AutoPipForVideo && !string.IsNullOrEmpty(url) && IsYouTubeUrl(url))
                        {
                            Dispatcher.InvokeAsync(() => OpenFocusMode(url, isVideo: true));
                        }
                    }
                    else if (type == "openFocusVideo")
                    {
                        string? url = json?["url"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(url) && IsYouTubeUrl(url))
                        {
                            _focusedColumnData = col;
                            Dispatcher.InvokeAsync(() => EnterFocusMode(url));
                        }
                    }
                    else if (type == "inputState")
                    {
                        bool isActive = json?["val"]?.GetValue<bool>() ?? false;
                        col.IsInputActive = isActive;
                        if (isActive)
                        {
                            col.LastInputTime = DateTime.Now;
                        }
                    }
                    else if (type == "keyInput")
                    {
                        string? key = json?["key"]?.GetValue<string>();
                        if (key == "Escape")
                        {
                            if (_isFocusMode)
                            {
                                ExitFocusMode();
                            }
                            else
                            {
                                col.GoBack();
                            }
                        }
                    }
                    else if (type == "openNewColumn")
                    {
                        string? url = json?["url"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(url) && IsAllowedDomain(url))
                        {
                            Dispatcher.InvokeAsync(() => AddNewColumn(url));
                        }
                    }
                    else if (type == "openExternalLink")
                    {
                        string? url = json?["url"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(url) && !IsAllowedDomain(url) && !IsAllowedDomain(url, true))
                        {
                            Dispatcher.InvokeAsync(() => OpenExternalLinkByMode(url));
                        }
                    }
                    else if (type == "scrollState")
                    {
                        bool isTop = json?["isTop"]?.GetValue<bool>() ?? true;
                        col.IsAtTop = isTop;
                    }
                    else if (type == "horizontalScroll")
                    {
                        double delta = json?["delta"]?.GetValue<double>() ?? 0;
                        PerformHorizontalScroll(delta);
                    }
                    else if (type == "twitterBackClicked")
                    {
                        // Twitter画面内の戻るボタンが押されたときの復帰支援
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(250);
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (webView.CoreWebView2 != null)
                                {
                                    string currentSrc = webView.CoreWebView2.Source ?? "";
                                    if (!string.IsNullOrEmpty(col.Url) &&
                                        !string.Equals(currentSrc.TrimEnd('/'), col.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (webView.CoreWebView2.CanGoBack)
                                        {
                                            webView.CoreWebView2.GoBack();
                                        }
                                        else
                                        {
                                            webView.CoreWebView2.Navigate(col.Url);
                                        }
                                    }
                                }
                            });
                        });
                    }
                }
                catch { }
            };

            // ColumnViewModel からの委譲イベント・要求を購読
            col.GoBackRequested += async (c) =>
            {
                if (webView.CoreWebView2 == null) return;

                string currentSrc = webView.CoreWebView2.Source ?? "";
                bool isDifferentFromBase = !string.IsNullOrEmpty(c.Url) &&
                    !string.Equals(currentSrc.TrimEnd('/'), c.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

                if (webView.CoreWebView2.CanGoBack)
                {
                    webView.CoreWebView2.GoBack();
                }
                else if (isDifferentFromBase)
                {
                    // 履歴スタックが切れている場合は SPA の history.back() を試行
                    try
                    {
                        await webView.CoreWebView2.ExecuteScriptAsync("window.history.back();");
                    }
                    catch { }

                    // 一定時間待ってURLが基点に戻らなければ基点URLへ強制復帰
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(250);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (webView.CoreWebView2 != null)
                            {
                                string afterSrc = webView.CoreWebView2.Source ?? "";
                                if (!string.IsNullOrEmpty(c.Url) &&
                                    !string.Equals(afterSrc.TrimEnd('/'), c.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                                {
                                    webView.CoreWebView2.Navigate(c.Url);
                                }
                            }
                        });
                    });
                }
                else if (!string.IsNullOrEmpty(c.Url))
                {
                    webView.CoreWebView2.Navigate(c.Url);
                }
            };

            col.NavigateHomeRequested += (c) =>
            {
                if (webView.CoreWebView2 != null && !string.IsNullOrEmpty(c.Url))
                {
                    webView.CoreWebView2.Navigate(c.Url);
                }
            };

            col.SuspendRequested += (c, suspend) =>
            {
                OnColumnSuspendRequested(webView, c, suspend);
            };

            col.RateLimitSuspendRequested += (c, resumeAt) =>
            {
                OnColumnRateLimitSuspendRequested(webView, c, resumeAt);
            };

            col.ReloadRequested += async (c, forceReload, isUserInitiated) =>
            {
                await ReloadColumnWebViewAsync(webView, c, forceReload, isUserInitiated);
            };

            col.IsReadingOrHoveredQuery += (c) =>
            {
                return IsCursorOverWebView(webView);
            };

            // プロパティ変更連動
            col.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ColumnViewModel.ZoomFactor))
                {
                    try { webView.ZoomFactor = col.ZoomFactor; } catch { }
                }
                else if (e.PropertyName == nameof(ColumnViewModel.MediaScalePercentage))
                {
                    try
                    {
                        if (webView.CoreWebView2 != null)
                        {
                            _ = _scriptService.ApplyCustomCssAsync(webView.CoreWebView2, webView.CoreWebView2.Source, col, _currentSettings);
                        }
                    }
                    catch { }
                }
            };

            // ナビゲーション監視
            webView.CoreWebView2.NavigationStarting += (s, e) =>
            {
                string url = e.Uri;
                if (col.IsExternalSite) return;

                if (!IsAllowedDomain(url))
                {
                    e.Cancel = true;
                    OpenExternalLinkByMode(url);
                }
            };

            webView.CoreWebView2.NavigationCompleted += async (s, e) =>
            {
                if (e.IsSuccess && webView.CoreWebView2 != null)
                {
                    string currentUrl = webView.CoreWebView2.Source;
                    if (IsAllowedDomain(currentUrl) || col.IsExternalSite)
                    {
                        col.LastValidUrl = currentUrl;
                    }

                    // CSS / スクリプト注入を一元適用
                    await _scriptService.ApplyCustomCssAsync(webView.CoreWebView2, currentUrl, col, _currentSettings);
                    await _scriptService.ApplyVolumeAsync(webView.CoreWebView2, _currentSettings.AppVolume);
                    await _scriptService.ApplyYouTubeClickScriptAsync(webView.CoreWebView2);
                    await _scriptService.ApplyInterceptClickAsync(webView.CoreWebView2, _currentSettings.DisableFocusModeOnMediaClick, _currentSettings.DisableFocusModeOnTweetClick);
                    await _scriptService.ApplyTrendingClickScriptAsync(webView.CoreWebView2);
                    await _scriptService.ApplyScrollSyncAsync(webView.CoreWebView2);
                    await _scriptService.ApplyNgWordsAsync(webView.CoreWebView2, _currentSettings.NgWords);
                    await _scriptService.ApplyAbsoluteTimeAsync(webView.CoreWebView2, _currentSettings.ShowAbsoluteTime);
                }
            };

            // 初期ナビゲーション
            if (!string.IsNullOrEmpty(col.Url))
            {
                webView.CoreWebView2.Navigate(col.Url);
            }
        }

        #endregion

        #region Reload & Soft Refresh

        private async Task ReloadColumnWebViewAsync(WebView2 webView, ColumnViewModel col, bool forceReload, bool isUserInitiated)
        {
            if (webView.CoreWebView2 == null) return;

            string currentUrl = webView.CoreWebView2.Source;
            if (string.IsNullOrEmpty(currentUrl)) currentUrl = col.Url;

            // 表示中のURLが基点URLと異なっており、かつ手動更新または強制リロードの場合、
            // 以前の仕様どおり基点URLへの復帰を最優先で行う
            bool isDifferentFromBase = !string.IsNullOrEmpty(col.Url) &&
                !string.Equals(currentUrl.TrimEnd('/'), col.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

            if (isDifferentFromBase && (isUserInitiated || forceReload))
            {
                if (webView.CoreWebView2.CanGoBack)
                {
                    webView.CoreWebView2.GoBack();
                }
                else
                {
                    webView.CoreWebView2.Navigate(col.Url);
                }
                return;
            }

            // 非Xドメインは強制リロード
            if (!IsXDomain(currentUrl)) forceReload = true;

            if (!forceReload)
            {
                try
                {
                    // フォーカス外し
                    await webView.ExecuteScriptAsync("if (document.activeElement instanceof HTMLElement) document.activeElement.blur();");

                    // ピリオドキー送信
                    string keyDownJson = @"{""type"": ""keyDown"", ""modifiers"": 0, ""text"": ""."", ""unmodifiedText"": ""."", ""key"": ""."", ""code"": ""Period"", ""windowsVirtualKeyCode"": 190}";
                    await webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyDownJson);

                    string keyUpJson = @"{""type"": ""keyUp"", ""modifiers"": 0, ""key"": ""."", ""code"": ""Period"", ""windowsVirtualKeyCode"": 190}";
                    await webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyUpJson);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[MainWindow] Soft refresh failed: {ex.Message}");
                }
            }
            else
            {
                try
                {
                    webView.CoreWebView2.Reload();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[MainWindow] Force reload failed: {ex.Message}");
                }
            }
        }

        private static bool IsXDomain(string? url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            try
            {
                string host = new Uri(url).Host;
                return host.EndsWith("x.com") || host.EndsWith("twitter.com");
            }
            catch { return false; }
        }

        #endregion

        #region Rate Limit & Network Error Detection (バグ#6対策: 非同期ディスパッチ)

        private void CoreWebView2_WebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            int statusCode = e.Response?.StatusCode ?? 0;
            if (statusCode >= 400 && statusCode != 404)
            {
                _statusService.ReportNetworkError(statusCode);
            }

            if (sender is CoreWebView2 coreWebView && e.Response != null)
            {
                var col = ViewModel.Columns.FirstOrDefault(c => _columnWebViewMap.TryGetValue(c, out var wv) && wv.CoreWebView2 == coreWebView);
                if (col == null) return;

                // レスポンスヘッダーを辞書化
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in e.Response.Headers)
                {
                    headers[header.Key] = header.Value;
                }

                // IRateLimitService にヘッダー解析を委譲（非同期InvokeAsyncでUIスレッドへ通知）
                _rateLimitService.ProcessResponse(e.Request.Uri, statusCode, headers, col, _currentSettings.IgnoreRateLimit429);
            }
        }

        #endregion

        #region Suspend & Resume UI Operations

        private static string BuildSuspendScreenHtml(string title, string bodyHtml)
        {
            string body = (bodyHtml ?? "")
                .Replace("\r\n", "<br>")
                .Replace("\n", "<br>")
                .Replace("\r", "<br>");

            return $@"
                <html>
                <head><meta charset='utf-8'></head>
                <body style='background-color:#15202B; color:#8899A6; display:flex; justify-content:center; align-items:center; height:100vh; margin:0; font-family:sans-serif;'>
                    <div style='text-align:center;'>
                        <h2 style='margin-bottom:10px;'>{title}</h2>
                        <p style='font-size:14px;'>{body}</p>
                    </div>
                </body>
                </html>";
        }

        private void OnColumnSuspendRequested(WebView2 webView, ColumnViewModel col, bool suspend)
        {
            if (webView.CoreWebView2 == null) return;

            if (suspend)
            {
                string suspendHtml = BuildSuspendScreenHtml(R.Suspend_PausedTitle, R.Suspend_PausedBody);
                webView.CoreWebView2.NavigateToString(suspendHtml);
            }
            else
            {
                if (!string.IsNullOrEmpty(col.Url))
                {
                    webView.CoreWebView2.Navigate(col.Url);
                }
            }
        }

        private void OnColumnRateLimitSuspendRequested(WebView2 webView, ColumnViewModel col, DateTimeOffset resumeAt)
        {
            if (webView.CoreWebView2 == null) return;

            if (_currentSettings.ShowRateLimit429Screen)
            {
                col.DidShowRateLimitScreen = true;
                string body = string.Format(R.Suspend_RateLimitBody, resumeAt.LocalDateTime.ToString("HH:mm:ss"));
                string suspendHtml = BuildSuspendScreenHtml(R.Suspend_RateLimitTitle, body);
                webView.CoreWebView2.NavigateToString(suspendHtml);
            }
            else
            {
                col.DidShowRateLimitScreen = false;
            }
        }

        #endregion

        #region Focus Mode & Navigation (直前閲覧状態維持・自動バック廃止)

        private async void EnterFocusMode(string url)
        {
            _isFocusMode = true;
            FocusViewGrid.Visibility = Visibility.Visible;
            ColumnItemsControl.Visibility = Visibility.Collapsed;

            await InitializeFocusWebView();
            if (FocusWebView.CoreWebView2 != null)
            {
                FocusWebView.CoreWebView2.Navigate(url);
            }
        }

        private void ExitFocusMode()
        {
            _isFocusMode = false;
            _isMediaFocusIntent = false;

            if (FocusWebView?.CoreWebView2 != null)
            {
                try
                {
                    FocusWebView.CoreWebView2.Stop();
                    FocusWebView.CoreWebView2.Navigate("about:blank");
                }
                catch { }
            }

            FocusViewGrid.Visibility = Visibility.Collapsed;
            ColumnItemsControl.Visibility = Visibility.Visible;

            // フォーカスモード解除時の直前閲覧状態維持（強制 GoBack / Navigate を完全廃止）
            if (_focusedColumnData != null && _columnWebViewMap.TryGetValue(_focusedColumnData, out var webView))
            {
                webView.Focus();
            }
            _focusedColumnData = null;
        }

        private void CloseFocusView_Click(object sender, RoutedEventArgs e) => ExitFocusMode();

        private void FocusViewGrid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == FocusViewGrid)
            {
                ExitFocusMode();
            }
        }

        private void OpenExternalLinkByMode(string url)
        {
            switch (_currentSettings.ExternalLinkOpenMode)
            {
                case "Focus":
                    EnterFocusMode(url);
                    break;
                case "Pip":
                    var pip = new PipWindow(url, _settingsService.UserDataFolder) { Owner = this };
                    pip.Show();
                    break;
                default:
                    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                    break;
            }
        }

        private static bool IsAllowedDomain(string url, bool focus = false)
        {
            if (string.IsNullOrEmpty(url)) return false;
            try
            {
                var uri = new Uri(url);
                string host = uri.Host.ToLowerInvariant();
                return host.EndsWith("x.com") || host.EndsWith("twitter.com") || host.EndsWith("twimg.com");
            }
            catch { return false; }
        }

        private static bool IsYouTubeUrl(string url)
        {
            try
            {
                string host = new Uri(url).Host.ToLowerInvariant();
                return host.EndsWith("youtube.com") || host.EndsWith("youtu.be") || host.EndsWith("youtube-nocookie.com");
            }
            catch { return false; }
        }

        #endregion

        #region Context Menu & WebMessage

        private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            OpenExternalLinkByMode(e.Uri);
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string jsonString = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(jsonString)) jsonString = e.WebMessageAsJson;
                if (string.IsNullOrEmpty(jsonString)) return;

                var node = JsonNode.Parse(jsonString);
                if (node == null) return;

                string? type = node["type"]?.GetValue<string>();
                if ((type == "openFocusMode" || type == "openFocus") && node["url"] != null)
                {
                    string focusUrl = node["url"]!.GetValue<string>();
                    bool isVideo = node["isVideo"]?.GetValue<bool>() ?? false;
                    _isMediaFocusIntent = focusUrl.Contains("/photo/") || focusUrl.Contains("/video/");

                    if (sender is CoreWebView2 coreWv)
                    {
                        var col = ViewModel.Columns.FirstOrDefault(c => _columnWebViewMap.TryGetValue(c, out var wv) && wv.CoreWebView2 == coreWv);
                        if (col != null && !string.IsNullOrEmpty(col.ProfileName) && col.ProfileName != _activeProfileName)
                        {
                            if (_columnWebViewMap.TryGetValue(col, out var targetWv)) targetWv.CoreWebView2?.Navigate(focusUrl);
                            return;
                        }
                        _focusedColumnData = col;
                    }

                    Dispatcher.InvokeAsync(() => OpenFocusMode(focusUrl, isVideo));
                }
                else if (type == "keyInput")
                {
                    string? key = node["key"]?.GetValue<string>();
                    if (key == "Escape" && _isFocusMode)
                    {
                        ExitFocusMode();
                    }
                }
            }
            catch { }
        }

        private void CoreWebView2_ContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            // デフォルトのコンテキストメニューを許可
        }

        #endregion

        #region Win32 Cursor Helper

        private static bool IsCursorOverWebView(WebView2? webView)
        {
            if (webView == null || !webView.IsVisible) return false;
            try
            {
                Point screenPos = webView.PointToScreen(new Point(0, 0));
                double width = webView.ActualWidth;
                double height = webView.ActualHeight;

                if (GetCursorPos(out POINT pt))
                {
                    return pt.X >= screenPos.X && pt.X <= screenPos.X + width &&
                           pt.Y >= screenPos.Y && pt.Y <= screenPos.Y + height;
                }
            }
            catch { }
            return false;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        #endregion
    }
}
