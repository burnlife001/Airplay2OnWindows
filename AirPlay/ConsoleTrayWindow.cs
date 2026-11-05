#if USE_WINDOWS_TRAY

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using AirPlay.Utils;

namespace AirPlay
{
    internal static class ConsoleTrayWindow
    {
        private const int GwlWndproc = -4;
        private const uint WmClose = 0x0010;
        private const uint WmSize = 0x0005;
        private const uint WmSysCommand = 0x0112;
        private const int ScMinimize = 0xF020;
        private const int SizeMinimized = 1;
        private const int SwHide = 0;
        private const int SwRestore = 9;
        private const int WmSetIcon = 0x0080;
        private const int IconBig = 1;
        private const int IconSmall = 0;
        private const uint SwpNomove = 0x0002;
        private const uint SwpNosize = 0x0001;
        private const uint SwpNozorder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;
        private const uint SwpNoactivate = 0x0010;

        private static Icon? _iconSmall;
        private static Icon? _iconLarge;
        private static WndProc? _wndProc;
        private static ConsoleCtrlHandlerDelegate? _consoleCtrlHandler;

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private delegate bool ConsoleCtrlHandlerDelegate(CtrlTypes ctrlType);

        private enum CtrlTypes : uint
        {
            CtrlCEvent = 0,
            CtrlBreakEvent = 1,
            CtrlCloseEvent = 2,
            CtrlLogoffEvent = 5,
            CtrlShutdownEvent = 6,
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandlerDelegate handler, bool add);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);

        private static IntPtr _originalWndProc;
        private static IntPtr _attachedHandle;

        public static void Attach()
        {
            var handle = GetConsoleWindow();
            var className = new StringBuilder(64);
            if (handle != IntPtr.Zero)
            {
                _ = GetClassName(handle, className, className.Capacity);
            }

            // #region agent log
            DebugSessionLog.Write("A", "ConsoleTrayWindow.Attach", "十秒诊断", new
            {
                hwnd = handle.ToInt64(),
                windowClass = className.ToString(),
                wtSession = Environment.GetEnvironmentVariable("WT_SESSION") is not null,
                isPseudoConsole = className.ToString() == "PseudoConsoleWindow",
                isClassicConsole = className.ToString() == "ConsoleWindowClass",
            });
            // #endregion

            if (handle == IntPtr.Zero)
            {
                return;
            }

            EnsureSubclassAttached(handle);
            ApplyConsoleWindowIcon(handle);
        }

        public static void Hide()
        {
            var handle = GetConsoleWindow();
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var ok = ShowWindow(handle, SwHide);
            // #region agent log
            DebugSessionLog.Write("C", "ConsoleTrayWindow.Hide", "SW_HIDE", new { hwnd = handle.ToInt64(), showWindow = ok, visible = IsWindowVisible(handle), iconic = IsIconic(handle) });
            // #endregion
        }

        public static void Show()
        {
            var handle = GetConsoleWindow();
            if (handle == IntPtr.Zero)
            {
                return;
            }

            EnsureSubclassAttached(handle);
            ShowWindow(handle, SwRestore);
            ApplyConsoleWindowIcon(handle);
            SetForegroundWindow(handle);
        }

        private static void EnsureSubclassAttached(IntPtr handle)
        {
            _wndProc ??= ConsoleWindowWndProc;
            var ourProc = Marshal.GetFunctionPointerForDelegate(_wndProc);
            var currentProc = GetWindowLongPtr(handle, GwlWndproc);

            if (handle == _attachedHandle && currentProc == ourProc)
            {
                // #region agent log
                DebugSessionLog.Write("B", "ConsoleTrayWindow.EnsureSubclassAttached", "subclass already ours", new { hwnd = handle.ToInt64(), procMatch = true });
                // #endregion
                return;
            }

            if (currentProc != ourProc)
            {
                _originalWndProc = currentProc;
                SetWindowLongPtr(handle, GwlWndproc, ourProc);
            }

            _attachedHandle = handle;
            // #region agent log
            var after = GetWindowLongPtr(handle, GwlWndproc);
            DebugSessionLog.Write("B", "ConsoleTrayWindow.EnsureSubclassAttached", "subclass (re)attached", new { hwnd = handle.ToInt64(), replaced = currentProc != ourProc, afterMatch = after == ourProc, original = _originalWndProc.ToInt64() });
            // #endregion

            if (_consoleCtrlHandler == null)
            {
                _consoleCtrlHandler = OnConsoleCtrl;
                SetConsoleCtrlHandler(_consoleCtrlHandler, true);
            }
        }

        private static void ApplyConsoleWindowIcon(IntPtr handle)
        {
            EnsureConsoleIcons();

            if (_iconLarge is not null)
            {
                SendMessage(handle, WmSetIcon, (IntPtr)IconBig, _iconLarge.Handle);
            }

            if (_iconSmall is not null)
            {
                SendMessage(handle, WmSetIcon, (IntPtr)IconSmall, _iconSmall.Handle);
            }

            SetWindowPos(
                handle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNomove | SwpNosize | SwpNozorder | SwpFrameChanged | SwpNoactivate);
        }

        private static void EnsureConsoleIcons()
        {
            if (_iconSmall is not null && _iconLarge is not null)
            {
                return;
            }

            _iconSmall?.Dispose();
            _iconLarge?.Dispose();

            _iconSmall = AirPlayTrayIcon.TryLoadFromExecutable(16, 16)
                ?? AirPlayTrayIcon.LoadSized(16, 16);
            _iconLarge = AirPlayTrayIcon.TryLoadFromExecutable(32, 32)
                ?? AirPlayTrayIcon.LoadSized(32, 32);
        }

        private static bool OnConsoleCtrl(CtrlTypes ctrlType)
        {
            if (ctrlType == CtrlTypes.CtrlCloseEvent)
            {
                Hide();
                return true;
            }

            return false;
        }

        private static IntPtr ConsoleWindowWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WmClose)
            {
                // #region agent log
                DebugSessionLog.Write("B", "ConsoleTrayWindow.WndProc", "WM_CLOSE", new { hwnd = hWnd.ToInt64() });
                // #endregion
                Hide();
                return IntPtr.Zero;
            }

            if (msg == WmSysCommand)
            {
                var cmd = wParam.ToInt32() & 0xFFF0;
                // #region agent log
                DebugSessionLog.Write("B", "ConsoleTrayWindow.WndProc", "WM_SYSCOMMAND", new { hwnd = hWnd.ToInt64(), cmd, isMinimize = cmd == ScMinimize });
                // #endregion
                if (cmd == ScMinimize)
                {
                    Hide();
                    return IntPtr.Zero;
                }
            }

            if (msg == WmSize && wParam.ToInt32() == SizeMinimized)
            {
                // #region agent log
                DebugSessionLog.Write("B", "ConsoleTrayWindow.WndProc", "WM_SIZE minimized", new { hwnd = hWnd.ToInt64() });
                // #endregion
                Hide();
                return IntPtr.Zero;
            }

            return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
        }

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
            {
                return GetWindowLongPtr64(hWnd, nIndex);
            }

            return new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newValue)
        {
            if (IntPtr.Size == 8)
            {
                return SetWindowLongPtr64(hWnd, nIndex, newValue);
            }

            var previous = SetWindowLong32(hWnd, nIndex, newValue.ToInt32());
            return new IntPtr(previous);
        }

    }
}

#endif
