using Microsoft.VisualStudio.TestTools.UnitTesting;
using XColumn.Models;
using XColumn.Services;

namespace XColumn.Tests
{
    [TestClass]
    public class RateLimitServiceTests
    {
        private class DummyRateLimitTarget : IRateLimitTarget
        {
            public string Url { get; set; } = "https://x.com/home";
            public string? ProfileName { get; set; } = "default";
            public bool IsExternalSite { get; set; } = false;
            public bool IsAutoRefreshEnabled { get; set; } = true;
            public int RefreshIntervalSeconds { get; set; } = 300;
            public RateLimitStatus RateLimitStatus { get; set; } = RateLimitStatus.Normal;
            public int RateLimitRemaining { get; set; } = 100;
            public DateTimeOffset RateLimitResetTime { get; set; } = DateTimeOffset.Now.AddMinutes(15);
            public bool IsSuspended { get; set; } = false;

            public bool UpdatedRateLimitCalled { get; private set; }
            public bool OnRateLimitTriggeredCalled { get; private set; }

            public void UpdateRateLimit(int remaining, DateTimeOffset resetTime, bool ignoreProtection)
            {
                UpdatedRateLimitCalled = true;
                RateLimitRemaining = remaining;
                RateLimitResetTime = resetTime;

                if (ignoreProtection) return;

                if (remaining < 5)
                    RateLimitStatus = RateLimitStatus.Stopped;
                else if (remaining < 10)
                    RateLimitStatus = RateLimitStatus.Caution;
                else
                    RateLimitStatus = RateLimitStatus.Normal;
            }

            public void OnRateLimitTriggered(DateTimeOffset? resetTime, bool hasResetHeader)
            {
                OnRateLimitTriggeredCalled = true;
                RateLimitStatus = RateLimitStatus.Stopped;
            }
        }

        [TestMethod]
        public void ClassifyOperation_ReturnsCorrectOperationName()
        {
            // Arrange
            var service = new RateLimitService();

            // Act & Assert
            Assert.AreEqual("HomeTimeline", service.ClassifyOperation("https://x.com/home"));
            Assert.AreEqual("SearchTimeline", service.ClassifyOperation("https://x.com/search?q=test"));
            Assert.AreEqual("ListLatestTweetsTimeline", service.ClassifyOperation("https://x.com/i/lists/123456"));
            Assert.AreEqual("NotificationsTimeline", service.ClassifyOperation("https://x.com/notifications"));
            Assert.AreEqual("UserTweets", service.ClassifyOperation("https://x.com/jack"));
            Assert.AreEqual("Other", service.ClassifyOperation("https://example.com"));
        }

        [TestMethod]
        public void IsPrimaryTimelineOperation_MatchesCorrectly()
        {
            // Arrange
            var service = new RateLimitService();

            // Act & Assert
            Assert.IsTrue(service.IsPrimaryTimelineOperation("https://x.com/home", "HomeTimeline"));
            Assert.IsTrue(service.IsPrimaryTimelineOperation("https://x.com/home", "HomeLatestTimeline"));
            Assert.IsFalse(service.IsPrimaryTimelineOperation("https://x.com/home", "BadgeCount"));
            Assert.IsTrue(service.IsPrimaryTimelineOperation("https://x.com/notifications", "NotificationsTimeline"));
        }

        [TestMethod]
        public void BuildRateLimitEstimate_CalculatesSafeRemainingTime()
        {
            // Arrange
            var service = new RateLimitService();
            var targets = new List<IRateLimitTarget>
            {
                new DummyRateLimitTarget
                {
                    Url = "https://x.com/home",
                    RateLimitRemaining = 50,
                    RefreshIntervalSeconds = 60,
                    IsAutoRefreshEnabled = true
                }
            };

            // Act
            string estimate = service.BuildRateLimitEstimate(targets, "default");

            // Assert
            Assert.IsFalse(string.IsNullOrEmpty(estimate));
            StringAssert.Contains(estimate, "HomeTimeline");
        }
    }
}
