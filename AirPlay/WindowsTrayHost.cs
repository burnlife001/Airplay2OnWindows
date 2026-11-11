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
            ConsoleTrayWindow.BindRequestExit(context.RequestExit);
            TrayStartupNotifier.Register(context.NotifyIcon);
            TrayStartupNotifier.ShowStarting();

            var hostTask = runHost(cts.Token);
            ConsoleTrayWindow.InstallMinimizeHook();
            // #region agent log
            DebugSessionLog.Write("D", "WindowsTrayHost.RunAsync", "Application.Run starting", new { uiThreadId = Environment.CurrentManagedThreadId });
            // #endregion
            Application.Run(context);
            // #region agent log
            DebugSessionLog.Write("H6", "WindowsTrayHost.RunAsync", "Application.Run exited", new
            {
                cancelled = cts.IsCancellationRequested,
                thread = Environment.CurrentManagedThreadId,
                sync = SynchronizationContext.Current?.GetType().Name,
            });
            // #endregion

            VideoWindow.CloseForShutdown();

            ConsoleTrayWindow.Shutdown();
            // #region agent log
            DebugSessionLog.Write("H6", "WindowsTrayHost.RunAsync", "after Shutdown", null);
            // #endregion
            cts.Cancel();
            // #region agent log
            DebugSessionLog.Write("H6", "WindowsTrayHost.RunAsync", "after Cancel", new { cancelled = cts.IsCancellationRequested });
            // #endregion
            try
            {
                var finished = await Task.WhenAny(hostTask, Task.Delay(3000)).ConfigureAwait(false);
                // #region agent log
                DebugSessionLog.Write("H6", "WindowsTrayHost.RunAsync", finished == hostTask ? "hostTask finished within 3s" : "hostTask still running after 3s", new
                {
                    status = hostTask.Status.ToString(),
                    thread = Environment.CurrentManagedThreadId,
                });
                // #endregion
                await hostTask.ConfigureAwait(false);
                // #region agent log
                DebugSessionLog.Write("H6", "WindowsTrayHost.RunAsync", "hostTask awaited", null);
                // #endregion
            }
            catch (Exception ex)
            {
                // #region agent log
                DebugSessionLog.Write("H6", "WindowsTrayHost.RunAsync", "hostTask faulted", new { type = ex.GetType().Name, message = ex.Message });
                // #endregion
            }
        }

        internal sealed class TrayApplicationContext : ApplicationContext
        {
            private readonly NotifyIcon _notifyIcon;
            private readonly Icon _trayIcon;
            private readonly CancellationTokenSource _cts;
            private readonly SynchronizationContext? _uiContext;

            public NotifyIcon NotifyIcon => _notifyIcon;

            public TrayApplicationContext(CancellationTokenSource cts)
            {
                _cts = cts;
                _uiContext = SynchronizationContext.Current;

                VideoWindow.Initialize();

                var menu = new ContextMenuStrip();
                var showItem = new ToolStripMenuItem("显示窗口");
                showItem.Click += (_, _) => ConsoleTrayWindow.Show();
                menu.Items.Add(showItem);

                var exitItem = new ToolStripMenuItem("退出");
                exitItem.Click += (_, _) => RequestExit();
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

            public void RequestExit()
            {
                if (_uiContext != null && _uiContext != SynchronizationContext.Current)
                {
                    _uiContext.Post(_ => ExitApplication(), null);
                    return;
                }

                ExitApplication();
            }

            private void ExitApplication()
            {
                // #region agent log
                DebugSessionLog.Write("H6", "WindowsTrayHost.ExitApplication", "enter", new
                {
                    thread = Environment.CurrentManagedThreadId,
                    sync = SynchronizationContext.Current?.GetType().Name,
                });
                // #endregion
                _notifyIcon.Visible = false;
                ConsoleTrayWindow.Shutdown();
                // #region agent log
                DebugSessionLog.Write("H6", "WindowsTrayHost.ExitApplication", "before Cancel", null);
                // #endregion
                _cts.Cancel();
                // #region agent log
                DebugSessionLog.Write("H6", "WindowsTrayHost.ExitApplication", "before ExitThread", new { cancelled = _cts.IsCancellationRequested });
                // #endregion
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
