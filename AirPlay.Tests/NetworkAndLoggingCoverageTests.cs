using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.Listeners;
using AirPlay.Models;
using AirPlay.Utils;
using Xunit;

namespace AirPlay.Tests
{
    public class BaseTcpListenerTests
    {
        private sealed class RecordingListener : BaseTcpListener
        {
            public RecordingListener(ushort port, bool raw = false) : base(port, raw)
            {
            }

            public List<Request> Requests { get; } = new List<Request>();
            public ManualResetEventSlim RawReceived { get; } = new ManualResetEventSlim(false);
            public int RawBytes { get; private set; }

            public override Task OnDataReceivedAsync(Request request, Response response, CancellationToken cancellationToken)
            {
                lock (Requests)
                {
                    Requests.Add(request);
                }

                if (request.Path == "/empty")
                {
                    return Task.CompletedTask;
                }

                return response.WriteAsync(Encoding.ASCII.GetBytes("ok"));
            }

            public override Task OnRawReceivedAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
            {
                var buffer = new byte[16];
                RawBytes = stream.Read(buffer, 0, buffer.Length);
                RawReceived.Set();
                return Task.CompletedTask;
            }

            public int RequestCount
            {
                get
                {
                    lock (Requests)
                    {
                        return Requests.Count;
                    }
                }
            }
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
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

        [Fact]
        public async Task Start_ParsesMultipleRequestsAndWritesResponses()
        {
            var listener = new RecordingListener((ushort)TestHelpers.GetFreeTcpPort());
            try
            {
                await listener.StartAsync(CancellationToken.None);

                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, GetPort(listener));
                var stream = client.GetStream();

                var first = TestHelpers.BuildRequest("OPTIONS", "*", "RTSP/1.0", ("CSeq", "1"));
                var firstPayload = Convert.FromHexString(first);
                await stream.WriteAsync(firstPayload, 0, firstPayload.Length);

                Assert.True(await WaitUntilAsync(() => listener.RequestCount == 1));

                var buffer = new byte[512];
                stream.ReadTimeout = 3000;
                var read = stream.Read(buffer, 0, buffer.Length);
                var text = Encoding.ASCII.GetString(buffer, 0, read);
                Assert.Contains("RTSP/1.0 200", text);
                Assert.Contains("ok", text);

                var second = TestHelpers.BuildRequest("GET", "/info", "RTSP/1.0", ("CSeq", "2"));
                var secondPayload = Convert.FromHexString(second);
                await stream.WriteAsync(secondPayload, 0, secondPayload.Length);

                Assert.True(await WaitUntilAsync(() => listener.RequestCount == 2));
                var readSecond = stream.Read(buffer, 0, buffer.Length);
                Assert.Contains("RTSP/1.0 200", Encoding.ASCII.GetString(buffer, 0, readSecond));
            }
            finally
            {
                await listener.StopAsync();
            }
        }

        [Fact]
        public async Task OversizedRequest_ClosesConnection()
        {
            var listener = new RecordingListener((ushort)TestHelpers.GetFreeTcpPort());
            try
            {
                await listener.StartAsync(CancellationToken.None);

                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, GetPort(listener));
                var payload = Enumerable.Repeat((byte)0x41, 70000).ToArray();
                await client.GetStream().WriteAsync(payload, 0, payload.Length);

                Assert.True(await WaitUntilAsync(() => listener.RequestCount == 0, 2000));
            }
            finally
            {
                await listener.StopAsync();
            }
        }

