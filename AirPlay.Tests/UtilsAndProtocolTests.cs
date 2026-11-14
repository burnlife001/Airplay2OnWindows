using System;
using System.IO;
using System.Linq;
using System.Text;
using AirPlay.Listeners;
using AirPlay.Models;
using AirPlay.Models.Enums;
using AirPlay.Utils;
using Xunit;

namespace AirPlay.Tests
{
    public class UtilitiesTests
    {
        [Fact]
        public void CopyOfRange_CopiesRequestedWindow()
        {
            Assert.Equal(new byte[] { 2, 3, 4 }, Utilities.CopyOfRange(new byte[] { 1, 2, 3, 4, 5 }, 1, 4));
        }

        [Fact]
        public void Hash_IsSha512OfConcatenation()
        {
            var hash = Utilities.Hash(new byte[] { 1, 2 }, new byte[] { 3 });
            Assert.Equal(64, hash.Length);
            Assert.Equal(
                System.Security.Cryptography.SHA512.HashData(new byte[] { 1, 2, 3 }),
                hash);
        }

        [Fact]
        public void SeqNumCmp_ReturnsUnsignedDelta()
        {
            Assert.Equal(3, Utilities.SeqNumCmp(7, 4));
            Assert.Equal(65534, Utilities.SeqNumCmp(1, 3));
        }

        [Fact]
        public void Swap_ExchangesFourByteGroups()
        {
            var buffer = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            Utilities.Swap(buffer, 0, 4);
            Assert.Equal(new byte[] { 5, 6, 7, 8, 1, 2, 3, 4 }, buffer);
        }

