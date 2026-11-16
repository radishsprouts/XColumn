using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using XColumn.Helpers;
using XColumn.Models;
using XColumn.Properties;
using XColumn.Services;
using XColumn.ViewModels;
using XColumn.Views;
using Resources = XColumn.Properties.Resources;

// 曖昧さ回避
using MessageBox = System.Windows.MessageBox;

namespace XColumn
{
    /// <summary>
    /// アプリケーションのエントリポイント。
    /// DIコンテナ、グローバル未処理例外ハンドラー、クラッシュログ保存、
    /// および初期化パイプラインを統括します。
    /// </summary>
    public partial class App : System.Windows.Application
    {
        /// <summary>
        /// 型付きで App インスタンスにアクセスするためのショートカット。
        /// </summary>
        public new static App Current => (App)System.Windows.Application.Current;

        /// <summary>
        /// アプリ全体のDIコンテナ。
        /// </summary>
        public IServiceProvider Services { get; set; } = null!;

        private readonly string _userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XColumn");

        /// <summary>
        /// スタートアップ処理。
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            // グローバル未処理例外ハンドラーの登録（クラッシュ保護とログ出力）
            RegisterGlobalExceptionHandlers();

            // 自己更新(リネーム方式)で残った旧実行ファイル XColumn.exe.old を削除
            CleanupOldExecutable();

            // 拡張機能フォルダの自動移行（バグ#7対策）
            ChromeExtensionImporter.MigrateLegacyExtensionsFolder();

            // DIコンテナの構築
            Services = ConfigureServices();

            // 言語設定の適用（UI表示前に行う）
            ApplyLanguageSettings();

            // 起動前に、保留されていたプロファイル複製処理を実行
            ProcessPendingProfileClone();

            base.OnStartup(e);

            // プロファイル指定用変数
            string? targetProfile = null;
            bool enableDevTools = false;
            bool disableGpu = false;
            int waitPid = 0;

            // コマンドライン引数の解析
            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--profile" && i + 1 < e.Args.Length)
                {
                    targetProfile = e.Args[i + 1];
                    i++;
                }
                else if (e.Args[i] == "--wait-pid" && i + 1 < e.Args.Length)
                {
                    int.TryParse(e.Args[i + 1], out waitPid);
                    i++;
                }
                else if (e.Args[i] == "--enable-devtools")
                {
                    enableDevTools = true;
                }
                else if (e.Args[i] == "--disable-gpu")
                {
                    disableGpu = true;
                }
            }

            // 再起動時は旧プロセスの終了を待機（0x8007139F クラッシュ防止）
            if (waitPid > 0) WaitForPreviousInstanceExit(waitPid);

            // デフォルト起動プロファイルの読み込み
            if (string.IsNullOrEmpty(targetProfile))
            {
                string appConfigPath = Path.Combine(_userDataFolder, "app_config.json");
                if (File.Exists(appConfigPath))
                {
                    try
                    {
                        string json = File.ReadAllText(appConfigPath);
                        var config = JsonSerializer.Deserialize<AppConfig>(json);
                        if (config != null && !string.IsNullOrEmpty(config.StartupProfile))
                        {
                            targetProfile = config.StartupProfile;
                        }
                    }
                    catch { }
                }
            }

