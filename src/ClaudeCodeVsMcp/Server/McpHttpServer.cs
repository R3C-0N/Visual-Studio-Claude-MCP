using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Discovery;
using ClaudeCodeVsMcp.Infrastructure;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Server
{
    /// <summary>
    /// Transport HTTP du serveur MCP.
    ///
    /// Chaque instance de Visual Studio ecoute TOUJOURS sur son port d'instance, et tente EN PLUS
    /// de se lier au port hub fixe. Le bind reussi fait office d'election : il n'y a pas de course
    /// possible, le systeme d'exploitation arbitre. Claude Code garde ainsi une URL statique
    /// (le port hub) tandis que les ports d'instance servent au relais entre instances.
    ///
    /// La boucle d'acceptation tourne sur des threads de fond. Elle ne touche jamais a DTE
    /// directement : c'est UiThread.RunAsync, appele depuis les outils, qui bascule sur le thread UI.
    /// </summary>
    internal sealed class McpHttpServer : IDisposable
    {
        private const int MaxBodyBytes = 1024 * 1024;
        private static readonly TimeSpan HubRetryInterval = TimeSpan.FromSeconds(10);

        private readonly McpDispatcher _dispatcher;
        private readonly int _hubPort;
        private readonly object _gate = new object();

        private CancellationTokenSource _cts;
        private HttpListener _instanceListener;
        private HttpListener _hubListener;
        private Timer _hubRetryTimer;

        internal McpHttpServer(McpDispatcher dispatcher, int hubPort)
        {
            _dispatcher = dispatcher;
            _hubPort = hubPort;
        }

        internal int InstancePort { get; private set; }

        internal bool IsHub
        {
            get { lock (_gate) { return _hubListener != null; } }
        }

        internal int HubPort
        {
            get { return _hubPort; }
        }

        /// <summary>Notifie le package quand le role de hub est pris, pour rafraichir le registre.</summary>
        internal event Action HubStatusChanged;

        internal bool Start()
        {
            _cts = new CancellationTokenSource();

            int port;
            var listener = PortAllocator.BindFirstFree(out port);
            if (listener == null)
            {
                ExtensionLog.Error("Aucun port libre entre " + PortAllocator.InstancePortFirst +
                                   " et " + PortAllocator.InstancePortLast + ". Serveur MCP non demarre.");
                return false;
            }

            _instanceListener = listener;
            InstancePort = port;
            ExtensionLog.Info("Port d'instance : " + port + ".");

            StartAcceptLoop(_instanceListener, _cts.Token);

            TryBecomeHub();
            if (!IsHub)
            {
                ExtensionLog.Info("Le port hub " + _hubPort + " est deja pris par une autre instance. " +
                                  "Cette instance reste joignable via le hub, et reprendra le role si celui-ci se libere.");
                _hubRetryTimer = new Timer(_ => TryBecomeHub(), null, HubRetryInterval, HubRetryInterval);
            }

            return true;
        }

        /// <summary>
        /// Tente de prendre le role de hub. Le bind du port est lui-meme le mecanisme d'election.
        /// </summary>
        private void TryBecomeHub()
        {
            lock (_gate)
            {
                if (_hubListener != null) return;
                if (_cts == null || _cts.IsCancellationRequested) return;

                var listener = PortAllocator.TryBind(_hubPort);
                if (listener == null) return;

                _hubListener = listener;
                ExtensionLog.Info("Role de hub pris sur le port " + _hubPort +
                                  " : cette instance sert desormais de point d'entree a Claude Code.");

                StartAcceptLoop(_hubListener, _cts.Token);

                if (_hubRetryTimer != null)
                {
                    _hubRetryTimer.Dispose();
                    _hubRetryTimer = null;
                }
            }

            var handler = HubStatusChanged;
            if (handler != null)
            {
                try { handler(); } catch (Exception ex) { ExtensionLog.Error("Notification de changement de hub.", ex); }
            }
        }

        private void StartAcceptLoop(HttpListener listener, CancellationToken ct)
        {
            Task.Run(() => AcceptLoopAsync(listener, ct), ct).Forget();
        }

        private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    return; // listener arrete : sortie normale.
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    ExtensionLog.Error("Boucle d'acceptation HTTP interrompue.", ex);
                    return;
                }

                // Chaque requete est traitee a part : un build de 5 minutes ne doit pas
                // empecher d'accepter les requetes suivantes.
                Task.Run(() => HandleContextAsync(context, ct), ct).Forget();
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context, CancellationToken ct)
        {
            try
            {
                var request = context.Request;

                // Defense en profondeur : meme lie a 127.0.0.1, on refuse tout ce qui n'est pas loopback.
                if (request.RemoteEndPoint == null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address))
                {
                    await WriteStatusAsync(context, 403, "Connexions loopback uniquement.").ConfigureAwait(false);
                    return;
                }

                if (!IsOriginAllowed(request))
                {
                    // Protection anti DNS-rebinding exigee par la spec MCP pour un serveur HTTP local :
                    // sans elle, une page web quelconque pourrait piloter Visual Studio.
                    await WriteStatusAsync(context, 403, "En-tete Origin ou Host refuse.").ConfigureAwait(false);
                    return;
                }

                if (!IsAuthorized(request))
                {
                    await WriteJsonAsync(context, 401,
                        JsonRpcResponse.Error(null, JsonRpcErrors.Unauthorized, "Jeton absent ou invalide."))
                        .ConfigureAwait(false);
                    return;
                }

                if (!IsMcpPath(request))
                {
                    await WriteStatusAsync(context, 404,
                        "Point d'entree inconnu. Utiliser POST /mcp.").ConfigureAwait(false);
                    return;
                }

                if (request.HttpMethod == "GET")
                {
                    // Pas de flux SSE en v1 : le serveur ne pousse aucune notification.
                    context.Response.AddHeader("Allow", "POST, DELETE");
                    await WriteStatusAsync(context, 405, "Utiliser POST.").ConfigureAwait(false);
                    return;
                }

                if (request.HttpMethod == "DELETE")
                {
                    await WriteStatusAsync(context, 200, "Session fermee.").ConfigureAwait(false);
                    return;
                }

                if (request.HttpMethod != "POST")
                {
                    context.Response.AddHeader("Allow", "POST, DELETE");
                    await WriteStatusAsync(context, 405, "Methode non supportee.").ConfigureAwait(false);
                    return;
                }

                if (request.ContentLength64 > MaxBodyBytes)
                {
                    await WriteStatusAsync(context, 413, "Corps de requete trop volumineux.").ConfigureAwait(false);
                    return;
                }

                var body = await ReadBodyAsync(request).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body))
                {
                    await WriteJsonAsync(context, 400,
                        JsonRpcResponse.Error(null, JsonRpcErrors.InvalidRequest, "Corps vide.")).ConfigureAwait(false);
                    return;
                }

                var requestContext = new RequestContext
                {
                    SessionId = request.Headers["Mcp-Session-Id"],
                    IsForwarded = string.Equals(request.Headers[InstanceProxy.ForwardedHeader], "1", StringComparison.Ordinal)
                };

                await DispatchAsync(context, body, requestContext, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ExtensionLog.Error("Traitement de requete HTTP.", ex);
                try
                {
                    await WriteJsonAsync(context, 500,
                        JsonRpcResponse.Error(null, JsonRpcErrors.InternalError, ex.Message)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // La reponse est peut-etre deja partiellement ecrite : plus rien a faire.
                }
            }
        }

        private async Task DispatchAsync(HttpListenerContext context, string body, RequestContext requestContext, CancellationToken ct)
        {
            JToken parsed;
            try
            {
                parsed = JToken.Parse(body);
            }
            catch (JsonException ex)
            {
                await WriteJsonAsync(context, 400,
                    JsonRpcResponse.Error(null, JsonRpcErrors.ParseError, "JSON invalide : " + ex.Message))
                    .ConfigureAwait(false);
                return;
            }

            // Lot JSON-RPC : peu utilise par les clients MCP, mais trivial a supporter
            // et evite un echec surprise.
            if (parsed.Type == JTokenType.Array)
            {
                var responses = new JArray();
                foreach (var item in (JArray)parsed)
                {
                    var obj = item as JObject;
                    if (obj == null) continue;

                    var response = await _dispatcher.HandleAsync(obj, requestContext, ct).ConfigureAwait(false);
                    if (response != null) responses.Add(response);
                }

                if (responses.Count == 0)
                {
                    await WriteAcceptedAsync(context).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(context, 200, responses).ConfigureAwait(false);
                return;
            }

            var single = parsed as JObject;
            if (single == null)
            {
                await WriteJsonAsync(context, 400,
                    JsonRpcResponse.Error(null, JsonRpcErrors.InvalidRequest, "Objet JSON-RPC attendu."))
                    .ConfigureAwait(false);
                return;
            }

            var result = await _dispatcher.HandleAsync(single, requestContext, ct).ConfigureAwait(false);

            if (result == null)
            {
                // Notification : la spec Streamable HTTP impose 202 sans corps.
                await WriteAcceptedAsync(context).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(context, 200, result).ConfigureAwait(false);
        }

        /// <summary>
        /// Le listener est lie a la racine : on filtre ici le seul chemin servi, avec ou sans
        /// barre finale.
        /// </summary>
        private static bool IsMcpPath(HttpListenerRequest request)
        {
            var path = (request.Url?.AbsolutePath ?? string.Empty).TrimEnd('/');
            return path.Equals("/mcp", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAuthorized(HttpListenerRequest request)
        {
            var header = request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                if (TokenStore.Matches(header.Substring("Bearer ".Length).Trim())) return true;
            }

            var alternate = request.Headers["X-Claude-MCP-Token"];
            return !string.IsNullOrEmpty(alternate) && TokenStore.Matches(alternate.Trim());
        }

        private static bool IsOriginAllowed(HttpListenerRequest request)
        {
            var origin = request.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin) && !string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
            {
                Uri uri;
                if (!Uri.TryCreate(origin, UriKind.Absolute, out uri)) return false;
                if (!IsLoopbackHost(uri.Host)) return false;
            }

            var host = request.Headers["Host"];
            if (string.IsNullOrEmpty(host)) return true;

            var hostName = host.Split(':')[0];
            return IsLoopbackHost(hostName);
        }

        private static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;

            IPAddress address;
            return IPAddress.TryParse(host, out address) && IPAddress.IsLoopback(address);
        }

        private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        private static Task WriteJsonAsync(HttpListenerContext context, int statusCode, JToken payload)
        {
            return WriteAsync(context, statusCode, "application/json; charset=utf-8",
                payload.ToString(Formatting.None));
        }

        private static Task WriteStatusAsync(HttpListenerContext context, int statusCode, string message)
        {
            return WriteAsync(context, statusCode, "text/plain; charset=utf-8", message);
        }

        private static async Task WriteAcceptedAsync(HttpListenerContext context)
        {
            context.Response.StatusCode = 202;
            context.Response.ContentLength64 = 0;
            await Task.Yield();
            context.Response.Close();
        }

        private static async Task WriteAsync(HttpListenerContext context, int statusCode, string contentType, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;

            using (var output = context.Response.OutputStream)
            {
                await output.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
        }

        internal void Stop()
        {
            try { _cts?.Cancel(); } catch (Exception) { /* deja annule */ }

            lock (_gate)
            {
                if (_hubRetryTimer != null)
                {
                    _hubRetryTimer.Dispose();
                    _hubRetryTimer = null;
                }

                CloseListener(ref _hubListener);
                CloseListener(ref _instanceListener);
            }
        }

        private static void CloseListener(ref HttpListener listener)
        {
            if (listener == null) return;
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch (Exception)
            {
                // Arret de VS : rien d'utile a faire.
            }
            listener = null;
        }

        public void Dispose()
        {
            Stop();
            try { _cts?.Dispose(); } catch (Exception) { /* deja libere */ }
            _cts = null;
        }
    }
}
