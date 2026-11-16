using System.Diagnostics;
using NAudio.CoreAudioApi;
using XColumn.Helpers;

namespace XColumn.Services
{
    /// <summary>
    /// IAudioService の既定実装。
    /// CoreAudioAPI を用いて、XColumn 自身および自インスタンスの WebView2 プロセスのみに絞り込んで音量を調整します。
    /// </summary>
    public class AudioService : IAudioService
    {
        public void SetVolume(double volume, IEnumerable<int> targetProcessIds)
        {
            try
            {
                float vol = (float)Math.Clamp(volume, 0.0, 1.0);

                var allowedPids = new HashSet<int>(targetProcessIds.Where(pid => pid > 0));
                allowedPids.Add(Environment.ProcessId);

                using var deviceEnumerator = new MMDeviceEnumerator();
                if (!deviceEnumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    return;
                }

                using var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessionManager = defaultDevice.AudioSessionManager;
                var sessionCollection = sessionManager.Sessions;

                for (int i = 0; i < sessionCollection.Count; i++)
                {
                    var session = sessionCollection[i];
                    try
                    {
                        int sessionPid = (int)session.GetProcessID;
                        // 自プロセスまたは自WebView2プロセスのセッションのみ変更
                        if (allowedPids.Contains(sessionPid))
                        {
                            session.SimpleAudioVolume.Volume = vol;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Audio session volume error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"SetVolume failed: {ex.Message}");
            }
        }
    }
}
