using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XColumn.Models;
using XColumn.Services;

namespace XColumn.Tests
{
    [TestClass]
    public class SettingsServiceTests
    {
        private string _tempDir = "";

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "XColumn_Settings_Tests_" + Guid.NewGuid().ToString("N"));
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
        public void SaveAndReadSettings_Roundtrip_PreservesData()
        {
            // Arrange
            var service = new SettingsService(_tempDir);
            var settings = new AppSettings
            {
                ConfigVersion = 1,
                WindowLeft = 250,
                WindowTop = 150,
                WindowWidth = 1000,
                WindowHeight = 700,
                AppVolume = 0.8,
                Columns = new List<ColumnConfig>
                {
                    new ColumnConfig { Url = "https://x.com/home", Width = 400 },
                    new ColumnConfig { Url = "https://x.com/notifications", Width = 350 }
                }
            };

            // Act
            service.SaveSettings("test_profile", settings);
            var loaded = service.ReadSettings("test_profile");

            // Assert
            Assert.IsNotNull(loaded);
            Assert.AreEqual(250, loaded.WindowLeft);
            Assert.AreEqual(150, loaded.WindowTop);
            Assert.AreEqual(1000, loaded.WindowWidth);
            Assert.AreEqual(700, loaded.WindowHeight);
            Assert.AreEqual(0.8, loaded.AppVolume, 0.001);
            Assert.AreEqual(2, loaded.Columns.Count);
            Assert.AreEqual("https://x.com/home", loaded.Columns[0].Url);
            Assert.AreEqual(400, loaded.Columns[0].Width);
        }

        [TestMethod]
        public void ReadSettings_WhenFileCorrupted_ReturnsSafeDefaults()
        {
            // Arrange
            var service = new SettingsService(_tempDir);
            string profilePath = service.GetProfilePath("corrupted_profile");
            File.WriteAllText(profilePath, "THIS_IS_NOT_VALID_ENCRYPTED_DATA");

            // Act
            var settings = service.ReadSettings("corrupted_profile");

            // Assert - Safe fallback to defaults without throwing
            Assert.IsNotNull(settings);
            Assert.IsNotNull(settings.Columns);
        }

        [TestMethod]
        public void SaveAndLoadAppConfig_Roundtrip_WorksCorrectly()
        {
            // Arrange
            var service = new SettingsService(_tempDir);
            var config = new AppConfig
            {
                ActiveProfile = "custom",
                Language = "en-US",
                ProfileNames = new List<string> { "default", "custom" }
            };

            // Act
            service.SaveAppConfig(config);
            var loaded = service.LoadAppConfig();

            // Assert
            Assert.IsNotNull(loaded);
            Assert.AreEqual("custom", loaded.ActiveProfile);
            Assert.AreEqual("en-US", loaded.Language);
            Assert.IsTrue(loaded.ProfileNames.Contains("custom"));
        }
    }
}
