using Microsoft.VisualStudio.TestTools.UnitTesting;
using XColumn.Models;
using XColumn.ViewModels;

namespace XColumn.Tests
{
    [TestClass]
    public class ColumnViewModelTests
    {
        [TestMethod]
        public void GoBack_WhenLocked_DoesNotInvokeGoBackRequested()
        {
            // Arrange
            var vm = new ColumnViewModel { IsLocked = true };
            bool invoked = false;
            vm.GoBackRequested += (c) => invoked = true;

            // Act
            vm.GoBackCommand.Execute(null);

            // Assert
            Assert.IsFalse(invoked, "Locked column must not trigger GoBackRequested.");
        }

        [TestMethod]
        public void GoBack_WhenNotLocked_InvokesGoBackRequested()
        {
            // Arrange
            var vm = new ColumnViewModel { IsLocked = false };
            bool invoked = false;
            vm.GoBackRequested += (c) => invoked = true;

            // Act
            vm.GoBackCommand.Execute(null);

            // Assert
            Assert.IsTrue(invoked, "Unlocked column must trigger GoBackRequested.");
        }

        [TestMethod]
        public void ZoomPercentage_ClampRange_WorksCorrectly()
        {
            // Arrange
            var vm = new ColumnViewModel();

            // Act & Assert - Under minimum
            vm.ZoomPercentage = 5;
            Assert.AreEqual(10, vm.ZoomPercentage, "ZoomPercentage must clamp to min 10%");
            Assert.AreEqual(0.1, vm.ZoomFactor, 0.001);

            // Act & Assert - Over maximum
            vm.ZoomPercentage = 600;
            Assert.AreEqual(500, vm.ZoomPercentage, "ZoomPercentage must clamp to max 500%");
            Assert.AreEqual(5.0, vm.ZoomFactor, 0.001);

            // Act & Assert - Normal
            vm.ZoomFactor = 1.25;
            Assert.AreEqual(125, vm.ZoomPercentage);
        }

        [TestMethod]
        public void ToConfig_And_FromConfig_MaintainsDataIntegrity()
        {
            // Arrange
            var original = new ColumnViewModel
            {
                Url = "https://x.com/search?q=test",
                Width = 450,
                RefreshIntervalSeconds = 120,
                IsAutoRefreshEnabled = true,
                IsRetweetHidden = true,
                IsReplyHidden = true,
                IsLocked = true,
                ZoomFactor = 1.2,
                MediaScalePercentage = 80,
                ProfileName = "testProfile",
                IsExternalSite = false,
                IsListAutoNav = false,
                LastValidUrl = "https://x.com/search?q=test"
            };

            // Act
            var config = original.ToConfig();
            var restored = ColumnViewModel.FromConfig(config);

            // Assert
            Assert.AreEqual(original.Url, restored.Url);
            Assert.AreEqual(original.Width, restored.Width);
            Assert.AreEqual(original.RefreshIntervalSeconds, restored.RefreshIntervalSeconds);
            Assert.AreEqual(original.IsAutoRefreshEnabled, restored.IsAutoRefreshEnabled);
            Assert.AreEqual(original.IsRetweetHidden, restored.IsRetweetHidden);
            Assert.AreEqual(original.IsReplyHidden, restored.IsReplyHidden);
            Assert.AreEqual(original.IsLocked, restored.IsLocked);
            Assert.AreEqual(original.ZoomFactor, restored.ZoomFactor, 0.001);
            Assert.AreEqual(original.MediaScalePercentage, restored.MediaScalePercentage);
            Assert.AreEqual(original.ProfileName, restored.ProfileName);
        }

        [TestMethod]
        public void SuspendCommand_TogglesSuspendedState()
        {
            // Arrange
            var vm = new ColumnViewModel { IsSuspended = false };
            bool suspendRequested = false;
            bool targetState = false;
            vm.SuspendRequested += (c, isSuspended) =>
            {
                suspendRequested = true;
                targetState = isSuspended;
            };

            // Act - Suspend
            vm.SuspendCommand.Execute(null);

            // Assert
            Assert.IsTrue(vm.IsSuspended);
            Assert.IsTrue(suspendRequested);
            Assert.IsTrue(targetState);

            // Act - Resume
            suspendRequested = false;
            vm.SuspendCommand.Execute(null);

            // Assert
            Assert.IsFalse(vm.IsSuspended);
            Assert.IsTrue(suspendRequested);
            Assert.IsFalse(targetState);
        }
    }
}
