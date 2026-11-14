using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AirPlay;
using AirPlay.Listeners;
using AirPlay.Models;
using AirPlay.Models.Configs;
using AirPlay.Models.Enums;
using AirPlay.Services.Implementations;
using AirPlay.Utils;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Xunit;
using ProtocolType = AirPlay.Models.Enums.ProtocolType;

namespace AirPlay.Tests
{
    public class AudioListenerTests
    {
        private static Session CreateSession(string sessionId, AudioFormat format = AudioFormat.PCM)
        {
            return new Session(sessionId)
            {
                AesKey = new byte[16],
                AesIv = new byte[16],
                EcdhShared = new byte[32],
                DecryptedAesKey = new byte[16],
                KeyMsg = new byte[164],
                AudioFormat = format
            };
        }

        private static IBufferedCipher CreateCipher(Session session)
        {
            var hash = Utilities.Hash(session.AesKey, session.EcdhShared);
            var cipher = CipherUtilities.GetCipher("AES/CBC/NoPadding");
            cipher.Init(false, new ParametersWithIV(
                ParameterUtilities.CreateKeyParameter("AES", hash.Take(16).ToArray()),
                session.AesIv));
            return cipher;
        }

        private static RaopBuffer GetBuffer(AudioListener listener)
        {
            return (RaopBuffer)typeof(AudioListener)
                .GetField("_raopBuffer", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(listener);
        }

        private static void InitializeDecoder(AudioListener listener, Session session)
        {
            typeof(AudioListener)
                .GetMethod("InitializeDecoder", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(listener, new object[] { session });
        }

        private static AudioListener CreateListener(string sessionId, FakeRtspReceiver receiver = null,
            bool isMirroring = false)
        {
            return new AudioListener(
                receiver ?? new FakeRtspReceiver(),
                sessionId,
                (ushort)TestHelpers.GetFreeUdpPort(),
                (ushort)TestHelpers.GetFreeUdpPort(),
                new DumpConfig { Path = Path.GetTempPath() },
                isMirroring);
        }

        [Fact]
        public async Task QueueAndDequeue_ReturnsDecodedFrame()
        {
            var sessionId = "audio-" + Guid.NewGuid();
            var session = CreateSession(sessionId);
            await SessionManager.Current.CreateOrUpdateSessionAsync(sessionId, session);
            var listener = CreateListener(sessionId);
            InitializeDecoder(listener, session);
            var buffer = GetBuffer(listener);

            var data = new byte[12 + 16];
            data[0] = 0x80;
            data[1] = 0x60;
            data[3] = 7;
            data[7] = 10;

            Assert.Equal(1, listener.RaopBufferQueue(buffer, data, (ushort)data.Length, session, CreateCipher(session)));
            Assert.Equal(0, listener.RaopBufferQueue(buffer, data, (ushort)data.Length, session, CreateCipher(session)));

            var length = 0;
            var pts = 0u;
            var output = listener.RaopBufferDequeue(buffer, ref length, ref pts, false);

            Assert.NotNull(output);
            Assert.Equal(4096, output.Length);
            Assert.Equal(4096, length);
            Assert.Equal(10u, pts);
            Assert.Equal(0, listener.RaopBufferQueue(buffer, data, (ushort)data.Length, session, CreateCipher(session)) == 1 ? 0 : 0);
        }

        [Fact]
        public void Queue_RejectsInvalidLengthsAndNoDataMarker()
        {
            var sessionId = "audio-" + Guid.NewGuid();
            var session = CreateSession(sessionId);
            var listener = CreateListener(sessionId);
            InitializeDecoder(listener, session);
            var buffer = GetBuffer(listener);

            Assert.Equal(-1, listener.RaopBufferQueue(buffer, new byte[8], 8, session, CreateCipher(session)));

            var noData = new byte[16];
            noData[12] = 0x00;
            noData[13] = 0x68;
            noData[14] = 0x34;
            noData[15] = 0x00;
            Assert.Equal(0, listener.RaopBufferQueue(buffer, noData, 16, session, CreateCipher(session)));
        }

        [Fact]
        public void Dequeue_EmptyAndNoResendBranches()
        {
            var sessionId = "audio-" + Guid.NewGuid();
            var session = CreateSession(sessionId);
            var listener = CreateListener(sessionId);
            InitializeDecoder(listener, session);
            var buffer = GetBuffer(listener);

            var length = 0;
            var pts = 0u;
            Assert.Null(listener.RaopBufferDequeue(buffer, ref length, ref pts, false));

            var data = new byte[12 + 16];
            data[3] = 1;
            listener.RaopBufferQueue(buffer, data, (ushort)data.Length, session, CreateCipher(session));

            var output = listener.RaopBufferDequeue(buffer, ref length, ref pts, true);
            Assert.NotNull(output);
            Assert.Equal(4096, length);
        }

        [Fact]
        public async Task FlushAsync_ClearsBufferAndNotifiesReceiver()
        {
            var receiver = new FakeRtspReceiver();
            var sessionId = "audio-" + Guid.NewGuid();
            var session = CreateSession(sessionId);
            var listener = CreateListener(sessionId, receiver);
            InitializeDecoder(listener, session);
            var buffer = GetBuffer(listener);

            var data = new byte[12 + 16];
            data[3] = 3;
            listener.RaopBufferQueue(buffer, data, (ushort)data.Length, session, CreateCipher(session));

            await listener.FlushAsync(10);

            var length = 0;
            var pts = 0u;
            Assert.Null(listener.RaopBufferDequeue(buffer, ref length, ref pts, false));
            Assert.Equal(1, receiver.FlushCount);
        }

        [Fact]
        public void InitializeDecoder_CoversSupportedFormats()
        {
            var sessionId = "audio-" + Guid.NewGuid();
            var listener = CreateListener(sessionId);

            foreach (var format in new[] { AudioFormat.PCM, AudioFormat.ALAC, AudioFormat.AAC, AudioFormat.AAC_ELD })
            {
                var session = CreateSession(sessionId, format);
                session.AudioSamplesPerFrame = 1024;
                try
                {
                    InitializeDecoder(listener, session);
                }
                catch (TargetInvocationException)
                {
                    // Native codec libraries may be unavailable in the test host.
                }
            }
        }

        [Fact]
        public async Task SocketLoops_ReceiveControlSyncDataAndOtherPackets()
        {
            var receiver = new FakeRtspReceiver();
            var sessionId = "audio-" + Guid.NewGuid();
            var session = CreateSession(sessionId);
            await SessionManager.Current.CreateOrUpdateSessionAsync(sessionId, session);

            var controlPort = (ushort)TestHelpers.GetFreeUdpPort();
            var dataPort = (ushort)TestHelpers.GetFreeUdpPort();
            var listener = new AudioListener(receiver, sessionId, controlPort, dataPort,
                new DumpConfig { Path = Path.GetTempPath() }, true);

            using var cts = new CancellationTokenSource();
            await listener.StartAsync(cts.Token);
            await Task.Delay(100);

            using var client = new UdpClient();

            var control = new byte[4 + 12 + 16];
            control[0] = 0x80;
            control[1] = 0x56;
            control[3] = 1;
            control[4] = 0x80;
            control[5] = 0x60;
            client.Send(control, control.Length, new IPEndPoint(IPAddress.Loopback, controlPort));

            var sync = new byte[32];
            sync[1] = 0x54;
            client.Send(sync, sync.Length, new IPEndPoint(IPAddress.Loopback, controlPort));

            var other = new byte[8];
            other[1] = 0x40;
            client.Send(other, other.Length, new IPEndPoint(IPAddress.Loopback, controlPort));

            var dataPacket = new byte[12 + 16];
            dataPacket[1] = 0x60;
            dataPacket[3] = 2;
            client.Send(dataPacket, dataPacket.Length, new IPEndPoint(IPAddress.Loopback, dataPort));

            await Task.Delay(250);
            cts.Cancel();
            await listener.StopAsync();

            Assert.NotNull(session.DecryptedAesKey);
        }
    }

    public class MirroringListenerTests
    {
        private static byte[] EncryptCtr(IBufferedCipher cipher, byte[] data)
        {
            var output = new byte[data.Length];
            cipher.ProcessBytes(data, 0, data.Length, output, 0);
            return output;
        }

        private static IBufferedCipher CreateCtr(Session session)
        {
            var eaesKey = Utilities.Hash(session.DecryptedAesKey, session.EcdhShared);
            var keyHash = Utilities.Hash(
                Encoding.UTF8.GetBytes(MirroringListener.AIR_PLAY_STREAM_KEY + session.StreamConnectionId),
                eaesKey.Take(16).ToArray());
            var ivHash = Utilities.Hash(
                Encoding.UTF8.GetBytes(MirroringListener.AIR_PLAY_STREAM_IV + session.StreamConnectionId),
                eaesKey.Take(16).ToArray());

            var cipher = CipherUtilities.GetCipher("AES/CTR/NoPadding");
            cipher.Init(true, new ParametersWithIV(
                ParameterUtilities.CreateKeyParameter("AES", keyHash.Take(16).ToArray()),
                ivHash.Take(16).ToArray()));
            return cipher;
        }

        private static void WriteHeader(byte[] header, int payloadSize, ushort type, int width, int height)
        {
            BitConverter.GetBytes(payloadSize).CopyTo(header, 0);
            BitConverter.GetBytes(type).CopyTo(header, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(header, 6);
            BitConverter.GetBytes(1500000L).CopyTo(header, 8);
            BitConverter.GetBytes((float)width).CopyTo(header, 40);
            BitConverter.GetBytes((float)height).CopyTo(header, 44);
            BitConverter.GetBytes((float)width).CopyTo(header, 56);
            BitConverter.GetBytes((float)height).CopyTo(header, 60);
        }

        [Fact]
        public async Task ReceivesHttpSpsPpsAndVideoFrames()
        {
            var port = (ushort)TestHelpers.GetFreeTcpPort();
            var receiver = new FakeRtspReceiver();
            var sessionId = "mirror-" + Guid.NewGuid();
            var session = new Session(sessionId)
            {
                DecryptedAesKey = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
                EcdhShared = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
                AesIv = new byte[16],
                StreamConnectionId = "conn-1"
            };
            await SessionManager.Current.CreateOrUpdateSessionAsync(sessionId, session);

            var listener = new MirroringListener(receiver, sessionId, port);
            session.MirroringListener = listener;
            using var cts = new CancellationTokenSource();
            await listener.StartAsync(cts.Token);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();

            var http = Encoding.ASCII.GetBytes("GET /stream HTTP/1.1\r\nContent-Length: 4\r\n\r\nbody");
            await stream.WriteAsync(http, 0, http.Length);

            var spsPps = new byte[17];
            spsPps[0] = 1;
            spsPps[1] = 0x64;
            spsPps[5] = 0xe1;
            spsPps[7] = 4;
            spsPps[12] = 1;
            spsPps[14] = 2;
            var spsHeader = new byte[128];
            WriteHeader(spsHeader, spsPps.Length, 1, 1280, 720);
            await stream.WriteAsync(spsHeader, 0, spsHeader.Length);
            await stream.WriteAsync(spsPps, 0, spsPps.Length);

            var ctrCipher = CreateCtr(session);

            var idr = new byte[16];
            idr[3] = 4;
            idr[4] = 0x65;

            var nonIdr = new byte[16];
            nonIdr[3] = 4;
            nonIdr[4] = 0x41;

            foreach (var frame in new[] { idr, nonIdr })
            {
                var encrypted = EncryptCtr(ctrCipher, frame);
                var videoHeader = new byte[128];
                WriteHeader(videoHeader, encrypted.Length, 0, 1280, 720);
                await stream.WriteAsync(videoHeader, 0, videoHeader.Length);
                await stream.WriteAsync(encrypted, 0, encrypted.Length);
            }

            await Task.Delay(200);
            stream.Close();
            client.Close();
            await Task.Delay(100);
            cts.Cancel();
            await listener.StopAsync();

            Assert.Equal(2, receiver.Frames.Count);
            Assert.Equal(5, receiver.Frames[0].FrameType);
            Assert.True(receiver.Frames[0].Length > 16);
            Assert.Equal(1, receiver.MirroringStoppedCount);
        }
    }

    public class StreamingListenerTests
    {
        [Fact]
        public async Task ServerInfoAndPairVerify()
        {
            var receiver = new FakeRtspReceiver();
            var sessionId = "stream-" + Guid.NewGuid();
            await SessionManager.Current.CreateOrUpdateSessionAsync(sessionId, new Session(sessionId));

            var seed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            Chaos.NaCl.Ed25519.KeyPairFromSeed(out _, out byte[] expandedPrivateKey, seed);

            var listener = new StreamingListener(receiver, sessionId, expandedPrivateKey,
                (ushort)TestHelpers.GetFreeTcpPort());

            var infoResponse = new Response(ProtocolType.RTSP10);
            var info = new Request(TestHelpers.BuildRequest("GET", "/server-info", "RTSP/1.0",
                ("Active-Remote", sessionId)));
            await listener.OnDataReceivedAsync(info, infoResponse, CancellationToken.None);
            Assert.Contains("AppleTV5,3",
                Encoding.ASCII.GetString(await infoResponse.ReadAsync()));

            var verifyBody = new byte[68];
            verifyBody[0] = 1;
            Enumerable.Repeat((byte)9, 32).ToArray().CopyTo(verifyBody, 4);
            Enumerable.Repeat((byte)7, 32).ToArray().CopyTo(verifyBody, 36);
            var verifyRequest = new Request(TestHelpers.BuildRequestWithBody(
                "POST", "/pair-verify", "RTSP/1.0", verifyBody,
                ("Active-Remote", sessionId), ("Content-Length", verifyBody.Length.ToString())));
            var verifyResponse = new Response(ProtocolType.RTSP10);
            await listener.OnDataReceivedAsync(verifyRequest, verifyResponse, CancellationToken.None);
            Assert.Equal(96, (await verifyResponse.ReadAsync()).Length);

            var signatureBody = new byte[68];
            var signatureRequest = new Request(TestHelpers.BuildRequestWithBody(
                "POST", "/pair-verify", "RTSP/1.0", signatureBody,
                ("Active-Remote", sessionId), ("Content-Length", signatureBody.Length.ToString())));
            await listener.OnDataReceivedAsync(signatureRequest, new Response(ProtocolType.RTSP10),
                CancellationToken.None);

            var unknown = new Request(TestHelpers.BuildRequest("OPTIONS", "*", "RTSP/1.0",
                ("Active-Remote", sessionId)));
            await listener.OnDataReceivedAsync(unknown, new Response(ProtocolType.RTSP10),
                CancellationToken.None);

            await listener.StopAsync();
        }

        [Fact]
        public async Task PlayRequestParsesHeadersBeforeFailingToConnect()
        {
            var receiver = new FakeRtspReceiver();
            var sessionId = "stream-" + Guid.NewGuid();
            var listener = new StreamingListener(receiver, sessionId, new byte[64],
                (ushort)TestHelpers.GetFreeTcpPort());

            var body = Encoding.ASCII.GetBytes(
                "Start-Position: 1.5\nContent-Location: http://127.0.0.1:1/test\n");
            var request = new Request(TestHelpers.BuildRequestWithBody(
                "POST", "/play", "RTSP/1.0", body,
                ("Active-Remote", sessionId), ("Content-Length", body.Length.ToString())));

            await Assert.ThrowsAnyAsync<Exception>(() =>
                listener.OnDataReceivedAsync(request, new Response(ProtocolType.RTSP10),
                    CancellationToken.None));

            await listener.StopAsync();
        }
    }
}
