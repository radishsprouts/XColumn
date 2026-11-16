using System.Net.Http;
using System.Windows.Threading;
using XColumn.Helpers;

namespace XColumn.Services
{
    /// <summary>
    /// IStatusService の既定実装。
    /// </summary>
    public class StatusService : IStatusService
    {
        private const string TargetUrl = "https://x.com";
        private const string ApiTargetUrl = "https://api.x.com";

        private readonly HttpClient _httpClient;
        private DispatcherTimer? _statusTimer;
        private int _checkIntervalMinutes = 5;
        private DateTime _lastNetworkErrorTime = DateTime.MinValue;

        public ConnectionStatus CurrentStatus { get; private set; } = ConnectionStatus.Normal;
        public event EventHandler<ConnectionStatusChangedEventArgs>? StatusChanged;

        public StatusService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }

        public void ReportNetworkError(int statusCode)
        {
            _lastNetworkErrorTime = DateTime.Now;
            Logger.Log($"[StatusService] Reported network error: {statusCode}");
        }

        public void UpdateCheckInterval(int intervalMinutes)
        {
            _checkIntervalMinutes = Math.Max(1, intervalMinutes);
            if (_statusTimer != null)
            {
                _statusTimer.Stop();
                _statusTimer.Interval = TimeSpan.FromMinutes(_checkIntervalMinutes);
                _statusTimer.Start();
            }
            else
            {
                _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(_checkIntervalMinutes) };
                _statusTimer.Tick += async (s, e) => await CheckStatusAsync();
                _statusTimer.Start();
            }
        }

        public async Task CheckStatusAsync()
        {
            try
            {
                bool isTargetOk = await CheckEndpointAsync(TargetUrl);
                bool isApiOk = await CheckEndpointAsync(ApiTargetUrl);

                bool isRecentError = (DateTime.Now - _lastNetworkErrorTime).TotalMinutes < 2;

                ConnectionStatus newStatus;
                string message;

                if (!isTargetOk && !isApiOk)
                {
                    newStatus = ConnectionStatus.Disconnected;
                    message = "X サーバーへの接続が切断されています。";
                }
                else if (!isTargetOk || !isApiOk || isRecentError)
                {
                    newStatus = ConnectionStatus.Degraded;
                    message = "X の応答が不安定です。一時的な障害が発生している可能性があります。";
                }
                else
                {
                    newStatus = ConnectionStatus.Normal;
                    message = "X サーバーは正常に稼働しています。";
                }

                if (newStatus != CurrentStatus)
                {
                    CurrentStatus = newStatus;
                    StatusChanged?.Invoke(this, new ConnectionStatusChangedEventArgs(newStatus, message));
                    Logger.Log($"[StatusService] Status changed: {newStatus} - {message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[StatusService] Check failed: {ex.Message}");
            }
        }

        private async Task<bool> CheckEndpointAsync(string url)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                return (int)res.StatusCode < 500;
            }
            catch
            {
                return false;
            }
        }
    }
}
