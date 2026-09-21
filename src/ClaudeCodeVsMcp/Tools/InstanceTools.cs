using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Discovery;
using ClaudeCodeVsMcp.Server;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    /// <summary>
    /// Outils de selection d'instance. Ils sont marques globaux : ils repondent toujours sur
    /// le noeud qui recoit la requete (le hub) et ne sont jamais relayes.
    /// </summary>
    internal static class InstanceTools
    {
        internal static void Register(ToolRegistry registry, SessionStore sessions)
        {
            var selfPid = Process.GetCurrentProcess().Id;

            registry.Add(
                "list_instances",
                "Liste les instances de Visual Studio ouvertes sur cette machine, avec la solution chargee " +
                "dans chacune. A appeler en premier quand plusieurs solutions peuvent etre ouvertes, pour " +
                "choisir laquelle piloter.",
                SchemaBuilder.New().Build(),
                (args, ctx, ct) =>
                {
                    var instances = InstanceRegistry.ReadAll();
                    var sticky = sessions.Resolve(ctx.SessionId);

                    var array = new JArray(instances.Select(i => new JObject
                    {
                        ["pid"] = i.Pid,
                        ["port"] = i.Port,
                        ["isHub"] = i.IsHub,
                        ["isSelf"] = i.Pid == selfPid,
                        ["isSelected"] = sticky != 0 && i.Pid == sticky,
                        ["solutionName"] = string.IsNullOrEmpty(i.SolutionName) ? null : i.SolutionName,
                        ["solutionPath"] = string.IsNullOrEmpty(i.SolutionPath) ? null : i.SolutionPath,
                        ["vsVersion"] = i.VsVersion,
                        ["startedUtc"] = i.StartedUtc.ToString("o")
                    }));

                    return Task.FromResult(new JObject
                    {
                        ["count"] = instances.Count,
                        ["instances"] = array,
                        ["note"] = instances.Count > 1 && sticky == 0
                            ? "Plusieurs instances ouvertes : appeler use_instance, ou passer 'instance' a chaque outil."
                            : null
                    });
                },
                isGlobal: true);

            registry.Add(
                "use_instance",
                "Fixe l'instance de Visual Studio que les outils suivants piloteront, pour ne plus avoir a " +
                "passer 'instance' a chaque appel. Accepte un PID ou un nom de solution.",
                SchemaBuilder.New()
                    .Str("instance", "PID ou nom de solution de l'instance a selectionner.", required: true)
                    .Build(),
                (args, ctx, ct) =>
                {
                    var requested = Args.RequireStr(args, "instance");
                    var instances = InstanceRegistry.ReadAll();

                    var match = instances.FirstOrDefault(i =>
                                    i.Pid.ToString() == requested ||
                                    string.Equals(i.SolutionName, requested, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(i.SolutionPath, requested, StringComparison.OrdinalIgnoreCase));

                    if (match == null)
                    {
                        var partial = instances.Where(i =>
                            (!string.IsNullOrEmpty(i.SolutionName) &&
                             i.SolutionName.IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0) ||
                            (!string.IsNullOrEmpty(i.SolutionPath) &&
                             i.SolutionPath.IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();

                        if (partial.Count == 1) match = partial[0];
                    }

                    if (match == null)
                    {
                        throw McpToolException.NotFound(
                            "Aucune instance ne correspond a '" + requested + "'. Appeler list_instances pour voir les choix.");
                    }

                    sessions.Select(ctx.SessionId, match.Pid);

                    return Task.FromResult(new JObject
                    {
                        ["selected"] = new JObject
                        {
                            ["pid"] = match.Pid,
                            ["solutionName"] = match.SolutionName,
                            ["solutionPath"] = match.SolutionPath
                        }
                    });
                },
                isGlobal: true);
        }
    }
}
