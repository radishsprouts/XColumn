using System;
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
    public class FocusModeIntegrationTests
    {
        private string _tempDir = "";

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "XColumn_Focus_Tests_" + Guid.NewGuid().ToString("N"));
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
        public void OpenFocusMode_ActivatesFocusModeState()
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

                    // Act: 画像URLでフォーカスモードを開く
                    string imageUrl = "https://x.com/user/status/123456789/photo/1";
                    window.OpenFocusMode(imageUrl, isVideo: false);

                    // Assert: _isFocusMode フラグが true になっていること
                    var isFocusField = typeof(MainWindow).GetField("_isFocusMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    Assert.IsNotNull(isFocusField);
                    bool isFocus = (bool)isFocusField.GetValue(window)!;
                    Assert.IsTrue(isFocus, "_isFocusMode should be true after OpenFocusMode");

                    // Assert: FocusViewGrid が Visible になり、ColumnItemsControl が Collapsed になっていること
                    var focusGridField = typeof(MainWindow).GetField("FocusViewGrid", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var colControlField = typeof(MainWindow).GetField("ColumnItemsControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var focusGrid = (Grid?)focusGridField?.GetValue(window);
                    var colControl = (ItemsControl?)colControlField?.GetValue(window);
                    Assert.IsNotNull(focusGrid);
                    Assert.IsNotNull(colControl);

                    Assert.AreEqual(Visibility.Visible, focusGrid.Visibility);
                    Assert.AreEqual(Visibility.Collapsed, colControl.Visibility);

                    // Act: ESC / 終了処理（ExitFocusMode）
                    var exitMethod = typeof(MainWindow).GetMethod("ExitFocusMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    Assert.IsNotNull(exitMethod);
                    exitMethod.Invoke(window, null);

                    // Assert: 正常にカラム表示に戻ること
                    isFocus = (bool)isFocusField.GetValue(window)!;
                    Assert.IsFalse(isFocus, "_isFocusMode should be false after ExitFocusMode");
                    Assert.AreEqual(Visibility.Collapsed, focusGrid.Visibility);
                    Assert.AreEqual(Visibility.Visible, colControl.Visibility);

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
