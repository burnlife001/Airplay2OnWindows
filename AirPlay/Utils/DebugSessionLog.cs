using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AirPlay.Utils
{
    internal static class DebugSessionLog
    {
        private const string SessionId = "03ba6d";
        private static readonly string LogPath = @"e:\__work\__github\Airplay2OnWindows\.cursor\debug-03ba6d.log";
        private static readonly object Gate = new object();

        public static void Write(string hypothesisId, string location, string message, object? data = null, string runId = "pre-fix")
        {
            // #region agent log
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
            }
            catch
            {
            }
            // #endregion
        }
    }
}
