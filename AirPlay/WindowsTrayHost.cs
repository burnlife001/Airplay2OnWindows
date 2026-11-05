#if USE_WINDOWS_TRAY

using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AirPlay.Utils;

namespace AirPlay
{
    internal static class WindowsTrayHost
    {
        public static async Task RunAsync(Func<CancellationToken, Task> runHost)
        {
            ConsoleTrayWindow.Attach();
            ConsoleTrayWindow.Hide();

            using var cts = new CancellationTokenSource();
            using var context = new TrayApplicationContext(cts);
            TrayStartupNotifier.Register(context.NotifyIcon);
            TrayStartupNotifier.ShowStarting();

            var hostTask = runHost(cts.Token);
            // #region agent log
            DebugSessionLog.Write("D", "WindowsTrayHost.RunAsync", "Application.Run starting", new { uiThreadId = Environment.CurrentManagedThreadId });
            // #endregion
            Application.Run(context);
            // #region agent log
            DebugSessionLog.Write("D", "WindowsTrayHost.RunAsync", "Application.Run exited", null);
            // #endregion

            cts.Cancel();
            try
            {
                await hostTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        internal sealed class TrayApplicationContext : ApplicationContext
        {
            private readonly NotifyIcon _notifyIcon;
            private readonly Icon _trayIcon;
            private readonly CancellationTokenSource _cts;

            public NotifyIcon NotifyIcon => _notifyIcon;

            public TrayApplicationContext(CancellationTokenSource cts)
            {
                _cts = cts;

                var menu = new ContextMenuStrip();
                var showItem = new ToolStripMenuItem("显示窗口");
                showItem.Click += (_, _) => ConsoleTrayWindow.Show();
                menu.Items.Add(showItem);

                var exitItem = new ToolStripMenuItem("退出");
                exitItem.Click += (_, _) => ExitApplication();
                menu.Items.Add(exitItem);

                _trayIcon = AirPlayTrayIcon.Load();

                _notifyIcon = new NotifyIcon
                {
                    Icon = _trayIcon,
                    Visible = true,
                    Text = "AirPlay",
                    ContextMenuStrip = menu,
                };

                _notifyIcon.MouseClick += (_, e) =>
                {
                    // #region agent log
                    DebugSessionLog.Write("D", "WindowsTrayHost.TrayIcon", "MouseClick", new { button = e.Button.ToString() });
                    // #endregion
                    if (e.Button == MouseButtons.Left)
                    {
                        ConsoleTrayWindow.Show();
                    }
                };
            }

            private void ExitApplication()
            {
                _notifyIcon.Visible = false;
                _cts.Cancel();
                ExitThread();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _notifyIcon.Dispose();
                    _trayIcon.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}

#endif
