using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Plists;
using System.Text;
using AirPlay.DmapTagged;
using Xunit;

namespace AirPlay.Tests
{
    public class PlistTests
    {
        private static MemoryStream Write(IDictionary dictionary)
        {
            var stream = new MemoryStream();
            new BinaryPlistWriter().WriteObject(stream, dictionary, false);
            stream.Position = 0;
            return stream;
        }

        [Fact]
        public void RoundTrip_PrimitivesAndContainers()
        {
            var date = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            var plist = new Dictionary<string, object>
            {
                { "string", "hello" },
                { "ascii", "plain" },
                { "int", 42 },
                { "negative", -7L },
                { "bool", true },
                { "boolFalse", false },
                { "double", 3.5d },
                { "float", 1.25f },
                { "data", new byte[] { 1, 2, 3, 4 } },
                { "date", date },
                { "array", new object[] { "a", 1, new Dictionary<string, object> { { "nested", "v" } } } },
                { "dict", new Dictionary<string, object> { { "k", "v" }, { "n", 2 } } },
                { "null", null }
            };

            using var stream = Write(plist);
            var result = new BinaryPlistReader().ReadObject(stream);

            Assert.Equal("hello", result["string"]);
            Assert.Equal("plain", result["ascii"]);
            Assert.Equal(42L, Convert.ToInt64(result["int"]));
            Assert.Equal(-7L, Convert.ToInt64(result["negative"]));
            Assert.Equal(true, result["bool"]);
            Assert.Equal(false, result["boolFalse"]);
            Assert.Equal(3.5d, result["double"]);
            Assert.Equal(1.25d, Convert.ToDouble(result["float"]), 5);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, result["data"]);
            Assert.IsType<DateTime>(result["date"]);

            var array = Assert.IsType<object[]>(result["array"]);
            Assert.Equal("a", array[0]);
            var nested = Assert.IsType<Dictionary<object, object>>(array[2]);
            Assert.Equal("v", nested["nested"]);

            var dict = Assert.IsType<Dictionary<object, object>>(result["dict"]);
            Assert.Equal("v", dict["k"]);
            Assert.Equal(2L, Convert.ToInt64(dict["n"]));
        }

        [Fact]
        public void RoundTrip_DeduplicatesRepeatedValues()
        {
            var plist = new Dictionary<string, object>
            {
                { "a", "same" },
                { "b", "same" },
                { "c", 11 },
                { "d", 11 },
                { "e", true },
                { "f", true }
            };

            using var stream = Write(plist);
            var result = new BinaryPlistReader().ReadObject(stream);

            Assert.Equal("same", result["a"]);
            Assert.Equal("same", result["b"]);
            Assert.Equal(11L, Convert.ToInt64(result["c"]));
            Assert.Equal(11L, Convert.ToInt64(result["d"]));
            Assert.Equal(true, result["e"]);
            Assert.Equal(true, result["f"]);
        }

        [Fact]
        public void RoundTrip_LargeArrayUsesExtendedCount()
        {
            var values = Enumerable.Range(0, 40).Select(i => (object)(long)i).ToArray();
            var plist = new Dictionary<string, object> { { "items", values } };

            using var stream = Write(plist);
            var result = new BinaryPlistReader().ReadObject(stream);

            var array = Assert.IsType<object[]>(result["items"]);
            Assert.Equal(40, array.Length);
            Assert.Equal(39L, Convert.ToInt64(array[39]));
        }

        [Fact]
        public void RoundTrip_LongStringUsesExtendedLength()
        {
            var text = new string('x', 300);
            var plist = new Dictionary<string, object> { { "text", text } };

            using var stream = Write(plist);
            var result = new BinaryPlistReader().ReadObject(stream);

            Assert.Equal(text, result["text"]);
        }