        [Fact]
        public void WriteWavHeader_ProducesRiffHeader()
        {
            var header = Utilities.WriteWavHeader(2, 44100, 16, 1024);

            Assert.Equal(44, header.Length);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(header, 0, 4));
            Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(header, 8, 8));
            Assert.Equal("data", Encoding.ASCII.GetString(header, 36, 4));
            Assert.Equal(1024u, BitConverter.ToUInt32(header, 40));
        }

        [Fact]
        public void InitializeChiper_ProducesUsableAesCipher()
        {
            var cipher = Utilities.InitializeChiper(new byte[32]);
            var plain = Encoding.ASCII.GetBytes("0123456789abcdef");
            var encrypted = cipher.DoFinal(plain);

            Assert.NotNull(encrypted);
            Assert.Equal(plain.Length, encrypted.Length);
            Assert.NotEqual(plain, encrypted);
        }
    }

    public class ExtensionsTests
    {
        [Fact]
        public void HexToBytes_ConvertsAsciiHex()
        {
            Assert.Equal(new byte[] { 0x41, 0x42 }, "4142".HexToBytes());
        }

        [Fact]
        public void Reverse_ReversesInPlace()
        {
            Assert.Equal(new byte[] { 3, 2, 1 }, new byte[] { 1, 2, 3 }.Reverse());
        }

        [Fact]
        public void ReadBigEndianValues_AreDecoded()
        {
            using var ms = new MemoryStream(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12 });
            using var reader = new BinaryReader(ms);

            Assert.Equal((ushort)0x0102, reader.ReadUInt16BE());
            Assert.Equal((uint)0x03040506, reader.ReadUInt32BE());
            Assert.Equal(0x0708090A, reader.ReadInt32BE());
            Assert.Equal((ulong)0x0B0C0D0E0F101112, reader.ReadUInt64BE());
        }

        [Fact]
        public void ReadBytesRequired_ThrowsWhenStreamTooShort()
        {
            using var reader = new BinaryReader(new MemoryStream(new byte[] { 1 }));
            Assert.Throws<EndOfStreamException>(() => reader.ReadBytesRequired(2));
        }
    }

    public class HeaderTests
    {
        [Fact]
        public void ParsesNameAndSingleValue()
        {
            var header = new Header(TestHelpers.Hex("CSeq: 1"));

            Assert.True(header.IsValid);
            Assert.Equal("CSeq", header.Name);
            Assert.Equal(new[] { "1" }, header.Values);
        }

        [Fact]
        public void ParsesMultipleCommaSeparatedValues()
        {
            var header = new Header(TestHelpers.Hex("Content-Type: audio/aac, audio/mp4"));

            Assert.Equal("Content-Type", header.Name);
            Assert.Equal(new[] { "audio/aac", "audio/mp4" }, header.Values);
        }

        [Fact]
        public void NameValueConstructorSplitsValues()
        {
            var header = new Header("X", "a, b");

            Assert.Equal("X", header.Name);
            Assert.Equal(new[] { "a", "b" }, header.Values);
        }

        [Fact]
        public void ParamsConstructorKeepsValues()
        {
            var header = new Header("X", "a", "b");

            Assert.Equal(new[] { "a", "b" }, header.Values);
        }
    }

    public class HeadersCollectionTests
    {
        [Fact]
        public void AddGetAndIndexerWork()
        {
            var headers = new HeadersCollection();
            headers.Add("CSeq", "1");
            headers.Add("X-Test", new Header("X-Test", "a", "b"));

            Assert.Equal(2, headers.Count);
            Assert.True(headers.ContainsKey("cseq"));
            Assert.Equal("1", headers["CSeq"]);
            Assert.Equal("a,b", headers["X-Test"]);
            Assert.Equal(1, headers.GetValue<int>("CSeq"));
            Assert.Contains("CSeq", headers.Keys);
            Assert.Contains("1", headers.Values);
            Assert.Equal(2, headers.Count());
        }

        [Fact]
        public void IndexerSetterAndClearWork()
        {
            var headers = new HeadersCollection { ["X"] = "1" };
            Assert.Equal("1", headers["X"]);

            headers.Clear();
            Assert.Equal(0, headers.Count);
            Assert.False(headers.ContainsKey("X"));
            Assert.Throws<NotImplementedException>(() => headers.IsReadOnly);
        }
    }

    public class RequestTests
    {
        [Theory]
        [InlineData("GET", "RTSP/1.0", RequestType.GET)]
        [InlineData("POST", "RTSP/1.0", RequestType.POST)]
        [InlineData("SETUP", "RTSP/1.0", RequestType.SETUP)]
        [InlineData("RECORD", "RTSP/1.0", RequestType.RECORD)]
        [InlineData("GET_PARAMETER", "RTSP/1.0", RequestType.GET_PARAMETER)]
        [InlineData("SET_PARAMETER", "RTSP/1.0", RequestType.SET_PARAMETER)]
        [InlineData("OPTIONS", "RTSP/1.0", RequestType.OPTIONS)]
        [InlineData("ANNOUNCE", "RTSP/1.0", RequestType.ANNOUNCE)]
        [InlineData("FLUSH", "RTSP/1.0", RequestType.FLUSH)]
        [InlineData("TEARDOWN", "RTSP/1.0", RequestType.TEARDOWN)]
        [InlineData("PAUSE", "RTSP/1.0", RequestType.PAUSE)]
        [InlineData("SETPEERS", "RTSP/1.0", RequestType.SETPEERS)]
        public void ParsesRequestType(string method, string protocol, RequestType expected)
        {
            var request = new Request(TestHelpers.BuildRequest(method, "/test", protocol, ("CSeq", "1")));

            Assert.True(request.IsValid);
            Assert.Equal(expected, request.Type);
        }

        [Theory]
        [InlineData("HTTP/1.0", ProtocolType.HTTP10)]
        [InlineData("HTTP/1.1", ProtocolType.HTTP11)]
        [InlineData("RTSP/1.0", ProtocolType.RTSP10)]
        public void ParsesProtocolAndPath(string protocol, ProtocolType expected)
        {
            var request = new Request(TestHelpers.BuildRequest("GET", "/info", protocol));

            Assert.True(request.IsValid);
            Assert.Equal(expected, request.Protocol);
            Assert.Equal("/info", request.Path);
        }

        [Fact]
        public void ParsesHeadersAndBody()
        {
            var body = new byte[] { 0x01, 0x02, 0x0D, 0x0A, 0x03 };
            var request = new Request(TestHelpers.BuildRequestWithBody("SETUP", "/stream", "RTSP/1.0", body,
                ("CSeq", "4"), ("Content-Length", body.Length.ToString())));

            Assert.True(request.IsValid);
            Assert.Equal("4", request.Headers["CSeq"]);
            Assert.Equal(body, request.Body);
        }

        [Fact]
        public void ZeroContentLengthProducesEmptyBody()
        {
            var request = new Request(TestHelpers.BuildRequest("SETUP", "/stream", "RTSP/1.0",
                ("Content-Length", "0")));

            Assert.True(request.IsValid);
            Assert.Empty(request.Body);
        }

        [Fact]
        public void InvalidContentLengthMarksRequestInvalid()
        {
            var request = new Request(TestHelpers.BuildRequest("SETUP", "/stream", "RTSP/1.0",
                ("Content-Length", "99")));

            Assert.False(request.IsValid);
            Assert.Empty(request.Body);
        }

        [Fact]
        public void UnknownMethodMarksRequestInvalid()
        {
            var request = new Request(TestHelpers.Hex("ABCD /test RTSP/1.0"));

            Assert.False(request.IsValid);
        }

        [Fact]
        public void MissingPathMarksRequestInvalid()
        {
            Assert.False(new Request(TestHelpers.Hex("GET")).IsValid);
        }

        [Fact]
        public void UnknownProtocolMarksRequestInvalid()
        {
            Assert.False(new Request(TestHelpers.Hex("GET /test XYZ/1.0")).IsValid);
        }

        [Fact]
        public void NullHexThrows()
        {
            Assert.Throws<ArgumentNullException>(() => new Request(null));
        }

        [Fact]
        public void GetBaseResponseCopiesCSeqAndServer()
        {
            var request = new Request(TestHelpers.BuildRequest("OPTIONS", "*", "RTSP/1.0", ("CSeq", "9")));
            var response = request.GetBaseResponse();

            Assert.Equal(StatusCode.OK, response.StatusCode);
            Assert.Equal("AirTunes/220.68", response.Headers["Server"]);
            Assert.Equal("9", response.Headers["CSeq"]);
        }
    }

    public class ResponseTests
    {
        [Fact]
        public void WriteAndReadRoundTrip()
        {
            var response = new Response(ProtocolType.RTSP10);
            var data = new byte[] { 1, 2, 3, 4 };

            response.WriteAsync(data).GetAwaiter().GetResult();

            Assert.Equal(data, response.ReadAsync().GetAwaiter().GetResult());
            Assert.Equal("4", response.Headers["Content-Length"]);
        }

        [Fact]
        public void WriteWithOffsetOnlyWritesRequestedBytes()
        {
            var response = new Response(ProtocolType.HTTP11);
            response.WriteAsync(new byte[] { 9, 1, 2, 9 }, 1, 2).GetAwaiter().GetResult();

            Assert.Equal(new byte[] { 1, 2 }, response.ReadAsync().GetAwaiter().GetResult());
            Assert.Equal("2", response.Headers["Content-Length"]);
        }

        [Theory]
        [InlineData(ProtocolType.HTTP10, "HTTP/1.0")]
        [InlineData(ProtocolType.HTTP11, "HTTP/1.1")]
        [InlineData(ProtocolType.RTSP10, "RTSP/1.0")]
        public void GetProtocolReturnsText(ProtocolType protocol, string expected)
        {
            Assert.Equal(expected, new Response(protocol).GetProtocol());
        }

        [Fact]
        public void PropertiesAreSettable()
        {
            var response = new Response();
            response.Protocol = ProtocolType.RTSP10;
            response.StatusCode = StatusCode.UNAUTHORIZED;

            Assert.Equal(ProtocolType.RTSP10, response.Protocol);
            Assert.Equal(StatusCode.UNAUTHORIZED, response.StatusCode);
        }
    }

    public class MirroringHeaderTests
    {
        [Fact]
        public void ParsesPayloadTypeZeroTimeHeader()
        {
            var header = new byte[64];
            WriteUInt32(header, 0, 190);
            WriteUInt16(header, 4, 0);
            WriteUInt16(header, 6, 2);
            var ntp = (1UL << 32) | 0x80000000UL;
            WriteUInt64(header, 8, ntp);

            var parsed = new MirroringHeader(header);

            Assert.Equal(190, parsed.PayloadSize);
            Assert.Equal(0, parsed.PayloadType);
            Assert.Equal(2, parsed.PayloadOption);
            Assert.Equal((long)ntp, parsed.PayloadNtp);
            Assert.Equal(1500000, parsed.PayloadPts);
        }

        [Fact]
        public void ParsesPayloadTypeOneDimensions()
        {
            var header = new byte[64];
            WriteUInt16(header, 4, 1);
            WriteFloat(header, 40, 1920f);
            WriteFloat(header, 44, 1080f);
            WriteFloat(header, 56, 1280f);
            WriteFloat(header, 60, 720f);

            var parsed = new MirroringHeader(header);

            Assert.Equal(1, parsed.PayloadType);
            Assert.Equal(1920, parsed.WidthSource);
            Assert.Equal(1080, parsed.HeightSource);
            Assert.Equal(1280, parsed.Width);
            Assert.Equal(720, parsed.Height);
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            BitConverter.GetBytes(value).CopyTo(buffer, offset);
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            BitConverter.GetBytes(value).CopyTo(buffer, offset);
        }

        private static void WriteUInt64(byte[] buffer, int offset, ulong value)
        {
            BitConverter.GetBytes(value).CopyTo(buffer, offset);
        }

        private static void WriteFloat(byte[] buffer, int offset, float value)
        {
            BitConverter.GetBytes(value).CopyTo(buffer, offset);
        }
    }

    public class ModelTests
    {
        [Fact]
        public void SessionTracksReadiness()
        {
            var session = new Session("abc");

            Assert.Equal("abc", session.SessionId);
            Assert.False(session.PairCompleted);
            Assert.False(session.FairPlaySetupCompleted);
            Assert.False(session.FairPlayReady);
            Assert.False(session.MirroringSessionReady);
            Assert.False(session.AudioSessionReady);

            session.EcdhShared = new byte[32];
            session.PairVerified = true;
            session.KeyMsg = new byte[164];
            session.AesKey = new byte[16];
            session.AesIv = new byte[16];
            session.StreamConnectionId = "stream";
            session.MirroringSession = true;
            session.AudioFormat = AudioFormat.ALAC;

            Assert.True(session.PairCompleted);
            Assert.True(session.FairPlaySetupCompleted);
            Assert.True(session.FairPlayReady);
            Assert.True(session.MirroringSessionReady);
            Assert.True(session.AudioSessionReady);
        }

        [Fact]
        public void RaopBufferHasExpectedLength()
        {
            var buffer = new RaopBuffer();

            Assert.Equal(AudioListener.RAOP_BUFFER_LENGTH, buffer.Entries.Length);
            Assert.False(buffer.IsEmpty);
        }

        [Fact]
        public void ValueTypesStoreData()
        {
            var pcm = new PcmData { Data = new byte[] { 1 }, Length = 1, Pts = 42 };
            var frame = new H264Data { Data = new byte[] { 2 }, FrameType = 1, Length = 1, Pts = 7 };

            Assert.Equal(42UL, pcm.Pts);
            Assert.Equal(7, frame.Pts);
        }
    }
}
