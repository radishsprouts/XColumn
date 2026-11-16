using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using XColumn.Helpers;
using XColumn.Models;
using XColumn.Scripts;
using XColumn.ViewModels;

namespace XColumn.Services
{
    /// <summary>
    /// IScriptInjectionService の既定実装。
    /// </summary>
    public class ScriptInjectionService : IScriptInjectionService
    {
        public async Task ApplyCustomCssAsync(CoreWebView2 webView, string url, bool isRetweetHidden, bool isReplyHidden, int mediaScalePercentage, AppSettings settings)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return;

                // 除外URLチェック（投稿/インテント画面にはCSSを当てない）
                if (url.Contains("/compose/") || url.Contains("/intent/")) return;

                string cssToInject = "";

                // 1. フォント設定
                if (!string.IsNullOrEmpty(settings.AppFontFamily))
                {
                    cssToInject += $@"body, div, span, p, a, h1, h2, h3, h4, h5, h6, input, textarea, button, select {{ font-family: '{settings.AppFontFamily}', sans-serif !important; }} ";
                }

                // 2. フォントサイズ設定
                if (settings.AppFontSize > 0)
                {
                    cssToInject += $@"html {{ font-size: {settings.AppFontSize}px !important; }} body {{ font-size: {settings.AppFontSize}px !important; }} div[dir='auto'], span, p, a, [data-testid='tweetText'] span, [data-testid='user-cell'] span {{ font-size: {settings.AppFontSize}px !important; line-height: 1.4 !important; }} ";
                }

                // 3. RT非表示
                if (isRetweetHidden)
                {
                    cssToInject += ScriptDefinitions.CssHideSocialContext + "\n";
                }

                // 4. Rep非表示
                if (isReplyHidden)
                {
                    cssToInject += ScriptDefinitions.CssHideRepliesClass + "\n";
                }

                // 5. 画像サイズ倍率
                bool isFocusMode = url.Contains("/status/");
                if (mediaScalePercentage != 100 && mediaScalePercentage > 0 && !isFocusMode)
                {
                    double scale = mediaScalePercentage / 100.0;
                    cssToInject += ScriptDefinitions.GetMediaScaleCss(scale) + "\n";
                }

                // 6. ユーザー定義カスタムCSS
                if (!string.IsNullOrEmpty(settings.CustomCss))
                {
                    cssToInject += settings.CustomCss + "\n";
                }

                // 7. 表示オプション
                bool isXDomain = url.Contains("twitter.com") || url.Contains("x.com");
                if (isXDomain)
                {
                    bool isHome = url.TrimEnd('/').Equals("https://x.com/home", StringComparison.OrdinalIgnoreCase) ||
                                  url.TrimEnd('/').Equals("https://twitter.com/home", StringComparison.OrdinalIgnoreCase);

                    if ((settings.HideMenuInHome && isHome) || (settings.HideMenuInNonHome && !isHome))
                    {
                        cssToInject += ScriptDefinitions.CssHideMenu + "\n";
                    }
                    if (settings.HideListHeader && url.Contains("/lists/"))
                    {
                        cssToInject += ScriptDefinitions.CssHideListHeader + "\n";
                    }
                    if (settings.HideRightSidebar)
                    {
                        cssToInject += ScriptDefinitions.CssHideRightSidebar + "\n";
                    }
                }

                if (string.IsNullOrEmpty(cssToInject)) return;

