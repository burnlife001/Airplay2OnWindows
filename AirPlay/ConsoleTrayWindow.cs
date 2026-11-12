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
        private const uint EventSystemMinimizeStart = 0x0016;
        private const uint WineventOutofcontext = 0;
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
        private static ConsoleCtrlHandlerDelegate? _consoleCtrlHandler;
        private static WinEventProc? _winEventCallback;
        private static IntPtr _minimizeHook;
        private static IntPtr _trackedConsoleHwnd;
        private static bool _skipLegacyWindowHooks;
        private static Action? _requestExit;

        public static void BindRequestExit(Action requestExit)
        {
            _requestExit = requestExit;
        }

        private delegate bool ConsoleCtrlHandlerDelegate(CtrlTypes ctrlType);

        private delegate void WinEventProc(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime);

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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandlerDelegate handler, bool add);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventProc lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

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

            _trackedConsoleHwnd = handle;
            _skipLegacyWindowHooks = className.ToString() == "PseudoConsoleWindow";

            EnsureConsoleCloseHandler();
            ApplyConsoleWindowIcon(handle);

            if (_skipLegacyWindowHooks)
            {
                // #region agent log
                DebugSessionLog.Write("A", "ConsoleTrayWindow.Attach", "ConPTY：跳过 WinEvent/WndProc 最小化 Hook", null);
                // #endregion
            }
        }

        /// <summary>须在即将调用 Application.Run 的同一线程上安装，以便 WinEvent 回调能派发。</summary>
        public static void InstallMinimizeHook()
        {
            if (_skipLegacyWindowHooks || _trackedConsoleHwnd == IntPtr.Zero || _minimizeHook != IntPtr.Zero)
            {
                return;
            }

            _winEventCallback = OnMinimizeWinEvent;
            _minimizeHook = SetWinEventHook(
                EventSystemMinimizeStart,
                EventSystemMinimizeStart,
                IntPtr.Zero,
                _winEventCallback,
                0,
                0,
                WineventOutofcontext);

            // #region agent log
            DebugSessionLog.Write("F", "ConsoleTrayWindow.InstallMinimizeHook", "SetWinEventHook", new
            {
                hook = _minimizeHook.ToInt64(),
                ok = _minimizeHook != IntPtr.Zero,
                consoleHwnd = _trackedConsoleHwnd.ToInt64(),
            });
            // #endregion
        }

        public static void Shutdown()
        {
            if (_minimizeHook == IntPtr.Zero)
            {
                return;
            }

            UnhookWinEvent(_minimizeHook);
            _minimizeHook = IntPtr.Zero;
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

            EnsureConsoleCloseHandler();
            ShowWindow(handle, SwRestore);
            ApplyConsoleWindowIcon(handle);
            SetForegroundWindow(handle);
        }

        private static void EnsureConsoleCloseHandler()
        {
            if (_consoleCtrlHandler != null)
            {
                return;
            }

            _consoleCtrlHandler = OnConsoleCtrl;
            SetConsoleCtrlHandler(_consoleCtrlHandler, true);
        }

        private static void OnMinimizeWinEvent(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime)
        {
            if (eventType != EventSystemMinimizeStart)
            {
                return;
            }

            var consoleHwnd = _trackedConsoleHwnd != IntPtr.Zero ? _trackedConsoleHwnd : GetConsoleWindow();
            if (hwnd != consoleHwnd)
            {
                return;
            }

            // #region agent log
            DebugSessionLog.Write("F", "ConsoleTrayWindow.OnMinimizeWinEvent", "EVENT_SYSTEM_MINIMIZESTART", new
            {
                hwnd = hwnd.ToInt64(),
                consoleHwnd = consoleHwnd.ToInt64(),
            });
            // #endregion

            Hide();
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
            if (ctrlType == CtrlTypes.CtrlCloseEvent
                || ctrlType == CtrlTypes.CtrlLogoffEvent
                || ctrlType == CtrlTypes.CtrlShutdownEvent)
            {
                // #region agent log
                DebugSessionLog.Write("H5", "ConsoleTrayWindow.OnConsoleCtrl", "request application exit", new
                {
                    ctrlType = ctrlType.ToString(),
                    hasExitHandler = _requestExit != null,
                }, runId: "post-fix");
                // #endregion
                _requestExit?.Invoke();
                return true;
            }

            return false;
        }

    }
}

#endif
