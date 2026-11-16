using Microsoft.VisualStudio.TestTools.UnitTesting;
using XColumn.Services;

namespace XColumn.Tests
{
    [TestClass]
    public class AudioServiceTests
    {
        [TestMethod]
        public void SetVolume_DoesNotThrow_WithVariousInputs()
        {
            // Arrange
            var audioService = new AudioService();

            // Act & Assert - 負の数、超過値、空PIDリストでも安全に処理されること
            audioService.SetVolume(-0.5, Array.Empty<int>());
            audioService.SetVolume(1.5, Array.Empty<int>());
            audioService.SetVolume(0.5, new[] { Environment.ProcessId });

            Assert.IsNotNull(audioService);
        }
    }
}
