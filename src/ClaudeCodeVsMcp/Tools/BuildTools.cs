using System;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using EnvDTE;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    internal static class BuildTools
    {
        /// <summary>
        /// Plafond de l'attente cote serveur. Un client MCP a son propre timeout d'appel
        /// (de l'ordre de la minute) : depasser ce plafond garantirait une erreur cote client
        /// plutot qu'une reponse "en cours" exploitable.
        /// </summary>
        private const int MaxWaitMs = 120000;

        internal static void Register(ToolRegistry registry, BuildWatcher watcher)
        {
            registry.Add(
                "build",
                "Compile la solution ou un projet, et renvoie le verdict avec les erreurs. " +
                "Si le build depasse wait_ms, la reponse a le statut 'running' : ce n'est pas une erreur, " +
                "rappeler build_status pour obtenir le verdict.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("action", "Operation a realiser.", allowed: new[] { "build", "rebuild", "clean" }, defaultValue: "build")
                    .Str("project", "Nom ou uniqueName d'un projet. Par defaut, toute la solution.")
                    .Int("wait_ms", "Duree d'attente maximale en millisecondes.", defaultValue: 90000)
                    .Build(),
                async (args, ctx, ct) =>
                {
                    var action = Args.Str(args, "action", "build");
                    var project = Args.Str(args, "project");
                    var waitMs = Math.Min(Math.Max(1000, Args.Int(args, "wait_ms", 90000)), MaxWaitMs);

                    // Lancement sur le thread UI, attente en arriere-plan : bloquer le thread UI
                    // ici empecherait le build de progresser et figerait l'IDE.
                    var started = await UiThread.RunAsync(() =>
                    {
                        var solution = DteProvider.RequireSolution();
                        var build = solution.SolutionBuild;

                        if (build.BuildState == vsBuildState.vsBuildStateInProgress)
                        {
                            return new JObject { ["alreadyRunning"] = true };
                        }

                        var task = watcher.Arm();

                        switch (action.ToLowerInvariant())
                        {
                            case "clean":
                                build.Clean(false);
                                break;

                            case "rebuild":
                                // SolutionBuild n'expose pas de Rebuild : on passe par la commande,
                                // qui est non bloquante mais ne renvoie aucun statut.
                                DteProvider.Dte.ExecuteCommand(
                                    project == null ? "Build.RebuildSolution" : "Build.RebuildSelection");
                                break;

                            default:
                                if (project == null)
                                {
                                    build.Build(false);
                                }
                                else
                                {
                                    var found = SolutionHelper.FindProject(solution, project);
                                    build.BuildProject(build.ActiveConfiguration.Name, found.UniqueName, false);
                                }
                                break;
                        }

                        return new JObject { ["alreadyRunning"] = false };
                    }, ct);

                    var pending = watcher.Pending;
                    if (pending == null)
                    {
                        return new JObject
                        {
                            ["status"] = "unknown",
                            ["hint"] = "Aucune attente de build armee. Appeler build_status."
                        };
                    }

                    var wasAlreadyRunning = (bool)started["alreadyRunning"];

                    var completed = await Task.WhenAny(pending, Task.Delay(waitMs, ct)).ConfigureAwait(false);
                    if (completed != pending)
                    {
                        return new JObject
                        {
                            ["status"] = "running",
                            ["action"] = action,
                            ["joinedExistingBuild"] = wasAlreadyRunning,
                            ["hint"] = "Le build depasse wait_ms. Rappeler build_status pour le verdict."
                        };
                    }

                    var outcome = await pending.ConfigureAwait(false);
                    return await SummarizeAsync(outcome, action, ct).ConfigureAwait(false);
                });

            registry.Add(
                "build_status",
                "Etat du build en cours, ou verdict du dernier build termine.",
                SchemaBuilder.New()
                    .Instance()
                    .Int("wait_ms", "Attente supplementaire si un build est en cours.", defaultValue: 0)
                    .Build(),
                async (args, ctx, ct) =>
                {
                    var waitMs = Math.Min(Math.Max(0, Args.Int(args, "wait_ms", 0)), MaxWaitMs);

                    var state = await UiThread.RunAsync(() =>
                    {
                        var solution = DteProvider.RequireSolution();
                        return solution.SolutionBuild.BuildState.ToString();
                    }, ct);

                    var pending = watcher.Pending;
                    if (pending == null)
                    {
                        return new JObject
                        {
                            ["status"] = "none",
                            ["buildState"] = state,
                            ["hint"] = "Aucun build lance via MCP depuis le demarrage de cette instance."
                        };
                    }

                    if (!pending.IsCompleted && waitMs > 0)
                    {
                        await Task.WhenAny(pending, Task.Delay(waitMs, ct)).ConfigureAwait(false);
                    }

                    if (!pending.IsCompleted)
                    {
                        return new JObject { ["status"] = "running", ["buildState"] = state };
                    }

                    var outcome = await pending.ConfigureAwait(false);
                    return await SummarizeAsync(outcome, outcome.Action, ct).ConfigureAwait(false);
                });

            registry.Add(
                "cancel_build",
                "Annule le build en cours.",
                SchemaBuilder.New().Instance().Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    DteProvider.Dte.ExecuteCommand("Build.Cancel");
                    return new JObject { ["cancelled"] = true };
                }, ct));

            registry.Add(
                "get_errors",
                "Erreurs et avertissements de la fenetre Liste d'erreurs, sous forme structuree " +
                "(fichier, ligne, colonne, projet) plutot que du texte brut de la fenetre Sortie.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("severity", "Niveau a retourner.", allowed: new[] { "error", "warning", "message", "all" }, defaultValue: "error")
                    .Int("max_items", "Nombre maximum d'elements retournes.", defaultValue: 100)
                    .Str("project", "Ne garder que les entrees dont le projet contient ce texte.")
                    .Str("file", "Ne garder que les entrees dont le fichier contient ce texte.")
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var result = ErrorListReader.Read(
                        Args.Str(args, "severity", "error"),
                        Math.Max(1, Args.Int(args, "max_items", 100)),
                        Args.Str(args, "project"),
                        Args.Str(args, "file"));

                    return new JObject
                    {
                        ["matched"] = result.Total,
                        ["returned"] = result.Items.Count,
                        ["truncated"] = result.Truncated,
                        ["totals"] = new JObject
                        {
                            ["errors"] = result.Errors,
                            ["warnings"] = result.Warnings,
                            ["messages"] = result.Messages
                        },
                        ["items"] = result.Items,
                        // Limite assumee : EnvDTE.ErrorItem n'expose pas le code (CS0103, ...).
                        ["note"] = "Le champ 'code' est toujours null : EnvDTE ne l'expose pas."
                    };
                }, ct));
        }

        /// <summary>Complete le verdict du build par les erreurs de la Liste d'erreurs.</summary>
        private static async Task<JObject> SummarizeAsync(BuildOutcome outcome, string action, CancellationToken ct)
        {
            return await UiThread.RunAsync(() =>
            {
                var errors = ErrorListReader.Read("error", 20, null, null);

                return new JObject
                {
                    ["status"] = outcome.Succeeded ? "succeeded" : "failed",
                    ["action"] = action,
                    ["failedProjects"] = outcome.FailedProjects,
                    ["durationMs"] = outcome.DurationMs,
                    ["errorCount"] = errors.Errors,
                    ["warningCount"] = errors.Warnings,
                    ["errors"] = errors.Items
                };
            }, ct);
        }
    }
}
