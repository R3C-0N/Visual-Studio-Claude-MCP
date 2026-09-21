using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Discovery
{
    /// <summary>
    /// Relais HTTP vers une autre instance de Visual Studio. Utilise par l'instance hub
    /// quand l'outil appele cible une instance differente d'elle-meme.
    /// </summary>
    internal static class InstanceProxy
    {
        /// <summary>
        /// Marqueur anti-boucle : une requete deja relayee ne peut pas etre relayee une seconde fois.
        /// </summary>
        internal const string ForwardedHeader = "X-Vs-Mcp-Forwarded";

        private static readonly HttpClient Client = new HttpClient
        {
            // Volontairement long : un appel relaye peut etre un build ou une attente de point d'arret.
            Timeout = TimeSpan.FromMinutes(10)
        };

        internal static async Task<JObject> ForwardAsync(InstanceInfo target, JObject jsonRpcRequest, CancellationToken ct)
        {
            using (var message = new HttpRequestMessage(HttpMethod.Post, target.Url))
            {
                message.Content = new StringContent(
                    jsonRpcRequest.ToString(Newtonsoft.Json.Formatting.None),
                    Encoding.UTF8,
                    "application/json");

                message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + TokenStore.GetOrCreate());
                message.Headers.TryAddWithoutValidation(ForwardedHeader, "1");

                using (var response = await Client.SendAsync(message, ct).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(body))
                    {
                        throw new InvalidOperationException(
                            "L'instance " + target.Pid + " a repondu " + (int)response.StatusCode + " sans corps.");
                    }

                    return JObject.Parse(body);
                }
            }
        }
    }
}