            // メインウィンドウを起動
            var mainWindow = new MainWindow(targetProfile, enableDevTools, disableGpu);
            mainWindow.Show();
        }

        /// <summary>
        /// 全サービスとViewModelをDIコンテナに登録します。
        /// </summary>
        private static IServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            // --- Core Services ---
            services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<IProfileService, ProfileService>();
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IAppTimerService, AppTimerService>();
            services.AddSingleton<IRateLimitService, RateLimitService>();
            services.AddSingleton<IAudioService, AudioService>();
            services.AddSingleton<IUpdateService, UpdateService>();
            services.AddSingleton<IStatusService, StatusService>();
            services.AddSingleton<IWebViewEnvironmentService, WebViewEnvironmentService>();
            services.AddSingleton<IScriptInjectionService, ScriptInjectionService>();

            // --- ViewModels ---
            services.AddTransient<MainWindowViewModel>();
            services.AddTransient<SettingsViewModel>();

            return services.BuildServiceProvider();
        }

        /// <summary>
        /// グローバル例外ハンドラーを登録し、予期せぬクラッシュの抑止とログ保存を行います。
        /// </summary>
        private void RegisterGlobalExceptionHandlers()
        {
            // UIスレッドの未処理例外
            DispatcherUnhandledException += (s, args) =>
            {
                LogCrash("DispatcherUnhandledException", args.Exception);
                string msg = XColumn.Properties.Resources.ResourceManager.GetString("Err_UnhandledException") 
                    ?? "予期せぬエラーが発生しました。\nログを保存しました。";
                MessageBox.Show($"{msg}\n\n{args.Exception.Message}", "XColumn Error", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true; // プロセス強制終了を阻止
            };

            // バックグラウンドスレッドの未処理例外
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    LogCrash("AppDomainUnhandledException", ex);
                }
            };

            // 非監視 Task の例外
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                LogCrash("UnobservedTaskException", args.Exception);
                args.SetObserved();
            };
        }

        /// <summary>
        /// クラッシュログを %AppData%\XColumn\Logs\ に出力します。
        /// </summary>
        private void LogCrash(string source, Exception ex)
        {
            try
            {
                string logDir = Path.Combine(_userDataFolder, "Logs");
                Directory.CreateDirectory(logDir);
                string filename = $"crash_{DateTime.Now:yyyyMMdd_HHmmss}_{source}.log";
                string fullPath = Path.Combine(logDir, filename);

                string content = $"=== XColumn Crash Log ===\n" +
                                 $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                                 $"Source: {source}\n" +
                                 $"OS: {Environment.OSVersion}\n" +
                                 $"Process Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n" +
                                 $"Exception Type: {ex.GetType().FullName}\n" +
                                 $"Message: {ex.Message}\n" +
                                 $"StackTrace:\n{ex.StackTrace}\n";

                if (ex.InnerException != null)
                {
                    content += $"\n--- Inner Exception ---\n" +
                               $"Type: {ex.InnerException.GetType().FullName}\n" +
                               $"Message: {ex.InnerException.Message}\n" +
                               $"StackTrace:\n{ex.InnerException.StackTrace}\n";
                }

                File.WriteAllText(fullPath, content);
                Logger.Log($"Crash log saved: {fullPath}");
            }
            catch { }
        }

        /// <summary>
        /// app_config.json を読み込み、設定された言語をスレッドに適用します。
        /// </summary>
        private void ApplyLanguageSettings()
        {
            string appConfigPath = Path.Combine(_userDataFolder, "app_config.json");
            string language = "ja-JP"; // デフォルト

            if (File.Exists(appConfigPath))
            {
                try
                {
                    string json = File.ReadAllText(appConfigPath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null && !string.IsNullOrEmpty(config.Language))
                    {
                        language = config.Language;
                    }
                }
                catch { }
            }

            try
            {
                var culture = new CultureInfo(language);
                Thread.CurrentThread.CurrentCulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
            }
            catch { }
        }

        /// <summary>
        /// 再起動前に予約されたプロファイル複製処理を実行します。
        /// </summary>
        private void ProcessPendingProfileClone()
        {
            try
            {
                Services.GetRequiredService<IProfileService>().ProcessPendingClone();
            }
            catch (Exception ex)
            {
                string msg = string.Format(XColumn.Properties.Resources.Msg_Err_ProfileCloneFailed, ex.Message);
                MessageWindow.Show(msg, XColumn.Properties.Resources.Title_Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 自己更新(リネーム方式)で退避した旧実行ファイル "XColumn.exe.old" を削除します。
        /// </summary>
        private void CleanupOldExecutable()
        {
            string? exe = Environment.ProcessPath
                          ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;

            string oldExe = exe + ".old";

            Task.Run(() =>
            {
                for (int i = 0; i < 60; i++)
                {
                    try
                    {
                        if (!File.Exists(oldExe)) return;
                        File.Delete(oldExe);
                        Logger.Log("Self-update: old executable removed.");
                        return;
                    }
                    catch
                    {
                        Thread.Sleep(500);
                    }
                }
                Logger.Log("Self-update: old executable still locked; will retry on next launch.");
            });
        }

        /// <summary>
        /// 再起動元の旧プロセスの終了を待機します（最大10秒）。
        /// </summary>
        private static void WaitForPreviousInstanceExit(int pid)
        {
            try
            {
                using var prev = System.Diagnostics.Process.GetProcessById(pid);
                prev.WaitForExit(10000);
            }
            catch { }

            Thread.Sleep(500);
        }
    }
}
