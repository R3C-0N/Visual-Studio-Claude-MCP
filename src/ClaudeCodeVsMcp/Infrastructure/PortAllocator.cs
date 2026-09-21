using System;
using System.Net;

namespace ClaudeCodeVsMcp.Infrastructure
{
    /// <summary>
    /// Allocation du port d'ecoute.
    ///
    /// HttpListener n'accepte PAS le port 0 : le prefixe doit nommer un port explicite et
    /// aucune API ne permet de relire le port reellement lie. On balaye donc une plage
    /// jusqu'a ce que Start() reussisse, ce qui est aussi le test de disponibilite le plus fiable.
    /// </summary>
    internal static class PortAllocator
    {
        internal const int InstancePortFirst = 51234;
        internal const int InstancePortLast = 51299;

        /// <summary>Port fixe du hub : c'est l'URL stable que Claude Code garde en configuration.</summary>
        internal const int DefaultHubPort = 5230;

        internal static string PrefixFor(int port)
        {
            // Adresse litterale de boucle locale : evite d'avoir besoin d'une urlacl ou de droits admin,
            // contrairement a "+" ou "*".
            //
            // Le prefixe couvre la RACINE et non "/mcp/" : HttpListener exige que le chemin demande
            // commence par le prefixe, donc un POST vers ".../mcp" (sans barre finale, ce qu'envoie
            // tout client MCP) ne correspondrait pas a un prefixe "/mcp/". Le routage du chemin est
            // fait dans le serveur.
            return "http://127.0.0.1:" + port + "/";
        }

        /// <summary>
        /// Tente d'ouvrir un listener sur le port demande. Renvoie null si le port est pris.
        /// L'appelant est proprietaire du listener retourne.
        /// </summary>
        internal static HttpListener TryBind(int port)
        {
            HttpListener listener = null;
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add(PrefixFor(port));
                listener.Start();
                return listener;
            }
            catch (HttpListenerException)
            {
                SafeClose(listener);
                return null;
            }
            catch (ObjectDisposedException)
            {
                SafeClose(listener);
                return null;
            }
        }

        /// <summary>Premier port libre de la plage d'instance.</summary>
        internal static HttpListener BindFirstFree(out int port)
        {
            for (var candidate = InstancePortFirst; candidate <= InstancePortLast; candidate++)
            {
                var listener = TryBind(candidate);
                if (listener != null)
                {
                    port = candidate;
                    return listener;
                }
            }

            port = 0;
            return null;
        }

        private static void SafeClose(HttpListener listener)
        {
            try { listener?.Close(); } catch (Exception) { /* deja ferme */ }
        }
    }
}