                string script = ScriptDefinitions.GetCssInjectionScript(cssToInject);
                await webView.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ScriptInjectionService] ApplyCustomCss failed: {ex.Message}");
            }
        }

        public async Task ApplyNgWordsAsync(CoreWebView2 webView, IEnumerable<string> ngWords)
        {
            try
            {
                var wordList = ngWords?.Where(w => !string.IsNullOrWhiteSpace(w)).ToList() ?? new List<string>();
                string wordsJson = JsonSerializer.Serialize(wordList);

                // 既存の Observer があれば切断してから再設定する冪等性ガード
                string script = $@"
                    (function() {{
                        if (window.xColumnNgObserver) {{
                            try {{ window.xColumnNgObserver.disconnect(); }} catch(e) {{}}
                            window.xColumnNgObserver = null;
                        }}
                        const ngWords = {wordsJson};
                        if (!ngWords || ngWords.length === 0) return;

                        function checkAndHide(node) {{
                            if (!node || !node.querySelector) return;
                            const tweetTextNode = node.querySelector('[data-testid=""tweetText""]');
                            if (!tweetTextNode) return;
                            const text = (tweetTextNode.innerText || '').toLowerCase();
                            const isNg = ngWords.some(w => text.includes(w.toLowerCase()));
                            if (isNg) {{
                                node.style.display = 'none';
                            }}
                        }}

                        document.querySelectorAll('[data-testid=""cellInnerDiv""]').forEach(checkAndHide);

                        window.xColumnNgObserver = new MutationObserver((mutations) => {{
                            for (let m of mutations) {{
                                for (let node of m.addedNodes) {{
                                    if (node.nodeType === 1) {{
                                        if (node.getAttribute && node.getAttribute('data-testid') === 'cellInnerDiv') {{
                                            checkAndHide(node);
                                        }} else if (node.querySelectorAll) {{
                                            node.querySelectorAll('[data-testid=""cellInnerDiv""]').forEach(checkAndHide);
                                        }}
                                    }}
                                }}
                            }}
                        }});
                        window.xColumnNgObserver.observe(document.body, {{ childList: true, subtree: true }});
                    }})();
                ";

                await webView.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ScriptInjectionService] ApplyNgWords failed: {ex.Message}");
            }
        }

        public async Task ApplyScrollSyncAsync(CoreWebView2 webView)
        {
            try
            {
                await webView.ExecuteScriptAsync(ScriptDefinitions.ScriptPreserveScrollPosition);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ScriptInjectionService] ApplyScrollSync failed: {ex.Message}");
            }
        }

        public async Task ApplyInterceptClickAsync(CoreWebView2 webView, bool disableMediaFocus = false, bool disableTweetFocus = false)
        {
            try
            {
                string mediaFocusStr = disableMediaFocus ? "true" : "false";
                string tweetFocusStr = disableTweetFocus ? "true" : "false";
                await webView.ExecuteScriptAsync($"window.xColumnDisableMediaFocus = {mediaFocusStr}; window.xColumnDisableTweetFocus = {tweetFocusStr};");
                await webView.ExecuteScriptAsync(ScriptDefinitions.ScriptInterceptClick);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ScriptInjectionService] ApplyInterceptClick failed: {ex.Message}");
            }
        }

        public async Task ApplyMediaExpandAsync(CoreWebView2 webView, bool forceExpand = true)
        {
            try
            {
                string expandStr = forceExpand ? "true" : "false";
                await webView.ExecuteScriptAsync($"window.xColumnForceExpand = {expandStr};");
                await webView.ExecuteScriptAsync(ScriptDefinitions.ScriptMediaExpand);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ScriptInjectionService] ApplyMediaExpand failed: {ex.Message}");
            }
        }

        public async Task ApplyAbsoluteTimeAsync(CoreWebView2 webView, bool showAbsoluteTime)
        {
            try
            {
                string boolStr = showAbsoluteTime ? "true" : "false"; // バグ#10対策: カルチャ非依存化
                await webView.ExecuteScriptAsync($"window.xColumnShowAbsoluteTime = {boolStr};");
                await webView.ExecuteScriptAsync(ScriptDefinitions.ScriptAbsoluteTime);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ScriptInjectionService] ApplyAbsoluteTime failed: {ex.Message}");
            }
        }

        public async Task ApplyInputDetectionAsync(CoreWebView2 webView)
        {
            try
            {
                await webView.ExecuteScriptAsync(ScriptDefinitions.ScriptDetectInput);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ScriptInjectionService] ApplyInputDetection failed: {ex.Message}");
            }
        }

        public async Task ApplyKeyDetectionAsync(CoreWebView2 webView)
        {
            try
            {
                await webView.ExecuteScriptAsync(ScriptDefinitions.ScriptDetectKeyInput);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ScriptInjectionService] ApplyKeyDetection failed: {ex.Message}");
            }
        }

        public async Task ApplyDisableVideoAutoplayAsync(CoreWebView2 webView)
        {
            try
            {
                await webView.ExecuteScriptAsync(ScriptDefinitions.ScriptDisableVideoAutoplay);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ScriptInjectionService] ApplyDisableVideoAutoplay failed: {ex.Message}");
            }
        }

        public async Task ApplyCustomCssAsync(CoreWebView2 webView, string url, ColumnViewModel? column, AppSettings settings)
        {
            bool retweet = column?.IsRetweetHidden ?? false;
            bool reply = column?.IsReplyHidden ?? false;
            int scale = column?.MediaScalePercentage ?? 100;
            await ApplyCustomCssAsync(webView, url, retweet, reply, scale, settings);
        }

        public async Task ApplyYouTubeClickScriptAsync(CoreWebView2 webView)
        {
            try
            {
                await webView.ExecuteScriptAsync(ScriptDefinitions.ScriptYouTubeClick);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ScriptInjectionService] ApplyYouTubeClickScript failed: {ex.Message}");
            }
        }

        public async Task ApplyTrendingClickScriptAsync(CoreWebView2 webView)
        {
            try
            {
                await webView.ExecuteScriptAsync(ScriptDefinitions.ScriptTrendingClick);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ScriptInjectionService] ApplyTrendingClickScript failed: {ex.Message}");
            }
        }

        public async Task ApplyVolumeAsync(CoreWebView2 webView, double volume)
        {
            try
            {
                // カルチャ非依存（バグ#10対策）
                string volStr = volume.ToString("F2", CultureInfo.InvariantCulture);
                string script = $@"
                    (function() {{
                        const v = {volStr};
                        document.querySelectorAll('video, audio').forEach(el => {{ el.volume = v; }});
                    }})();";
                await webView.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ScriptInjectionService] ApplyVolume failed: {ex.Message}");
            }
        }
    }
}
