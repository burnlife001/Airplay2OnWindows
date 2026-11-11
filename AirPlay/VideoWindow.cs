#if USE_WINDOWS_TRAY

using System;
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
        private const int ResizeBorder = 6;
        private const int CloseButtonSize = 24;
        private const int DefaultWidth = 1280;
        private const int DefaultHeight = 720;
        private const int MinWidth = 320;
        private const int MinHeight = 180;

        private const int WmNcHitTest = 0x0084;

        private const int HtClient = 1;
        private const int HtCaption = 2;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtTop = 12;
        private const int HtTopLeft = 13;
        private const int HtTopRight = 14;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;

        private const int GwlStyle = -16;
        private const int WsChild = 0x40000000;
        private const int WsPopup = unchecked((int)0x80000000);

        private const int SwHide = 0;
        private const int SwShow = 5;

        private const uint SwpNosize = 0x0001;
        private const uint SwpNomove = 0x0002;
        private const uint SwpNozorder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;

        private static VideoForm? _form;
        private static IntPtr _playerHwnd = IntPtr.Zero;

        /// <summary>
        /// Raised on the UI thread when the user clicks the close button of the video window.
        /// </summary>
        public static event EventHandler? CloseRequested;

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
                style = (style & ~WsPopup) | WsChild;
                SetWindowLongPtrSafe(playerHwnd, GwlStyle, new IntPtr(unchecked((long)(uint)style)));
                SetWindowPos(playerHwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNomove | SwpNosize | SwpNozorder | SwpFrameChanged);

                form.LayoutChildren();

                if (!form.Visible) form.Show();
                form.Activate();

                ShowWindow(playerHwnd, SwShow);

                // #region agent log
                DebugSessionLog.Write("V1", "VideoWindow.AttachPlayer", "ffplay window attached", new
                {
                    hwnd = playerHwnd.ToInt64(),
                    parent = form.Handle.ToInt64(),
                    videoWidth,
                    videoHeight,
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
                double maxVideoWidth = area.Width * 0.9;
                double maxVideoHeight = (area.Height * 0.9) - DragStripHeight;
                double scale = Math.Min(1.0, Math.Min(maxVideoWidth / width, maxVideoHeight / height));

                int clientWidth = Math.Max(MinWidth, (int)Math.Round(width * scale));
                int clientHeight = Math.Max(MinHeight, (int)Math.Round(height * scale) + DragStripHeight);
                clientWidth = Math.Min(clientWidth, Math.Max(MinWidth, (int)Math.Round(area.Width * 0.9)));
                clientHeight = Math.Min(clientHeight, Math.Max(MinHeight, (int)Math.Round(area.Height * 0.9)));

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

        private static int HitTest(Point clientPoint, Size client)
        {
            int width = client.Width;
            int height = client.Height;

            bool left = clientPoint.X < ResizeBorder;
            bool right = clientPoint.X >= width - ResizeBorder;
            bool top = clientPoint.Y < ResizeBorder;
            bool bottom = clientPoint.Y >= height - ResizeBorder;

            if (top && left) return HtTopLeft;
            if (top && right) return HtTopRight;
            if (bottom && left) return HtBottomLeft;
            if (bottom && right) return HtBottomRight;
            if (left) return HtLeft;
            if (right) return HtRight;
            if (top) return HtTop;
            if (bottom) return HtBottom;

            if (clientPoint.Y >= 0 && clientPoint.Y < DragStripHeight)
            {
                return HtCaption;
            }

            return HtClient;
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

                MinimumSize = new Size(MinWidth, MinHeight);
            }

            /// <summary>
            /// Positions the close button and the embedded player inside the client area.
            /// Safe to call before the close button exists (form construction re-entrancy).
            /// </summary>
            public void LayoutChildren()
            {
                var client = ClientRectangle;
                if (client.Width <= 0 || client.Height <= 0) return;

                if (_closeButton != null)
                {
                    _closeButton.Location = new Point(Math.Max(0, client.Width - CloseButtonSize), 0);
                }

                var player = _playerHwnd;
                if (player != IntPtr.Zero)
                {
                    int videoHeight = Math.Max(0, client.Height - DragStripHeight);
                    MoveWindow(player, 0, DragStripHeight, client.Width, videoHeight, true);
                }
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                LayoutChildren();
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmNcHitTest)
                {
                    int lParam = m.LParam.ToInt32();
                    int screenX = unchecked((short)(lParam & 0xFFFF));
                    int screenY = unchecked((short)((lParam >> 16) & 0xFFFF));
                    var clientPoint = PointToClient(new Point(screenX, screenY));
                    m.Result = (IntPtr)HitTest(clientPoint, ClientSize);
                    return;
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

                try
                {
                    CloseRequested?.Invoke(null, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    // #region agent log
                    DebugSessionLog.Write("V1", "VideoWindow.RequestClose", "CloseRequested handler failed", new { error = ex.GetType().Name, message = ex.Message });
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

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}

#endif
