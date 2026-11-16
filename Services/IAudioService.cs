namespace XColumn.Services
{
    /// <summary>
    /// XColumn およびその管理下にある WebView2 プロセスの音量を制御するサービス。
    /// 外部アプリケーション（Teams, Outlook 等）の WebView2 プロセスに影響を与えません。
    /// </summary>
    public interface IAudioService
    {
        /// <summary>
        /// 指定された音量（0.0～1.0）を、自プロセスおよび指定された WebView2 プロセス群にのみ適用します。
        /// </summary>
        /// <param name="volume">音量（0.0～1.0）。</param>
        /// <param name="targetProcessIds">音量変更を許可する自 WebView2 プロセスIDのリスト。</param>
        void SetVolume(double volume, IEnumerable<int> targetProcessIds);
    }
}
