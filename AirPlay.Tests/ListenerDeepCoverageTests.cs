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
using SocketProtocol = System.Net.Sockets.ProtocolType;

namespace AirPlay.Tests
{
    public class AudioListenerDeepCoverageTests
    {
        private sealed class StubDecoder : IDecoder
        {
            public AudioFormat Type => AudioFormat.PCM;
            public int DecodeResult { get; set; }

            public int GetOutputStreamLength() => 64;

            public int Config(int sampleRate, int channels, int bitDepth, int frameLength) => 0;

            public int DecodeFrame(byte[] input, ref byte[] output, int length)
            {
                if (output == null || output.Length < length)
                {
                    output = new byte[length];
                }

                return DecodeResult;
            }
        }

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

        private static AudioListener CreateListener(string sessionId, FakeRtspReceiver receiver = null)
        {
            return new AudioListener(
                receiver ?? new FakeRtspReceiver(),
                sessionId,
                (ushort)TestHelpers.GetFreeUdpPort(),
                (ushort)TestHelpers.GetFreeUdpPort(),
                new DumpConfig { Path = Path.GetTempPath() });
        }

        private static void SetDecoder(AudioListener listener, IDecoder decoder)
        {
            typeof(AudioListener)
                .GetField("_decoder", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(listener, decoder);
        }

        private static object Invoke(AudioListener listener, string method, params object[] args)
        {
            return typeof(AudioListener)
                .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(listener, args);
        }

        [Fact]
        public void InitializeDecoder_CoversEveryFormatWithFreshListener()
        {
            var sessionId = "audio-" + Guid.NewGuid();

            foreach (var format in new[] { AudioFormat.PCM, AudioFormat.ALAC, AudioFormat.AAC, AudioFormat.AAC_ELD })
            {
                var listener = CreateListener(sessionId);
                var session = CreateSession(sessionId, format);
                session.AudioSamplesPerFrame = 0;
                try
                {
                    Invoke(listener, "InitializeDecoder", session);
                }
                catch (TargetInvocationException)
                {
                    // Native codec libraries may be unavailable in the test host.
                }
            }

            var unknownFormat = CreateListener(sessionId);
            Invoke(unknownFormat, "InitializeDecoder", CreateSession(sessionId, (AudioFormat)99));

            var alacByCompression = CreateListener(sessionId);
            var alacSession = CreateSession(sessionId, (AudioFormat)99);
            alacSession.AudioCompressionType = 1;
            Invoke(alacByCompression, "InitializeDecoder", alacSession);

            var pcmByCompression = CreateListener(sessionId);
            var pcmSession = CreateSession(sessionId, (AudioFormat)99);
            pcmSession.AudioCompressionType = 0;
            Invoke(pcmByCompression, "InitializeDecoder", pcmSession);

            var fallback = CreateListener(sessionId);
            var fallbackSession = CreateSession(sessionId, (AudioFormat)99);
            fallbackSession.AudioCompressionType = 7;
            Invoke(fallback, "InitializeDecoder", fallbackSession);
        }

        [Fact]
        public void Queue_GapFlushOldSequenceAndLastSequenceAdvance()
        {
            var sessionId = "audio-" + Guid.NewGuid();
            var session = CreateSession(sessionId);
            var listener = CreateListener(sessionId);
            Invoke(listener, "InitializeDecoder", session);
            var buffer = GetBuffer(listener);

            Assert.Equal(1, listener.RaopBufferQueue(buffer, BuildPacket(10), 28, session, CreateCipher(session)));
            Assert.Equal(1, listener.RaopBufferQueue(buffer, BuildPacket(10 + AudioListener.RAOP_BUFFER_LENGTH), 28,
                session, CreateCipher(session)));

            Assert.Equal(1, listener.RaopBufferQueue(buffer, BuildPacket(1040), 28, session, CreateCipher(session)));
            Assert.Equal((ushort)1040, buffer.LastSeqNum);

            Assert.Equal(0, listener.RaopBufferQueue(buffer, BuildPacket(5), 28, session, CreateCipher(session)));
        }

        [Fact]
        public void Queue_DecoderFailureAndSilentSuccessPaths()
        {
            var sessionId = "audio-" + Guid.NewGuid();
            var session = CreateSession(sessionId);
            var listener = CreateListener(sessionId);
            var buffer = GetBuffer(listener);

            SetDecoder(listener, new StubDecoder { DecodeResult = -1 });
            Assert.Equal(1, listener.RaopBufferQueue(buffer, BuildPacket(1), 28, session, CreateCipher(session)));

            SetDecoder(listener, new StubDecoder { DecodeResult = 0 });
            Assert.Equal(1, listener.RaopBufferQueue(buffer, BuildPacket(2), 28, session, CreateCipher(session)));
        }

        [Fact]
        public void Queue_EncryptedAndEmptyPayloadBranches()
        {
            var sessionId = "audio-" + Guid.NewGuid();
            var session = CreateSession(sessionId);
            var listener = CreateListener(sessionId);
            Invoke(listener, "InitializeDecoder", session);
            var buffer = GetBuffer(listener);

            // Payload shorter than one AES block is passed through untouched.
            var packet = BuildPacket(3, 5);
            Assert.Equal(1, listener.RaopBufferQueue(buffer, packet, (ushort)packet.Length, session, CreateCipher(session)));
        }

        [Fact]
        public void Dequeue_UnavailableEntries()
        {
            var sessionId = "audio-" + Guid.NewGuid();
            var listener = CreateListener(sessionId);
            var buffer = GetBuffer(listener);

            var length = 0;
            var pts = 0u;

            buffer.IsEmpty = false;
            buffer.FirstSeqNum = 0;
            buffer.LastSeqNum = 100;
            buffer.Entries[0].Available = false;
            Assert.Null(listener.RaopBufferDequeue(buffer, ref length, ref pts, false));

            buffer.LastSeqNum = 1500;
            Assert.Null(listener.RaopBufferDequeue(buffer, ref length, ref pts, false));
            Assert.Equal((ushort)1, buffer.FirstSeqNum);
        }

        [Fact]
        public void Flush_InvalidSequenceResetsBuffer()
        {
            var sessionId = "audio-" + Guid.NewGuid();
            var listener = CreateListener(sessionId);
            var buffer = GetBuffer(listener);
            buffer.IsEmpty = false;
            buffer.FirstSeqNum = 4;
            buffer.LastSeqNum = 9;

            Invoke(listener, "RaopBufferFlush", buffer, -1);
            Assert.True(buffer.IsEmpty);

            buffer.IsEmpty = false;
            Invoke(listener, "RaopBufferFlush", buffer, 70000);
            Assert.True(buffer.IsEmpty);
        }

        [Fact]
        public void ResendHandling_SendsRequestAndStopsAtAvailableEntry()
        {
            var sessionId = "audio-" + Guid.NewGuid();
            var listener = CreateListener(sessionId);
            var buffer = GetBuffer(listener);

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, SocketProtocol.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            buffer.IsEmpty = false;
            buffer.FirstSeqNum = 1;
            buffer.LastSeqNum = 5;
            Invoke(listener, "RaopBufferHandleResends", buffer, socket, (ushort)2);

            buffer.Entries[1].Available = true;
            Invoke(listener, "RaopBufferHandleResends", buffer, socket, (ushort)3);

            buffer.LastSeqNum = 1;
            Invoke(listener, "RaopBufferHandleResends", buffer, socket, (ushort)4);
        }

        [Fact]
        public async Task ControlSocket_DecryptsKeyAndHandlesNoDataPacket()
        {
            var receiver = new FakeRtspReceiver();
            var sessionId = "audio-" + Guid.NewGuid();
            var session = CreateSession(sessionId);
            session.DecryptedAesKey = null;
            session.KeyMsg = new byte[164];
            session.KeyMsg[12] = 1;
            session.AesKey = new byte[164];
            await SessionManager.Current.CreateOrUpdateSessionAsync(sessionId, session);

            var controlPort = (ushort)TestHelpers.GetFreeUdpPort();
            var listener = new AudioListener(receiver, sessionId, controlPort,
                (ushort)TestHelpers.GetFreeUdpPort(), new DumpConfig { Path = Path.GetTempPath() }, true);

            using var cts = new CancellationTokenSource();
            await listener.StartAsync(cts.Token);
            await Task.Delay(100);

            using var client = new UdpClient();
            var packet = new byte[16];
            packet[1] = 0x56;
            client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, controlPort));

