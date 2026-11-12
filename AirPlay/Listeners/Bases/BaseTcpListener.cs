using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.Models;
using AirPlay.Models.Enums;
using AirPlay.Utils;

namespace AirPlay.Listeners
{
    public class BaseTcpListener : BaseListener
    {
        private static int _nextListenerId;
        private readonly ushort _port;
        private readonly TcpListener _listener;
        private readonly ConcurrentDictionary<string, TcpClient> _connections;
        private readonly bool _rawData = false;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private Task _acceptTask;

        public int ListenerId { get; }

        public BaseTcpListener(ushort port, bool rawData = false)
        {
            ListenerId = Interlocked.Increment(ref _nextListenerId);
            _port = port;
            _rawData = rawData;
            _listener = new TcpListener(IPAddress.Any, _port);
            _connections = new ConcurrentDictionary<string, TcpClient>();

            _cancellationTokenSource = new CancellationTokenSource();
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                _listener.Start();
                DebugSessionLog.Write("H1", "BaseTcpListener.cs:StartAsync", "TcpListener started", new { listenerId = ListenerId, port = _port });
            }
            catch (SocketException ex)
            {
                DebugSessionLog.Write("H1", "BaseTcpListener.cs:StartAsync", "TcpListener start failed", new { listenerId = ListenerId, port = _port, error = ex.SocketErrorCode.ToString(), message = ex.Message });
                throw;
            }

            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token);
            _acceptTask = AcceptClientsAsync(source.Token);
            return Task.CompletedTask;
        }

        public override async Task StopAsync()
        {
            DebugSessionLog.Write("H1", "BaseTcpListener.cs:StopAsync", "TcpListener stopping", new { listenerId = ListenerId, port = _port });
            try
            {
                _cancellationTokenSource.Cancel();
                _listener.Stop();

                // 关闭所有已建连的客户端：否则客户端处理线程会一直阻塞在同步 stream.Read 上，
                // socket 也不会释放（StopAsync 之后这些线程并不在 _acceptTask 里）。
                foreach (var connection in _connections)
                {
                    try
                    {
                        connection.Value.Close();
                        DebugSessionLog.Write("H1", "BaseTcpListener.cs:StopAsync", "Client closed on stop", new { listenerId = ListenerId, port = _port, remote = connection.Key });
                    }
                    catch (Exception ex)
                    {
                        DebugSessionLog.Write("H1", "BaseTcpListener.cs:StopAsync", "Client close failed", new { listenerId = ListenerId, port = _port, remote = connection.Key, error = ex.GetType().Name, message = ex.Message });
                    }
                }
                _connections.Clear();
            }
            catch (Exception ex)
            {
                DebugSessionLog.Write("H1", "BaseTcpListener.cs:StopAsync", "TcpListener stop failed", new { listenerId = ListenerId, port = _port, error = ex.GetType().Name, message = ex.Message });
                throw;
            }
            finally
            {
                if (_acceptTask != null)
                {
                    await _acceptTask.ConfigureAwait(false);
                }
                DebugSessionLog.Write("H1", "BaseTcpListener.cs:StopAsync", "TcpListener stopped", new { listenerId = ListenerId, port = _port });
            }
        }

        public virtual Task OnDataReceivedAsync(Request request, Response response, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public virtual Task OnRawReceivedAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private async Task AcceptClientsAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    var remoteEndpoint = client.Client.RemoteEndPoint.ToString();

                    // 客户端处理里是同步阻塞的 stream.Read，必须放到线程池执行：
                    // 否则 accept 线程会卡在读某个长连接上，Stop() 之后 _acceptTask 永不结束，进程退不出去。
                    _ = Task.Run(() => HandleClientAsync(client, cancellationToken));

                    if (!_connections.TryAdd(remoteEndpoint, client))
                    {
                        client.Close();
                    }
                }
            }
            catch (ObjectDisposedException ex)
            {
                DebugSessionLog.Write("H1", "BaseTcpListener.cs:AcceptClientsAsync", "Accept loop disposed", new { listenerId = ListenerId, port = _port, message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                DebugSessionLog.Write("H1", "BaseTcpListener.cs:AcceptClientsAsync", "Accept loop stopped", new { listenerId = ListenerId, port = _port, message = ex.Message });
            }
            catch (SocketException ex)
            {
                DebugSessionLog.Write("H1", "BaseTcpListener.cs:AcceptClientsAsync", "Accept loop socket error", new { listenerId = ListenerId, port = _port, error = ex.SocketErrorCode.ToString(), message = ex.Message });
            }
            finally
            {
                DebugSessionLog.Write("H1", "BaseTcpListener.cs:AcceptClientsAsync", "Accept loop exited", new { listenerId = ListenerId, port = _port });
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            string remoteEndpoint = null;
            try
            {
                remoteEndpoint = client.Client.RemoteEndPoint.ToString();
                var stream = client.GetStream();
                // #region agent log
                DebugSessionLog.Write("H5", "BaseTcpListener.cs:HandleClientAsync", "Client connected", new { listenerId = ListenerId, port = _port, remote = remoteEndpoint, rawData = _rawData });
                // #endregion
                Console.WriteLine($"Client connected: {remoteEndpoint}");

                if (_rawData)
                {
                    await OnRawReceivedAsync(client, stream, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ReadFormattedAsync(client, stream, cancellationToken).ConfigureAwait(false);
                }

                if (client.Connected)
                {
                    client.Close();
                }

                Console.WriteLine($"Client disconnected: {remoteEndpoint}");
            }
            catch (System.Exception ex)
            {
                Console.WriteLine(ex);
            }
            finally
            {
                // 无论正常结束还是异常，都要从连接表移除并关闭 socket，避免逐次泄漏。
                if (remoteEndpoint != null)
                {
                    _connections.TryRemove(remoteEndpoint, out _);
                }

                try
                {
                    client.Close();
                }
                catch (Exception ex)
                {
                    DebugSessionLog.Write("H5", "BaseTcpListener.cs:HandleClientAsync", "Client close failed", new { listenerId = ListenerId, port = _port, remote = remoteEndpoint, error = ex.GetType().Name, message = ex.Message });
                }
            }
        }

        private async Task ReadFormattedAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
        {
#if DEBUG
            var depth = Interlocked.Increment(ref DebugSessionLog.RtspDepth);
            try
            {
            if (depth == 30 || depth == 100 || depth == 500)
                DebugSessionLog.Write("H3", "BaseTcpListener.cs:ReadFormattedAsync", "RTSP recursion depth", new { depth, port = _port });
#endif
            if (client.Connected && stream.CanRead)
            {
                int retBytes = 0;
                string raw = string.Empty;

                do
                {
                    try
                    {
                        var buffer = new byte[1024];
                        var readCount = stream.Read(buffer, 0, buffer.Length);
                        retBytes += readCount;
                        raw += string.Join(string.Empty, buffer.Take(readCount).Select(b => b.ToString("X2")));

                        // Wait for other possible data
                        await Task.Delay(10);
                    }
                    catch (System.IO.IOException) { }
                } while (client.Connected && stream.DataAvailable);

                // Now we have all data inside raw var
                // Make sure the socket is still connected (if it closed we don't really care about the message because we can not reply)
                if (client.Connected)
                {
                    // That listener can accept only this methods: GET | POST | SETUP | GET_PARAMETER | RECORD | SET_PARAMETER | ANNOUNCE | FLUSH | TEARDOWN | OPTIONS | PAUSE
                    // Because of the persistent connection we might receive more than one request at a time
                    // I'm using a regex to find all request by 'magic numbers' (ex. GET, POST, SETUP, ecc)

                    var pattern =
                        $"^{RequestConst.GET}[.]*|" +
                        $"^{RequestConst.POST}[.]*|" +
                        $"^{RequestConst.SETUP}[.]*|" +
                        $"^{RequestConst.GET_PARAMETER}[.]*|" +
                        $"^{RequestConst.RECORD}[.]*|" +
                        $"^{RequestConst.SET_PARAMETER}[.]*|" +
                        $"^{RequestConst.ANNOUNCE}[.]*|" +
                        $"^{RequestConst.FLUSH}[.]*|" +
                        $"^{RequestConst.OPTIONS}[.]*|" +
                        $"^{RequestConst.PAUSE}[.]*|" +
                        $"^{RequestConst.SETPEERS}[.]*|" +
                        $"^{RequestConst.TEARDOWN}[.]*";

                    var r = new Regex(pattern, RegexOptions.Multiline);
                    var m = r.Matches(raw);

                    if (m.Count == 0 && raw.Length > 0)
                    {
                        // Log first 200 chars of unrecognized data for debugging
                        var preview = raw.Length > 200 ? raw.Substring(0, 200) : raw;
                        DebugSessionLog.WriteLine($"[DEBUG-RTSP] Unrecognized data (no regex match), len={raw.Length}, preview: {preview}");
                    }

                    // Split requests and create models
                    var requests = new List<Request>();
                    for (int i = 0; i < m.Count; i++)
                    {
                        if (i + 1 < m.Count)
                        {
                            var hexReq = raw.Substring(m[i].Index, m[i + 1].Index - m[i].Index);
                            var req = new Request(hexReq);
                            requests.Add(req);
                        }
                        else
                        {
                            var hexReq = raw.Substring(m[i].Index);
                            var req = new Request(hexReq);
                            requests.Add(req);
                        }
                    }

                    foreach (var request in requests)
                    {
                        var response = request.GetBaseResponse();

                        var cseq = request.Headers.ContainsKey("CSeq") ? request.Headers["CSeq"] : "?";
                        DebugSessionLog.WriteLine($"[DEBUG-RTSP] >>> {request.Type} CSeq={cseq}");

                        await OnDataReceivedAsync(request, response, cancellationToken).ConfigureAwait(false);
                        await SendResponseAsync(stream, response);

                        DebugSessionLog.WriteLine($"[DEBUG-RTSP] <<< {response.StatusCode}");                    }
                }

                // If we have read some bytes, leave connection open and wait for next message
                if (retBytes != 0)
                {
                    await ReadFormattedAsync(client, stream, cancellationToken);
                }
            }
#if DEBUG
            }
            finally
            {
                Interlocked.Decrement(ref DebugSessionLog.RtspDepth);
            }
#endif
        }

        private async Task SendResponseAsync(NetworkStream stream, Response response)
        {
            var format = $"{response.GetProtocol()} {(int)response.StatusCode} {response.StatusCode.ToString().ToUpperInvariant()}\r\n";

            foreach (var header in response.Headers)
            {
                format += $"{header.Name}: {string.Join(",", header.Values)}\r\n";
            }

            // Ready for body
            format += "\r\n";

            var formatBuffer = Encoding.ASCII.GetBytes(format);

            byte[] payload;
            var bodyBuffer = await response.ReadAsync();
            if (bodyBuffer?.Any() == true)
            {
                payload = formatBuffer.Concat(bodyBuffer).ToArray();
            }
            else
            {
                payload = formatBuffer;
            }

            try
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
            catch (System.IO.IOException)
            {
                //
            }
        }
    }
}
