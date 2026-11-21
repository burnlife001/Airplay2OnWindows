#if USE_WINDOWS_TRAY

using System;
using System.Windows.Forms;

namespace AirPlay
{
    internal static class TrayStartupNotifier
    {
        private static NotifyIcon? _notifyIcon;

        public static void Register(NotifyIcon notifyIcon)
        {
            _notifyIcon = notifyIcon;
        }

        public static void ShowStarting()
        {
            SetTrayText("AirPlay — 启动中…");
        }

        public static void ShowReady(string? detail = null)
        {
            SetTrayText(string.IsNullOrEmpty(detail)
                ? "Airplay就绪，请在同网段局域网连接"
                : detail);
        }

        public static void ShowError(string message)
        {
            SetTrayText($"AirPlay — 启动失败：{message}");
        }

        private static void SetTrayText(string text)
        {
            if (_notifyIcon == null)
            {
                return;
            }

            RunOnUiThread(() => _notifyIcon.Text = text.Length > 63 ? text[..63] : text);
        }

        private static void RunOnUiThread(Action action)
        {
            if (_notifyIcon?.ContextMenuStrip?.InvokeRequired == true)
            {
                _notifyIcon.ContextMenuStrip.Invoke(action);
                return;
            }

            action();
        }
    }
}

#endif
