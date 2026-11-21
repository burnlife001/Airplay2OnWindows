using System;
using System.Threading.Tasks;
using AirPlay.Models;
using AirPlay.Models.Enums;
using AirPlay.Services;
using AirPlay.Services.Implementations;
using NAudio.Wave;
using Xunit;

namespace AirPlay.Tests
{
    public class StreamingWaveProviderTests
    {
        private static StreamingWaveProvider CreateProvider(int maxQueueSize = 500)
        {
            return new StreamingWaveProvider(new WaveFormat(44100, 16, 2), maxQueueSize);
        }

        [Fact]
        public void AddSamples_RejectsNullAndEmptyBuffers()
        {
            var provider = CreateProvider();

            Assert.False(provider.AddSamples(null, 0, 1));
            Assert.False(provider.AddSamples(new byte[] { 1 }, 0, 0));
            Assert.Equal(0, provider.QueuedBuffers);
        }

        [Fact]
        public void AddSamples_QueuesCopyAndRespectsLimit()
        {
            var provider = CreateProvider(maxQueueSize: 2);
            var buffer = new byte[] { 1, 2 };

            Assert.True(provider.AddSamples(buffer, 0, buffer.Length));
            buffer[0] = 99;
            Assert.True(provider.AddSamples(new byte[] { 3, 4 }, 0, 2));
            Assert.False(provider.AddSamples(new byte[] { 5, 6 }, 0, 2));

            var output = new byte[2];
            Assert.Equal(2, provider.Read(output, 0, 2));
            Assert.Equal(new byte[] { 1, 2 }, output);
        }

        [Fact]
        public void Read_FillsSilenceWhenEmpty()
        {
            var provider = CreateProvider();
            var output = new byte[] { 9, 9, 9, 9 };

            Assert.Equal(4, provider.Read(output, 0, 4));
            Assert.Equal(new byte[4], output);
        }

        [Fact]
        public void Read_SplitsQueuedBuffersAcrossCalls()
        {
            var provider = CreateProvider();
            provider.AddSamples(new byte[] { 1, 2, 3, 4 }, 0, 4);

            var first = new byte[3];
            Assert.Equal(3, provider.Read(first, 0, 3));
            Assert.Equal(new byte[] { 1, 2, 3 }, first);

            var second = new byte[3];
            Assert.Equal(3, provider.Read(second, 0, 3));
            Assert.Equal(new byte[] { 4, 0, 0 }, second);
        }

        [Fact]
        public void Read_AppliesVolumeTo16BitSamples()
        {
            var provider = CreateProvider();
            provider.Volume = 0.5f;
            provider.AddSamples(new byte[] { 0x00, 0x40, 0x00, 0x40 }, 0, 4);

            var output = new byte[4];
            provider.Read(output, 0, 4);

            Assert.Equal(new byte[] { 0x00, 0x20, 0x00, 0x20 }, output);
        }

        [Fact]
        public void Volume_IsClamped()
        {
            var provider = CreateProvider();

            provider.Volume = 4f;
            Assert.Equal(1f, provider.Volume);

            provider.Volume = -1f;
            Assert.Equal(0f, provider.Volume);
        }

        [Fact]
        public void Clear_EmptiesQueueAndPartialBuffer()
        {
            var provider = CreateProvider();
            provider.AddSamples(new byte[] { 1, 2, 3, 4 }, 0, 4);
            provider.Read(new byte[1], 0, 1);

            provider.Clear();

            Assert.Equal(0, provider.QueuedBuffers);
            Assert.Equal(new byte[4], new byte[4]);
            var output = new byte[2];
            provider.Read(output, 0, 2);
            Assert.Equal(new byte[2], output);
        }
    }

    public class SessionManagerTests
    {
        [Fact]
        public async Task GetSessionAsync_ReturnsEmptySessionForUnknownKey()
        {
            var session = await SessionManager.Current.GetSessionAsync("unknown-" + Guid.NewGuid());

            Assert.NotNull(session);
            Assert.Null(session.EcdhShared);
        }

        [Fact]
        public async Task CreateOrUpdateSessionAsync_StoresAndReplacesSession()
        {
            var key = "session-" + Guid.NewGuid();
            var first = new Session(key) { StreamConnectionId = "a" };

            await SessionManager.Current.CreateOrUpdateSessionAsync(key, first);
            Assert.Same(first, await SessionManager.Current.GetSessionAsync(key));

            var second = new Session(key) { StreamConnectionId = "b" };
            await SessionManager.Current.CreateOrUpdateSessionAsync(key, second);
            Assert.Same(second, await SessionManager.Current.GetSessionAsync(key));
        }

        [Fact]
        public async Task ReleaseMirroringAsync_ClearsSessionAndInvokesCallback()
        {
            var key = "mirroring-" + Guid.NewGuid();
            var session = new Session(key)
            {
                StreamConnectionId = "stream",
                MirroringSession = true,
                SpsPps = new byte[] { 1 }
            };
            await SessionManager.Current.CreateOrUpdateSessionAsync(key, session);

            var callbackCount = 0;
            await SessionManager.Current.ReleaseMirroringAsync(key, () => callbackCount++, "test");

            var released = await SessionManager.Current.GetSessionAsync(key);
            Assert.Null(released.StreamConnectionId);
            Assert.Null(released.MirroringSession);
            Assert.Null(released.SpsPps);
            Assert.Equal(1, callbackCount);
        }

        [Fact]
        public async Task ReleaseMirroringAsync_NoopWhenNothingToRelease()
        {
            var key = "empty-" + Guid.NewGuid();
            await SessionManager.Current.CreateOrUpdateSessionAsync(key, new Session(key));

            var called = false;
            await SessionManager.Current.ReleaseMirroringAsync(key, () => called = true, "test");

            Assert.False(called);
        }

        [Fact]
        public async Task ReleaseAllMirroringAsync_ReleasesSessionsWithStreams()
        {
            var key = "all-" + Guid.NewGuid();
            var session = new Session(key)
            {
                StreamConnectionId = "s",
                MirroringSession = true,
                MirroringListener = new AirPlay.Listeners.MirroringListener(
                    new FakeRtspReceiver(), key, (ushort)TestHelpers.GetFreeTcpPort())
            };
            await SessionManager.Current.CreateOrUpdateSessionAsync(key, session);

            var called = false;
            await SessionManager.Current.ReleaseAllMirroringAsync(() => called = true, "test");

            Assert.True(called);
            Assert.Null((await SessionManager.Current.GetSessionAsync(key)).StreamConnectionId);
        }
    }
}
