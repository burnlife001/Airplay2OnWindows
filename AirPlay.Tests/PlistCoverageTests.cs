using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Plists;
using System.Text;
using Xunit;

namespace AirPlay.Tests
{
    public class PlistTypeCoverageTests
    {
        private static MemoryStream Write(IDictionary dictionary)
        {
            var stream = new MemoryStream();
            new BinaryPlistWriter().WriteObject(stream, dictionary, false);
            stream.Position = 0;
            return stream;
        }

        [Fact]
        public void RoundTrip_AllScalarTypeCodes()
        {
            var plist = new Dictionary<string, object>
            {
                { "byte", (byte)5 },
                { "sbyte", (sbyte)-6 },
                { "char", 'A' },
                { "short", (short)-1234 },
                { "ushort", (ushort)4321 },
                { "uint", 4000000000u },
                { "decimal", 1.5m },
                { "single", 2.5f },
                { "double", 7.25d },
                { "dbnull", DBNull.Value },
                { "enum", SampleEnum.Second },
                { "unicodeShort", "héllo" },
                { "unicodeLong", new string('é', 20) },
                { "asciiLong", new string('z', 20) },
                { "dataLong", Enumerable.Range(0, 20).Select(i => (byte)i).ToArray() },
                { "list", new List<int> { 1, 2, 3 } },
                { "serializable", new SettablePlistSerializable { Name = "n" } }
            };

            using var stream = Write(plist);
            var result = new BinaryPlistReader().ReadObject(stream);

            Assert.Equal(5L, Convert.ToInt64(result["byte"]));
            Assert.Equal(-6L, Convert.ToInt64(result["sbyte"]));
            Assert.Equal((long)'A', Convert.ToInt64(result["char"]));
            Assert.Equal(-1234L, Convert.ToInt64(result["short"]));
            Assert.Equal(4321L, Convert.ToInt64(result["ushort"]));
            Assert.Equal(4000000000L, Convert.ToInt64(result["uint"]));
            Assert.Equal(1.5d, Convert.ToDouble(result["decimal"]));
            Assert.Equal(2.5d, Convert.ToDouble(result["single"]));
            Assert.Equal(7.25d, Convert.ToDouble(result["double"]));
            Assert.Null(result["dbnull"]);
            Assert.Equal(1L, Convert.ToInt64(result["enum"]));
            Assert.Equal("héllo", result["unicodeShort"]);
            Assert.Equal(new string('é', 20), result["unicodeLong"]);
            Assert.Equal(new string('z', 20), result["asciiLong"]);
            Assert.Equal(20, ((byte[])result["dataLong"]).Length);
            Assert.Equal(3, ((object[])result["list"]).Length);
            Assert.Equal("n", ((Dictionary<object, object>)result["serializable"])["Name"]);
        }

        [Fact]
        public void RoundTrip_ExtendedDictionaryAndArrayCounts()
        {
            var bigDict = new Dictionary<string, object>();
            for (int i = 0; i < 20; i++)
            {
                bigDict["k" + i] = i;
            }

            var plist = new Dictionary<string, object>
            {
                { "bigDict", bigDict },
                { "bigArray", Enumerable.Range(0, 20).Cast<object>().ToArray() }
            };

            using var stream = Write(plist);
            var result = new BinaryPlistReader().ReadObject(stream);

            Assert.Equal(20, ((Dictionary<object, object>)result["bigDict"]).Count);
            Assert.Equal(20, ((object[])result["bigArray"]).Length);
        }

        [Fact]
        public void RoundTrip_HugeStringUsesWideOffsets()
        {
            var text = new string('a', 40000);
            var plist = new Dictionary<string, object> { { "text", text } };

            using var stream = Write(plist);
            var result = new BinaryPlistReader().ReadObject(stream);

            Assert.Equal(text, result["text"]);
        }