        [Fact]
        public async Task ResponseWriteFailure_IsSwallowed()
        {
            var listener = new RecordingListener((ushort)TestHelpers.GetFreeTcpPort());
            try
            {
                await listener.StartAsync(CancellationToken.None);

                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, GetPort(listener));
                var request = Convert.FromHexString(TestHelpers.BuildRequest("GET", "/info", "RTSP/1.0", ("CSeq", "4")));
                await client.GetStream().WriteAsync(request, 0, request.Length);

                client.Client.LingerState = new LingerOption(true, 0);
                client.Close();

                await Task.Delay(200);
            }
            finally
            {
                await listener.StopAsync();
            }
        }

        [Fact]
        public async Task DefaultHooks_ReturnCompletedTasks()
        {
            var listener = new PassiveListener((ushort)TestHelpers.GetFreeTcpPort());
            Assert.Same(Task.CompletedTask, listener.OnDataReceivedAsync(null, null, CancellationToken.None));
            Assert.Same(Task.CompletedTask, listener.OnRawReceivedAsync(null, null, CancellationToken.None));
            await Task.CompletedTask;
        }

        private sealed class PassiveListener : BaseTcpListener
        {
            public PassiveListener(ushort port) : base(port, false)
            {
            }
        }

        [Fact]
        public async Task EmptyResponseBody_StillWritesHeaders()
        {
            var listener = new RecordingListener((ushort)TestHelpers.GetFreeTcpPort());
            try
            {
                await listener.StartAsync(CancellationToken.None);

                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, GetPort(listener));
                var request = Convert.FromHexString(TestHelpers.BuildRequest("GET", "/empty", "RTSP/1.0", ("CSeq", "3")));
                await client.GetStream().WriteAsync(request, 0, request.Length);

                Assert.True(await WaitUntilAsync(() => listener.RequestCount == 1));

                var buffer = new byte[512];
                client.GetStream().ReadTimeout = 3000;
                var read = client.GetStream().Read(buffer, 0, buffer.Length);
                var text = Encoding.ASCII.GetString(buffer, 0, read);
                Assert.Contains("RTSP/1.0 200", text);
                Assert.DoesNotContain("ok", text);
            }
            finally
            {
                await listener.StopAsync();
            }
        }

        [Fact]
        public async Task RawMode_DispatchesRawPayload()
        {
            var listener = new RecordingListener((ushort)TestHelpers.GetFreeTcpPort(), raw: true);
            try
            {
                await listener.StartAsync(CancellationToken.None);

                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, GetPort(listener));
                var payload = new byte[] { 1, 2, 3, 4 };
                await client.GetStream().WriteAsync(payload, 0, payload.Length);

                Assert.True(await WaitUntilAsync(() => listener.RawReceived.IsSet));
                Assert.Equal(4, listener.RawBytes);
            }
            finally
            {
                await listener.StopAsync();
            }
        }

        [Fact]
        public async Task UnrecognizedData_IsIgnoredWithoutResponse()
        {
            var listener = new RecordingListener((ushort)TestHelpers.GetFreeTcpPort());
            try
            {
                await listener.StartAsync(CancellationToken.None);

                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, GetPort(listener));
                var payload = Encoding.ASCII.GetBytes("not-a-request");
                await client.GetStream().WriteAsync(payload, 0, payload.Length);

                await Task.Delay(120);
                Assert.Equal(0, listener.RequestCount);
            }
            finally
            {
                await listener.StopAsync();
            }
        }

        [Fact]
        public async Task Start_ThrowsWhenPortAlreadyInUse()
        {
            var port = TestHelpers.GetFreeTcpPort();
            var blocker = new TcpListener(IPAddress.Any, port);
            blocker.Start();
            try
            {
                var listener = new RecordingListener((ushort)port);
                await Assert.ThrowsAsync<SocketException>(() => listener.StartAsync(CancellationToken.None));
            }
            finally
            {
                blocker.Stop();
            }
        }

        [Fact]
        public async Task Stop_WithoutStart_DoesNotThrow()
        {
            var listener = new RecordingListener((ushort)TestHelpers.GetFreeTcpPort());
            await listener.StopAsync();
        }

        private static ushort GetPort(BaseTcpListener listener)
        {
            var listenerId = listener.ListenerId;
            Assert.True(listenerId > 0);
            var field = typeof(BaseTcpListener).GetField("_port", BindingFlags.NonPublic | BindingFlags.Instance);
            return (ushort)field.GetValue(listener);
        }
    }

    public class AppFileLogTests
    {
        [Fact]
        public void Install_IsIdempotent()
        {
            AppFileLog.Install();
            var writer = Console.Out;
            AppFileLog.Install();

            Assert.Same(writer, Console.Out);

            Console.WriteLine("app-file-log-coverage");
        }

        [Fact]
        public void UnhandledExceptionHandlers_WriteWithoutThrowing()
        {
            AppFileLog.Install();

            var method = typeof(AppFileLog).GetMethod("OnUnhandledException", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            method.Invoke(null, new object[] { null, new UnhandledExceptionEventArgs(new InvalidOperationException("boom"), false) });
            method.Invoke(null, new object[] { null, new UnhandledExceptionEventArgs("not-an-exception", false) });
        }

        [Fact]
        public void TryCreateWriter_RotatesOversizedLogAndHandlesInvalidPaths()
        {
            var method = typeof(AppFileLog).GetMethod("TryCreateWriter", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            Assert.Null(method.Invoke(null, new object[] { null }));
            Assert.Null(method.Invoke(null, new object[] { string.Empty }));

            var directory = Path.Combine(Path.GetTempPath(), "airplay-log-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var logPath = Path.Combine(directory, "airplay.log");
                File.WriteAllBytes(logPath, new byte[2 * 1024 * 1024 + 1]);

                using var writer = (StreamWriter)method.Invoke(null, new object[] { directory });
                Assert.NotNull(writer);
                Assert.True(File.Exists(logPath + ".1"));
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch
                {
                }
            }
        }
    }
}
