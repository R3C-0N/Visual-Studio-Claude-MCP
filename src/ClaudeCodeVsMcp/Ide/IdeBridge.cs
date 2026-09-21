using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Ide
{
    /// <summary>
    /// Second transport : le protocole d'integration IDE de Claude Code.
    ///
    /// Contrairement au transport HTTP, celui-ci est BIDIRECTIONNEL : une fois Claude Code
    /// connecte, l'IDE peut lui pousser des notifications, ce qui rend possible le bouton
    /// "Envoyer a Claude". C'est la seule voie pour cela : MCP sur HTTP est strictement
    /// pull, le serveur n'y a jamais la parole en premier.
    ///
    /// La couche JSON-RPC est exactement la meme : on reutilise McpDispatcher tel quel, et
    /// Claude Code voit les memes outils que par le pont HTTP.
    ///
    /// Protocole INTERNE a Claude Code, reproduit depuis l'extension Visual Studio Code
    /// officielle. Non documente, susceptible de changer : en cas de rupture, le pont HTTP
    /// continue de fonctionner et seul le push est perdu.
    /// </summary>
    internal sealed class IdeBridge : IDisposable
    {
        private const string AuthHeader = "x-claude-code-ide-authorization";
        private const int MinPort = 10000;
        private const int MaxPort = 65535;
        private const int PortAttempts = 50;

        private readonly McpDispatcher _dispatcher;
        private readonly Random _random = new Random();
        private readonly object _gate = new object();

        /// <summary>Un WebSocket n'admet qu'un envoi a la fois.</summary>
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);

        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private WebSocket _client;
        private string _authToken;

        internal IdeBridge(McpDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        internal int Port { get; private set; }

        internal bool IsConnected
        {
            get { lock (_gate) { return _client != null && _client.State == WebSocketState.Open; } }
        }

        internal bool Start(int pid, string ideName, IEnumerable<string> workspaceFolders)
        {
            _cts = new CancellationTokenSource();
            _authToken = Guid.NewGuid().ToString();

            IdeLockFile.PurgeStale();

            for (var attempt = 0; attempt < PortAttempts; attempt++)
            {
                var port = _random.Next(MinPort, MaxPort);
                var listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");

                try
                {
                    listener.Start();
                }
                catch (HttpListenerException)
                {
                    try { listener.Close(); } catch (Exception) { }
                    continue;
                }

                _listener = listener;
                Port = port;

                IdeLockFile.Write(port, _authToken, pid, ideName, workspaceFolders);
                Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token).Forget();

                ExtensionLog.Info("Integration IDE a l'ecoute sur le port " + port +
                                  ". Dans Claude Code : /ide, puis choisir cette instance.");
                return true;
            }

            ExtensionLog.Error("Aucun port libre pour l'integration IDE : le push est desactive.");
            return false;
        }

        /// <summary>Republie le lockfile, par exemple quand la solution ouverte change.</summary>
        internal void UpdateWorkspace(int pid, string ideName, IEnumerable<string> workspaceFolders)
        {
            if (Port == 0 || _authToken == null) return;
            IdeLockFile.Write(Port, _authToken, pid, ideName, workspaceFolders);
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (Exception ex)
                {
                    ExtensionLog.Error("Boucle d'acceptation de l'integration IDE interrompue.", ex);
                    return;
                }

                try
                {
                    if (!context.Request.IsWebSocketRequest)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        continue;
                    }

                    if (!string.Equals(context.Request.Headers[AuthHeader], _authToken, StringComparison.Ordinal))
                    {
                        ExtensionLog.Warn("Connexion refusee sur l'integration IDE : jeton invalide.");
                        context.Response.StatusCode = 401;
                        context.Response.Close();
                        continue;
                    }

                    var wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                    ExtensionLog.Info("Claude Code est connecte a l'integration IDE.");

                    WebSocket previous;
                    lock (_gate)
                    {
                        previous = _client;
                        _client = wsContext.WebSocket;
                    }

                    // Claude Code ne maintient qu'une connexion : on ferme la precedente.
                    if (previous != null)
                    {
                        try { previous.Dispose(); } catch (Exception) { }
                    }

                    Task.Run(() => ReceiveLoopAsync(wsContext.WebSocket, ct), ct).Forget();
                }
                catch (Exception ex)
                {
                    ExtensionLog.Error("Etablissement de la connexion IDE impossible.", ex);
                }
            }
        }

        private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];

            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                string message;
                try
                {
                    message = await ReceiveMessageAsync(socket, buffer, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ExtensionLog.Warn("Connexion IDE interrompue : " + ex.Message);
                    break;
                }

                if (message == null) break;
                if (string.IsNullOrWhiteSpace(message)) continue;

                // Chaque message est traite a part : un build lance depuis Claude Code ne doit
                // pas empecher de recevoir les suivants.
                var payload = message;
                Task.Run(() => HandleMessageAsync(socket, payload, ct), ct).Forget();
            }

            lock (_gate)
            {
                if (ReferenceEquals(_client, socket)) _client = null;
            }
            ExtensionLog.Info("Claude Code s'est deconnecte de l'integration IDE.");
        }

        private static async Task<string> ReceiveMessageAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
        {
            var builder = new StringBuilder();

            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close) return null;

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage) return builder.ToString();
            }
        }

        private async Task HandleMessageAsync(WebSocket socket, string message, CancellationToken ct)
        {
            try
            {
                var parsed = JObject.Parse(message);

                var response = await _dispatcher.HandleAsync(parsed, new RequestContext
                {
                    SessionId = "ide",
                    IsForwarded = false
                }, ct).ConfigureAwait(false);

                // null = notification entrante : rien a renvoyer.
                if (response != null) await SendAsync(socket, response, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                ExtensionLog.Warn("Message IDE illisible : " + ex.Message);
            }
            catch (Exception ex)
            {
                ExtensionLog.Error("Traitement d'un message IDE.", ex);
            }
        }

        private async Task SendAsync(WebSocket socket, JObject payload, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));

            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (socket.State != WebSocketState.Open) return;
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        /// <summary>
        /// Pousse une @-mention dans le prompt de Claude Code.
        ///
        /// ATTENTION : la charge utile ne transporte PAS de texte, seulement un chemin et une
        /// plage de lignes ; Claude Code relit le fichier lui-meme. Pousser du texte qui ne vit
        /// pas dans un fichier (sortie de console) impose donc de l'ecrire dans un fichier
        /// temporaire et de mentionner celui-ci.
        ///
        /// Les lignes sont 0-BASED, contrairement a EnvDTE qui compte a partir de 1.
        /// </summary>
        internal async Task<bool> SendAtMentionAsync(string filePath, int? zeroBasedStart, int? zeroBasedEnd, CancellationToken ct)
        {
            WebSocket socket;
            lock (_gate) { socket = _client; }

            if (socket == null || socket.State != WebSocketState.Open) return false;

            var parameters = new JObject { ["filePath"] = filePath };
            if (zeroBasedStart.HasValue && zeroBasedEnd.HasValue)
            {
                parameters["lineStart"] = zeroBasedStart.Value;
                parameters["lineEnd"] = zeroBasedEnd.Value;
            }

            await SendAsync(socket, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "at_mentioned",
                ["params"] = parameters
            }, ct).ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// Informe Claude Code de la selection courante. Purement contextuel : cela ne modifie
        /// pas le prompt, contrairement a at_mentioned.
        /// </summary>
        internal async Task<bool> SendSelectionChangedAsync(string filePath, string text,
            int zeroBasedStartLine, int zeroBasedStartChar, int zeroBasedEndLine, int zeroBasedEndChar, CancellationToken ct)
        {
            WebSocket socket;
            lock (_gate) { socket = _client; }

            if (socket == null || socket.State != WebSocketState.Open) return false;

            await SendAsync(socket, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "selection_changed",
                ["params"] = new JObject
                {
                    ["text"] = text ?? string.Empty,
                    ["filePath"] = filePath,
                    ["fileUrl"] = new Uri(filePath).AbsoluteUri,
                    ["selection"] = new JObject
                    {
                        ["start"] = new JObject { ["line"] = zeroBasedStartLine, ["character"] = zeroBasedStartChar },
                        ["end"] = new JObject { ["line"] = zeroBasedEndLine, ["character"] = zeroBasedEndChar },
                        ["isEmpty"] = string.IsNullOrEmpty(text)
                    }
                }
            }, ct).ConfigureAwait(false);

            return true;
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch (Exception) { }

            if (Port != 0) IdeLockFile.Remove(Port);

            lock (_gate)
            {
                try { _client?.Dispose(); } catch (Exception) { }
                _client = null;
            }

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (Exception) { }
            _listener = null;

            try { _cts?.Dispose(); } catch (Exception) { }
            _cts = null;
        }
    }
}
