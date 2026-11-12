using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.Models;
using AirPlay.Utils;

namespace AirPlay.Services
{
    /// <summary>
    /// Video output service that writes H.264 Annex B data to a named pipe.
    /// Automatically launches ffplay when mirroring starts and kills it when mirroring stops.
    /// Supports repeated start/stop cycles without requiring a program restart.
    /// </summary>
    public class VideoOutputService : IDisposable
    {
        private const string PIPE_NAME = "AirPlayVideo";
        private const string UNIX_PIPE_PATH = "/tmp/airplay_video";

        private NamedPipeServerStream _pipeServer;
        private FileStream _unixPipeStream;
        private readonly object _lock = new object();
        private bool _disposed = false;
        private bool _connected = false;
        private long _frameCount = 0;
        private CancellationTokenSource _cts;
        private Process _ffplayProcess;
        private List<byte[]> _pendingFrames = new List<byte[]>();
        private bool _hasReceivedKeyFrame = false;
#if USE_WINDOWS_TRAY
        private bool _videoSizeAdjusted = false;
#endif

        public event EventHandler<string> OnStatusChanged;
        public event EventHandler FfplayExited;

        private bool _ffplayExitHandled;

        /// <summary>
        /// Start a new mirroring session: create the pipe, launch ffplay, and wait for connection.
        /// Can be called multiple times across mirroring sessions.
        /// </summary>
        public void StartMirroring()
        {
            lock (_lock)
            {
                if (_disposed) return;

                // Clean up any previous session
                CleanupSession();

                _cts = new CancellationTokenSource();
                _frameCount = 0;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    StartWindowsSession();
                }
                else
                {
                    StartUnixSession();
                }
            }
        }

        /// <summary>
        /// Stop the current mirroring session: kill ffplay and clean up the pipe.
        /// </summary>
        public void StopMirroring()
        {
            lock (_lock)
            {
                Console.WriteLine($"Mirroring stopped ({_frameCount} frames written)");
                CleanupSession();
            }
        }

