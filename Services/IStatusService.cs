namespace XColumn.Services
{
    /// <summary>
    /// X/Twitter サーバーとの接続ステータス。
    /// </summary>
    public enum ConnectionStatus
    {
        /// <summary>正常稼働（緑）。</summary>
        Normal,

        /// <summary>一時的な障害・パフォーマンス低下の検知（黄）。</summary>
        Degraded,

        /// <summary>サーバーダウン・接続不能（赤）。</summary>
        Disconnected
    }

    public class ConnectionStatusChangedEventArgs : EventArgs
    {
        public ConnectionStatus NewStatus { get; }
        public string Message { get; }

        public ConnectionStatusChangedEventArgs(ConnectionStatus newStatus, string message)
        {
            NewStatus = newStatus;
            Message = message;
        }
    }

    /// <summary>
    /// X サーバーおよび API の稼働状況（接続性）を定期監視するサービス。
    /// </summary>
    public interface IStatusService
    {
        /// <summary>現在の接続ステータス。</summary>
        ConnectionStatus CurrentStatus { get; }

        /// <summary>ステータス変更イベント。</summary>
        event EventHandler<ConnectionStatusChangedEventArgs>? StatusChanged;

        /// <summary>WebView等からネットワークエラーを報告します。</summary>
        void ReportNetworkError(int statusCode);

        /// <summary>監視タイマーの間隔（分）を設定します。</summary>
        void UpdateCheckInterval(int intervalMinutes);

        /// <summary>手動で接続チェックを実行します。</summary>
        Task CheckStatusAsync();
    }
}
