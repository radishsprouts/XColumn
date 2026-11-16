using Microsoft.Web.WebView2.Core;
using XColumn.Models;

namespace XColumn.Services
{
    /// <summary>
    /// WebView2 の実行環境（Environment）、プロファイル設定、拡張機能ロード、テーマ連動、クラッシュリカバリを管理するサービス。
    /// </summary>
    public interface IWebViewEnvironmentService
    {
        /// <summary>
        /// 指定プロファイルの CoreWebView2Environment を取得（または生成）します。
        /// </summary>
        Task<CoreWebView2Environment?> GetOrCreateEnvironmentAsync(string profileName, bool disablePopupBlocking = false);

        /// <summary>
        /// 指定プロファイルに有効な Chrome 拡張機能をロードします。
        /// </summary>
        Task LoadExtensionsAsync(CoreWebView2Profile profile, string profileName, IEnumerable<ExtensionItem> extensions);

        /// <summary>
        /// アプリテーマに合わせて WebView2 の PreferredColorScheme を更新します。
        /// </summary>
        void SetPreferredColorScheme(CoreWebView2Profile profile, string theme);

        /// <summary>
        /// レンダラープロセスのクラッシュ（ProcessFailed）時に自動リカバリを試行します。
        /// </summary>
        Task<bool> TryRecoverFromProcessFailureAsync(CoreWebView2ProcessFailedEventArgs args, Action onReloadRequested);
    }
}
