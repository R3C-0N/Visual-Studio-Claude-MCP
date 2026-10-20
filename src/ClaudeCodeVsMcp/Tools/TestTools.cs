using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using EnvDTE;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    /// <summary>
    /// Explorateur de tests : decouverte, execution, resultats, annulation et debogage, par les
    /// services internes de TestWindow (voir TestWindowBridge) plutot qu'en lisant la fenetre.
    ///
    /// Meme contrat que build : run_tests attend au plus wait_ms puis repond 'running', et
    /// test_status prend le relais. L'Explorateur compile lui-meme avant d'executer.
    /// </summary>
    internal static class TestTools
    {
        /// <summary>Meme plafond que build : au-dela, le client MCP expirerait avant la reponse.</summary>
        private const int MaxWaitMs = 120000;

        /// <summary>Delai laisse au magasin de tests pour enregistrer les resultats apres l'evenement de fin.</summary>
        private const int ResultsSettleMs = 300;

        /// <summary>Attente de la fin apres cancel_tests, avant de considerer l'execution annulee.</summary>
        private const int CancelGraceMs = 5000;

        private const int MaxTextLength = 4000;
        private const int SummaryFailures = 20;

        internal static void Register(ToolRegistry registry, TestWindowBridge bridge, TestRunWatcher watcher, DebugWatcher debugWatcher)
        {
            registry.Add(
                "list_tests",
                "Tests decouverts par l'Explorateur de tests, avec leur dernier etat (passed, failed, skipped, notRun), " +
                "leur fichier et leur ligne. 'discovery' vaut 'discovering' tant que la decouverte n'est pas finie : " +
                "rappeler alors list_tests.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("filter", "Ne garder que les tests dont le nom complet ou affiche contient ce texte.")
                    .Str("project", "Ne garder que les tests dont l'assemblage (Source) contient ce texte.")
                    .Str("state", "Ne garder que les tests dans cet etat.", allowed: new[] { "passed", "failed", "skipped", "notRun" })
                    .Int("max", "Nombre maximum de tests retournes.", defaultValue: 200)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(async () =>
                {
                    var all = await bridge.GetTestsAsync();
                    var state = Args.Str(args, "state");
                    var matched = Filter(all, Args.Str(args, "filter"), Args.Str(args, "project"))
                        .Where(t => state == null || string.Equals(StateName(t.State), state, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var max = Math.Max(1, Args.Int(args, "max", 200));

                    var response = new JObject
                    {
                        ["discovery"] = DiscoveryState(bridge, watcher),
                        ["total"] = all.Count,
                        ["matched"] = matched.Count,
                        ["returned"] = Math.Min(max, matched.Count),
                        ["truncated"] = matched.Count > max,
                        ["tests"] = new JArray(matched.Take(max).Select(t => DescribeTest(t)))
                    };
                    if (all.Count == 0)
                    {
                        response["hint"] = "Aucun test connu. La decouverte suit le build : compiler la solution " +
                                           "(build), puis rappeler list_tests.";
                    }
                    return response;
                }, ct));

            registry.Add(
                "run_tests",
                "Execute des tests dans l'Explorateur de tests (qui compile d'abord ce qui a change) et renvoie le bilan : " +
                "compteurs et echecs avec message et pile. Cibler par 'tests' (noms complets), 'filter'/'project', " +
                "'failed_only', ou rien pour tous les tests. Au-dela de wait_ms, le statut 'running' n'est pas une erreur : " +
                "rappeler test_status.",
                SchemaBuilder.New()
                    .Instance()
                    .StrList("tests", "Noms complets des tests (FullyQualifiedName, tels que list_tests les donne).")
                    .Str("filter", "Tous les tests dont le nom complet ou affiche contient ce texte.")
                    .Str("project", "Tous les tests dont l'assemblage (Source) contient ce texte.")
                    .Bool("failed_only", "Rejouer les tests en echec.", defaultValue: false)
                    .Int("wait_ms", "Duree d'attente maximale en millisecondes.", defaultValue: 90000)
                    .Build(),
                async (args, ctx, ct) =>
                {
                    var waitMs = Math.Min(Math.Max(1000, Args.Int(args, "wait_ms", 90000)), MaxWaitMs);

                    var started = await UiThread.RunAsync(async () =>
                    {
                        var all = await bridge.GetTestsAsync();
                        var selection = SelectTests(all, args, allowEverything: true);

                        var kind = selection.FailedOnly ? TestRunKind.Failed
                            : selection.Names == null ? TestRunKind.All
                            : TestRunKind.Names;

                        var run = watcher.Begin(kind, selection.Names, Baseline(all));
                        try
                        {
                            var task = kind == TestRunKind.Failed ? bridge.StartRunFailed()
                                : kind == TestRunKind.All ? bridge.StartRunAll()
                                : bridge.StartRunByNames(selection.Names);
                            watcher.Track(run, task);
                        }
                        catch (Exception ex)
                        {
                            watcher.ForceEnd(run, "failed", ex.Message);
                            throw;
                        }
                        return new StartedRun { Run = run, Unknown = selection.Unknown };
                    }, ct);

                    var tracked = started.Run;
                    var completed = await Task.WhenAny(tracked.Completion, Task.Delay(waitMs, ct)).ConfigureAwait(false);
                    if (completed != tracked.Completion)
                    {
                        var running = DescribeRunning(tracked, watcher);
                        AddUnknown(running, started.Unknown);
                        return running;
                    }

                    var summary = await SummarizeAsync(bridge, tracked, ct).ConfigureAwait(false);
                    AddUnknown(summary, started.Unknown);
                    return summary;
                });

            registry.Add(
                "test_status",
                "Etat de l'Explorateur de tests (idle, discovering, building, running, cancelling) et de la derniere " +
                "execution, y compris celle lancee depuis l'interface de Visual Studio. Une execution terminee " +
                "renvoie son bilan.",
                SchemaBuilder.New()
                    .Instance()
                    .Int("wait_ms", "Attente supplementaire si une execution est en cours.", defaultValue: 0)
                    .Bool("verbose", "Ajouter les derniers evenements bruts de l'Explorateur (diagnostic).", defaultValue: false)
                    .Build(),
                async (args, ctx, ct) =>
                {
                    var waitMs = Math.Min(Math.Max(0, Args.Int(args, "wait_ms", 0)), MaxWaitMs);

                    var counts = await UiThread.RunAsync(() =>
                    {
                        bridge.EnsureInitialized();
                        return bridge.GetCounts();
                    }, ct);

                    var run = watcher.Last;
                    if (run != null && !run.Completion.IsCompleted && waitMs > 0)
                    {
                        await Task.WhenAny(run.Completion, Task.Delay(waitMs, ct)).ConfigureAwait(false);
                    }

                    JObject response;
                    if (run == null)
                    {
                        response = new JObject
                        {
                            ["status"] = "none",
                            ["hint"] = "Aucune execution observee depuis l'initialisation des outils de test."
                        };
                    }
                    else if (!run.Completion.IsCompleted)
                    {
                        response = DescribeRunning(run, watcher);
                    }
                    else
                    {
                        response = await SummarizeAsync(bridge, run, ct).ConfigureAwait(false);
                    }

                    response["phase"] = watcher.Phase;
                    response["explorerCounts"] = DescribeCounts(counts);
                    if (Args.Bool(args, "verbose", false))
                    {
                        response["recentEvents"] = new JArray(watcher.RecentEvents);
                    }
                    return response;
                });

            registry.Add(
                "test_results",
                "Resultats par test de la derniere execution : etat, duree, message d'erreur, pile et, sur demande, " +
                "sorties console. Sans execution observee, l'etat courant de tous les tests.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("outcome", "Resultats a retourner.", allowed: new[] { "failed", "passed", "skipped", "notRun", "all" }, defaultValue: "failed")
                    .Str("filter", "Ne garder que les tests dont le nom complet ou affiche contient ce texte.")
                    .Bool("include_output", "Inclure les sorties standard et d'erreur.", defaultValue: false)
                    .Int("max", "Nombre maximum de tests retournes.", defaultValue: 50)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(async () =>
                {
                    var outcome = Args.Str(args, "outcome", "failed");
                    var includeOutput = Args.Bool(args, "include_output", false);
                    var max = Math.Max(1, Args.Int(args, "max", 50));

                    var run = watcher.Last;
                    var all = await bridge.GetTestsAsync();
                    var scope = Filter(Scope(all, run), Args.Str(args, "filter"), null).ToList();

                    var matched = scope
                        .Where(t => outcome == "all" || string.Equals(Outcome(t, run), outcome, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    return new JObject
                    {
                        ["run"] = run == null ? JValue.CreateNull() : (JToken)DescribeRunHeader(run),
                        ["inScope"] = scope.Count,
                        ["matched"] = matched.Count,
                        ["returned"] = Math.Min(max, matched.Count),
                        ["truncated"] = matched.Count > max,
                        ["tests"] = new JArray(matched.Take(max).Select(t => DescribeResult(t, run, includeOutput)))
                    };
                }, ct));

            registry.Add(
                "cancel_tests",
                "Annule l'execution de tests en cours, qu'elle ait ete lancee par run_tests, debug_tests ou depuis " +
                "l'interface de Visual Studio.",
                SchemaBuilder.New().Instance().Build(),
                async (args, ctx, ct) =>
                {
                    var run = watcher.RequestCancel();
                    await UiThread.RunAsync(() =>
                    {
                        var task = bridge.Cancel();
                        LogFault(task);
                        return true;
                    }, ct);

                    if (run == null)
                    {
                        return new JObject
                        {
                            ["cancelled"] = false,
                            ["hint"] = "Aucune execution suivie : la demande d'annulation a tout de meme ete transmise."
                        };
                    }

                    var completed = await Task.WhenAny(run.Completion, Task.Delay(CancelGraceMs, ct)).ConfigureAwait(false);
                    if (completed != run.Completion)
                    {
                        // L'evenement de fin n'arrive pas (execution bloquee, evenement manque) : sans cela,
                        // l'execution resterait 'en cours' et bloquerait tout nouveau run_tests.
                        watcher.ForceEnd(run, "cancelled", "Fin non signalee par l'Explorateur de tests apres l'annulation.");
                    }

                    var end = await run.Completion.ConfigureAwait(false);
                    return new JObject
                    {
                        ["cancelled"] = true,
                        ["status"] = end.Status,
                        ["detail"] = end.Detail
                    };
                });

            registry.Add(
                "debug_tests",
                "Lance des tests sous le debogueur, comme 'Deboguer' dans l'Explorateur de tests, et attend un arret " +
                "(point d'arret, exception) ou la fin. Statut 'break' : get_stack, get_locals et evaluate sont " +
                "utilisables, puis debug_continue ou debug_stop. Statut 'running' : rappeler debug_state.",
                SchemaBuilder.New()
                    .Instance()
                    .StrList("tests", "Noms complets des tests a deboguer.")
                    .Str("filter", "Tous les tests dont le nom complet ou affiche contient ce texte.")
                    .Str("project", "Tous les tests dont l'assemblage (Source) contient ce texte.")
                    .Int("wait_ms", "Attente maximale d'un arret, build compris.", defaultValue: 60000)
                    .Build(),
                async (args, ctx, ct) =>
                {
                    var waitMs = Math.Min(Math.Max(1000, Args.Int(args, "wait_ms", 60000)), MaxWaitMs);

                    Task<DebugTransition> transition = null;
                    var started = await UiThread.RunAsync(async () =>
                    {
                        var debugger = DteProvider.Dte.Debugger;
                        if (debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
                        {
                            throw new McpToolException("INVALID_STATE",
                                "Une session de debogage est deja en cours (" + DteProvider.DebugModeName(debugger.CurrentMode) +
                                "). L'arreter avec debug_stop avant debug_tests.");
                        }

                        var all = await bridge.GetTestsAsync();
                        var selection = SelectTests(all, args, allowEverything: false);

                        var run = watcher.Begin(TestRunKind.Debug, selection.Names, Baseline(all));
                        try
                        {
                            transition = debugWatcher.Arm();
                            watcher.Track(run, bridge.StartDebugByNames(selection.Names));
                        }
                        catch (Exception ex)
                        {
                            watcher.ForceEnd(run, "failed", ex.Message);
                            throw;
                        }
                        return new StartedRun { Run = run, Unknown = selection.Unknown };
                    }, ct);

                    var tracked = started.Run;
                    await Task.WhenAny(transition, tracked.Completion, Task.Delay(waitMs, ct)).ConfigureAwait(false);

                    JObject response;
                    if (transition.Status == TaskStatus.RanToCompletion && transition.Result.Kind == DebugTransitionKind.Break)
                    {
                        response = new JObject
                        {
                            ["status"] = "break",
                            ["reason"] = transition.Result.Reason,
                            ["debugger"] = await UiThread.RunAsync(() => DebugTools.BuildState(), ct)
                        };
                    }
                    else if (transition.IsCompleted || tracked.Completion.IsCompleted)
                    {
                        response = new JObject
                        {
                            ["status"] = "completed_without_break",
                            ["hint"] = "Les tests ont tourne sans s'arreter : verifier le point d'arret avec list_breakpoints. " +
                                       "Le bilan arrive avec test_status."
                        };
                    }
                    else
                    {
                        response = DescribeRunning(tracked, watcher);
                        response["hint"] = "Pas encore d'arret (build, demarrage de l'hote de test ou test en cours). " +
                                           "Rappeler debug_state ou test_status.";
                    }

                    AddUnknown(response, started.Unknown);
                    return response;
                });
        }

        private sealed class StartedRun
        {
            internal TestRun Run { get; set; }
            internal List<string> Unknown { get; set; }
        }

        private sealed class Selection
        {
            /// <summary>Noms complets a executer, ou null pour tous les tests.</summary>
            internal List<string> Names { get; set; }

            internal List<string> Unknown { get; set; }
            internal bool FailedOnly { get; set; }
        }

        /// <summary>Traduit tests / filter / project / failed_only en noms complets, en verifiant qu'ils existent.</summary>
        private static Selection SelectTests(List<TestInfo> all, JObject args, bool allowEverything)
        {
            var names = Args.StrList(args, "tests");
            var filter = Args.Str(args, "filter");
            var project = Args.Str(args, "project");

            if (allowEverything && Args.Bool(args, "failed_only", false))
            {
                if (names != null || filter != null || project != null)
                {
                    throw McpToolException.InvalidArgs("'failed_only' ne se combine pas avec 'tests', 'filter' ou 'project'.");
                }
                if (!all.Any(t => t.State == "Failed"))
                {
                    throw McpToolException.NotFound("Aucun test en echec a rejouer.");
                }
                return new Selection { FailedOnly = true };
            }

            if (names != null)
            {
                // L'Explorateur compare les noms a l'identique : on renvoie l'orthographe exacte du catalogue.
                var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var test in all.Where(t => t.FullyQualifiedName != null))
                {
                    byName[test.FullyQualifiedName] = test.FullyQualifiedName;
                }

                var known = new List<string>();
                var unknown = new List<string>();
                foreach (var name in names)
                {
                    if (byName.TryGetValue(name, out var exact)) known.Add(exact);
                    else unknown.Add(name);
                }

                if (known.Count == 0)
                {
                    throw McpToolException.NotFound("Aucun des tests demandes n'est connu de l'Explorateur de tests (" +
                                                    string.Join(", ", unknown.Take(5)) + "). Verifier les noms avec list_tests.");
                }
                return new Selection { Names = known.Distinct().ToList(), Unknown = unknown };
            }

            if (filter != null || project != null)
            {
                var matched = Filter(all, filter, project)
                    .Select(t => t.FullyQualifiedName)
                    .Where(n => n != null)
                    .Distinct()
                    .ToList();
                if (matched.Count == 0)
                {
                    throw McpToolException.NotFound("Aucun test ne correspond au filtre. Verifier avec list_tests.");
                }
                return new Selection { Names = matched };
            }

            if (!allowEverything)
            {
                throw McpToolException.InvalidArgs("Designer les tests par 'tests', 'filter' ou 'project'.");
            }
            if (all.Count == 0)
            {
                throw McpToolException.NotFound("Aucun test decouvert. Compiler la solution (build), puis appeler list_tests.");
            }
            return new Selection();
        }

        private static IEnumerable<TestInfo> Filter(IEnumerable<TestInfo> tests, string filter, string project)
        {
            return tests.Where(t =>
                (filter == null ||
                 Contains(t.FullyQualifiedName, filter) ||
                 Contains(t.DisplayName, filter)) &&
                (project == null || Contains(t.Source, project)));
        }

        private static bool Contains(string text, string part)
        {
            return text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Dictionary<Guid, long> Baseline(IEnumerable<TestInfo> tests)
        {
            var baseline = new Dictionary<Guid, long>();
            foreach (var test in tests) baseline[test.Id] = test.LastReceivedOrder;
            return baseline;
        }

        /// <summary>Tests concernes par une execution : ses cibles, ou tous les tests.</summary>
        private static IEnumerable<TestInfo> Scope(List<TestInfo> all, TestRun run)
        {
            if (run?.Targets == null) return all;

            var targets = new HashSet<string>(run.Targets, StringComparer.Ordinal);
            return all.Where(t => t.FullyQualifiedName != null && targets.Contains(t.FullyQualifiedName));
        }

        /// <summary>
        /// Vrai si le test a recu un resultat depuis le lancement. Sans reference (execution externe),
        /// on ne sait pas : le test est repute avoir tourne.
        /// </summary>
        private static bool Ran(TestInfo test, TestRun run)
        {
            if (run?.Baseline == null) return true;
            return run.Baseline.TryGetValue(test.Id, out var before)
                ? test.LastReceivedOrder > before
                : test.LastReceivedOrder >= 0;
        }

        /// <summary>Resultat du test pour cette execution : son etat s'il a tourne, notRun sinon.</summary>
        private static string Outcome(TestInfo test, TestRun run)
        {
            return Ran(test, run) ? StateName(test.State) : "notRun";
        }

        private static string StateName(string state)
        {
            switch (state)
            {
                case "Passed": return "passed";
                case "Failed": return "failed";
                case "Skipped": return "skipped";
                case "NotFound": return "notFound";
                case "None": return "none";
                default: return "notRun";
            }
        }

        private static async Task<JObject> SummarizeAsync(TestWindowBridge bridge, TestRun run, CancellationToken ct)
        {
            var end = await run.Completion.ConfigureAwait(false);
            await Task.Delay(ResultsSettleMs, ct).ConfigureAwait(false);

            return await UiThread.RunAsync(async () =>
            {
                var all = await bridge.GetTestsAsync();

                // Un rejeu des echecs n'a pas de cibles nommees : on retient ce qui a tourne.
                var scope = Scope(all, run).ToList();
                if (run.Kind == TestRunKind.Failed) scope = scope.Where(t => Ran(t, run)).ToList();

                var outcomes = scope.Select(t => Outcome(t, run)).ToList();
                var failures = scope.Where(t => Outcome(t, run) == "failed").ToList();

                var summary = DescribeRunHeader(run);
                summary["status"] = end.Status;
                summary["durationMs"] = end.DurationMs;
                if (end.Detail != null) summary["detail"] = end.Detail;
                summary["total"] = scope.Count;
                summary["passed"] = outcomes.Count(o => o == "passed");
                summary["failed"] = failures.Count;
                summary["skipped"] = outcomes.Count(o => o == "skipped");
                summary["notRun"] = outcomes.Count(o => o == "notRun");
                summary["failures"] = new JArray(failures.Take(SummaryFailures).Select(t => DescribeResult(t, run, false)));
                if (failures.Count > SummaryFailures)
                {
                    summary["hint"] = "Seuls les " + SummaryFailures + " premiers echecs sont detailles : test_results donne la suite.";
                }
                else if (end.Status == "not_started")
                {
                    summary["hint"] = "Aucun test n'a tourne : le build a peut-etre echoue (get_errors) ou les tests ont change.";
                }
                return summary;
            }, ct);
        }

        private static JObject DescribeRunHeader(TestRun run)
        {
            var header = new JObject
            {
                ["kind"] = TestRunWatcher.Describe(run.Kind),
                ["startedUtc"] = run.StartedUtc.ToString("o")
            };
            if (run.Targets != null) header["targets"] = run.Targets.Count;
            return header;
        }

        private static JObject DescribeRunning(TestRun run, TestRunWatcher watcher)
        {
            var response = DescribeRunHeader(run);
            response["status"] = "running";
            response["phase"] = watcher.Phase;
            response["elapsedMs"] = (int)(DateTime.UtcNow - run.StartedUtc).TotalMilliseconds;
            response["hint"] = "Execution en cours. Rappeler test_status (avec wait_ms) pour le bilan.";
            return response;
        }

        private static JObject DescribeTest(TestInfo test)
        {
            var item = new JObject
            {
                ["name"] = test.FullyQualifiedName,
                ["displayName"] = test.DisplayName,
                ["state"] = StateName(test.State),
                ["project"] = SafeFileName(test.Source)
            };
            if (!string.IsNullOrEmpty(test.FilePath)) item["file"] = test.FilePath;
            if (test.LineNumber > 0) item["line"] = test.LineNumber;
            if (test.Duration > TimeSpan.Zero) item["durationMs"] = (int)test.Duration.TotalMilliseconds;
            if (test.Stale) item["stale"] = true;
            return item;
        }

        private static JObject DescribeResult(TestInfo test, TestRun run, bool includeOutput)
        {
            var item = DescribeTest(test);
            item["outcome"] = Outcome(test, run);

            // Un test parametre a plusieurs resultats : on garde ceux qui portent un message ou une sortie.
            var results = test.Results
                .Where(r => !string.IsNullOrEmpty(r.ErrorMessage) || !string.IsNullOrEmpty(r.ErrorStackTrace) ||
                            (includeOutput && (!string.IsNullOrEmpty(r.StandardOutput) || !string.IsNullOrEmpty(r.StandardError))))
                .OrderByDescending(r => r.ReceivedOrder)
                .Take(5)
                .ToList();

            if (results.Count == 0) return item;

            var details = new JArray();
            foreach (var result in results)
            {
                var detail = new JObject();
                if (results.Count > 1 || !string.Equals(result.DisplayName, test.DisplayName, StringComparison.Ordinal))
                {
                    detail["displayName"] = result.DisplayName;
                }
                if (result.State != null) detail["state"] = StateName(result.State);
                if (!string.IsNullOrEmpty(result.ErrorMessage)) detail["message"] = Truncate(result.ErrorMessage);
                if (!string.IsNullOrEmpty(result.ErrorStackTrace)) detail["stackTrace"] = Truncate(result.ErrorStackTrace);
                if (includeOutput && !string.IsNullOrEmpty(result.StandardOutput)) detail["stdout"] = Truncate(result.StandardOutput);
                if (includeOutput && !string.IsNullOrEmpty(result.StandardError)) detail["stderr"] = Truncate(result.StandardError);
                details.Add(detail);
            }
            item["results"] = details;
            return item;
        }

        private static JToken DescribeCounts(TestExplorerCounts counts)
        {
            if (counts == null) return JValue.CreateNull();
            return new JObject
            {
                ["passed"] = counts.Passed,
                ["failed"] = counts.Failed,
                ["skipped"] = counts.Skipped,
                ["notRun"] = counts.NotRun
            };
        }

        private static string DiscoveryState(TestWindowBridge bridge, TestRunWatcher watcher)
        {
            if (watcher.Phase == "discovering") return "discovering";
            return bridge.GetCounts() == null ? "not_initialized" : "ready";
        }

        private static void AddUnknown(JObject response, List<string> unknown)
        {
            if (unknown == null || unknown.Count == 0) return;
            response["unknownTests"] = new JArray(unknown);
        }

        private static string Truncate(string text)
        {
            if (text.Length <= MaxTextLength) return text;
            return text.Substring(0, MaxTextLength) + "... [" + (text.Length - MaxTextLength) + " caracteres de plus]";
        }

        private static string SafeFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return Path.GetFileNameWithoutExtension(path); }
            catch (ArgumentException) { return path; }
        }

        private static void LogFault(Task task)
        {
            _ = task?.ContinueWith(
                t => ExtensionLog.Error("Annulation des tests", t.Exception?.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
