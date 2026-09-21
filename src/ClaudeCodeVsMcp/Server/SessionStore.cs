using System;
using System.Collections.Concurrent;

namespace ClaudeCodeVsMcp.Server
{
    /// <summary>
    /// Memorise l'instance de Visual Studio choisie via use_instance.
    ///
    /// La cle est le header Mcp-Session-Id quand le client en envoie un. Comme ce n'est pas
    /// garanti, un defaut global sert de repli : en pratique un seul client MCP parle a ce
    /// serveur a la fois.
    /// </summary>
    internal sealed class SessionStore
    {
        private readonly ConcurrentDictionary<string, int> _bySession =
            new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        private int _globalDefault;

        internal void Select(string sessionId, int pid)
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                _bySession[sessionId] = pid;
            }
            _globalDefault = pid;
        }

        internal void Clear(string sessionId)
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                int ignored;
                _bySession.TryRemove(sessionId, out ignored);
            }
            _globalDefault = 0;
        }

        /// <summary>Renvoie 0 si aucune instance n'a ete choisie.</summary>
        internal int Resolve(string sessionId)
        {
            int pid;
            if (!string.IsNullOrEmpty(sessionId) && _bySession.TryGetValue(sessionId, out pid))
            {
                return pid;
            }
            return _globalDefault;
        }
    }
}
