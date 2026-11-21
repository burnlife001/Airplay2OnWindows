using System;
using System.IO;
using System.Text;
#if USE_WINDOWS_TRAY
using System.Windows.Forms;
#endif

namespace AirPlay.Utils
{
    internal static class AppFileLog
    {
        private const long MaxLogSize = 2 * 1024 * 1024;
        private static readonly object SyncRoot = new object();
        private static TextWriter? _writer;
        private static bool _installed;

        public static void Install()
        {
            lock (SyncRoot)
            {
                if (_installed) return;
                _installed = true;

                try
                {
                    var streamWriter = CreateWriter();
                    if (streamWriter == null) return;

                    var writer = TextWriter.Synchronized(streamWriter);
                    _writer = writer;
                    Console.SetOut(writer);
                    Console.SetError(writer);

                    AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
#if USE_WINDOWS_TRAY
                    Application.ThreadException += OnThreadException;
#endif
                }
                catch
                {
                    // Logging must never prevent the application from starting.
                }
            }
        }

        private static StreamWriter? CreateWriter()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var preferredDirectory = string.IsNullOrEmpty(localAppData)
                ? null
                : Path.Combine(localAppData, "AirPlay", "Logs");
            var fallbackDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

            return TryCreateWriter(preferredDirectory) ?? TryCreateWriter(fallbackDirectory);
        }

        private static StreamWriter? TryCreateWriter(string? directory)
        {
            if (string.IsNullOrEmpty(directory)) return null;

            try
            {
                Directory.CreateDirectory(directory);
                var logPath = Path.Combine(directory, "airplay.log");
                if (File.Exists(logPath) && new FileInfo(logPath).Length > MaxLogSize)
                {
                    File.Move(logPath, logPath + ".1", true);
                }

                var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                return new StreamWriter(stream, new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
            }
            catch
            {
                return null;
            }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception exception)
            {
                WriteException("Unhandled exception", exception);
                return;
            }

            try
            {
                _writer?.WriteLine($"Unhandled exception: {e.ExceptionObject}");
            }
            catch
            {
            }
        }

#if USE_WINDOWS_TRAY
        private static void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            WriteException("UI thread exception", e.Exception);
        }
#endif

        private static void WriteException(string source, Exception exception)
        {
            try
            {
                _writer?.WriteLine($"{source}: {exception}");
            }
            catch
            {
            }
        }
    }
}