        [Fact]
        public void ReadObjectOfT_UsesPlistSerializable()
        {
            var plist = new Dictionary<string, object> { { "Name", "abc" }, { "Value", 5 } };

            using var stream = Write(plist);
            var result = new BinaryPlistReader().ReadObject<FakePlistSerializable>(stream);

            Assert.Equal("abc", result.Name);
            Assert.Equal(5L, result.Value);
        }

        [Fact]
        public void WriteObject_ValidatesArguments()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new BinaryPlistWriter().WriteObject(null, new Dictionary<string, object>(), false));
            Assert.Throws<ArgumentNullException>(() =>
                new BinaryPlistWriter().WriteObject(new MemoryStream(), (IDictionary)null, false));
            Assert.Throws<ArgumentException>(() =>
                new BinaryPlistWriter().WriteObject(new MemoryStream(new byte[4], false), new Hashtable(), false));
        }

        [Fact]
        public void ReadObject_ValidatesArgumentsAndHeader()
        {
            Assert.Throws<ArgumentNullException>(() => new BinaryPlistReader().ReadObject((Stream)null));
            Assert.Throws<ArgumentException>(() => new BinaryPlistReader().ReadObject(new MemoryStream(new byte[8])));
            Assert.Throws<ArgumentException>(() => new BinaryPlistReader().ReadObject(new MemoryStream(new byte[64])));
        }

        [Fact]
        public void ReadObject_RejectsInvalidTrailer()
        {
            var data = new byte[64];
            Encoding.ASCII.GetBytes("bplist00").CopyTo(data, 0);
            data[64 - 26] = 0;
            data[64 - 25] = 0;
            data[64 - 18] = 0;

            Assert.Throws<ArgumentException>(() =>
                new BinaryPlistReader().ReadObject(new MemoryStream(data)));
        }

        [Fact]
        public void WriteObject_CloseStreamDisposesWriter()
        {
            var stream = new MemoryStream();
            new BinaryPlistWriter().WriteObject(stream, new Dictionary<string, object> { { "a", "b" } }, true);

            Assert.False(stream.CanWrite);
        }
    }

    public sealed class FakePlistSerializable : IPlistSerializable
    {
        public string Name { get; private set; }
        public long Value { get; private set; }

        public void FromPlistDictionary(IDictionary plist)
        {
            Name = (string)plist["Name"];
            Value = Convert.ToInt64(plist["Value"]);
        }

        public IDictionary ToPlistDictionary()
        {
            return new Dictionary<string, object> { { "Name", Name }, { "Value", Value } };
        }
    }

    public class DMapTaggedTests
    {
        private static byte[] BuildTagged(params (string Tag, byte[] Data)[] entries)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            writer.Write(new byte[8]);

            foreach (var entry in entries)
            {
                writer.Write(Encoding.ASCII.GetBytes(entry.Tag));
                var len = BitConverter.GetBytes((uint)entry.Data.Length);
                Array.Reverse(len);
                writer.Write(len);
                writer.Write(entry.Data);
            }

            return stream.ToArray();
        }

        [Fact]
        public void Decode_ReadsStringIntAndByteTags()
        {
            var buffer = BuildTagged(
                ("minm", Encoding.ASCII.GetBytes("Song")),
                ("mstt", new byte[] { 0, 0, 0, 200 }),
                ("caps", new byte[] { 1 }));

            var result = new DMapTagged().Decode(buffer);

            Assert.Equal("Song", result["minm"]);
            Assert.Equal(200u, result["mstt"]);
            Assert.Equal((byte)1, result["caps"]);
        }

        [Fact]
        public void Decode_UseNameMapsToFriendlyNames()
        {
            var buffer = BuildTagged(("minm", Encoding.ASCII.GetBytes("Song")));

            var result = new DMapTagged().Decode(buffer, true);

            Assert.Equal("Song", result["dmap.itemname"]);
            Assert.False(result.ContainsKey("minm"));
        }

        [Fact]
        public void Decode_SkipsZeroLengthValues()
        {
            var buffer = BuildTagged(("minm", Array.Empty<byte>()));

            Assert.Empty(new DMapTagged().Decode(buffer));
        }

        [Fact]
        public void Decode_UnknownTagThrows()
        {
            var buffer = BuildTagged(("zzzz", new byte[] { 1 }));

            Assert.Throws<KeyNotFoundException>(() => new DMapTagged().Decode(buffer));
        }

        [Fact]
        public void ConstructorExposesTagTable()
        {
            Assert.NotNull(new DMapTagged());
        }
    }

    public class CryptoTests
    {
        [Fact]
        public void OmgHaxHex_StaticTablesLoadFromResources()
        {
            Assert.NotEmpty(OmgHaxHex.TableS1);
            Assert.NotEmpty(OmgHaxHex.TableS2);
            Assert.NotEmpty(OmgHaxHex.TableS3);
            Assert.NotEmpty(OmgHaxHex.TableS4);
            Assert.NotEmpty(OmgHaxHex.TableS5);
            Assert.NotEmpty(OmgHaxHex.TableS6);
            Assert.NotEmpty(OmgHaxHex.TableS7);
            Assert.NotEmpty(OmgHaxHex.TableS8);
            Assert.NotEmpty(OmgHaxHex.TableS9);
            Assert.NotEmpty(OmgHaxHex.TableS10);
            Assert.Equal(11, OmgHaxHex.IndexMAngle.Length);
            Assert.Equal(4, OmgHaxHex.MessageKey.Length);
            Assert.Equal(4, OmgHaxHex.MessageIv.Length);
            Assert.Equal(16, OmgHaxHex.TKey.Length);
            Assert.Equal(16, OmgHaxHex.XKey.Length);
            Assert.Equal(16, OmgHaxHex.ZKey.Length);
            Assert.NotEmpty(OmgHaxHex.DefaultSap);
            Assert.NotEmpty(new OmgHaxHex().ToString());
        }

        [Fact]
        public void SapHash_IsDeterministicAndSized()
        {
            var block = Enumerable.Range(0, 320).Select(i => (byte)i).ToArray();
            var key1 = new byte[16];
            var key2 = new byte[16];

            new SapHash().Hash(block, key1);
            new SapHash().Hash(block, key2);

            Assert.Equal(key1, key2);
            Assert.Contains(key1, b => b != 0);
        }

        [Fact]
        public void ModifiedMd5_IsDeterministicAndChangesKey()
        {
            var block = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            var keyIn = Enumerable.Range(0, 16).Select(i => (byte)(i + 1)).ToArray();
            var out1 = new byte[16];
            var out2 = new byte[16];

            new ModifiedMD5().ModifiedMd5(block, keyIn, out1);
            new ModifiedMD5().ModifiedMd5(block, keyIn, out2);

            Assert.Equal(out1, out2);
            Assert.NotEqual(keyIn, out1);
        }

        [Fact]
        public void HandGarble_MutatesBuffers()
        {
            var b0 = new byte[20];
            var b1 = Enumerable.Range(0, 210).Select(i => (byte)i).ToArray();
            var b2 = new byte[35];
            var b3 = new byte[132];
            var b4 = new byte[21];
            var original = b1.ToArray();

            new HandGarble().Garble(b0, b1, b2, b3, b4);

            Assert.NotEqual(original, b1);
            Assert.Contains(b3, b => b != 0);
        }

        [Fact]
        public void OmgHax_DecryptAesKey_ProducesDeterministicKey()
        {
            var message3 = new byte[164];
            message3[12] = 3;
            var cipherText = Enumerable.Range(0, 164).Select(i => (byte)(255 - i)).ToArray();
            var key1 = new byte[16];
            var key2 = new byte[16];

            new OmgHax().DecryptAesKey(message3, cipherText, key1);
            new OmgHax().DecryptAesKey(message3, cipherText, key2);
            message3[12] = 1;
            new OmgHax().DecryptAesKey(message3, cipherText, new byte[16]);

            Assert.Equal(key1, key2);
        }
    }
}