        private void StartWindowsSession()
        {
            try
            {
                _pipeServer = new NamedPipeServerStream(
                    PIPE_NAME,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                Console.WriteLine($"Video pipe created: \\\\.\\pipe\\{PIPE_NAME}");

                // Launch ffplay to connect to the pipe
                LaunchFfplay($"\\\\.\\pipe\\{PIPE_NAME}");

                // Wait for ffplay to connect in background
                _ = Task.Run(() => WaitForPipeConnection(_cts.Token));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start video session: {ex.Message}");
            }
        }

        private void StartUnixSession()
        {
            try
            {
                if (File.Exists(UNIX_PIPE_PATH))
                {
                    File.Delete(UNIX_PIPE_PATH);
                }

                using (var mkfifo = Process.Start(new ProcessStartInfo
                {
                    FileName = "mkfifo",
                    Arguments = UNIX_PIPE_PATH,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    mkfifo?.WaitForExit();
                }

                Console.WriteLine($"Video FIFO created: {UNIX_PIPE_PATH}");

                // Launch ffplay to read from the FIFO
                LaunchFfplay(UNIX_PIPE_PATH);

                // Open the FIFO for writing (blocks until reader connects)
                _ = Task.Run(() => WaitForUnixPipeConnection(_cts.Token));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start video session: {ex.Message}");
            }
        }

        private void LaunchFfplay(string pipePath)
        {
            // Low-latency ffplay flags:
            //   -probesize 32768: small probe size (pre-buffered SPS/PPS+IDR makes this sufficient)
            //   -analyzeduration 0: don't wait to analyze stream duration
            //   -flags low_delay: enable low-delay decoding
            //   -framedrop: drop frames if display can't keep up
            //   -avioflags direct: bypass I/O buffering on pipe reads
            //   -vf setpts=0: zero out PTS so frames display immediately without pacing delay
            //
            // NOTE: do NOT pass "-fflags nobuffer". With AVFMT_FLAG_NOBUFFER the h264 parser
            // never hands packets to the decoder, so no frame is ever decoded, ffplay never
            // creates/shows its SDL video window (it stays hidden with the default "ffplay"
            // title) and the mirroring picture never appears.
            //
            // The ffplay window keeps its title bar (SDL only re-lays out the picture when it
            // treats the window as resizable) and is titled "AirPlay"; VideoWindow strips the
            // caption styles after reparenting it via SetParent.
            // 不能传 -x/-y：ffplay 会让窗口停留在按初始尺寸排版的状态，画面小且偏移。
            // 也不能传 -noborder：无边框窗口被 SDL 视为不可缩放，之后由宿主 SetWindowPos
            // 设置的尺寸会被 SDL 还原（客户区不变），画面永远无法重新适配窗口。
            // 这里用默认带标题栏的窗口，嵌入时再由 VideoWindow 去掉标题栏样式。
            var ffplayArgs = $"-f h264 -probesize 32768 -analyzeduration 0 -flags low_delay -framedrop -avioflags direct -vf setpts=0 -window_title \"AirPlay\" \"{pipePath}\"";

            try
            {
                var ffplayPath = FindFfplay();
                if (ffplayPath == null)
                {
                    Console.WriteLine("ffplay not found. Please ensure ffplay is in the application directory or PATH.");
                    Console.WriteLine($"  You can manually connect with: ffplay {ffplayArgs}");
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = ffplayPath,
                    Arguments = ffplayArgs,
                    UseShellExecute = false,
                    // ffplay 是控制台程序：不创建控制台窗口，只保留它自己的 SDL 视频窗口。
                    CreateNoWindow = true
                };

                _ffplayProcess = Process.Start(psi);
                if (_ffplayProcess != null)
                {
                    _ffplayExitHandled = false;
                    _ffplayProcess.EnableRaisingEvents = true;
                    _ffplayProcess.Exited += OnFfplayProcessExited;
                    Console.WriteLine($"ffplay launched (PID: {_ffplayProcess.Id})");

#if USE_WINDOWS_TRAY
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        AttachFfplayWindowInBackground(_ffplayProcess, _cts?.Token ?? CancellationToken.None);
                    }
#endif
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to launch ffplay: {ex.Message}");
                Console.WriteLine($"  You can manually connect with: ffplay {ffplayArgs}");
            }
        }

#if USE_WINDOWS_TRAY
        private const string PlayerWindowTitle = "AirPlay";

        /// <summary>
        /// Polls for the ffplay window and reparents it into the in-app video window.
        /// ffplay is a console app, so Process.MainWindowHandle may point at the console window
        /// (or be 0). The SDL window is therefore located by PID + title instead.
        /// </summary>
        private void AttachFfplayWindowInBackground(Process ffplay, CancellationToken token)
        {
            Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!token.IsCancellationRequested && DateTime.UtcNow < deadline)
                {
                    try
                    {
                        ffplay.Refresh();
                        if (ffplay.HasExited)
                        {
                            // #region agent log
                            DebugSessionLog.Write("V1", "VideoOutputService.AttachFfplayWindowInBackground", "ffplay exited before window appeared", null);
                            // #endregion
                            return;
                        }

                        IntPtr hwnd;
                        if (!TryFindPlayerWindow(ffplay.Id, out hwnd))
                        {
                            // Last resort: MainWindowHandle, but only if it really belongs to this process.
                            var main = ffplay.MainWindowHandle;
                            hwnd = main != IntPtr.Zero && IsWindowOfProcess(main, ffplay.Id) ? main : IntPtr.Zero;
                        }

                        if (hwnd != IntPtr.Zero)
                        {
                            var title = GetWindowTitle(hwnd);
                            // #region agent log
                            DebugSessionLog.Write("V1", "VideoOutputService.AttachFfplayWindowInBackground", "ffplay window found", new
                            {
                                pid = ffplay.Id,
                                hwnd = hwnd.ToInt64(),
                                windowTitle = title,
                            });
                            // #endregion
                            VideoWindow.AttachPlayer(hwnd, 0, 0);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        // #region agent log
                        DebugSessionLog.Write("V1", "VideoOutputService.AttachFfplayWindowInBackground", "window poll failed", new { error = ex.GetType().Name, message = ex.Message });
                        // #endregion
                        return;
                    }

                    try
                    {
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                // #region agent log
                DebugSessionLog.Write("V1", "VideoOutputService.AttachFfplayWindowInBackground", "ffplay window not found within timeout", null);
                // #endregion
            });
        }

        /// <summary>
        /// Enumerates top-level windows and finds the ffplay SDL window for <paramref name="pid"/>.
        /// Prefers the window titled <see cref="PlayerWindowTitle"/>; falls back to the first
        /// visible window with a non-empty title owned by the process.
        /// </summary>
        private static bool TryFindPlayerWindow(int pid, out IntPtr hwnd)
        {
            IntPtr exact = IntPtr.Zero;
            IntPtr fallback = IntPtr.Zero;

            EnumWindows((candidate, _) =>
            {
                if (!IsWindowVisible(candidate)) return true;

                GetWindowThreadProcessId(candidate, out uint windowPid);
                if (windowPid != (uint)pid) return true;

                var title = GetWindowTitle(candidate);
                if (title.Length == 0) return true;

                if (title == PlayerWindowTitle)
                {
                    exact = candidate;
                    return false;
                }

                if (fallback == IntPtr.Zero) fallback = candidate;
                return true;
            }, IntPtr.Zero);

            hwnd = exact != IntPtr.Zero ? exact : fallback;
            return hwnd != IntPtr.Zero;
        }

        private static bool IsWindowOfProcess(IntPtr hwnd, int pid)
        {
            GetWindowThreadProcessId(hwnd, out uint windowPid);
            return windowPid == (uint)pid;
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            var buffer = new StringBuilder(256);
            int length = GetWindowTextW(hwnd, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString(0, length) : string.Empty;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
#endif

        private string FindFfplay()
        {
            // Check application directory first
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var ffplayName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffplay.exe" : "ffplay";
            var localPath = Path.Combine(appDir, ffplayName);
            if (File.Exists(localPath))
            {
                return localPath;
            }

            // Fall back to PATH
            try
            {
                var whichCmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
                var psi = new ProcessStartInfo
                {
                    FileName = whichCmd,
                    Arguments = "ffplay",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                var output = proc?.StandardOutput.ReadToEnd()?.Trim();
                proc?.WaitForExit();
                var firstLine = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                if (proc?.ExitCode == 0 && !string.IsNullOrWhiteSpace(firstLine))
                {
                    return firstLine;
                }
            }
            catch { }

            return null;
        }

        private void OnFfplayProcessExited(object? sender, EventArgs e)
        {
            EventHandler? notify;
            lock (_lock)
            {
                if (_disposed || _ffplayExitHandled)
                {
                    return;
                }

                _ffplayExitHandled = true;
                // #region agent log
                DebugSessionLog.Write("H7", "VideoOutputService.OnFfplayProcessExited", "ffplay process exited", new { frameCount = _frameCount });
                // #endregion
                CleanupSession();
                notify = FfplayExited;
            }

            notify?.Invoke(this, EventArgs.Empty);
        }

        private void StopFfplay()
        {
            var process = _ffplayProcess;
            if (process == null)
            {
                DebugSessionLog.Write("H6", "VideoOutputService.StopFfplay", "no ffplay process", null);
                return;
            }

            // 先摘下字段并退订 Exited：Kill 之后的 WaitForExit 会在当前线程同步触发 Exited，
            // 事件里的 CleanupSession 会重入本方法把进程 Dispose 掉，外层 finally 再 Dispose 就会 NRE。
            _ffplayProcess = null;
            try
            {
                process.Exited -= OnFfplayProcessExited;
            }
            catch (Exception ex)
            {
                DebugSessionLog.Write("H6", "VideoOutputService.StopFfplay", "unsubscribe Exited failed", new { error = ex.GetType().Name, message = ex.Message });
            }

            int? pid = null;
            try
            {
                pid = process.Id;
                DebugSessionLog.Write("H6", "VideoOutputService.StopFfplay", "HasExited enter", new { pid });
                var hasExited = process.HasExited;
                DebugSessionLog.Write("H6", "VideoOutputService.StopFfplay", "HasExited exit", new { pid, hasExited });
                if (!hasExited)
                {
                    Console.WriteLine($"Stopping ffplay (PID: {pid})...");
                    DebugSessionLog.Write("H6", "VideoOutputService.StopFfplay", "Kill enter", new { pid });
                    process.Kill();
                    DebugSessionLog.Write("H6", "VideoOutputService.StopFfplay", "Kill exit", new { pid });
                    var exited = process.WaitForExit(3000);
                    DebugSessionLog.Write("H6", "VideoOutputService.StopFfplay", "WaitForExit returned", new { pid, timeoutMs = 3000, exited });
                    if (!exited)
                    {
                        Console.WriteLine("Warning: ffplay did not exit within timeout");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugSessionLog.Write("H6", "VideoOutputService.StopFfplay", "ffplay cleanup failed", new { pid, error = ex.GetType().Name, message = ex.Message });
                Console.WriteLine($"Error stopping ffplay: {ex.Message}");
            }
            finally
            {
                DebugSessionLog.Write("H6", "VideoOutputService.StopFfplay", "process Dispose enter", new { pid });
                process.Dispose();
                DebugSessionLog.Write("H6", "VideoOutputService.StopFfplay", "process Dispose exit", new { pid });
            }
        }

        private void CleanupSession()
        {
            DebugSessionLog.Write("H6", "VideoOutputService.CleanupSession", "enter", new { hasCts = _cts != null, hasPipe = _pipeServer != null, hasFfplay = _ffplayProcess != null });
            _cts?.Cancel();
            DebugSessionLog.Write("H6", "VideoOutputService.CleanupSession", "cts cancelled", null);
            _cts?.Dispose();
            _cts = null;
            DebugSessionLog.Write("H6", "VideoOutputService.CleanupSession", "cts disposed", null);

            _connected = false;
            _pendingFrames.Clear();
            _hasReceivedKeyFrame = false;
#if USE_WINDOWS_TRAY
            _videoSizeAdjusted = false;
#endif

            try
            {
                _pipeServer?.Dispose();
                DebugSessionLog.Write("H6", "VideoOutputService.CleanupSession", "pipe disposed", null);
            }
            catch (Exception ex)
            {
                DebugSessionLog.Write("H6", "VideoOutputService.CleanupSession", "pipe dispose failed", new { error = ex.GetType().Name, message = ex.Message });
            }
            _pipeServer = null;

            try
            {
                _unixPipeStream?.Dispose();
                DebugSessionLog.Write("H6", "VideoOutputService.CleanupSession", "unix pipe disposed", null);
            }
            catch (Exception ex)
            {
                DebugSessionLog.Write("H6", "VideoOutputService.CleanupSession", "unix pipe dispose failed", new { error = ex.GetType().Name, message = ex.Message });
            }
            _unixPipeStream = null;

#if USE_WINDOWS_TRAY
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // ffplay is about to be killed, so there is no need to reparent its window back.
                VideoWindow.DetachPlayer();
            }
#endif

            StopFfplay();

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(UNIX_PIPE_PATH))
            {
                try { File.Delete(UNIX_PIPE_PATH); } catch { }
            }
            DebugSessionLog.Write("H6", "VideoOutputService.CleanupSession", "exit", null);
        }

        private async Task WaitForPipeConnection(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Console.WriteLine("Waiting for video player to connect...");
                    await _pipeServer.WaitForConnectionAsync(token);

                    lock (_lock)
                    {
                        _connected = true;
                        FlushPendingFrames();
                    }
                    Console.WriteLine("Video player connected!");
                    OnStatusChanged?.Invoke(this, "connected");
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Pipe connection error: {ex.Message}");
                    await Task.Delay(1000, token);
                }
            }
        }

        private void WaitForUnixPipeConnection(CancellationToken token)
        {
            try
            {
                _unixPipeStream = new FileStream(UNIX_PIPE_PATH, FileMode.Open, FileAccess.Write);
                lock (_lock)
                {
                    _connected = true;
                    FlushPendingFrames();
                }
                Console.WriteLine("Video player connected to FIFO!");
                OnStatusChanged?.Invoke(this, "connected");
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Console.WriteLine($"FIFO connection error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Flush buffered frames to the pipe. Must be called under _lock.
        /// </summary>
        private void FlushPendingFrames()
        {
            if (_pendingFrames.Count == 0) return;

            Console.WriteLine($"Flushing {_pendingFrames.Count} buffered frames to video player...");
            try
            {
                foreach (var frameData in _pendingFrames)
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        _pipeServer?.Write(frameData, 0, frameData.Length);
                    }
                    else
                    {
                        _unixPipeStream?.Write(frameData, 0, frameData.Length);
                    }
                    _frameCount++;
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    _pipeServer?.Flush();
                }
                else
                {
                    _unixPipeStream?.Flush();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error flushing buffered frames: {ex.Message}");
            }
            _pendingFrames.Clear();
        }

        /// <summary>
        /// Write H.264 frame data to the pipe.
        /// Buffers frames before ffplay connects and replays them on connection.
        /// </summary>
        public void WriteFrame(H264Data data)
        {
            bool disconnected = false;

            lock (_lock)
            {
                if (_disposed) return;
                if (data.Data == null || data.Length <= 0) return;

                bool isKeyFrame = data.FrameType == 5;

#if USE_WINDOWS_TRAY
                // First keyframe of the session carries the real video resolution: size the window to it.
                if (!_videoSizeAdjusted && isKeyFrame && data.Width > 0 && data.Height > 0)
                {
                    _videoSizeAdjusted = true;
                    // #region agent log
                    DebugSessionLog.Write("V1", "VideoOutputService.WriteFrame", "first frame size received", new { width = data.Width, height = data.Height });
                    // #endregion
                    VideoWindow.AdjustToVideoSize(data.Width, data.Height);
                }
#endif

                if (!_connected)
                {
                    // Buffer frames until ffplay connects.
                    // On a new keyframe, discard older buffered frames (start fresh from latest keyframe).
                    if (isKeyFrame)
                    {
                        _pendingFrames.Clear();
                        _hasReceivedKeyFrame = true;
                    }
                    if (_hasReceivedKeyFrame)
                    {
                        var copy = new byte[data.Length];
                        Array.Copy(data.Data, 0, copy, 0, data.Length);
                        _pendingFrames.Add(copy);
                    }
                    return;
                }

                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        if (_pipeServer != null && _pipeServer.IsConnected)
                        {
                            _pipeServer.Write(data.Data, 0, data.Length);
                            if (isKeyFrame) _pipeServer.Flush();
                        }
                    }
                    else
                    {
                        if (_unixPipeStream != null && _unixPipeStream.CanWrite)
                        {
                            _unixPipeStream.Write(data.Data, 0, data.Length);
                            if (isKeyFrame) _unixPipeStream.Flush();
                        }
                    }

                    _frameCount++;

                    if (_frameCount % 300 == 0)
                    {
                        Console.WriteLine($"Video: {_frameCount} frames written | Type: {(data.FrameType == 5 ? "IDR" : "P")} | {data.Width}x{data.Height}");
                    }
                }
                catch (IOException)
                {
                    Console.WriteLine("Video player disconnected");
                    _connected = false;
                    disconnected = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Video write error: {ex.Message}");
                }
            }

            // 回调必须在锁外触发：订阅者可能调用 StopMirroring/Dispose（Monitor 可重入），
            // 持锁回调会让正在写入的管道被释放。
            if (disconnected)
            {
                NotifyStatusChanged("disconnected");
            }
        }

        private void NotifyStatusChanged(string status)
        {
            var handler = OnStatusChanged;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, status);
            }
            catch (Exception ex)
            {
                DebugSessionLog.Write("H6", "VideoOutputService.NotifyStatusChanged", "status callback failed", new { status, error = ex.GetType().Name, message = ex.Message });
            }
        }

        public void Dispose()
        {
            var stopwatch = Stopwatch.StartNew();
            DebugSessionLog.Write("H6", "VideoOutputService.Dispose", "waiting lock", new { thread = Environment.CurrentManagedThreadId });
            lock (_lock)
            {
                DebugSessionLog.Write("H6", "VideoOutputService.Dispose", "lock acquired", null);
                if (_disposed)
                {
                    DebugSessionLog.Write("H6", "VideoOutputService.Dispose", "already disposed", null);
                    return;
                }
                _disposed = true;

                CleanupSession();

                Console.WriteLine("Video output service disposed");
                DebugSessionLog.Write("H6", "VideoOutputService.Dispose", "dispose completed", new { elapsedMs = stopwatch.ElapsedMilliseconds });
            }
        }
    }
}
