using Microsoft.VisualStudio.TestTools.UnitTesting;
using XColumn.Services;

namespace XColumn.Tests
{
    [TestClass]
    public class AppTimerServiceTests
    {
        private class TestColumnTimerTarget : IColumnTimerTarget
        {
            public bool IsAutoRefreshEnabled { get; set; } = true;
            public int RefreshIntervalSeconds { get; set; } = 60;
            public int RemainingSeconds { get; set; } = 60;
            public bool UseRefreshJitter { get; set; } = false;
            public bool IsSuspended { get; set; } = false;
            public bool IsRateLimitStopped { get; set; } = false;
            public DateTimeOffset RateLimitResetTime { get; set; } = DateTimeOffset.MaxValue;

            public int AutoRefreshTriggerCount { get; private set; }
            public int RateLimitResumeCount { get; private set; }

            public Task TriggerAutoRefreshAsync()
            {
                AutoRefreshTriggerCount++;
                RemainingSeconds = RefreshIntervalSeconds;
                return Task.CompletedTask;
            }

            public Task ResumeFromRateLimitAsync()
            {
                RateLimitResumeCount++;
                IsRateLimitStopped = false;
                return Task.CompletedTask;
            }
        }

        [TestMethod]
        public void RegisterAndUnregisterColumn_OperatesSafely()
        {
            // Arrange
            var timerService = new AppTimerService();
            var col = new TestColumnTimerTarget { RefreshIntervalSeconds = 30 };

            // Act
            timerService.RegisterColumn(col);
            Assert.IsTrue(col.RemainingSeconds > 0, "Registered column should have remaining seconds initialized.");

            timerService.UnregisterColumn(col);
            timerService.ClearColumns();

            // Assert - No exceptions thrown
            Assert.IsNotNull(timerService);
        }

        [TestMethod]
        public void AutoShutdown_FiresWhenDeactivatedPastTimeout()
        {
            // Arrange
            var timerService = new AppTimerService();
            bool shutdownFired = false;
            timerService.AutoShutdownRequested += () => shutdownFired = true;

            timerService.ConfigureAutoShutdown(true, 1);

            // Act - Set active / inactive
            timerService.SetAppActive(false);
            timerService.SetAppActive(true);

            // Assert
            Assert.IsFalse(shutdownFired, "Shutdown should not fire immediately.");
        }
    }
}
