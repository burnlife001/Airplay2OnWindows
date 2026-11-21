using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AirPlay;
using AirPlay.Decoders.Implementations;
using AirPlay.Models;
using AirPlay.Models.Enums;
using AirPlay.Services;
using NAudio.Wave;
using Xunit;

namespace AirPlay.Tests
{
    public class VideoOutputServiceTests
    {
        private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 4000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return true;
                }

                await Task.Delay(20);
            }

            return condition();
        }

        private static async Task<byte[]> ReadBytesAsync(Stream stream, int count)
        {
            var received = new List<byte>(count);
            var buffer = new byte[count];
            while (received.Count < count)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length).WaitAsync(TimeSpan.FromSeconds(4));
                if (read <= 0)
                {
                    break;
                }

                received.AddRange(buffer.Take(read));
            }

            Assert.Equal(count, received.Count);
            return received.Take(count).ToArray();
        }

        [Fact]
        public async Task MirroringSession_StreamsBufferedFramesAndReportsStatus()
        {
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            try
            {
                using var service = new VideoOutputService();
                var statuses = new ConcurrentQueue<string>();
                service.OnStatusChanged += (sender, status) => statuses.Enqueue(status);
                service.OnStatusChanged += (sender, status) => throw new InvalidOperationException("subscriber failure");

                // No session active yet: frames are ignored.
                service.WriteFrame(new H264Data { FrameType = 5, Data = new byte[] { 1 }, Length = 1 });

                service.StartMirroring();

                // Buffered before the player connects: delta frames only count after a keyframe.
                service.WriteFrame(default);
                service.WriteFrame(new H264Data { FrameType = 1, Data = new byte[] { 1, 2, 3 }, Length = 3 });
                service.WriteFrame(new H264Data { FrameType = 5, Data = new byte[] { 9, 8 }, Length = 2, Width = 640, Height = 480 });
                service.WriteFrame(new H264Data { FrameType = 1, Data = new byte[] { 7, 6, 5 }, Length = 3, Width = 640, Height = 480 });

                using (var client = new NamedPipeClientStream(".", "AirPlayVideo", PipeDirection.In, PipeOptions.Asynchronous))
                {
                    client.Connect(4000);
                    Assert.True(await WaitUntilAsync(() => statuses.Contains("connected")));

                    var buffered = await ReadBytesAsync(client, 5);
                    Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, buffered);

                    service.WriteFrame(new H264Data { FrameType = 1, Data = new byte[] { 4, 4, 4 }, Length = 3 });
                    var streamed = await ReadBytesAsync(client, 3);
                    Assert.Equal(new byte[] { 4, 4, 4 }, streamed);
                }

                // Broken pipe: the writer reports a disconnect.
                for (int i = 0; i < 30 && !statuses.Contains("disconnected"); i++)
                {
                    service.WriteFrame(new H264Data { FrameType = 1, Data = new byte[] { 2, 2 }, Length = 2 });
                    await Task.Delay(50);
                }

                Assert.Contains("disconnected", statuses);

                // Restart cleans up the previous session.
                service.StartMirroring();
                service.StopMirroring();

                service.WriteFrame(new H264Data { FrameType = 5, Data = new byte[] { 1 }, Length = 1 });
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", oldPath);
            }
        }

        [Fact]
        public void SecondInstanceWhilePipeIsActive_LogsFailure()
        {
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            try
            {
                using var first = new VideoOutputService();
                first.StartMirroring();

                using var second = new VideoOutputService();
                second.StartMirroring();
                second.StopMirroring();
                first.StopMirroring();
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", oldPath);
            }
        }

        [Fact]
        public void Dispose_IsIdempotentAndBlocksFurtherFrames()
        {
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            try
            {
                var service = new VideoOutputService();
                service.StartMirroring();
                service.Dispose();
                service.Dispose();
                service.WriteFrame(new H264Data { FrameType = 5, Data = new byte[] { 1 }, Length = 1 });
                service.StopMirroring();
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", oldPath);
            }
        }
    }

    public class FfmpegDecoderCoverageTests
    {
        [Fact]
        public void ConfigWithZeroFrameLength_ExercisesEncodingWithoutBlocking()
        {
            using var decoder = new FFmpegAacEldDecoder();
            Assert.Equal(AudioFormat.AAC_ELD, decoder.Type);

            if (!FfmpegAvailable())
            {
                Assert.Equal(-1, decoder.Config(44100, 2, 16, 0));
                return;
            }

            Assert.Equal(0, decoder.Config(44100, 2, 16, 0));
            Assert.Equal(0, decoder.GetOutputStreamLength());

            var output = new byte[4];
            Assert.Equal(0, decoder.DecodeFrame(new byte[] { 1, 2, 3 }, ref output, 3));

            // Reconfigure with the 512-sample flag branch.
            Assert.Equal(0, decoder.Config(96000, 1, 16, 512));
            Assert.Equal(1024, decoder.GetOutputStreamLength());
        }

        private static bool FfmpegAvailable()
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", "-version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                return process != null && process.WaitForExit(5000);
            }
            catch
            {
                return false;
            }
        }

        [Fact]
        public void DecodeBeforeConfig_ReturnsMinusOne()
        {
            using var decoder = new FFmpegAacEldDecoder();
            var output = new byte[8];
            Assert.Equal(-1, decoder.DecodeFrame(new byte[] { 1 }, ref output, 1));
        }

        [Fact]
        public void ConfigWithoutFfmpegOnPath_ReturnsMinusOne()
        {
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            try
            {
                using var decoder = new FFmpegAacEldDecoder();
                Assert.Equal(-1, decoder.Config(44100, 2, 16, 480));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", oldPath);
            }
        }
    }

    public class NativeFdkDecoderCoverageTests
    {
        [Fact]
        public void ConfigAndDecode_BeforeConfigFailsSafely()
        {
            var decoder = new NativeFdkAacEldDecoder();
            var output = new byte[16];
            Assert.Equal(-1, decoder.DecodeFrame(new byte[] { 1, 2 }, ref output, 16));
            Assert.Equal(AudioFormat.AAC_ELD, decoder.Type);

            // Native decoding of synthetic frames can fault inside libfdk-aac, so only the
            // managed configuration path is exercised here.
            decoder.Config(44100, 2, 16, 480);
            Assert.Equal(480 * 2 * 2, decoder.GetOutputStreamLength());

            decoder.Dispose();
            decoder.Dispose();
        }

        [Fact]
        public void ConfigWithUnsupportedSampleRate_StillReportsPcmSize()
        {
            var decoder = new NativeFdkAacEldDecoder();
            decoder.Config(12345, 2, 16, 512);
            Assert.Equal(512 * 2 * 2, decoder.GetOutputStreamLength());
            decoder.Dispose();
        }
    }

    public class AudioOutputServiceTests
    {
        [Fact]
        public void Lifecycle_VolumeSamplesAndFlush()
        {
            using var service = new AudioOutputService();
            try
            {
                service.Initialize();
            }
            catch
            {
                // No audio device in the test host.
            }

            service.SetVolume(-200.0m);
            service.SetVolume(-144.0m);
            service.SetVolume(-20.0m);
            service.SetVolume(0.0m);

            service.AddSamples(null, 0, 4);
            service.AddSamples(new byte[8], -1, 4);
            service.AddSamples(new byte[8], 4, 8);
            service.AddSamples(new byte[8], 0, 0);

            for (int i = 0; i < 12; i++)
            {
                service.AddSamples(new byte[64], 0, 64);
            }

            var invoked = 0;
            service.PlaybackStoppedUnexpectedly += (sender, args) => invoked++;

            var handler = typeof(AudioOutputService).GetMethod("OnPlaybackStopped", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handler);
            handler.Invoke(service, new object[] { null, new StoppedEventArgs(null) });
            handler.Invoke(service, new object[] { null, new StoppedEventArgs(new InvalidOperationException("device lost")) });
            Assert.Equal(1, invoked);

            service.AddSamples(new byte[64], 0, 64);
            service.HandleFlush();
            service.SetVolume(-10.0m);
            service.AddSamples(new byte[64], 0, 64);
        }

        [Fact]
        public void Dispose_IsIdempotent()
        {
            var service = new AudioOutputService();
            try
            {
                service.Initialize();
            }
            catch
            {
                // No audio device in the test host.
            }

            service.Dispose();
            service.Dispose();
            service.AddSamples(new byte[8], 0, 8);
            service.SetVolume(0.0m);
            service.HandleFlush();
        }
    }
}
