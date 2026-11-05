#if USE_WINDOWS_TRAY

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AirPlay
{
    internal static class WindowsTrayHost
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SwHide = 0;

        public static async Task RunAsync(Func<CancellationToken, Task> runHost)
        {
            ShowWindow(GetConsoleWindow(), SwHide);

            using var cts = new CancellationTokenSource();
            var hostTask = runHost(cts.Token);

            using var context = new TrayApplicationContext(cts);
            Application.Run(context);

            cts.Cancel();
            try
            {
                await hostTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private sealed class TrayApplicationContext : ApplicationContext
        {
            private readonly NotifyIcon _notifyIcon;
            private readonly CancellationTokenSource _cts;

            public TrayApplicationContext(CancellationTokenSource cts)
            {
                _cts = cts;

                var menu = new ContextMenuStrip();
                var exitItem = new ToolStripMenuItem("退出");
                exitItem.Click += (_, _) => ExitApplication();
                menu.Items.Add(exitItem);

                _notifyIcon = new NotifyIcon
                {
                    Icon = SystemIcons.Application,
                    Visible = true,
                    Text = "AirPlay",
                    ContextMenuStrip = menu
                };

                _notifyIcon.MouseClick += (_, e) =>
                {
                    if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
                    {
                        menu.Show(Cursor.Position);
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
                }

                base.Dispose(disposing);
            }
        }
    }
}

#endif
