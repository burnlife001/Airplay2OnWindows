#if USE_WINDOWS_TRAY

using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AirPlay.Utils;

namespace AirPlay
{
    /// <summary>
    /// Borderless window that hosts the ffplay video window.
    /// The ffplay window is reparented into this form with SetParent so the mirroring
    /// picture appears inside our own app window instead of a separate ffplay window.
    /// Initialize() must be called on the WinForms UI thread (the thread that runs Application.Run).
    /// All public members are safe to call from any thread; work is dispatched with BeginInvoke.
    /// </summary>
    internal static class VideoWindow
    {
        private const int DragStripHeight = 24;
        private const int CloseButtonSize = 24;
        private const int DefaultWidth = 1280;
        private const int DefaultHeight = 720;
        private const int MinWidth = 320;
        private const int MinHeight = 180;

        private const int HtCaption = 2;

        private const int WmNcHitTest = 0x0084;
        private const int WmEnterSizeMove = 0x0231;
        private const int WmExitSizeMove = 0x0232;
        private const int WsThickFrame = 0x00040000;

        // 拖动缩放期间重设播放器的最小间隔（毫秒）。
        private const int ResizeThrottleMs = 60;

        // ffplay 有时会在收到外部尺寸后把子窗口改回自己那套尺寸；
        // 定时兜底重新对齐，否则画面会一直停在初始化时的 1280x720 布局里。
        private const int PlayerSyncIntervalMs = 300;

        // 跨进程读取的子窗口客户区尺寸允许的像素误差。
        private const int SizeTolerancePx = 1;

        private const int GwlStyle = -16;
        private const int WsChild = 0x40000000;
        private const int WsPopup = unchecked((int)0x80000000);

        // ffplay 以带标题栏的普通窗口启动（SDL 只对可缩放窗口响应外部尺寸变更），
        // 嵌入时去掉这些样式，让它变成纯客户区的子窗口。
        private const int WsCaption = 0x00C00000;
        private const int WsSysMenu = 0x00080000;
        private const int WsMinimizeBox = 0x00020000;
        private const int WsMaximizeBox = 0x00010000;

        private const int SwHide = 0;
        private const int SwShow = 5;

        private const uint SwpNosize = 0x0001;
        private const uint SwpNomove = 0x0002;
        private const uint SwpNozorder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;

        private static VideoForm? _form;
        private static IntPtr _playerHwnd = IntPtr.Zero;

        /// <summary>
        /// Raised on the UI thread when the user closes (hides) the video window.
        /// The ffplay process stays alive; the receiver should pause the mirroring stream.
        /// </summary>
        public static event EventHandler? Hidden;

        /// <summary>
        /// Raised on the UI thread when the video window is shown again from the tray icon,
        /// so the receiver can resume the mirroring stream.
        /// </summary>
        public static event EventHandler? Shown;

        /// <summary>
        /// Creates the (still hidden) borderless window. Must run on the WinForms UI thread.
        /// </summary>
        public static void Initialize()
        {
            if (_form != null) return;

            try
            {
                var form = new VideoForm();

                // Force handle creation on the UI thread first so BeginInvoke works from any thread later.
                var handle = form.Handle;

                var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, DefaultWidth, DefaultHeight);
                int width = Math.Min(DefaultWidth, (int)Math.Round(workingArea.Width * 0.9));
                int height = Math.Min(DefaultHeight, (int)Math.Round(workingArea.Height * 0.9));
                form.ClientSize = new Size(Math.Max(MinWidth, width), Math.Max(MinHeight, height));

                _form = form;

                // #region agent log
                DebugSessionLog.Write("V1", "VideoWindow.Initialize", "video window created", new
                {
                    hwnd = handle.ToInt64(),
                    width = form.ClientSize.Width,
                    height = form.ClientSize.Height,
                });
                // #endregion
            }
            catch (Exception ex)
            {
                // #region agent log
                DebugSessionLog.Write("V1", "VideoWindow.Initialize", "video window creation failed", new { error = ex.GetType().Name, message = ex.Message });
                // #endregion
            }
        }

