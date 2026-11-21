using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Plists;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.Listeners;
using AirPlay.Models;
using AirPlay.Models.Configs;
using AirPlay.Models.Enums;
using AirPlay.Services.Implementations;
using Xunit;

namespace AirPlay.Tests
{
    public class AirTunesListenerTests
    {
        private static byte[] ToPlist(IDictionary<string, object> dict)
        {
            using var stream = new MemoryStream();
            new BinaryPlistWriter().WriteObject(stream, (System.Collections.IDictionary)dict, false);
            return stream.ToArray();
        }

        private static async Task<(AirTunesListener Listener, FakeRtspReceiver Receiver, string SessionId)>
            CreateListenerAsync(ushort? airPlayPort = null)
        {
            var receiver = new FakeRtspReceiver();
            var sessionId = "s-" + Guid.NewGuid();
            var listener = new AirTunesListener(
                receiver,
                (ushort)TestHelpers.GetFreeTcpPort(),
                airPlayPort ?? (ushort)TestHelpers.GetFreeTcpPort(),
                new DumpConfig { Path = Path.GetTempPath() });

            // Ensure the listener object is usable without binding sockets.
            await Task.CompletedTask;
            return (listener, receiver, sessionId);
        }

        private static Request Build(string method, string path, byte[] body = null,
            params (string Name, string Value)[] extraHeaders)
        {
            var headers = new List<(string, string)> { ("Active-Remote", extraHeaders.FirstOrDefault(h => h.Name == "Active-Remote").Value ?? "default") };
            headers.AddRange(extraHeaders.Where(h => h.Name != "Active-Remote"));

            if (body != null)
            {
                headers.Add(("Content-Length", body.Length.ToString()));
            }

            var hex = body == null
                ? TestHelpers.BuildRequest(method, path, "RTSP/1.0", headers.ToArray())
                : TestHelpers.BuildRequestWithBody(method, path, "RTSP/1.0", body, headers.ToArray());

            return new Request(hex);
        }

        private static async Task<(Request Request, Response Response)> Send(AirTunesListener listener,
            string method, string path, string sessionId, byte[] body = null,
            params (string Name, string Value)[] extraHeaders)
        {
            var headers = new List<(string, string)> { ("Active-Remote", sessionId) };
            headers.AddRange(extraHeaders);

            if (body != null)
            {
                headers.Add(("Content-Length", body.Length.ToString()));
            }

            var hex = body == null
                ? TestHelpers.BuildRequest(method, path, "RTSP/1.0", headers.ToArray())
                : TestHelpers.BuildRequestWithBody(method, path, "RTSP/1.0", body, headers.ToArray());

            var request = new Request(hex);
            var response = request.GetBaseResponse();

            await listener.OnDataReceivedAsync(request, response, CancellationToken.None);
            return (request, response);
        }

        private static async Task<Session> CreatePairedSessionAsync(string sessionId,
            bool fairPlayReady = false, bool screenMirroring = false)
        {
            var session = new Session(sessionId)
            {
                EcdhShared = new byte[32],
                PairVerified = true,
                KeyMsg = new byte[164]
            };

            if (fairPlayReady)
            {
                session.AesKey = new byte[164];
                session.AesIv = new byte[16];
            }

            if (screenMirroring)
            {
                session.MirroringSession = true;
                session.StreamConnectionId = "12345";
            }

            await SessionManager.Current.CreateOrUpdateSessionAsync(sessionId, session);
            return session;
        }

