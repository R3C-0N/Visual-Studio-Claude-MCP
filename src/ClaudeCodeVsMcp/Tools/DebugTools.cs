using System;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using EnvDTE;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    internal static class DebugTools
    {
        private const int MaxWaitMs = 120000;

        internal static void Register(ToolRegistry registry, DebugWatcher watcher, BuildWatcher buildWatcher)
        {
            registry.Add(
                "debug_state",
                "Mode du debogueur et position courante. Seul outil d'inspection utilisable quand le " +
                "programme tourne : les autres exigent un arret sur point d'arret.",
                SchemaBuilder.New().Instance().Build(),
                (args, ctx, ct) => UiThread.RunAsync(() => BuildState(), ct));

            registry.Add(
                "debug_start",
                "Demarre une session de debogage (equivalent F5). Compile d'abord par defaut : un build " +
                "en echec ouvrirait sinon une boite de dialogue modale qui bloquerait toute automation.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("project", "Projet de demarrage a activer avant de lancer.")
                    .Bool("build_first", "Compiler et interrompre si la compilation echoue.", defaultValue: true)
                    .Int("wait_ms", "Attente d'un premier arret ou de la fin du programme.", defaultValue: 30000)
                    .Build(),
                async (args, ctx, ct) =>
                {
                    var project = Args.Str(args, "project");
                    var buildFirst = Args.Bool(args, "build_first", true);
                    var waitMs = Clamp(Args.Int(args, "wait_ms", 30000));

                    if (project != null)
                    {
                        await UiThread.RunAsync(() =>
                        {
                            var solution = DteProvider.RequireSolution();
                            var found = SolutionHelper.FindProject(solution, project);
                            solution.SolutionBuild.StartupProjects = new object[] { found.UniqueName };
                            return true;
                        }, ct);
                    }

                    if (buildFirst)
                    {
                        // Build(false) + attente de l'evenement. Build(true) bloquerait le thread UI,
                        // et le build a besoin de la pompe de messages pour progresser : deadlock garanti.
                        var buildTask = buildWatcher.Arm();

                        await UiThread.RunAsync(() =>
                        {
                            DteProvider.RequireSolution().SolutionBuild.Build(false);
                            return true;
                        }, ct);

                        var buildDone = await Task.WhenAny(buildTask, Task.Delay(MaxWaitMs, ct)).ConfigureAwait(false);
                        if (buildDone != buildTask)
                        {
                            return new JObject
                            {
                                ["status"] = "build_running",
                                ["hint"] = "La compilation prealable depasse le delai. Suivre build_status, " +
                                           "puis relancer debug_start avec build_first=false."
                            };
                        }

                        var outcome = await buildTask.ConfigureAwait(false);
                        if (!outcome.Succeeded)
                        {
                            var failed = outcome.FailedProjects;
                            var errors = await UiThread.RunAsync(
                                () => ErrorListReader.Read("error", 20, null, null), ct);

                            return new JObject
                            {
                                ["status"] = "build_failed",
                                ["failedProjects"] = failed,
                                ["errorCount"] = errors.Errors,
                                ["errors"] = errors.Items,
                                ["hint"] = "Le debogage n'a pas ete lance. Corriger les erreurs puis reessayer."
                            };
                        }
                    }

                    var transition = watcher.Arm();

                    await UiThread.RunAsync(() =>
                    {
                        // Go(false) : jamais true, qui bloquerait le thread UI et donc le debogueur lui-meme.
                        DteProvider.Dte.Debugger.Go(false);
                        return true;
                    }, ct);

                    return await AwaitTransitionAsync(transition, waitMs, ct).ConfigureAwait(false);
                });

            registry.Add(
                "debug_continue",
                "Reprend l'execution et attend le prochain arret, ou la fin du programme.",
                SchemaBuilder.New()
                    .Instance()
                    .Int("wait_ms", "Attente maximale.", defaultValue: 30000)
                    .Build(),
                async (args, ctx, ct) =>
                {
                    var waitMs = Clamp(Args.Int(args, "wait_ms", 30000));
                    var transition = watcher.Arm();

                    await UiThread.RunAsync(() =>
                    {
                        DteProvider.RequireBreakMode().Go(false);
                        return true;
                    }, ct);

                    return await AwaitTransitionAsync(transition, waitMs, ct).ConfigureAwait(false);
                });

            registry.Add(
                "debug_step",
                "Avance d'un pas : 'over' (ligne suivante), 'into' (entrer dans l'appel), 'out' (sortir).",
                SchemaBuilder.New()
                    .Instance()
                    .Str("kind", "Type de pas.", required: true, allowed: new[] { "over", "into", "out" })
                    .Int("count", "Nombre de pas consecutifs.", defaultValue: 1)
                    .Int("wait_ms", "Attente maximale par pas.", defaultValue: 15000)
                    .Build(),
                async (args, ctx, ct) =>
                {
                    var kind = Args.RequireStr(args, "kind").ToLowerInvariant();
                    var count = Math.Max(1, Math.Min(50, Args.Int(args, "count", 1)));
                    var waitMs = Clamp(Args.Int(args, "wait_ms", 15000));

                    JObject last = null;
                    var done = 0;

                    for (var i = 0; i < count; i++)
                    {
                        var transition = watcher.Arm();

                        await UiThread.RunAsync(() =>
                        {
                            var debugger = DteProvider.RequireBreakMode();
                            switch (kind)
                            {
                                case "into": debugger.StepInto(false); break;
                                case "out": debugger.StepOut(false); break;
                                default: debugger.StepOver(false); break;
                            }
                            return true;
                        }, ct);

                        last = await AwaitTransitionAsync(transition, waitMs, ct).ConfigureAwait(false);
                        done++;

                        // Programme termine ou toujours en cours d'execution : inutile d'insister.
                        if ((string)last["mode"] != "Break") break;
                    }

                    last = last ?? new JObject();
                    last["stepsDone"] = done;
                    last["stepsRequested"] = count;
                    return last;
                });

            registry.Add(
                "debug_pause",
                "Interrompt l'execution en cours pour passer en mode arret.",
                SchemaBuilder.New()
                    .Instance()
                    .Int("wait_ms", "Attente de l'arret effectif.", defaultValue: 10000)
                    .Build(),
                async (args, ctx, ct) =>
                {
                    var waitMs = Clamp(Args.Int(args, "wait_ms", 10000));
                    var transition = watcher.Arm();

                    await UiThread.RunAsync(() =>
                    {
                        DteProvider.Dte.Debugger.Break(false);
                        return true;
                    }, ct);

                    return await AwaitTransitionAsync(transition, waitMs, ct).ConfigureAwait(false);
                });

            registry.Add(
                "debug_stop",
                "Arrete la session de debogage et revient en mode conception.",
                SchemaBuilder.New()
                    .Instance()
                    .Int("wait_ms", "Attente du retour en mode conception.", defaultValue: 15000)
                    .Build(),
                async (args, ctx, ct) =>
                {
                    var waitMs = Clamp(Args.Int(args, "wait_ms", 15000));
                    var transition = watcher.Arm();

                    await UiThread.RunAsync(() =>
                    {
                        DteProvider.Dte.Debugger.Stop(false);
                        return true;
                    }, ct);

                    return await AwaitTransitionAsync(transition, waitMs, ct).ConfigureAwait(false);
                });
        }

        private static int Clamp(int waitMs)
        {
            return Math.Min(Math.Max(0, waitMs), MaxWaitMs);
        }

        /// <summary>
        /// Attend une transition du debogueur. Un depassement de delai n'est pas une erreur :
        /// le programme tourne peut-etre simplement toujours.
        /// </summary>
        private static async Task<JObject> AwaitTransitionAsync(Task<DebugTransition> transition, int waitMs, System.Threading.CancellationToken ct)
        {
            var completed = await Task.WhenAny(transition, Task.Delay(waitMs, ct)).ConfigureAwait(false);

            if (completed != transition)
            {
                var running = await UiThread.RunAsync(() => BuildState(), ct);
                running["waitTimedOut"] = true;
                running["hint"] = "Le programme s'execute toujours. Poser un point d'arret, ou appeler debug_pause.";
                return running;
            }

            var result = await transition.ConfigureAwait(false);
            var state = await UiThread.RunAsync(() => BuildState(), ct);
            state["transition"] = result.KindName;
            state["reason"] = result.Reason;
            return state;
        }

        /// <summary>Etat courant du debogueur, lu sur le thread UI.</summary>
        private static JObject BuildState()
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var debugger = DteProvider.Dte.Debugger;
            var mode = debugger.CurrentMode;

            var state = new JObject
            {
                ["mode"] = DteProvider.DebugModeName(mode),
                ["breakpointCount"] = SafeInt(() => debugger.Breakpoints.Count)
            };

            if (mode != dbgDebugMode.dbgBreakMode) return state;

            state["process"] = SafeString(() => debugger.CurrentProcess?.Name);
            state["thread"] = new JObject
            {
                ["id"] = SafeInt(() => debugger.CurrentThread?.ID ?? 0),
                ["name"] = SafeString(() => debugger.CurrentThread?.Name)
            };

            var frame = SafeGet(() => debugger.CurrentStackFrame);
            if (frame != null)
            {
                state["frame"] = new JObject
                {
                    ["function"] = SafeString(() => frame.FunctionName),
                    ["module"] = SafeString(() => frame.Module),
                    ["language"] = SafeString(() => frame.Language)
                };
            }

            // EnvDTE.StackFrame n'expose ni fichier ni ligne. A l'arret, Visual Studio place le
            // curseur sur l'instruction courante : on lit donc le document actif. C'est une
            // approximation fiable en pratique, mais qui suit la navigation de l'utilisateur.
            state["location"] = CurrentLocation();
            return state;
        }

        private static JToken CurrentLocation()
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var document = DteProvider.Dte.ActiveDocument;
                if (document == null) return JValue.CreateNull();

                var selection = document.Selection as TextSelection;
                return new JObject
                {
                    ["file"] = document.FullName,
                    ["line"] = selection?.CurrentLine ?? 0,
                    ["approximate"] = true
                };
            }
            catch (Exception)
            {
                return JValue.CreateNull();
            }
        }

        private static T SafeGet<T>(Func<T> accessor) where T : class
        {
            try { return accessor(); }
            catch (Exception) { return null; }
        }

        private static string SafeString(Func<string> accessor)
        {
            try { return accessor() ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        private static int SafeInt(Func<int> accessor)
        {
            try { return accessor(); }
            catch (Exception) { return 0; }
        }
    }
}