        [Fact]
        public void WriteAndRead_FileOverloads()
        {
            var path = Path.Combine(Path.GetTempPath(), "plist-" + Guid.NewGuid().ToString("N") + ".plist");
            try
            {
                new BinaryPlistWriter().WriteObject(path, new Dictionary<string, object> { { "a", "b" } });
                Assert.Equal("b", new BinaryPlistReader().ReadObject(path)["a"]);

                new BinaryPlistWriter().WriteObject(path, new SettablePlistSerializable { Name = "x" });
                Assert.Equal("x", new BinaryPlistReader().ReadObject<SettablePlistSerializable>(path).Name);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Fact]
        public void Write_RejectsUnsupportedValues()
        {
            var uint64 = new Dictionary<string, object> { { "big", ulong.MaxValue } };
            Assert.Throws<InvalidOperationException>(() => new BinaryPlistWriter().WriteObject(new MemoryStream(), uint64, false));

            var unsupported = new Dictionary<string, object> { { "o", new NonSerializableType() } };
            Assert.Throws<InvalidOperationException>(() => new BinaryPlistWriter().WriteObject(new MemoryStream(), unsupported, false));

            Assert.Throws<ArgumentNullException>(() =>
                new BinaryPlistWriter().WriteObject(new MemoryStream(), (IPlistSerializable)null, false));
        }

        [Fact]
        public void Write_SerializablePayloadFailsOnThisRuntime()
        {
            var plist = new Dictionary<string, object> { { "s", new SimpleSerializable { X = 1 } } };
            Assert.Throws<PlatformNotSupportedException>(() => new BinaryPlistWriter().WriteObject(new MemoryStream(), plist, false));
        }

        [Fact]
        public void Read_RejectsNonDictionaryRoot()
        {
            Assert.Throws<InvalidOperationException>(() =>
                new BinaryPlistReader().ReadObject(new MemoryStream(BuildArrayRootPlist())));
        }

        [Fact]
        public void Read_NonSeekableStreamIsCopied()
        {
            using var stream = Write(new Dictionary<string, object> { { "a", 1 } });
            var bytes = stream.ToArray();
            var nonSeekable = new NonSeekableStream(bytes);

            Assert.Throws<NotSupportedException>(() => new BinaryPlistReader().ReadObject(nonSeekable));
        }

        [Fact]
        public void Read_RejectsUnreadableStream()
        {
            Assert.Throws<ArgumentException>(() =>
                new BinaryPlistReader().ReadObject(new MemoryStream(new byte[64], false)));
        }

        private static byte[] BuildArrayRootPlist()
        {
            using var mem = new MemoryStream();
            mem.Write(Encoding.ASCII.GetBytes("bplist00"), 0, 8);
            mem.WriteByte(0xA0);
            mem.WriteByte(0x08);
            mem.Write(new byte[6], 0, 6);
            mem.WriteByte(1);
            mem.WriteByte(1);
            mem.Write(Be64(1), 0, 8);
            mem.Write(Be64(0), 0, 8);
            mem.Write(Be64(9), 0, 8);
            return mem.ToArray();
        }

        private static byte[] Be64(long value)
        {
            var buffer = BitConverter.GetBytes(value);
            Array.Reverse(buffer);
            return buffer;
        }

        private sealed class NonSeekableStream : Stream
        {
            private readonly MemoryStream _inner;

            public NonSeekableStream(byte[] data)
            {
                _inner = new MemoryStream(data);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;

            public override long Position
            {
                get => 0;
                set => throw new NotSupportedException();
            }

            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    [Serializable]
    public class SimpleSerializable
    {
        public int X;
    }

    public class NonSerializableType
    {
    }

    public sealed class SettablePlistSerializable : IPlistSerializable
    {
        public string Name { get; set; }
        public long Value { get; set; }

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

    public class ExtensionsCoverageTests
    {
        [Fact]
        public void GetConcreteTypeIfNullable_HandlesNullableAndPlainTypes()
        {
            Assert.Throws<ArgumentNullException>(() => ((Type)null).GetConcreteTypeIfNullable());
            Assert.Equal(typeof(int), typeof(int?).GetConcreteTypeIfNullable());
            Assert.Equal(typeof(int), typeof(int).GetConcreteTypeIfNullable());
        }

        [Fact]
        public void IsAscii_DetectsNonAscii()
        {
            Assert.True(((string)null).IsAscii());
            Assert.True(string.Empty.IsAscii());
            Assert.True("plain".IsAscii());
            Assert.False("héllo".IsAscii());
        }

        [Fact]
        public void IsCollection_ExcludesStringsAndByteArrays()
        {
            Assert.Throws<ArgumentNullException>(() => ((Type)null).IsCollection());
            Assert.True(typeof(int[]).IsCollection());
            Assert.True(typeof(List<int>).IsCollection());
            Assert.False(typeof(string).IsCollection());
            Assert.False(typeof(byte[]).IsCollection());
            Assert.False(typeof(int).IsCollection());
        }

        [Fact]
        public void IsDefaultValue_CoversTypeCodes()
        {
            Assert.Throws<ArgumentNullException>(() => ((Type)null).IsDefaultValue(0));
            Assert.Throws<ArgumentException>(() => typeof(int).IsDefaultValue(null));
            Assert.Throws<ArgumentException>(() => typeof(int).IsDefaultValue("x"));
            Assert.Throws<InvalidCastException>(() => typeof(int).IsDefaultValue(0));
            Assert.Throws<InvalidCastException>(() => typeof(float).IsDefaultValue(0f));
            Assert.Throws<InvalidCastException>(() => typeof(decimal).IsDefaultValue(0m));

            Assert.True(typeof(bool).IsDefaultValue(false));
            Assert.False(typeof(bool).IsDefaultValue(true));
            Assert.True(typeof(long).IsDefaultValue(0L));
            Assert.False(typeof(long).IsDefaultValue(1L));
            Assert.True(typeof(DateTime).IsDefaultValue(DateTime.MinValue));
            Assert.True(typeof(DBNull).IsDefaultValue(DBNull.Value));
            Assert.True(typeof(double).IsDefaultValue(0d));
            Assert.False(typeof(object).IsDefaultValue(new object()));
            Assert.False(typeof(SimpleSerializable).IsDefaultValue(new SimpleSerializable()));
        }

        [Fact]
        public void IsPrimitiveOrEnum_CoversSpecialTypes()
        {
            Assert.Throws<ArgumentNullException>(() => ((Type)null).IsPrimitiveOrEnum());
            Assert.True(typeof(int).IsPrimitiveOrEnum());
            Assert.True(typeof(SampleEnum).IsPrimitiveOrEnum());
            Assert.True(typeof(Guid).IsPrimitiveOrEnum());
            Assert.True(typeof(TimeSpan).IsPrimitiveOrEnum());
            Assert.True(typeof(byte[]).IsPrimitiveOrEnum());
            Assert.True(typeof(Uri).IsPrimitiveOrEnum());
            Assert.True(typeof(int?).IsPrimitiveOrEnum());
            Assert.False(typeof(SimpleSerializable).IsPrimitiveOrEnum());
        }

        [Fact]
        public void ToBinaryString_FormatsValues()
        {
            Assert.Equal("101", ((byte)5).ToBinaryString());
            Assert.Equal("1010", 10.ToBinaryString());
        }
    }

    public class EndianConverterTests
    {
        [Fact]
        public void Swap_AllWidths()
        {
            Assert.Equal((short)0x0201, ((short)0x0102).SwapEndian());
            Assert.Equal((ushort)0x0201, ((ushort)0x0102).SwapEndian());
            Assert.Equal(0x04030201, 0x01020304.SwapEndian());
            Assert.Equal(0x04030201u, 0x01020304u.SwapEndian());
            Assert.Equal(0x0807060504030201L, 0x0102030405060708L.SwapEndian());
            Assert.Equal(0x0807060504030201UL, 0x0102030405060708UL.SwapEndian());
        }

        [Fact]
        public void ConditionalHelpers_MatchLittleEndian()
        {
            Assert.Equal(EndianConverter.SwapInt16((short)0x0102), ((short)0x0102).ToBigEndianConditional());
            Assert.Equal(EndianConverter.SwapUInt16((ushort)0x0102), ((ushort)0x0102).ToBigEndianConditional());
            Assert.Equal(EndianConverter.SwapInt32(0x01020304), 0x01020304.ToBigEndianConditional());
            Assert.Equal(EndianConverter.SwapUInt32(0x01020304u), 0x01020304u.ToBigEndianConditional());
            Assert.Equal(EndianConverter.SwapInt64(0x0102030405060708L), 0x0102030405060708L.ToBigEndianConditional());
            Assert.Equal(EndianConverter.SwapUInt64(0x0102030405060708UL), 0x0102030405060708UL.ToBigEndianConditional());
        }
    }

    public class PlistContainerCoverageTests
    {
        [Fact]
        public void Array_ToArrayAndToString_HandleSelfAndInvalidReferences()
        {
            var table = new List<BinaryPlistItem>
            {
                new BinaryPlistItem("a"),
                new BinaryPlistItem(1)
            };
            var array = new BinaryPlistArray(table);
            var self = new BinaryPlistItem(array);
            table.Add(self);
            array.ObjectReference.Add(0);
            array.ObjectReference.Add(-1);
            array.ObjectReference.Add(99);
            array.ObjectReference.Add(2);

            var result = array.ToArray();
            Assert.Equal("a", result[0]);
            Assert.Null(result[1]);
            Assert.Null(result[2]);
            Assert.Null(result[3]);

            array.ObjectReference.RemoveAt(1);
            var text = array.ToString();
            Assert.Contains("*2", text);
            Assert.StartsWith("[", text);
        }

        [Fact]
        public void Dictionary_ToDictionaryAndToString_HandleSelfAndInvalidReferences()
        {
            var table = new List<BinaryPlistItem>
            {
                new BinaryPlistItem("key"),
                new BinaryPlistItem("value")
            };
            var dict = new BinaryPlistDictionary(table, 2);
            var self = new BinaryPlistItem(dict);
            table.Add(self);

            dict.KeyReference.Add(0);
            dict.ObjectReference.Add(1);
            dict.KeyReference.Add(-1);
            dict.ObjectReference.Add(99);
            dict.KeyReference.Add(2);
            dict.ObjectReference.Add(2);

            var result = dict.ToDictionary();
            Assert.Equal("value", result["key"]);
            Assert.Single(result);

            var text = dict.ToString();
            Assert.Contains("#-1", text);
            Assert.Contains("#99", text);
            Assert.Contains("*2", text);
        }
    }

    public class PlistReaderEdgeCaseTests
    {
        private static byte[] BuildSingleObjectPlist(byte[] objectBytes, byte offsetIntSize = 1)
        {
            using var mem = new MemoryStream();
            mem.Write(Encoding.ASCII.GetBytes("bplist00"), 0, 8);
            mem.Write(objectBytes, 0, objectBytes.Length);
            var offsetTableOffset = mem.Length;
            for (int i = offsetIntSize - 1; i >= 0; i--)
            {
                mem.WriteByte((byte)(8 >> (8 * i)));
            }

            var trailer = new byte[32];
            trailer[6] = offsetIntSize;
            trailer[7] = 1;
            WriteBe64(trailer, 8, 1);
            WriteBe64(trailer, 16, 0);
            WriteBe64(trailer, 24, offsetTableOffset);
            mem.Write(trailer, 0, trailer.Length);
            return mem.ToArray();
        }

        private static void WriteBe64(byte[] target, int offset, long value)
        {
            var buffer = BitConverter.GetBytes(value);
            Array.Reverse(buffer);
            Array.Copy(buffer, 0, target, offset, 8);
        }

        [Fact]
        public void Read_UniqueIdValue_ReturnsCfuIdDictionary()
        {
            using var mem = new MemoryStream();
            mem.Write(Encoding.ASCII.GetBytes("bplist00"), 0, 8);
            mem.Write(new byte[] { 0xD1, 0x01, 0x02 }, 0, 3);
            mem.Write(new byte[] { 0x51, 0x41 }, 0, 2);
            mem.Write(new byte[] { 0x80, 0x2A }, 0, 2);

            var trailer = new byte[32];
            trailer[6] = 1;
            trailer[7] = 1;
            WriteBe64(trailer, 8, 3);
            WriteBe64(trailer, 16, 0);
            WriteBe64(trailer, 24, 15);
            mem.Write(new byte[] { 8, 11, 13 }, 0, 3);
            mem.Write(trailer, 0, trailer.Length);

            mem.Position = 0;
            var result = new BinaryPlistReader().ReadObject(mem);

            var uid = (System.Collections.IDictionary)result["A"];
            Assert.Equal(42UL, Convert.ToUInt64(uid["CF$UID"]));
        }

        [Fact]
        public void Read_RejectsInvalidMarkersAndSizes()
        {
            Assert.Throws<InvalidOperationException>(() =>
                new BinaryPlistReader().ReadObject(new MemoryStream(BuildSingleObjectPlist(new byte[] { 0x05 }))));

            Assert.Throws<InvalidOperationException>(() =>
                new BinaryPlistReader().ReadObject(new MemoryStream(BuildSingleObjectPlist(new byte[] { 0x21, 0, 0 }))));

            Assert.Throws<InvalidOperationException>(() =>
                new BinaryPlistReader().ReadObject(new MemoryStream(BuildSingleObjectPlist(new byte[] { 0x14, 0, 0 }))));

            Assert.Throws<InvalidOperationException>(() =>
                new BinaryPlistReader().ReadObject(new MemoryStream(BuildSingleObjectPlist(new byte[] { 0xA0 }, 3))));
        }

        [Fact]
        public void Read_RejectsUnreadableStream()
        {
            Assert.Throws<ArgumentException>(() =>
                new BinaryPlistReader().ReadObject(new WriteOnlyStream()));
        }

        private sealed class WriteOnlyStream : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => true;
            public override bool CanWrite => true;
            public override long Length => 64;
            public override long Position { get; set; }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => offset;
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }

    public enum SampleEnum
    {
        First = 0,
        Second = 1
    }
}
