using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.Models;

namespace AirPlay.Tests
{
    internal static class TestHelpers
    {
        public static string Hex(string value)
        {
            return Convert.ToHexString(Encoding.ASCII.GetBytes(value));
        }

        public static string BuildRequest(string method, string path, string protocol,
            params (string Name, string Value)[] headers)
        {
            var sb = new StringBuilder();
            sb.Append(Hex($"{method} {path} {protocol}"));

            foreach (var header in headers)
            {
                sb.Append("0D0A").Append(Hex($"{header.Name}: {header.Value}"));
            }

            sb.Append("0D0A0D0A");
            return sb.ToString();
        }

        public static string BuildRequestWithBody(string method, string path, string protocol, byte[] body,
            params (string Name, string Value)[] headers)
        {
            return BuildRequest(method, path, protocol, headers) + Convert.ToHexString(body);
        }

        public static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public static int GetFreeUdpPort()
        {
            using (var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                return ((IPEndPoint)client.Client.LocalEndPoint).Port;
            }
        }
    }

    internal sealed class FakeRtspReceiver : IRtspReceiver
    {
        public List<decimal> Volumes { get; } = new List<decimal>();
        public List<H264Data> Frames { get; } = new List<H264Data>();
        public List<PcmData> Pcm { get; } = new List<PcmData>();
        public int FlushCount;
        public int MirroringStartedCount;
        public int MirroringStoppedCount;

        public void OnSetVolume(decimal volume) => Volumes.Add(volume);

        public void OnData(H264Data data) => Frames.Add(data);

        public void OnPCMData(PcmData data) => Pcm.Add(data);

        public void OnAudioFlush() => Interlocked.Increment(ref FlushCount);

        public void OnMirroringStarted() => Interlocked.Increment(ref MirroringStartedCount);

        public void OnMirroringStopped() => Interlocked.Increment(ref MirroringStoppedCount);
    }

    internal sealed class FakeAirPlayReceiver : IAirPlayReceiver, IDisposable
    {
        public event EventHandler<decimal> OnSetVolumeReceived;
        public event EventHandler<H264Data> OnH264DataReceived;
        public event EventHandler<PcmData> OnPCMDataReceived;
        public event EventHandler OnAudioFlushReceived;
        public event EventHandler OnMirroringStartedReceived;
        public event EventHandler OnMirroringStoppedReceived;

        public int StartListenersCount;
        public int StartMdnsCount;
        public int StopListenersCount;
        public int DisposeCount;
        public Exception StopListenersException;

        public Task StartListeners(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StartListenersCount);
            return Task.CompletedTask;
        }

        public Task StartMdnsAsync()
        {
            Interlocked.Increment(ref StartMdnsCount);
            return Task.CompletedTask;
        }

        public Task StopListenersAsync()
        {
            Interlocked.Increment(ref StopListenersCount);
            if (StopListenersException != null)
            {
                throw StopListenersException;
            }

            return Task.CompletedTask;
        }

        public void RaiseVolume(decimal value) => OnSetVolumeReceived?.Invoke(this, value);

        public void RaiseFrame(H264Data data) => OnH264DataReceived?.Invoke(this, data);

        public void RaisePcm(PcmData data) => OnPCMDataReceived?.Invoke(this, data);

        public void RaiseAudioFlush() => OnAudioFlushReceived?.Invoke(this, EventArgs.Empty);

        public void RaiseMirroringStarted() => OnMirroringStartedReceived?.Invoke(this, EventArgs.Empty);

        public void RaiseMirroringStopped() => OnMirroringStoppedReceived?.Invoke(this, EventArgs.Empty);

        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }
}