        /// <summary>
        /// Shows and activates the existing video window only while a player is attached.
        /// </summary>
        public static void ShowIfActive()
        {
            var form = _form;
            if (_playerHwnd == IntPtr.Zero || form == null || !form.IsHandleCreated || form.IsDisposed || form.Disposing) return;

            try
            {
                form.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_playerHwnd == IntPtr.Zero || form.IsDisposed || form.Disposing) return;
                        form.Show();
                        form.Activate();

                        // 从托盘重新显示：通知接收端继续投屏。
                        Shown?.Invoke(null, EventArgs.Empty);
                    }
                    catch
                    {
                    }
                }));
            }
            catch
            {
            }
        }

        /// <summary>
        /// Reparents the ffplay window into the video window and shows it.
        /// Safe to call from any thread.
        /// </summary>
        public static void AttachPlayer(IntPtr playerHwnd, int videoWidth, int videoHeight)
        {
            var form = _form;
            if (form == null || playerHwnd == IntPtr.Zero) return;

            if (form.IsHandleCreated && form.InvokeRequired)
            {
                try
                {
                    form.BeginInvoke(new Action(() => AttachPlayer(playerHwnd, videoWidth, videoHeight)));
                }
                catch (Exception ex)
                {
                    // #region agent log
                    DebugSessionLog.Write("V1", "VideoWindow.AttachPlayer", "BeginInvoke failed", new { error = ex.GetType().Name, message = ex.Message });
                    // #endregion
                }
                return;
            }

            try
            {
                _playerHwnd = playerHwnd;

                ShowWindow(playerHwnd, SwHide);
                SetParent(playerHwnd, form.Handle);

                int style = unchecked((int)GetWindowLongPtrSafe(playerHwnd, GwlStyle).ToInt64());
                style &= ~(WsPopup | WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox);
                style |= WsChild;
                SetWindowLongPtrSafe(playerHwnd, GwlStyle, new IntPtr(unchecked((long)(uint)style)));
                SetWindowPos(playerHwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNomove | SwpNosize | SwpNozorder | SwpFrameChanged);

                form.LayoutChildren();

                if (!form.Visible) form.Show();
                form.Activate();

                ShowWindow(playerHwnd, SwShow);

                // SDL 在窗口隐藏期间可能丢弃尺寸事件，显示后重新对齐一次，避免画面被裁切。
                form.LayoutChildren();

                // ffplay 之后可能自行改写窗口尺寸，启动定时兜底把它拉回目标矩形。
                form.StartPlayerTimer();

                // #region agent log
                DebugSessionLog.Write("V1", "VideoWindow.AttachPlayer", "ffplay window attached", new
                {
                    hwnd = playerHwnd.ToInt64(),
                    parent = form.Handle.ToInt64(),
                    videoWidth,
                    videoHeight,
                    clientWidth = form.ClientSize.Width,
                    clientHeight = form.ClientSize.Height,
                    deviceDpi = form.DeviceDpi,
                    stripPx = form.StripPx,
                    targetWidth = form.PlayerTarget.Width,
                    targetHeight = form.PlayerTarget.Height,
                    targetX = form.PlayerTarget.X,
                    targetY = form.PlayerTarget.Y,
                    playerClientWidth = GetPlayerClientSize(playerHwnd).Width,
                    playerClientHeight = GetPlayerClientSize(playerHwnd).Height,
                });
                // #endregion
            }
            catch (Exception ex)
            {
                // #region agent log
                DebugSessionLog.Write("V1", "VideoWindow.AttachPlayer", "attach failed", new { error = ex.GetType().Name, message = ex.Message });
                // #endregion
            }
        }

        /// <summary>
        /// Resizes the window to match the real video resolution (first frame only, caller decides).
        /// Aspect ratio is preserved and the result is capped at 90% of the working area.
        /// </summary>
        public static void AdjustToVideoSize(int width, int height)
        {
            var form = _form;
            if (form == null || width <= 0 || height <= 0) return;

            if (form.IsHandleCreated && form.InvokeRequired)
            {
                try
                {
                    form.BeginInvoke(new Action(() => AdjustToVideoSize(width, height)));
                }
                catch
                {
                }
                return;
            }

            try
            {
                var area = Screen.FromControl(form).WorkingArea;
                int dpi = form.DeviceDpi;
                int strip = ScaleForDpi(DragStripHeight, dpi);
                double maxClientWidth = area.Width * 0.9;
                double maxClientHeight = area.Height * 0.9;
                double maxVideoWidth = maxClientWidth;
                double maxVideoHeight = maxClientHeight - strip;
                // Allow upscaling (up to 2x) so low-resolution streams are not shown tiny.
                double scale = Math.Min(2.0, Math.Min(maxVideoWidth / width, maxVideoHeight / height));

                int clientWidth = (int)Math.Round(width * scale);
                int clientHeight = (int)Math.Round(height * scale) + strip;
                clientWidth = Math.Max(MinWidth, Math.Min(clientWidth, (int)Math.Round(maxClientWidth)));
                clientHeight = Math.Max(MinHeight, Math.Min(clientHeight, (int)Math.Round(maxClientHeight)));

                form.ClientSize = new Size(clientWidth, clientHeight);
                form.Location = new Point(
                    area.Left + (area.Width - form.Width) / 2,
                    area.Top + (area.Height - form.Height) / 2);

                // #region agent log
                DebugSessionLog.Write("V1", "VideoWindow.AdjustToVideoSize", "window resized to video size", new
                {
                    videoWidth = width,
                    videoHeight = height,
                    clientWidth,
                    clientHeight,
                    scale,
                });
                // #endregion
            }
            catch (Exception ex)
            {
                // #region agent log
                DebugSessionLog.Write("V1", "VideoWindow.AdjustToVideoSize", "resize failed", new { error = ex.GetType().Name, message = ex.Message });
                // #endregion
            }
        }

        /// <summary>
        /// Hides the window and forgets the ffplay handle. ffplay is about to be killed,
        /// so there is no need to reparent it back.
        /// </summary>
        public static void DetachPlayer()
        {
            var form = _form;
            if (form == null) return;

            if (form.IsHandleCreated && form.InvokeRequired)
            {
                try
                {
                    form.BeginInvoke(new Action(DetachPlayer));
                }
                catch
                {
                }
                return;
            }

            _playerHwnd = IntPtr.Zero;
            try
            {
                form.StopPlayerTimer();
                form.Hide();
            }
            catch
            {
            }

            // #region agent log
            DebugSessionLog.Write("V1", "VideoWindow.DetachPlayer", "video window detached", null);
            // #endregion
        }

        /// <summary>
        /// Closes and disposes the window during application shutdown.
        /// </summary>
        public static void CloseForShutdown()
        {
            var form = _form;
            _form = null;
            _playerHwnd = IntPtr.Zero;
            if (form == null) return;

            try
            {
                if (form.IsHandleCreated && !form.IsDisposed && form.InvokeRequired)
                {
                    try
                    {
                        form.BeginInvoke(new Action(() => SafeClose(form)));
                    }
                    catch
                    {
                        SafeClose(form);
                    }
                }
                else
                {
                    SafeClose(form);
                }

                // #region agent log
                DebugSessionLog.Write("V1", "VideoWindow.CloseForShutdown", "video window closed", null);
                // #endregion
            }
            catch (Exception ex)
            {
                // #region agent log
                DebugSessionLog.Write("V1", "VideoWindow.CloseForShutdown", "close failed", new { error = ex.GetType().Name, message = ex.Message });
                // #endregion
            }
        }

        private static void SafeClose(Form form)
        {
            try
            {
                if (form.IsDisposed) return;
                if (form is VideoForm videoForm) videoForm.AllowRealClose();
                form.Close();
                form.Dispose();
            }
            catch
            {
            }
        }

        private static int ScaleForDpi(int value, int dpi)
        {
            return Math.Max(1, (int)Math.Round(value * dpi / 96.0));
        }

        /// <summary>
        /// 读取 ffplay 子窗口的真实客户区尺寸。读取失败返回空，调用方据此跳过比较。
        /// </summary>
        private static Size GetPlayerClientSize(IntPtr player)
        {
            if (player == IntPtr.Zero) return Size.Empty;
            if (!GetClientRect(player, out var rect)) return Size.Empty;
            return new Size(Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
        }

        private static IntPtr GetWindowLongPtrSafe(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        private static IntPtr SetWindowLongPtrSafe(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
                : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        private sealed class VideoForm : Form
        {
            private readonly Label? _closeButton;
            private bool _allowRealClose;
            private readonly Stopwatch _resizeClock = new Stopwatch();
            private bool _inSizeMove;

            // 最近一次按窗体客户区算出的播放器目标矩形（客户区坐标）。
            private Rectangle _playerTarget;

            // 兜底对齐定时器：ffplay 可能在布局后自行改写窗口尺寸。
            private readonly Timer _playerTimer;

            public VideoForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                BackColor = Color.Black;
                ForeColor = Color.White;
                Text = "AirPlay";
                ShowInTaskbar = true;
                StartPosition = FormStartPosition.CenterScreen;

                // Create and add the close button before any property that may resize the form
                // (MinimumSize/ClientSize), because OnResize -> LayoutChildren reads _closeButton.
                _closeButton = new Label
                {
                    AutoSize = false,
                    Size = new Size(CloseButtonSize, CloseButtonSize),
                    Location = new Point(0, 0),
                    BackColor = Color.Black,
                    ForeColor = Color.White,
                    Text = "✕",
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                };
                _closeButton.Click += OnCloseButtonClick;
                Controls.Add(_closeButton);

                // Timer 必须在 UI 线程创建（构造函数在 Initialize 的 UI 线程上执行），Tick 才会回到 UI 线程。
                _playerTimer = new Timer { Interval = PlayerSyncIntervalMs };
                _playerTimer.Tick += OnPlayerTimerTick;

                MinimumSize = new Size(MinWidth, MinHeight);
            }

            /// <summary>
            /// 摆放关闭按钮和内嵌播放器。播放器只让出顶部拖动条，四周缩放边框由系统的
            /// WS_THICKFRAME 处理，所以不会被子窗口遮住。
            /// 窗体构造期间也可能被调用，需容忍 _closeButton 为空。
            /// </summary>
            public void LayoutChildren()
            {
                UpdateCloseButton();
                UpdatePlayerBounds();
            }

            private void UpdateCloseButton()
            {
                var client = ClientRectangle;
                if (client.Width <= 0 || client.Height <= 0) return;
                if (_closeButton == null) return;

                int close = ClosePx;
                _closeButton.Size = new Size(close, close);
                _closeButton.Location = new Point(Math.Max(0, client.Width - close), 0);
            }

            /// <summary>
            /// 最后一次计算出的播放器目标矩形（客户区坐标）。
            /// </summary>
            public Rectangle PlayerTarget => _playerTarget;

            /// <summary>
            /// 启动兜底对齐定时器。只在播放器已嵌入且窗体可见时才有意义。
            /// </summary>
            public void StartPlayerTimer()
            {
                if (IsDisposed || Disposing) return;
                _playerTimer.Start();
            }

            public void StopPlayerTimer()
            {
                _playerTimer.Stop();
            }

            private void UpdatePlayerBounds()
            {
                if (_playerHwnd == IntPtr.Zero) return;

                var client = ClientRectangle;
                int strip = StripPx;
                int videoHeight = client.Height - strip;
                // 画面只让出顶部拖动条，其余整个客户区都归 ffplay。
                if (client.Width <= 0 || videoHeight <= 0) return;

                _playerTarget = new Rectangle(0, strip, client.Width, videoHeight);
                ApplyPlayerBounds();
            }

            /// <summary>
            /// 把 _playerTarget 下发给 ffplay，再用子窗口真实客户区校验。
            /// ffplay/SDL 收到外部尺寸后可能再改写一次窗口，尺寸不符就补发一次。
            /// </summary>
            private void ApplyPlayerBounds()
            {
                var player = _playerHwnd;
                if (player == IntPtr.Zero) return;

                var target = _playerTarget;
                if (target.Width <= 0 || target.Height <= 0) return;

                MoveWindow(player, target.X, target.Y, target.Width, target.Height, true);

                var actual = GetPlayerClientSize(player);
                bool matched = SizeMatches(actual, target);
                if (!matched && !actual.IsEmpty)
                {
                    MoveWindow(player, target.X, target.Y, target.Width, target.Height, true);
                    actual = GetPlayerClientSize(player);
                    matched = SizeMatches(actual, target);
                }

                // 补发后仍不符说明 ffplay 不认这个尺寸，只在此时记日志，避免每 300ms 刷一条。
                if (!matched && !actual.IsEmpty)
                {
                    // #region agent log
                    DebugSessionLog.Write("V1", "VideoForm.ApplyPlayerBounds", "player client size differs from target", new
                    {
                        targetWidth = target.Width,
                        targetHeight = target.Height,
                        actualWidth = actual.Width,
                        actualHeight = actual.Height,
                    });
                    // #endregion
                }
            }

            private static bool SizeMatches(Size actual, Rectangle target)
            {
                // 读不到（窗口正在销毁等）时不做判断，避免误判后反复跨进程重设。
                if (actual.IsEmpty) return true;
                return Math.Abs(actual.Width - target.Width) <= SizeTolerancePx &&
                       Math.Abs(actual.Height - target.Height) <= SizeTolerancePx;
            }

            private void OnPlayerTimerTick(object? sender, EventArgs e)
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                if (_playerHwnd == IntPtr.Zero || !Visible) return;
                // 拖动期间由 OnResize 节流处理，定时器不要插进来加重 ffplay 的重排。
                if (_inSizeMove) return;

                var client = ClientRectangle;
                int strip = StripPx;
                int videoHeight = client.Height - strip;
                if (client.Width <= 0 || videoHeight <= 0) return;

                var target = new Rectangle(0, strip, client.Width, videoHeight);
                bool targetChanged = target != _playerTarget;
                _playerTarget = target;

                // 目标没变、子窗口客户区也符合时不发 MoveWindow，避免无谓的跨进程调用。
                if (!targetChanged && SizeMatches(GetPlayerClientSize(_playerHwnd), target)) return;

                ApplyPlayerBounds();
            }

            internal int StripPx => ScaleForDpi(DragStripHeight, DeviceDpi);

            private int ClosePx => ScaleForDpi(CloseButtonSize, DeviceDpi);

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    // 无边框窗体默认没有缩放手柄；补上 WS_THICKFRAME，
                    // 由 Windows 负责四边/四角的缩放和光标，避免自行命中测试。
                    cp.Style |= WsThickFrame;
                    return cp;
                }
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                UpdateCloseButton();

                if (_inSizeMove)
                {
                    // 拖动时按固定间隔节流：每个像素都同步 MoveWindow 会让 ffplay 反复重建
                    // 交换链，拖动明显卡顿；松手后由 WM_EXITSIZEMOVE 补一次最终对齐。
                    if (_resizeClock.IsRunning && _resizeClock.ElapsedMilliseconds < ResizeThrottleMs) return;
                    _resizeClock.Restart();
                }

                UpdatePlayerBounds();
            }

            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);
                // 窗体隐藏时 ffplay 画面不可见，定时器没必要继续跑；重新显示且仍挂着播放器时恢复。
                if (!Visible) _playerTimer.Stop();
                else if (_playerHwnd != IntPtr.Zero) _playerTimer.Start();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _playerTimer.Tick -= OnPlayerTimerTick;
                    _playerTimer.Dispose();
                }
                base.Dispose(disposing);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmNcHitTest)
                {
                    int lParam = m.LParam.ToInt32();
                    int screenX = unchecked((short)(lParam & 0xFFFF));
                    int screenY = unchecked((short)((lParam >> 16) & 0xFFFF));
                    var clientPoint = PointToClient(new Point(screenX, screenY));
                    var client = ClientSize;
                    // 顶部条的客户区部分作为标题栏，用来拖动窗口；边框、四角等交给系统处理。
                    if (clientPoint.X >= 0 && clientPoint.X < client.Width &&
                        clientPoint.Y >= 0 && clientPoint.Y < StripPx)
                    {
                        m.Result = (IntPtr)HtCaption;
                        return;
                    }
                }

                if (m.Msg == WmEnterSizeMove)
                {
                    _inSizeMove = true;
                    _resizeClock.Restart();
                }
                else if (m.Msg == WmExitSizeMove)
                {
                    _inSizeMove = false;
                    _resizeClock.Reset();
                    UpdatePlayerBounds();
                }

                base.WndProc(ref m);
            }

            private void OnCloseButtonClick(object? sender, EventArgs e)
            {
                // #region agent log
                DebugSessionLog.Write("V1", "VideoWindow.CloseButton", "close requested", null);
                // #endregion
                RequestClose();
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                // Alt+F4 / taskbar close / task manager: hide instead of destroying the window,
                // otherwise _form would keep a disposed instance and the next session could not attach.
                if (!_allowRealClose &&
                    (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.TaskManagerClosing))
                {
                    e.Cancel = true;
                    // #region agent log
                    DebugSessionLog.Write("V1", "VideoWindow.OnFormClosing", "close requested", new { reason = e.CloseReason.ToString() });
                    // #endregion
                    RequestClose();
                    return;
                }

                base.OnFormClosing(e);
            }

            /// <summary>
            /// Allows the next Close() to really destroy the window (used by CloseForShutdown).
            /// </summary>
            public void AllowRealClose()
            {
                _allowRealClose = true;
            }

            private void RequestClose()
            {
                try
                {
                    Hide();
                }
                catch
                {
                }

                // 只隐藏窗口、保留 ffplay 进程，通知接收端暂停投屏；托盘左键点击可恢复。
                try
                {
                    Hidden?.Invoke(null, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    // #region agent log
                    DebugSessionLog.Write("V1", "VideoWindow.RequestClose", "Hidden handler failed", new { error = ex.GetType().Name, message = ex.Message });
                    // #endregion
                }
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}

#endif
