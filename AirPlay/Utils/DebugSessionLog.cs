using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AirPlay.Utils
{
    /// <summary>
    /// Unified debug diagnostics. All methods use [Conditional("DEBUG")] so Release builds
    /// omit call sites entirely. In Debug builds, writes NDJSON to the session log file and mirrors to console.
    /// </summary>
    internal static class DebugSessionLog
    {
#if DEBUG
        private const string SessionId = "e50611";
        private static readonly string LogPath = @"e:\__work\__github\Airplay2OnWindows\.cursor\debug-e50611.log";
        private static readonly object Gate = new object();

        public static int CZeroReads;
        public static int CErrors;
        public static int CPackets;
        public static int DZeroReads;
        public static int DErrors;
        public static int DPackets;
        public static int RtspDepth;
        public static int NicEvents;
#endif

        [Conditional("DEBUG")]
        public static void Write(string hypothesisId, string location, string message, object? data = null, string runId = "pre-fix")
        {
#if DEBUG
            try
            {
                var payload = new Dictionary<string, object?>
                {
                    ["sessionId"] = SessionId,
                    ["hypothesisId"] = hypothesisId,
                    ["location"] = location,
                    ["message"] = message,
                    ["data"] = data,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["runId"] = runId,
                };
                var json = JsonSerializer.Serialize(payload);
                lock (Gate)
                {
                    File.AppendAllText(LogPath, json + Environment.NewLine);
                }

                Console.WriteLine($"[agent:{hypothesisId}] {location} — {message}");
            }
            catch
            {
            }
#endif
        }

        [Conditional("DEBUG")]
        public static void WriteLine(string message)
        {
#if DEBUG
            Console.WriteLine(message);
#endif
        }

        [Conditional("DEBUG")]
        public static void WriteLine(string format, params object?[] args)
        {
#if DEBUG
            Console.WriteLine(format, args);
#endif
        }
    }
}
