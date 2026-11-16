using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using XColumn.Models;
using XColumn.Services;

namespace XColumn.Tests
{
    [TestClass]
    public class ProfileServiceTests
    {
        private string _tempDir = "";
        private SettingsService _settingsService = null!;
        private ProfileService _profileService = null!;

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "XColumn_Profile_Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _settingsService = new SettingsService(_tempDir);
            _profileService = new ProfileService(_settingsService);
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
        public void RenameProfile_RenamesSettingsFileAndBrowserDataFolder()
        {
            // Arrange
            _settingsService.SaveSettings("ProfileA", new AppSettings { AppVolume = 0.5 });
            string oldBrowserPath = _profileService.GetBrowserDataPath("ProfileA");
            Directory.CreateDirectory(oldBrowserPath);
            File.WriteAllText(Path.Combine(oldBrowserPath, "test.txt"), "hello");

            // Act
            _profileService.RenameProfile("ProfileA", "ProfileB");

            // Assert
            Assert.IsFalse(_profileService.SettingsFileExists("ProfileA"));
            Assert.IsTrue(_profileService.SettingsFileExists("ProfileB"));
            Assert.IsFalse(Directory.Exists(oldBrowserPath));
            string newBrowserPath = _profileService.GetBrowserDataPath("ProfileB");
            Assert.IsTrue(Directory.Exists(newBrowserPath));
            Assert.IsTrue(File.Exists(Path.Combine(newBrowserPath, "test.txt")));
        }

        [TestMethod]
        public void DuplicateProfileSettings_CopiesSettings()
        {
            // Arrange
            _settingsService.SaveSettings("ProfileSrc", new AppSettings { AppVolume = 0.75 });

            // Act
            _profileService.DuplicateProfileSettings("ProfileSrc", "ProfileDst");

            // Assert
            Assert.IsTrue(_profileService.SettingsFileExists("ProfileDst"));
            var dstSettings = _settingsService.ReadSettings("ProfileDst");
            Assert.AreEqual(0.75, dstSettings.AppVolume, 0.001);
        }

        [TestMethod]
        public void DeleteProfile_DeletesFiles()
        {
            // Arrange
            _settingsService.SaveSettings("ProfileDel", new AppSettings());
            string browserPath = _profileService.GetBrowserDataPath("ProfileDel");
            Directory.CreateDirectory(browserPath);
            File.WriteAllText(Path.Combine(browserPath, "cache.txt"), "data");

            // Act
            _profileService.DeleteProfile("ProfileDel");

            // Assert
            Assert.IsFalse(_profileService.SettingsFileExists("ProfileDel"));
            Assert.IsFalse(Directory.Exists(browserPath));
        }
    }
}
