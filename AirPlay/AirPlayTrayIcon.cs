#if USE_WINDOWS_TRAY

using System;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace AirPlay
{
    internal static class AirPlayTrayIcon
    {
        public static Icon Load()
        {
            using var stream = OpenIconStream();
            return new Icon(stream);
        }

        public static Icon LoadSized(int width, int height)
        {
            using var stream = OpenIconStream();
            using var source = new Icon(stream);
            return new Icon(source, new Size(width, height));
        }

        public static Icon? TryLoadFromExecutable(int width, int height)
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                return null;
            }

            using var associated = Icon.ExtractAssociatedIcon(exePath);
            if (associated is null)
            {
                return null;
            }

            return new Icon(associated, new Size(width, height));
        }

        private static Stream OpenIconStream()
        {
            var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("AirPlay.Resources.airplay.ico");
            if (stream is null)
            {
                throw new InvalidOperationException("Missing embedded resource AirPlay.Resources.airplay.ico");
            }

            return stream;
        }
    }
}

#endif
