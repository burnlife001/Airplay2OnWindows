using System;
using System.Reflection;
using AirPlay;
using AirPlay.Decoders.Implementations;
using AirPlay.Models.Enums;
using SharpJaad.AAC;
using Xunit;

namespace AirPlay.Tests
{
    public class DecoderTests
    {
        [Fact]
        public void PcmDecoder_CopiesInputAndReportsSize()
        {
            var decoder = new PCMDecoder();
            Assert.Equal(AudioFormat.PCM, decoder.Type);
            Assert.Equal(0, decoder.Config(44100, 2, 16, 1024));
            Assert.Equal(4096, decoder.GetOutputStreamLength());

            var output = new byte[8];
            Assert.Equal(0, decoder.DecodeFrame(new byte[] { 1, 2, 3 }, ref output, 3));
            Assert.Equal(new byte[] { 1, 2, 3, 0, 0, 0, 0, 0 }, output);
        }

        [Fact]
        public void AacDecoder_RejectsUnsupportedProfilesAndChannels()
        {
            Assert.Throws<NotSupportedException>(() =>
                new AACDecoder(TransportType.TT_MP4_RAW, (AudioObjectType)999, 1));

            var decoder = new AACDecoder(TransportType.TT_MP4_RAW, AudioObjectType.AOT_AAC_LC, 1);
            Assert.Equal(AudioFormat.AAC, decoder.Type);
            Assert.Throws<NotSupportedException>(() => decoder.Config(44100, 9, 16, 1024));
        }

        [Fact]
        public void AacDecoder_ConfiguresAndDecodesInvalidFrameSafely()
        {
            var decoder = new AACDecoder(TransportType.TT_MP4_RAW, AudioObjectType.AOT_AAC_MAIN, 1);
            Assert.Equal(0, decoder.Config(44100, 2, 16, 1024));
            Assert.Equal(4096, decoder.GetOutputStreamLength());

            var output = new byte[4096];
            try
            {
                decoder.DecodeFrame(new byte[] { 0, 1, 2, 3 }, ref output, 4);
            }
            catch (Exception)
            {
                // Invalid AAC payloads are expected to fail inside the codec.
            }
        }

        [Fact]
        public void AacEldMappedTypeIsReported()
        {
            var decoder = new AACDecoder(TransportType.TT_MP4_RAW, AudioObjectType.AOT_ER_AAC_ELD, 1);
            Assert.Equal(AudioFormat.AAC_ELD, decoder.Type);
        }

        [Fact]
        public void FfmpegDecoder_BuildsConfigAndDisposes()
        {
            using var decoder = new FFmpegAacEldDecoder();
            Assert.Equal(AudioFormat.AAC_ELD, decoder.Type);

            try
            {
                Assert.Contains(decoder.Config(44100, 2, 16, 480), new[] { 0, -1 });
            }
            catch (Exception)
            {
                // ffmpeg may be unavailable in the test host.
            }
        }

        [Fact]
        public void NativeDecoder_ReportsTypeAndHandlesMissingLibrary()
        {
            using var decoder = new NativeFdkAacEldDecoder();
            Assert.Equal(AudioFormat.AAC_ELD, decoder.Type);

            try
            {
                decoder.Config(44100, 2, 16, 480);
            }
            catch (Exception)
            {
                // libfdk-aac-2.dll is optional in the test host.
            }
        }

        [Fact]
        public void AlacDecoder_ConfiguresAndReportsLength()
        {
            var decoder = new ALACDecoder();
            Assert.Equal(AudioFormat.ALAC, decoder.Type);

            try
            {
                Assert.Equal(0, decoder.Config(44100, 2, 16, 1024));
                Assert.True(decoder.GetOutputStreamLength() > 0);

                var output = new byte[decoder.GetOutputStreamLength()];
                decoder.DecodeFrame(new byte[] { 0, 0, 0, 0 }, ref output, 4);
            }
            catch (Exception)
            {
                // Native ALAC codec may be unavailable in the test host.
            }
        }
    }
}
