using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Discovery;
using ClaudeCodeVsMcp.Infrastructure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Server
{
    internal sealed class RequestContext
    {
        internal string SessionId { get; set; }

        /// <summary>Vrai si la requete a deja ete relayee par une autre instance.</summary>
        internal bool IsForwarded { get; set; }
    }

    /// <summary>
    /// Traduit les requetes JSON-RPC en appels d'outils, et route chaque appel vers la bonne
    /// instance de Visual Studio.
    /// </summary>
    internal sealed class McpDispatcher
    {
        /// <summary>
        /// Lue depuis l'assembly, elle-meme versionnee depuis source.extension.vsixmanifest :
        /// la version ne se saisit qu'a un seul endroit.
        /// </summary>
        internal static readonly string ServerVersion =
            typeof(McpDispatcher).Assembly.GetName().Version.ToString(3);

        /// <summary>Versions du protocole MCP supportees, de la plus recente a la plus ancienne.</summary>
        private static readonly string[] SupportedProtocols =
        {
            "2025-06-18", "2025-03-26", "2024-11-05"
        };

        private readonly ToolRegistry _registry;
        private readonly SessionStore _sessions;
        private readonly int _selfPid = Process.GetCurrentProcess().Id;

        /// <summary>
        /// EnvDTE tolere mal la reentrance, et deux debug_step simultanes mettraient l'etat du
        /// debogueur en vrac. Les executions LOCALES sont donc serialisees. Le verrou n'est
        /// jamais tenu pendant un relais vers une autre instance.
        /// </summary>
        private readonly SemaphoreSlim _localGate = new SemaphoreSlim(1, 1);

        internal McpDispatcher(ToolRegistry registry, SessionStore sessions)
        {
            _registry = registry;
            _sessions = sessions;
        }

        /// <summary>Renvoie null pour une notification : le transport repond alors 202 sans corps.</summary>
        internal async Task<JObject> HandleAsync(JObject raw, RequestContext ctx, CancellationToken ct)
        {
            var request = JsonRpcRequest.Parse(raw);

            if (string.IsNullOrEmpty(request.Method))
            {
                return request.IsNotification
                    ? null
                    : JsonRpcResponse.Error(request.Id, JsonRpcErrors.InvalidRequest, "Champ 'method' absent.");
            }

            if (request.IsNotification)
            {
                // notifications/initialized, notifications/cancelled... : rien a renvoyer.
                return null;
            }

            try
            {
                switch (request.Method)
                {
                    case "initialize":
                        return JsonRpcResponse.Success(request.Id, Initialize(request.Params));

                    case "ping":
                        return JsonRpcResponse.Success(request.Id, new JObject());

                    case "tools/list":
                        return JsonRpcResponse.Success(request.Id, new JObject { ["tools"] = _registry.Describe() });

                    case "tools/call":
                        return await CallToolAsync(raw, request, ctx, ct).ConfigureAwait(false);

                    default:
                        return JsonRpcResponse.Error(request.Id, JsonRpcErrors.MethodNotFound,
                            "Methode inconnue : " + request.Method);
                }
            }
            catch (McpToolException ex)
            {
                return JsonRpcResponse.Success(request.Id, ToolError(ex.Code, ex.Message));
            }
            catch (Exception ex)
            {
                ExtensionLog.Error("Echec du traitement de " + request.Method, ex);
                return JsonRpcResponse.Error(request.Id, JsonRpcErrors.InternalError, ex.Message);
            }
        }

        private static JObject Initialize(JObject parameters)
        {
            var requested = (string)parameters?["protocolVersion"];
            var negotiated = SupportedProtocols.Contains(requested) ? requested : SupportedProtocols[0];

            ExtensionLog.Info("initialize : le client demande le protocole " + (requested ?? "(non precise)") +
                              ", version negociee " + negotiated + ".");

            return new JObject
            {
                ["protocolVersion"] = negotiated,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject { ["listChanged"] = false }
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = "visual-studio",
                    ["version"] = ServerVersion
                },
                ["instructions"] =
                    "Pilote une ou plusieurs instances de Visual Studio ouvertes sur cette machine. " +
                    "Appeler list_instances quand plusieurs solutions peuvent etre ouvertes, puis use_instance " +
                    "pour fixer la cible ; sinon les outils s'appliquent a l'unique instance ouverte. " +
                    "Les builds et les commandes de debogage sont asynchrones : un statut 'running' n'est pas une " +
                    "erreur, c'est un etat legitime a re-interroger. " +
                    "evaluate, get_locals et get_stack exigent que le debogueur soit arrete sur un point d'arret."
            };
        }

        private async Task<JObject> CallToolAsync(JObject raw, JsonRpcRequest request, RequestContext ctx, CancellationToken ct)
        {
            var name = (string)request.Params["name"];
            var arguments = request.Params["arguments"] as JObject ?? new JObject();

            var tool = _registry.Find(name);
            if (tool == null)
            {
                return JsonRpcResponse.Success(request.Id,
                    ToolError("UNKNOWN_TOOL", "Outil inconnu : " + name));
            }

            // Les outils globaux (list_instances, use_instance, server_info) repondent toujours localement.
            if (!tool.IsGlobal)
            {
                InstanceInfo target;
                try
                {
                    target = ResolveTarget(arguments, ctx);
                }
                catch (McpToolException ex)
                {
                    return JsonRpcResponse.Success(request.Id, ToolError(ex.Code, ex.Message));
                }

                if (target != null && target.Pid != _selfPid)
                {
                    if (ctx.IsForwarded)
                    {
                        return JsonRpcResponse.Success(request.Id, ToolError("PROXY_LOOP",
                            "Requete deja relayee mais ciblant encore une autre instance (" + target.Pid +
                            "). Relais interrompu."));
                    }

                    return await ForwardAsync(raw, request, target, ct).ConfigureAwait(false);
                }
            }

            await _localGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var payload = await tool.Handler(arguments, ctx, ct).ConfigureAwait(false);
                return JsonRpcResponse.Success(request.Id, ToolSuccess(payload));
            }
            catch (McpToolException ex)
            {
                return JsonRpcResponse.Success(request.Id, ToolError(ex.Code, ex.Message));
            }
            catch (TimeoutException ex)
            {
                return JsonRpcResponse.Success(request.Id, ToolError("TIMEOUT", ex.Message));
            }
            catch (OperationCanceledException)
            {
                return JsonRpcResponse.Success(request.Id, ToolError("CANCELLED", "Appel annule."));
            }
            catch (Exception ex)
            {
                ExtensionLog.Error("Echec de l'outil " + name, ex);
                return JsonRpcResponse.Success(request.Id, ToolError("INTERNAL", ex.Message));
            }
            finally
            {
                _localGate.Release();
            }
        }

        private async Task<JObject> ForwardAsync(JObject raw, JsonRpcRequest request, InstanceInfo target, CancellationToken ct)
        {
            try
            {
                var response = await InstanceProxy.ForwardAsync(target, raw, ct).ConfigureAwait(false);
                // L'instance cible repond avec l'id de la requete relayee ; on le realigne par securite.
                response["id"] = request.Id ?? JValue.CreateNull();
                return response;
            }
            catch (Exception ex)
            {
                ExtensionLog.Error("Relais vers l'instance " + target.Pid + " impossible.", ex);
                return JsonRpcResponse.Success(request.Id, ToolError("INSTANCE_UNREACHABLE",
                    "L'instance " + target.Pid + " (" + (target.SolutionName ?? "sans solution") +
                    ") n'a pas repondu : " + ex.Message));
            }
        }

        /// <summary>
        /// Determine l'instance a piloter. Ne devine jamais en silence quand plusieurs instances
        /// sont ouvertes : une erreur enumerant les choix est plus utile qu'un build lance dans
        /// la mauvaise solution.
        /// </summary>
        private InstanceInfo ResolveTarget(JObject arguments, RequestContext ctx)
        {
            var instances = InstanceRegistry.ReadAll();
            if (instances.Count == 0) return null;

            var requested = Args.Str(arguments, "instance");
            if (requested != null)
            {
                var match = Match(instances, requested);
                if (match == null)
                {
                    throw McpToolException.NotFound(
                        "Aucune instance de Visual Studio ne correspond a '" + requested + "'. " +
                        "Instances ouvertes : " + Describe(instances));
                }
                return match;
            }

            var sticky = _sessions.Resolve(ctx.SessionId);
            if (sticky != 0)
            {
                var match = instances.FirstOrDefault(i => i.Pid == sticky);
                if (match != null) return match;
                // L'instance choisie a ete fermee : on retombe sur la resolution automatique.
            }

            if (instances.Count == 1) return instances[0];

            throw new McpToolException("AMBIGUOUS_INSTANCE",
                "Plusieurs instances de Visual Studio sont ouvertes. Preciser le parametre 'instance' " +
                "ou appeler use_instance. Instances : " + Describe(instances));
        }

        private static InstanceInfo Match(IEnumerable<InstanceInfo> instances, string requested)
        {
            var list = instances.ToList();

            int pid;
            if (int.TryParse(requested, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
            {
                var byPid = list.FirstOrDefault(i => i.Pid == pid);
                if (byPid != null) return byPid;
            }

            var exact = list.FirstOrDefault(i =>
                string.Equals(i.SolutionPath, requested, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.SolutionName, requested, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            var partial = list.Where(i =>
                (!string.IsNullOrEmpty(i.SolutionName) &&
                 i.SolutionName.IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (!string.IsNullOrEmpty(i.SolutionPath) &&
                 i.SolutionPath.IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();

            // Une correspondance partielle ambigue est traitee comme une absence de correspondance.
            return partial.Count == 1 ? partial[0] : null;
        }

        private static string Describe(IEnumerable<InstanceInfo> instances)
        {
            return string.Join(", ", instances.Select(i =>
                "pid " + i.Pid + " => " +
                (string.IsNullOrEmpty(i.SolutionName) ? "(aucune solution)" : i.SolutionName)));
        }

        private static JObject ToolSuccess(JObject payload)
        {
            var body = payload ?? new JObject();
            if (body["ok"] == null) body["ok"] = true;

            return new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = body.ToString(Formatting.None)
                    }
                },
                ["isError"] = false
            };
        }

        private static JObject ToolError(string code, string message)
        {
            var body = new JObject
            {
                ["ok"] = false,
                ["code"] = code,
                ["error"] = message
            };

            return new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = body.ToString(Formatting.None)
                    }
                },
                ["isError"] = true
            };
        }
    }
}
