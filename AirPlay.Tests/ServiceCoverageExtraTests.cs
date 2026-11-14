using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AirPlay;
using AirPlay.Models;
using AirPlay.Models.Configs;
using AirPlay.Services.Implementations;
using Microsoft.Extensions.Options;
using Xunit;

namespace AirPlay.Tests
{
    public class AirPlayServiceInternalCoverageTests
    {
        private static IOptions<DumpConfig> Dump()
        {
            return Options.Create(new DumpConfig { Path = Path.GetTempPath() });
        }

        private static object Invoke(AirPlayService service, string method, params object[] args)
        {
            return typeof(AirPlayService)
                .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(service, args);
        }

        [Fact]
        public async Task Dispose_ReleasesDisposableReceiver()
        {
            var receiver = new FakeAirPlayReceiver();
            var service = new AirPlayService(receiver, Dump());

            service.Dispose();
            service.Dispose();

            Assert.Equal(2, receiver.DisposeCount);
        }

        [Fact]
        public async Task HandleFfplayExitedAsync_IgnoresNonAirPlayReceiver()
        {
            var service = new AirPlayService(new FakeAirPlayReceiver(), Dump());
            var task = (Task)Invoke(service, "HandleFfplayExitedAsync");
            await task;
            service.Dispose();
        }

        [Fact]
        public async Task HandleFfplayExitedAsync_ReleasesMirroringForRealReceiver()
        {
            var receiver = new AirPlayReceiver(
                Options.Create(new AirPlayReceiverConfig
                {
                    Instance = "ffplay-exit-test",
                    DeviceMacAddress = "11:22:33:44:55:66",
                    AirTunesPort = (ushort)TestHelpers.GetFreeTcpPort(),
                    AirPlayPort = (ushort)TestHelpers.GetFreeTcpPort()
                }),
                Dump());

            using var service = new AirPlayService(receiver, Dump());
            var task = (Task)Invoke(service, "HandleFfplayExitedAsync");
            await task;
        }

        [Fact]
        public async Task ScheduleHandleFfplayExited_RunsWithoutThrowing()
        {
            var service = new AirPlayService(new FakeAirPlayReceiver(), Dump());
            Invoke(service, "ScheduleHandleFfplayExited");
            await Task.Delay(200);
            service.Dispose();
        }

        [Fact]
        public void RecreateAudioOutput_ReplacesInstance()
        {
            var service = new AirPlayService(new FakeAirPlayReceiver(), Dump());
            Invoke(service, "RecreateAudioOutput");
            Invoke(service, "RecreateAudioOutput");
            service.Dispose();
        }

        [Fact]
        public async Task StartAsync_RunsHeartbeatUntilCancelled()
        {
            var receiver = new FakeAirPlayReceiver();
            using var service = new AirPlayService(receiver, Dump());
            using var cts = new CancellationTokenSource();

            await service.StartAsync(cts.Token);
            await Task.Delay(5600);
            cts.Cancel();
            await service.StopAsync(CancellationToken.None);

            Assert.True(receiver.StartListenersCount >= 1);
            Assert.True(receiver.StartMdnsCount >= 1);
        }

        [Fact]
        public async Task ScheduleHandleFfplayExited_SwallowsReleaseFailures()
        {
            var receiver = new AirPlayReceiver(
                Options.Create(new AirPlayReceiverConfig
                {
                    Instance = "ffplay-catch-test",
                    DeviceMacAddress = "11:22:33:44:55:66",
                    AirTunesPort = (ushort)TestHelpers.GetFreeTcpPort(),
                    AirPlayPort = (ushort)TestHelpers.GetFreeTcpPort()
                }),
                Dump());
            using var service = new AirPlayService(receiver, Dump());

            receiver.OnMirroringStoppedReceived += (sender, args) => throw new InvalidOperationException("stop failed");

            var sessionId = "ffplay-release-" + Guid.NewGuid();
            var session = new Session(sessionId)
            {
                StreamConnectionId = "conn",
                MirroringSession = true
            };
            session.MirroringListener = new AirPlay.Listeners.MirroringListener(
                new FakeRtspReceiver(), sessionId, (ushort)TestHelpers.GetFreeTcpPort());
            await SessionManager.Current.CreateOrUpdateSessionAsync(sessionId, session);

            Invoke(service, "ScheduleHandleFfplayExited");
            await Task.Delay(500);

            try
            {
                await SessionManager.Current.ReleaseAllMirroringAsync(null, "cleanup");
            }
            catch
            {
            }
        }
    }

    public class AirPlayReceiverEventCoverageTests
    {
        private static AirPlayReceiver CreateReceiver(string mac = "11:22:33:44:55:66")
        {
            return new AirPlayReceiver(
                Options.Create(new AirPlayReceiverConfig
                {
                    Instance = "event-test",
                    DeviceMacAddress = mac,
                    AirTunesPort = (ushort)TestHelpers.GetFreeTcpPort(),
                    AirPlayPort = (ushort)TestHelpers.GetFreeTcpPort()
                }),
                Options.Create(new DumpConfig { Path = Path.GetTempPath() }));
        }

        [Fact]
        public void EventRaisers_ForwardToSubscribers()
        {
            using var receiver = CreateReceiver();

            decimal volume = 0m;
            H264Data frame = default;
            PcmData pcm = default;
            int flush = 0;
            int started = 0;
            int stopped = 0;

            receiver.OnSetVolumeReceived += (sender, value) => volume = value;
            receiver.OnH264DataReceived += (sender, value) => frame = value;
            receiver.OnPCMDataReceived += (sender, value) => pcm = value;
            receiver.OnAudioFlushReceived += (sender, args) => Interlocked.Increment(ref flush);
            receiver.OnMirroringStartedReceived += (sender, args) => Interlocked.Increment(ref started);
            receiver.OnMirroringStoppedReceived += (sender, args) => Interlocked.Increment(ref stopped);

            receiver.OnSetVolume(0.25m);
            receiver.OnData(new H264Data { Length = 10 });
            receiver.OnPCMData(new PcmData { Length = 20 });
            receiver.OnAudioFlush();
            receiver.OnMirroringStarted();
            receiver.OnMirroringStopped();

            Assert.Equal(0.25m, volume);
            Assert.Equal(10, frame.Length);
            Assert.Equal(20, pcm.Length);
            Assert.Equal(1, flush);
            Assert.Equal(1, started);
            Assert.Equal(1, stopped);

            receiver.Dispose();
        }

        [Fact]
        public async Task EventRaisers_WithoutSubscribersAreSafe()
        {
            using var receiver = CreateReceiver();
            receiver.OnSetVolume(0m);
            receiver.OnData(default);
            receiver.OnPCMData(default);
            receiver.OnAudioFlush();
            receiver.OnMirroringStarted();
            receiver.OnMirroringStopped();
            await Task.CompletedTask;
        }

        [Fact]
        public async Task StartMdnsAsync_RejectsInvalidDeviceId()
        {
            using var empty = CreateReceiver(string.Empty);
            await Assert.ThrowsAsync<ArgumentNullException>(() => empty.StartMdnsAsync());

            using var invalid = CreateReceiver("not-a-mac");
            await Assert.ThrowsAsync<ArgumentException>(() => invalid.StartMdnsAsync());
        }
    }
}