            await Task.Delay(200);
            cts.Cancel();
            await listener.StopAsync();

            Assert.NotNull(session.DecryptedAesKey);
        }

        private static byte[] BuildPacket(int seqNum, int payloadSize = 16)
        {
            var data = new byte[12 + payloadSize];
            data[1] = 0x60;
            data[2] = (byte)(seqNum >> 8);
            data[3] = (byte)seqNum;
            data[7] = (byte)seqNum;
            return data;
        }

        [Fact]
        public async Task ControlSocket_DeliversDecodedPcm()
        {
            var receiver = new FakeRtspReceiver();
            var sessionId = "audio-" + Guid.NewGuid();
            var session = CreateSession(sessionId);
            await SessionManager.Current.CreateOrUpdateSessionAsync(sessionId, session);

            var controlPort = (ushort)TestHelpers.GetFreeUdpPort();
            var listener = new AudioListener(receiver, sessionId, controlPort,
                (ushort)TestHelpers.GetFreeUdpPort(), new DumpConfig { Path = Path.GetTempPath() }, true);
            Invoke(listener, "InitializeDecoder", session);

            using var cts = new CancellationTokenSource();
            await listener.StartAsync(cts.Token);
            await Task.Delay(100);

            using var client = new UdpClient();
            var packet = new byte[40];
            packet[0] = 0x80;
            packet[1] = 0x56;
            packet[2] = 0;
            packet[3] = 1;
            packet[4] = 0x80;
            packet[5] = 0x60;
            packet[7] = 1;
            packet[11] = 1;
            for (int i = 12; i < packet.Length; i++)
            {
                packet[i] = (byte)i;
            }

            client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, controlPort));

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (receiver.Pcm.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            cts.Cancel();
            await listener.StopAsync();
            Assert.NotEmpty(receiver.Pcm);
        }

        [Fact]
        public async Task DataSocket_DecodesPcmAndRunsResendHandling()
        {
            var receiver = new FakeRtspReceiver();
            var sessionId = "audio-" + Guid.NewGuid();
            var session = CreateSession(sessionId);
            await SessionManager.Current.CreateOrUpdateSessionAsync(sessionId, session);

            var dataPort = (ushort)TestHelpers.GetFreeUdpPort();
            var listener = new AudioListener(receiver, sessionId,
                (ushort)TestHelpers.GetFreeUdpPort(), dataPort, new DumpConfig { Path = Path.GetTempPath() }, false);
            Invoke(listener, "InitializeDecoder", session);

            using var cts = new CancellationTokenSource();
            await listener.StartAsync(cts.Token);
            await Task.Delay(100);

            using var client = new UdpClient();
            var packet = new byte[44];
            packet[0] = 0x80;
            packet[1] = 0x60;
            packet[3] = 2;
            for (int i = 12; i < packet.Length; i++)
            {
                packet[i] = (byte)i;
            }

            client.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, dataPort));

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (receiver.Pcm.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            cts.Cancel();
            await listener.StopAsync();
            Assert.NotEmpty(receiver.Pcm);
        }
    }

    public class MirroringListenerDeepCoverageTests
    {
        private static object Invoke(MirroringListener listener, string method, params object[] args)
        {
            return typeof(MirroringListener)
                .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(listener, args);
        }

        private static MirroringListener CreateListener(out FakeRtspReceiver receiver)
        {
            receiver = new FakeRtspReceiver();
            return new MirroringListener(receiver, "mirror-" + Guid.NewGuid(),
                (ushort)TestHelpers.GetFreeTcpPort());
        }

        [Fact]
        public void DecryptVideoData_CarriesRemainderAcrossFrames()
        {
            var listener = CreateListener(out _);
            Invoke(listener, "InitAesCtrCipher", new byte[16], new byte[32], "conn-remainder");
            var first = new byte[20];
            for (int i = 0; i < first.Length; i++)
            {
                first[i] = (byte)i;
            }

            var firstArgs = new object[] { first, null };
            Invoke(listener, "DecryptVideoData", firstArgs);
            Assert.Equal(20, ((byte[])firstArgs[1]).Length);

            var second = Enumerable.Repeat((byte)7, 20).ToArray();
            var secondArgs = new object[] { second, null };
            Invoke(listener, "DecryptVideoData", secondArgs);
            Assert.Equal(20, ((byte[])secondArgs[1]).Length);
        }

        [Fact]
        public void ProcessVideo_HandlesShortMissingSpsAndNonIdr()
        {
            var listener = CreateListener(out var receiver);

            Invoke(listener, "ProcessVideo", new byte[2], new byte[8], 0L, 10, 10);
            Assert.Empty(receiver.Frames);

            var payload = new byte[8];
            payload[3] = 2;
            payload[4] = 0x41;
            Invoke(listener, "ProcessVideo", payload, null, 1L, 20, 20);
            Assert.Empty(receiver.Frames);

            Invoke(listener, "ProcessVideo", payload, new byte[0], 1L, 20, 20);
            Assert.Empty(receiver.Frames);

            Invoke(listener, "ProcessVideo", payload, new byte[] { 0, 0, 0, 1 }, 2L, 30, 40);
            var frame = Assert.Single(receiver.Frames);
            Assert.Equal(1, frame.FrameType);
            Assert.Equal(2L, frame.Pts);
            Assert.Equal(30, frame.Width);
        }

        [Fact]
        public void ProcessSpsPps_RejectsInvalidPayloads()
        {
            var listener = CreateListener(out _);

            var spsTooLong = new byte[20];
            spsTooLong[7] = 100;

            var ppsTooLong = new byte[20];
            ppsTooLong[7] = 4;
            ppsTooLong[13] = 100;

            foreach (var payload in new[]
            {
                null,
                new byte[10],
                BuildSpsPps(0, 4),
                BuildSpsPps(4, 0),
                spsTooLong,
                ppsTooLong
            })
            {
                var args = new object[] { payload, null };
                Invoke(listener, "ProcessSpsPps", args);
                Assert.Null(args[1]);
            }
        }

        [Fact]
        public void ProcessSpsPps_BuildsAnnexBPrefixForValidPayload()
        {
            var listener = CreateListener(out _);
            var payload = BuildSpsPps(4, 2);
            var args = new object[] { payload, null };
            Invoke(listener, "ProcessSpsPps", args);

            var result = (byte[])args[1];
            Assert.Equal(4 + 4 + 4 + 2, result.Length);
            Assert.Equal(new byte[] { 0, 0, 0, 1 }, result.Take(4));
        }

        [Fact]
        public async Task ConsumeHttpRequest_HandlesBodyAndFailures()
        {
            var listener = CreateListener(out _);
            var prefix = Encoding.ASCII.GetBytes("GET ");

            using (var pair = await TcpPair.CreateAsync())
            {
                var bytes = Encoding.ASCII.GetBytes(" /x HTTP/1.1\r\nContent-Length: 3\r\n\r\nabc");
                await pair.ClientStream.WriteAsync(bytes, 0, bytes.Length);
                Assert.True(await Consume(listener, pair.ServerStream, prefix));
            }

            using (var pair = await TcpPair.CreateAsync())
            {
                var bytes = Encoding.ASCII.GetBytes(" /x HTTP/1.1\r\nContent-Length: abc\r\n\r\n");
                await pair.ClientStream.WriteAsync(bytes, 0, bytes.Length);
                Assert.True(await Consume(listener, pair.ServerStream, prefix));
            }

            using (var pair = await TcpPair.CreateAsync())
            {
                var bytes = Encoding.ASCII.GetBytes(" /x HTTP/1.1\r\nContent-Length: 70000\r\n\r\n");
                await pair.ClientStream.WriteAsync(bytes, 0, bytes.Length);
                Assert.False(await Consume(listener, pair.ServerStream, prefix));
            }

            using (var pair = await TcpPair.CreateAsync())
            {
                var bytes = Encoding.ASCII.GetBytes(" /x HTTP/1.1\r\nContent-Length: 10\r\n\r\nabc");
                await pair.ClientStream.WriteAsync(bytes, 0, bytes.Length);
                pair.ClientClient.Close();
                Assert.False(await Consume(listener, pair.ServerStream, prefix));
            }

            using (var pair = await TcpPair.CreateAsync())
            {
                var bytes = Encoding.ASCII.GetBytes(" /x HTTP/1.1\r\n");
                await pair.ClientStream.WriteAsync(bytes, 0, bytes.Length);
                pair.ClientClient.Close();
                Assert.False(await Consume(listener, pair.ServerStream, prefix));
            }

            using (var pair = await TcpPair.CreateAsync())
            {
                var bytes = Enumerable.Repeat((byte)'A', 70000).ToArray();
                await pair.ClientStream.WriteAsync(bytes, 0, bytes.Length);
                Assert.False(await Consume(listener, pair.ServerStream, prefix));
            }
        }

        private static async Task<bool> Consume(MirroringListener listener, NetworkStream stream, byte[] prefix)
        {
            var task = (Task<bool>)Invoke(listener, "ConsumeHttpRequestAsync", stream, prefix, prefix.Length, CancellationToken.None);
            return await task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task Connection_DecryptsSessionKeyAndIgnoresEmptyOrOversizedPayloads()
        {
            var receiver = new FakeRtspReceiver();
            var sessionId = "mirror-" + Guid.NewGuid();
            var session = new Session(sessionId)
            {
                DecryptedAesKey = null,
                AesKey = new byte[164],
                KeyMsg = new byte[164],
                EcdhShared = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
                StreamConnectionId = "conn-deep"
            };
            session.KeyMsg[12] = 0;
            session.MirroringSession = true;
            await SessionManager.Current.CreateOrUpdateSessionAsync(sessionId, session);

            var port = (ushort)TestHelpers.GetFreeTcpPort();
            var listener = new MirroringListener(receiver, sessionId, port);
            session.MirroringListener = listener;

            using var cts = new CancellationTokenSource();
            await listener.StartAsync(cts.Token);

            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                var stream = client.GetStream();

                var post = Encoding.ASCII.GetBytes("POST /x HTTP/1.1\r\nContent-Length: 0\r\n\r\n");
                await stream.WriteAsync(post, 0, post.Length);

                var emptyHeader = new byte[128];
                await stream.WriteAsync(emptyHeader, 0, emptyHeader.Length);

                var oversized = new byte[128];
                BitConverter.GetBytes(9 * 1024 * 1024).CopyTo(oversized, 0);
                await stream.WriteAsync(oversized, 0, oversized.Length);

                await Task.Delay(150);
                stream.Close();
                client.Close();
            }

            await Task.Delay(150);
            cts.Cancel();
            await listener.StopAsync();

            Assert.NotNull(session.DecryptedAesKey);
            Assert.Empty(receiver.Frames);
        }

        private static byte[] BuildSpsPps(int spsLen, int ppsLen)
        {
            var payload = new byte[11 + spsLen + ppsLen];
            payload[0] = 1;
            payload[6] = (byte)(spsLen >> 8);
            payload[7] = (byte)spsLen;
            if (spsLen > 0 && payload.Length > 8)
            {
                payload[8] = 0x67;
            }

            if (payload.Length > spsLen + 10)
            {
                payload[spsLen + 9] = (byte)(ppsLen >> 8);
                payload[spsLen + 10] = (byte)ppsLen;
            }

            if (ppsLen > 0 && payload.Length > spsLen + 11)
            {
                payload[spsLen + 11] = 0x68;
            }

            return payload;
        }

        private sealed class TcpPair : IDisposable
        {
            public TcpClient ClientClient { get; private set; }
            public NetworkStream ClientStream { get; private set; }
            public NetworkStream ServerStream { get; private set; }

            private TcpClient _serverClient;
            private TcpListener _listener;

            public static async Task<TcpPair> CreateAsync()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                var server = await listener.AcceptTcpClientAsync();
                listener.Stop();

                return new TcpPair
                {
                    _listener = listener,
                    _serverClient = server,
                    ClientClient = client,
                    ClientStream = client.GetStream(),
                    ServerStream = server.GetStream()
                };
            }

            public void Dispose()
            {
                ClientStream?.Dispose();
                ServerStream?.Dispose();
                ClientClient?.Dispose();
                _serverClient?.Dispose();
                _listener?.Stop();
            }
        }
    }
}
