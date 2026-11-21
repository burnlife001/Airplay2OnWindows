using System;
using AirPlay.Utils;
using Xunit;

namespace AirPlay.Tests
{
    public class SmokeTests
    {
        [Fact]
        public void CopyOfRange_ReturnsExpectedSlice()
        {
            var result = Utilities.CopyOfRange(new byte[] { 1, 2, 3, 4 }, 1, 3);
            Assert.Equal(new byte[] { 2, 3 }, result);
        }
    }
}
