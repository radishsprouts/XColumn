using Microsoft.Web.WebView2.Core;
using XColumn.Models;
using XColumn.ViewModels;

namespace XColumn.Services
{
    /// <summary>
    /// WebView2 への CSS および JavaScript の安全な注入を管理するサービス。
    /// カルチャ依存バグの防止、MutationObserver の重複登録防止（冪等性保証）を提供します。
    /// </summary>
    public interface IScriptInjectionService
    {
        /// <summary>カスタムCSSおよびレイアウトCSSをWebViewへ注入します。</summary>
        Task ApplyCustomCssAsync(CoreWebView2 webView, string url, bool isRetweetHidden, bool isReplyHidden, int mediaScalePercentage, AppSettings settings);

        /// <summary>ColumnViewModel からカスタムCSSをWebViewへ注入します。</summary>
        Task ApplyCustomCssAsync(CoreWebView2 webView, string url, ColumnViewModel? column, AppSettings settings);

        /// <summary>NGワード判定スクリプトを注入します（二重Observer防止ガード付き）。</summary>
        Task ApplyNgWordsAsync(CoreWebView2 webView, IEnumerable<string> ngWords);

        /// <summary>スクロール同期スクリプトを注入します。</summary>
        Task ApplyScrollSyncAsync(CoreWebView2 webView);

        /// <summary>クリック横取りスクリプト（メディア・ツイートクリック制御）を注入します。</summary>
        Task ApplyInterceptClickAsync(CoreWebView2 webView, bool disableMediaFocus = false, bool disableTweetFocus = false);

        /// <summary>フォーカスモードでのメディア自動展開スクリプトを注入します。</summary>
        Task ApplyMediaExpandAsync(CoreWebView2 webView, bool forceExpand = true);

        /// <summary>絶対時刻表示スクリプトを注入します。</summary>
        Task ApplyAbsoluteTimeAsync(CoreWebView2 webView, bool showAbsoluteTime);

        /// <summary>入力フォーム監視スクリプトを注入します。</summary>
        Task ApplyInputDetectionAsync(CoreWebView2 webView);

        /// <summary>キー入力監視スクリプト（ESC対応）を注入します。</summary>
        Task ApplyKeyDetectionAsync(CoreWebView2 webView);

        /// <summary>動画の自動再生無効化スクリプトを注入します。</summary>
        Task ApplyDisableVideoAutoplayAsync(CoreWebView2 webView);

        /// <summary>YouTubeリンクのクリック制御スクリプトを注入します。</summary>
        Task ApplyYouTubeClickScriptAsync(CoreWebView2 webView);

        /// <summary>トレンドクリック制御スクリプトを注入します。</summary>
        Task ApplyTrendingClickScriptAsync(CoreWebView2 webView);

        /// <summary>音量制御スクリプトを注入します。</summary>
        Task ApplyVolumeAsync(CoreWebView2 webView, double volume);
    }
}