        [Fact]
        public async Task GetInfo_ReturnsPlistWithDeviceInfo()
        {
            var (listener, _, sessionId) = await CreateListenerAsync();

            var (_, response) = await Send(listener, "GET", "/info", sessionId);

            var body = await response.ReadAsync();
            using var stream = new MemoryStream(body);
            var plist = new BinaryPlistReader().ReadObject(stream);

            Assert.Equal("airserver", plist["name"]);
            Assert.Equal("AppleTV5,3", plist["model"]);
            Assert.Equal(StatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task PairSetup_ReturnsPublicKey()
        {
            var (listener, _, sessionId) = await CreateListenerAsync();

            var (_, response) = await Send(listener, "POST", "/pair-setup", sessionId, new byte[] { 1, 2, 3 });

            Assert.Equal(32, (await response.ReadAsync()).Length);
        }

        [Fact]
        public async Task PairVerify_WithKeyExchangeThenSignature()
        {
            var (listener, _, sessionId) = await CreateListenerAsync();

            var body = new byte[68];
            body[0] = 1;
            Enumerable.Repeat((byte)9, 32).ToArray().CopyTo(body, 4);
            Enumerable.Repeat((byte)7, 32).ToArray().CopyTo(body, 36);

            var (_, first) = await Send(listener, "POST", "/pair-verify", sessionId, body);
            Assert.Equal(96, (await first.ReadAsync()).Length);

            var second = new byte[68];
            Array.Copy(body, second, 4);
            second[0] = 0;
            var (_, secondResponse) = await Send(listener, "POST", "/pair-verify", sessionId, second);
            Assert.Equal(StatusCode.OK, secondResponse.StatusCode);
        }

        [Fact]
        public async Task FpSetup_UnauthorizedWhenSessionNotPaired()
        {
            var (listener, _, sessionId) = await CreateListenerAsync();

            var (_, response) = await Send(listener, "POST", "/fp-setup", sessionId, new byte[16]);

            Assert.Equal(StatusCode.UNAUTHORIZED, response.StatusCode);
            Assert.Empty(await response.ReadAsync());
        }

        [Fact]
        public async Task FpSetup_ReturnsRepliesForAllModes()
        {
            var (listener, _, sessionId) = await CreateListenerAsync();
            await CreatePairedSessionAsync(sessionId);

            for (byte mode = 0; mode <= 3; mode++)
            {
                var body = new byte[16];
                body[4] = 0x03;
                body[14] = mode;

                var (_, response) = await Send(listener, "POST", "/fp-setup", sessionId, body);
                Assert.NotEmpty(await response.ReadAsync());
            }
        }

        [Fact]
        public async Task FpSetup_KeyMessageAndUnsupportedVersionBranches()
        {
            var (listener, _, sessionId) = await CreateListenerAsync();
            var session = await CreatePairedSessionAsync(sessionId);

            var keyMessage = new byte[164];
            keyMessage[4] = 0x03;
            var (_, response) = await Send(listener, "POST", "/fp-setup", sessionId, keyMessage);
            Assert.Equal(32, (await response.ReadAsync()).Length);
            Assert.Equal(164, session.KeyMsg.Length);

            var unsupported = new byte[164];
            unsupported[4] = 0x02;
            var (_, unsupportedResponse) = await Send(listener, "POST", "/fp-setup", sessionId, unsupported);
            Assert.Empty(await unsupportedResponse.ReadAsync());

            var unsupportedSmall = new byte[16];
            unsupportedSmall[4] = 0x02;
            var (_, unsupportedSmallResponse) = await Send(listener, "POST", "/fp-setup", sessionId, unsupportedSmall);
            Assert.Empty(await unsupportedSmallResponse.ReadAsync());
        }

        [Fact]
        public async Task Setup_RejectedWhenFairPlayNotComplete()
        {
            var (listener, _, sessionId) = await CreateListenerAsync();

            var (_, response) = await Send(listener, "SETUP", "/stream", sessionId,
                ToPlist(new Dictionary<string, object> { { "streams", new object[0] } }));

            Assert.Equal(StatusCode.BADREQUEST, response.StatusCode);
        }

        [Fact]
        public async Task Setup_MirroringStreamCreatesListenerAndResponds()
        {
            var airPlayPort = (ushort)TestHelpers.GetFreeTcpPort();
            var (listener, receiver, sessionId) = await CreateListenerAsync(airPlayPort);
            var session = await CreatePairedSessionAsync(sessionId, fairPlayReady: true, screenMirroring: true);

            var body = ToPlist(new Dictionary<string, object>
            {
                { "streams", new object[] { new Dictionary<string, object>
                    {
                        { "type", 110 },
                        { "streamConnectionID", 8589934592L }
                    }
                } }
            });

            var (_, response) = await Send(listener, "SETUP", "/stream", sessionId, body);
            var output = await response.ReadAsync();

            Assert.NotEmpty(output);
            Assert.Equal("8589934592", session.StreamConnectionId);
            Assert.NotNull(session.MirroringListener);
            Assert.Equal(1, receiver.MirroringStartedCount);

            await session.MirroringListener.StopAsync();
        }

        [Fact]
        public async Task Setup_AudioStreamParsesAudioParameters()
        {
            var (listener, _, sessionId) = await CreateListenerAsync();
            var session = await CreatePairedSessionAsync(sessionId);

            var body = ToPlist(new Dictionary<string, object>
            {
                { "streams", new object[] { new Dictionary<string, object>
                    {
                        { "type", 96 },
                        { "audioFormat", 0x40000 },
                        { "ct", 1 },
                        { "spf", 352 },
                        { "controlPort", 7002 }
                    }
                } }
            });

            var (_, response) = await Send(listener, "SETUP", "/stream", sessionId, body);

            Assert.NotEmpty(await response.ReadAsync());
            Assert.Equal(AudioFormat.ALAC, session.AudioFormat);
            Assert.Equal(1, session.AudioCompressionType);
            Assert.Equal(352, session.AudioSamplesPerFrame);
        }

        [Fact]
        public async Task Setup_AudioFormatDescriptionsCoverAllCases()
        {
            var (listener, _, sessionId) = await CreateListenerAsync();
            await CreatePairedSessionAsync(sessionId);

            foreach (var format in new[] { 0x0, 0x40000, 0x400000, 0x1000000, 0x1234 })
            {
                var body = ToPlist(new Dictionary<string, object>
                {
                    { "streams", new object[] { new Dictionary<string, object>
                        {
                            { "type", 96 },
                            { "audioFormat", format }
                        }
                    } }
                });

                var (_, response) = await Send(listener, "SETUP", "/stream", sessionId, body);
                Assert.NotEmpty(await response.ReadAsync());
            }
        }

        [Fact]
        public async Task InitialSetup_ParsesKeysAndStartsStreamingListener()
        {
            var airPlayPort = (ushort)TestHelpers.GetFreeTcpPort();
            var (listener, _, sessionId) = await CreateListenerAsync(airPlayPort);
            var session = await CreatePairedSessionAsync(sessionId);

            var body = ToPlist(new Dictionary<string, object>
            {
                { "et", 0 },
                { "ekey", new byte[164] },
                { "eiv", new byte[16] },
                { "isScreenMirroringSession", false },
                { "timingPort", 0 }
            });

            var (_, response) = await Send(listener, "SETUP", "/stream", sessionId, body);

            Assert.NotEmpty(await response.ReadAsync());
            Assert.NotNull(session.AesKey);
            Assert.NotNull(session.AesIv);
            Assert.False(session.MirroringSession);
            Assert.NotNull(session.StreamingListener);

            await session.StreamingListener.StopAsync();
        }

        [Fact]
        public async Task InitialSetup_StartsMirroringListenerForScreenSession()
        {
            var airPlayPort = (ushort)TestHelpers.GetFreeTcpPort();
            var (listener, receiver, sessionId) = await CreateListenerAsync(airPlayPort);
            var session = await CreatePairedSessionAsync(sessionId);
            session.MirroringSession = true;
            session.StreamConnectionId = "conn";

            var body = ToPlist(new Dictionary<string, object>
            {
                { "ekey", new byte[164] },
                { "eiv", new byte[16] },
                { "isScreenMirroringSession", true }
            });

            var (_, response) = await Send(listener, "SETUP", "/stream", sessionId, body);

            Assert.NotEmpty(await response.ReadAsync());
            Assert.NotNull(session.MirroringListener);
            Assert.Equal(1, receiver.MirroringStartedCount);

            await session.MirroringListener.StopAsync();
        }

        [Fact]
        public async Task GetParameter_ReturnsVolume()
        {
            var (listener, _, sessionId) = await CreateListenerAsync();

            var (_, response) = await Send(listener, "GET_PARAMETER", "/stream", sessionId,
                Encoding.ASCII.GetBytes("volume\r\n"));

            Assert.Equal("volume: 1.000000\r\n", Encoding.ASCII.GetString(await response.ReadAsync()));
        }

        [Fact]
        public async Task Record_AddsLatencyHeader()
        {
            var (listener, _, sessionId) = await CreateListenerAsync();

            var (_, response) = await Send(listener, "RECORD", "/stream", sessionId);

            Assert.Equal("0", response.Headers["Audio-Latency"]);
        }

        [Fact]
        public async Task SetParameter_VolumeProgressImageAndDmap()
        {
            var (listener, receiver, sessionId) = await CreateListenerAsync();

            await Send(listener, "SET_PARAMETER", "/stream", sessionId,
                Encoding.ASCII.GetBytes("volume: 0.25\r\n"), ("Content-Type", "text/parameters"));
            Assert.Equal(0.25m, receiver.Volumes.Single());

            await Send(listener, "SET_PARAMETER", "/stream", sessionId,
                Encoding.ASCII.GetBytes("progress: 1/2/3"), ("Content-Type", "text/parameters"));
            await Send(listener, "SET_PARAMETER", "/stream", sessionId,
                Encoding.ASCII.GetBytes("invalid"), ("Content-Type", "text/parameters"));
            await Send(listener, "SET_PARAMETER", "/stream", sessionId,
                new byte[] { 0xFF, 0xD8 }, ("Content-Type", "image/jpeg"));
            await Send(listener, "SET_PARAMETER", "/stream", sessionId,
                DmapBuffer("minm", Encoding.ASCII.GetBytes("Song")), ("Content-Type", "application/x-dmap-tagged"));
        }

        [Fact]
        public async Task OptionsAnnounceSetPeersAndFeedbackAreAcknowledged()
        {
            var (listener, _, sessionId) = await CreateListenerAsync();

            var (_, options) = await Send(listener, "OPTIONS", "*", sessionId);
            Assert.Contains("SETUP", options.Headers["Public"]);

            await Send(listener, "ANNOUNCE", "/stream", sessionId);
            await Send(listener, "SETPEERS", "/stream", sessionId);
            var (_, feedback) = await Send(listener, "GET", "/feedback", sessionId);
            Assert.Equal(StatusCode.OK, feedback.StatusCode);
        }

        [Fact]
        public async Task Flush_ParsesSequenceAndCallsAudioListener()
        {
            var (listener, receiver, sessionId) = await CreateListenerAsync();
            var session = await CreatePairedSessionAsync(sessionId);
            session.AudioControlListener = new AudioListener(
                receiver, sessionId,
                (ushort)TestHelpers.GetFreeUdpPort(), (ushort)TestHelpers.GetFreeUdpPort(),
                new DumpConfig(), false);
            await SessionManager.Current.CreateOrUpdateSessionAsync(sessionId, session);

            await Send(listener, "FLUSH", "/stream", sessionId, null, ("RTP-Info", "seq=123;rtptime=1"));
            await Send(listener, "FLUSH", "/stream", sessionId, null, ("RTP-Info", "rtptime=1"));
            await Send(listener, "FLUSH", "/stream", sessionId, null, ("RTP-Info", "  "));

            Assert.Equal(3, receiver.FlushCount);
        }

        [Fact]
        public async Task Teardown_ClearsMirroringAndAudioSessions()
        {
            var (listener, receiver, sessionId) = await CreateListenerAsync();
            var session = await CreatePairedSessionAsync(sessionId);
            session.SpsPps = new byte[] { 1 };
            session.AudioFormat = AudioFormat.PCM;
            await SessionManager.Current.CreateOrUpdateSessionAsync(sessionId, session);

            var mirroringBody = ToPlist(new Dictionary<string, object>
            {
                { "streams", new object[] { new Dictionary<string, object> { { "type", 110 } } } }
            });
            await Send(listener, "TEARDOWN", "/stream", sessionId, mirroringBody);

            Assert.Null(session.SpsPps);
            Assert.Null(session.StreamConnectionId);
            Assert.Null(session.MirroringSession);
            Assert.Equal(1, receiver.MirroringStoppedCount);

            var audioBody = ToPlist(new Dictionary<string, object>
            {
                { "streams", new object[] { new Dictionary<string, object> { { "type", 96 } } } }
            });
            await Send(listener, "TEARDOWN", "/stream", sessionId, audioBody);

            Assert.Equal(AudioFormat.Unknown, session.AudioFormat);
        }

        private static byte[] DmapBuffer(string tag, byte[] data)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(new byte[8]);
            writer.Write(Encoding.ASCII.GetBytes(tag));
            var length = BitConverter.GetBytes((uint)data.Length);
            Array.Reverse(length);
            writer.Write(length);
            writer.Write(data);
            return stream.ToArray();
        }
    }
}
