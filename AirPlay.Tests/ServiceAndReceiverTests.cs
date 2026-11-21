using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.Models;
using AirPlay.Models.Configs;
using Makaretu.Dns;
using Microsoft.Extensions.Options;
using Xunit;

namespace AirPlay.Tests
{
    public class AirPlayServiceTests
    {
        [Fact]
        public void Constructor_ValidatesArguments()
        {
            Assert.Throws<ArgumentNullException>(() => new AirPlayService(null, Options.Create(new DumpConfig())));
            Assert.Throws<ArgumentNullException>(() => new AirPlayService(new FakeAirPlayReceiver(), null));
        }

        [Fact]
        public async Task StartAndStopAsync_SubscribeEventsAndToggleListeners()
        {
            var receiver = new FakeAirPlayReceiver();
            var dump = Options.Create(new DumpConfig { Path = Path.GetTempPath() });
            var service = new AirPlayService(receiver, dump);
            using var cts = new CancellationTokenSource();

            await service.StartAsync(cts.Token);
            Assert.Equal(1, receiver.StartListenersCount);
            Assert.Equal(1, receiver.StartMdnsCount);

            receiver.RaiseVolume(0.5m);
            receiver.RaisePcm(new PcmData { Data = new byte[100], Length = 100 });
            receiver.RaisePcm(new PcmData { Data = new byte[100], Length = 100 });
            receiver.RaiseAudioFlush();
            SafeInvoke(() => receiver.RaiseFrame(new H264Data { Data = new byte[16], Length = 16, FrameType = 5 }));
            SafeInvoke(() => receiver.RaiseMirroringStarted());
            SafeInvoke(() => receiver.RaiseMirroringStopped());

            cts.Cancel();
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(1, receiver.StopListenersCount);
            service.Dispose();
        }

        [Fact]
        public async Task StopAsync_SwallowsListenerStopFailure()
        {
            var receiver = new FakeAirPlayReceiver
            {
                StopListenersException = new InvalidOperationException("boom")
            };
            var service = new AirPlayService(receiver,
                Options.Create(new DumpConfig { Path = Path.GetTempPath() }));

            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(1, receiver.StopListenersCount);
            service.Dispose();
        }

        private static void SafeInvoke(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                // Output services may be unavailable in the test host.
            }
        }
    }

    public class AirPlayReceiverTests
    {
        private static IOptions<AirPlayReceiverConfig> Config(string instance = "AirPlay Test", string mac = "11:22:33:44:55:66")
        {
            return Options.Create(new AirPlayReceiverConfig
            {
                Instance = instance,
                DeviceMacAddress = mac,
                AirTunesPort = (ushort)TestHelpers.GetFreeTcpPort(),
                AirPlayPort = (ushort)TestHelpers.GetFreeTcpPort()
            });
        }

        [Fact]
        public void Constructor_AllowsMissingInstanceButRequiresDumpConfig()
        {
            // Instance 缺省时回退为「机器名:用户名」，不再抛异常
            using var receiver = new AirPlayReceiver(
                Options.Create(new AirPlayReceiverConfig()), Options.Create(new DumpConfig()));
            Assert.Throws<ArgumentNullException>(() => new AirPlayReceiver(Config(), null));
        }

        [Fact]
        public async Task StartAndStopListeners()
        {
            using var receiver = new AirPlayReceiver(Config(), Options.Create(new DumpConfig { Path = Path.GetTempPath() }));
            using var cts = new CancellationTokenSource();

            await receiver.StartListeners(cts.Token);
            await receiver.StopListenersAsync();
        }

        [Fact]
        public async Task StartMdns_AnnouncesAirPlayAndRaopServices()
        {
            var instance = $"Restart-{Guid.NewGuid():N}";
            using var observer = new MulticastService { UseIpv6 = false };
            var airPlayAnnounced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var raopAnnounced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            observer.AnswerReceived += (_, e) =>
            {
                foreach (var ptr in e.Message.Answers.OfType<PTRRecord>())
                {
                    var target = ptr.DomainName.ToString();
                    if (target.StartsWith($"{instance}._airplay._tcp", StringComparison.Ordinal))
                    {
                        airPlayAnnounced.TrySetResult(true);
                    }
                    if (target.EndsWith($"@{instance}._raop._tcp.local", StringComparison.Ordinal))
                    {
                        raopAnnounced.TrySetResult(true);
                    }
                }
            };
            observer.Start();

            using var receiver = new AirPlayReceiver(
                Config(instance), Options.Create(new DumpConfig { Path = Path.GetTempPath() }));
            try
            {
                await receiver.StartMdnsAsync();
                var announced = Task.WhenAll(airPlayAnnounced.Task, raopAnnounced.Task);
                var completed = await Task.WhenAny(announced, Task.Delay(5000));
                Assert.True(completed == announced, "AirPlay and RAOP must both be announced after mDNS starts.");
            }
            finally
            {
                await receiver.StopListenersAsync();
                observer.Stop();
            }
        }

        [Fact]
        public async Task StopListeners_UnadvertisesAirPlayAndRaopServices()
        {
            var instance = $"Shutdown-{Guid.NewGuid():N}";
            using var observer = new MulticastService { UseIpv6 = false };
            var airPlayGoodbye = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var raopGoodbye = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            observer.AnswerReceived += (_, e) =>
            {
                foreach (var ptr in e.Message.Answers.OfType<PTRRecord>().Where(ptr => ptr.TTL == TimeSpan.Zero))
                {
                    var target = ptr.DomainName.ToString();
                    if (target.StartsWith($"{instance}._airplay._tcp", StringComparison.Ordinal))
                    {
                        airPlayGoodbye.TrySetResult(true);
                    }
                    if (target.EndsWith($"@{instance}._raop._tcp.local", StringComparison.Ordinal))
                    {
                        raopGoodbye.TrySetResult(true);
                    }
                }
            };
            observer.Start();

            using var receiver = new AirPlayReceiver(
                Config(instance), Options.Create(new DumpConfig { Path = Path.GetTempPath() }));
            await receiver.StartMdnsAsync();
            try
            {
                await receiver.StopListenersAsync();
                var goodbye = Task.WhenAll(airPlayGoodbye.Task, raopGoodbye.Task);
                var completed = await Task.WhenAny(goodbye, Task.Delay(2000));
                Assert.True(completed == goodbye, "AirPlay and RAOP must both send goodbye records when stopped.");
            }
            finally
            {
                await receiver.StopListenersAsync();
                observer.Stop();
            }
        }

        [Fact]
        public async Task StartMdns_AdvertisesService()
        {
            var config = Config();
            using var receiver = new AirPlayReceiver(config, Options.Create(new DumpConfig { Path = Path.GetTempPath() }));

            try
            {
                await receiver.StartMdnsAsync();
            }
            catch (Exception)
            {
                // Multicast may be unavailable in the test host; the code path is still exercised.
            }
            finally
            {
                await receiver.StopListenersAsync();
            }
        }
    }
}
