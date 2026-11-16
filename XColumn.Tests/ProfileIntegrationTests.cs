using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using XColumn.Models;
using XColumn.Services;
using XColumn.ViewModels;
using XColumn.Views;

namespace XColumn.Tests
{
    [TestClass]
    public class ProfileIntegrationTests
    {
        private string _tempDir = "";

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "XColumn_ProfileUI_Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, true);
            }
            catch { }
        }

        [TestMethod]
        public void StartupProfile_WhenNoArgs_ResolvesActiveProfileFromConfig()
        {
            // Arrange
            var settingsService = new SettingsService(_tempDir);
            var config = new AppConfig
            {
                ActiveProfile = "運営",
                ProfileNames = new List<string> { "default", "運営" }
            };
            settingsService.SaveAppConfig(config);

            // 起動プロファイル解決ロジックの単体検証
            var startupConfig = settingsService.LoadAppConfig();
            string? startupArg = null;
            string resolved = !string.IsNullOrEmpty(startupArg)
                ? startupArg
                : (!string.IsNullOrEmpty(startupConfig.StartupProfile)
                    ? startupConfig.StartupProfile
                    : (!string.IsNullOrEmpty(startupConfig.ActiveProfile) ? startupConfig.ActiveProfile : "default"));

            Assert.AreEqual("運営", resolved);

            // StartupProfileが指定されている場合はそちらが優先されることの検証
            config.StartupProfile = "special";
            settingsService.SaveAppConfig(config);
            var startupConfig2 = settingsService.LoadAppConfig();
            string resolved2 = !string.IsNullOrEmpty(startupArg)
                ? startupArg
                : (!string.IsNullOrEmpty(startupConfig2.StartupProfile)
                    ? startupConfig2.StartupProfile
                    : (!string.IsNullOrEmpty(startupConfig2.ActiveProfile) ? startupConfig2.ActiveProfile : "default"));

            Assert.AreEqual("special", resolved2);

            // 引数が指定されている場合は最優先されることの検証
            startupArg = "arg_profile";
            string resolved3 = !string.IsNullOrEmpty(startupArg)
                ? startupArg
                : (!string.IsNullOrEmpty(startupConfig2.StartupProfile)
                    ? startupConfig2.StartupProfile
                    : (!string.IsNullOrEmpty(startupConfig2.ActiveProfile) ? startupConfig2.ActiveProfile : "default"));

            Assert.AreEqual("arg_profile", resolved3);
        }

        [TestMethod]
        public void MenuProfile_SubmenuBuilding_PopulatesCorrectMenuItems()
        {
            Exception? threadEx = null;
            var t = new Thread(() =>
            {
                try
                {
                    if (System.Windows.Application.Current == null)
                    {
                        new XColumn.App();
                    }

                    var settingsService = new SettingsService(_tempDir);
                    var config = new AppConfig
                    {
                        ActiveProfile = "運営",
                        ProfileNames = new List<string> { "default", "運営" }
                    };
                    settingsService.SaveAppConfig(config);

                    var services = new ServiceCollection();
                    services.AddSingleton<ISettingsService>(settingsService);
                    services.AddSingleton<IProfileService>(new ProfileService(settingsService));
                    services.AddSingleton<IDialogService>(new Mock<IDialogService>().Object);
                    services.AddSingleton<IAppTimerService>(new Mock<IAppTimerService>().Object);
                    services.AddSingleton<IRateLimitService>(new Mock<IRateLimitService>().Object);
                    services.AddSingleton<IAudioService>(new Mock<IAudioService>().Object);
                    services.AddSingleton<IUpdateService>(new Mock<IUpdateService>().Object);
                    services.AddSingleton<IStatusService>(new Mock<IStatusService>().Object);
                    services.AddSingleton<IWebViewEnvironmentService>(new Mock<IWebViewEnvironmentService>().Object);
                    services.AddSingleton<IScriptInjectionService>(new Mock<IScriptInjectionService>().Object);
                    services.AddTransient<MainWindowViewModel>();
                    services.AddTransient<SettingsViewModel>();

                    var provider = services.BuildServiceProvider();
                    App.Current.Services = provider;

                    var window = new MainWindow(null);

                    // MenuProfile フィールド取得
                    var menuProfileField = typeof(MainWindow).GetField("MenuProfile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var menuProfile = (MenuItem?)menuProfileField?.GetValue(window);
                    Assert.IsNotNull(menuProfile);

                    // MenuProfile_SubmenuOpened 呼び出し
                    var menuMethod = typeof(MainWindow).GetMethod("MenuProfile_SubmenuOpened", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    Assert.IsNotNull(menuMethod);

                    var routeEvent = new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, menuProfile);
                    menuMethod.Invoke(window, new object[] { menuProfile, routeEvent });

                    // MenuProfile.Items に [0]新規作成, [1]セパレータ, [2]default, [3]運営 が生成されていることを確認
                    Assert.AreEqual(4, menuProfile.Items.Count, "MenuProfile should contain 4 items (New, Sep, default, 運営)");

                    var defaultItem = menuProfile.Items[2] as MenuItem;
                    Assert.IsNotNull(defaultItem);
                    Assert.AreEqual("default", defaultItem.Header);
                    Assert.AreEqual(7, defaultItem.Items.Count, "Profile item should have 7 items: Switch, Sep, Rename, Dup, Launch, Sep, Delete");

                    var uneiItem = menuProfile.Items[3] as MenuItem;
                    Assert.IsNotNull(uneiItem);
                    Assert.AreEqual("運営", uneiItem.Header);
                    Assert.AreEqual(7, uneiItem.Items.Count);

                    // 運営がアクティブなので太字になっていること
                    Assert.AreEqual(FontWeights.Bold, uneiItem.FontWeight);

                    window.Close();
                }
                catch (Exception ex)
                {
                    threadEx = ex;
                }
            });

            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();

            if (threadEx != null)
            {
                throw new AssertFailedException($"STA thread failed: {threadEx.Message}\n{threadEx.StackTrace}", threadEx);
            }
        }
    }
}
