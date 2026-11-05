using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using AirPlay.Listeners;
using AirPlay.Models;
using AirPlay.Models.Enums;
using AirPlay.Utils;

namespace AirPlay.Services.Implementations
{
    public class SessionManager
    {
        private static SessionManager _current = null;
        private ConcurrentDictionary<string, Session> _sessions;

        public static SessionManager Current => _current ?? (_current = new SessionManager());

        private SessionManager()
        {
            _sessions = new ConcurrentDictionary<string, Session>();
        }

        public Task<Session> GetSessionAsync(string key)
        {
            _sessions.TryGetValue(key, out Session _session);
            return Task.FromResult(_session ?? new Session(key));
        }

        /// <summary>
        /// Store session state. Callers must always retrieve the session via GetSessionAsync first,
        /// modify it, then save it back. The incoming session replaces the old one entirely,
        /// allowing fields to be intentionally set to null (e.g., during TEARDOWN cleanup).
        /// </summary>
        public Task CreateOrUpdateSessionAsync(string key, Session session)
        {
            _sessions.AddOrUpdate(key, session, (k, old) => session);
            return Task.CompletedTask;
        }

        public async Task ClearMirroringStateAsync(string sessionId, Action onMirroringStopped, string reason)
        {
            var session = await GetSessionAsync(sessionId);
            if (session.MirroringListener == null && session.StreamConnectionId == null)
            {
                return;
            }

            session.MirroringListener = null;
            session.SpsPps = null;
            session.StreamConnectionId = null;
            session.MirroringSession = null;
            await CreateOrUpdateSessionAsync(sessionId, session);

            // #region agent log
            DebugSessionLog.Write("H7", "SessionManager.ClearMirroringStateAsync", "mirroring state cleared", new { sessionId, reason });
            // #endregion

            onMirroringStopped?.Invoke();
        }

        public async Task ReleaseMirroringAsync(string sessionId, Action onMirroringStopped, string reason)
        {
            var session = await GetSessionAsync(sessionId);
            var listener = session.MirroringListener;
            if (listener == null && session.StreamConnectionId == null)
            {
                return;
            }

            session.MirroringListener = null;
            session.SpsPps = null;
            session.StreamConnectionId = null;
            session.MirroringSession = null;
            await CreateOrUpdateSessionAsync(sessionId, session);

            // #region agent log
            DebugSessionLog.Write("H7", "SessionManager.ReleaseMirroringAsync", "releasing mirroring", new { sessionId, reason, hadListener = listener != null });
            // #endregion

            if (listener != null)
            {
                try
                {
                    await listener.StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // #region agent log
                    DebugSessionLog.Write("H7", "SessionManager.ReleaseMirroringAsync", "listener StopAsync failed", new { message = ex.Message });
                    // #endregion
                }
            }

            onMirroringStopped?.Invoke();
        }

        public async Task ReleaseAllMirroringAsync(Action onMirroringStopped, string reason)
        {
            foreach (var key in _sessions.Keys.ToList())
            {
                if (_sessions.TryGetValue(key, out var session) && session.MirroringListener != null)
                {
                    await ReleaseMirroringAsync(key, onMirroringStopped, reason).ConfigureAwait(false);
                }
            }
        }
    }
}
