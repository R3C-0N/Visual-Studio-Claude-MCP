using System;
using System.Diagnostics;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    internal static class ToolInstaller
    {
        /// <summary>
        /// Construit le catalogue d'outils. Le catalogue est identique dans toutes les instances :
        /// c'est ce qui permet au hub de relayer n'importe quel appel sans negocier de capacites.
        /// </summary>
        internal static ToolRegistry Build(
            SessionStore sessions,
            BuildWatcher buildWatcher,
            DebugWatcher debugWatcher,
            BreakpointManager breakpoints,
            Func<JObject> serverInfo)
        {
            var registry = new ToolRegistry();

            InstanceTools.Register(registry, sessions);
            SolutionTools.Register(registry);
            BuildTools.Register(registry, buildWatcher);
            BreakpointTools.Register(registry, breakpoints);
            DebugTools.Register(registry, debugWatcher, buildWatcher);
            InspectTools.Register(registry);
            OutputTools.Register(registry);
            SelectionTools.Register(registry);

            registry.Add(
                "server_info",
                "Diagnostic du pont MCP lui-meme : version, port d'ecoute, role de hub. " +
                "A utiliser quand une autre commande echoue de maniere inattendue.",
                SchemaBuilder.New().Build(),
                (args, ctx, ct) =>
                {
                    var info = serverInfo() ?? new JObject();
                    info["pid"] = Process.GetCurrentProcess().Id;
                    info["serverVersion"] = McpDispatcher.ServerVersion;
                    info["sessionId"] = ctx.SessionId;
                    return Task.FromResult(info);
                },
                isGlobal: true);

            return registry;
        }
    }
}
