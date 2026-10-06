using System;
using System.Collections.Generic;
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

                    var command = UiThread.RunAsync(() =>
                    {
                        // Go(false) : jamais true, qui bloquerait le thread UI et donc le debogueur lui-meme.
                        DteProvider.Dte.Debugger.Go(false);
                        return true;
                    }, ct);

                    return await AwaitTransitionAsync(command, transition, waitMs, false, ct).ConfigureAwait(false);
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

                    var command = UiThread.RunAsync(() =>
                    {
                        DteProvider.RequireBreakMode().Go(false);
                        return true;
                    }, ct);

                    return await AwaitTransitionAsync(command, transition, waitMs, false, ct).ConfigureAwait(false);
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

                        var command = UiThread.RunAsync(() =>
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

                        last = await AwaitTransitionAsync(command, transition, waitMs, false, ct).ConfigureAwait(false);
                        done++;

                        // Programme termine, toujours en cours d'execution ou boite ouverte : inutile d'insister.
                        if ((string)last["mode"] != "Break" || last["dialog"] != null) break;
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

                    var command = UiThread.RunAsync(() =>
                    {
                        DteProvider.Dte.Debugger.Break(false);
                        return true;
                    }, ct);

                    return await AwaitTransitionAsync(command, transition, waitMs, false, ct).ConfigureAwait(false);
                });

            registry.Add(
                "debug_stop",
                "Arrete la session de debogage et revient en mode conception. Si Visual Studio demande " +
                "confirmation, la boite est confirmee (confirm=true) ou decrite dans la reponse.",
                SchemaBuilder.New()
                    .Instance()
                    .Bool("force",
                        "Terminer directement les processus debogues plutot que d'emettre la commande " +
                        "Arreter de Visual Studio, qui ouvre selon le projet une confirmation modale. " +
                        "Mettre a false si la session est attachee a un processus qu'il ne faut pas tuer.",
                        defaultValue: true)
                    .Bool("confirm",
                        "Repondre Oui (ou OK) a la confirmation que Visual Studio ouvre pendant l'arret. " +
                        "A false, la boite reste ouverte et sa description est renvoyee : y repondre " +
                        "avec answer_dialog.",
                        defaultValue: true)
                    .Int("wait_ms", "Attente du retour en mode conception.", defaultValue: 15000)
                    .Build(),
                async (args, ctx, ct) =>
                {
                    var force = Args.Bool(args, "force", true);
                    var confirm = Args.Bool(args, "confirm", true);
                    var waitMs = Clamp(Args.Int(args, "wait_ms", 15000));

                    // Une boite deja ouverte bloque le thread UI. On ne la confirme pas a
                    // l'aveugle : rien ne dit qu'elle vient d'un arret.
                    var preexisting = ModalDialogs.FindTop();
                    if (preexisting != null) return DialogOpenState(preexisting, null);

                    // Rien a arreter : le signaler plutot qu'attendre une transition qui ne viendra pas.
                    var current = await UiThread.RunAsync(() => BuildState(), ct);
                    if ((string)current["mode"] == "Design")
                    {
                        current["alreadyStopped"] = true;
                        return current;
                    }

                    var transition = watcher.Arm();

                    var targets = force
                        ? await UiThread.RunAsync(() => DebuggedProcesses(), ct)
                        : new List<DebuggedProcess>();

                    var terminated = new JArray();
                    var failed = new JArray();

                    foreach (var target in targets)
                    {
                        // Hors thread UI : la terminaison ne passe pas par l'automation, donc
                        // par aucune boite de dialogue. Visual Studio voit le processus mourir
                        // et referme la session de lui-meme.
                        if (TryKill(target)) terminated.Add(Describe(target));
                        else failed.Add(Describe(target));
                    }

                    // Mode normal, ou aucun processus terminable : on emet la commande de Visual
                    // Studio, quitte a ce qu'elle demande confirmation. Surtout ne pas l'attendre :
                    // Stop ne rend la main qu'a la fermeture de la boite, qu'AwaitTransitionAsync
                    // doit justement pouvoir voir et confirmer.
                    Task command = null;
                    if (terminated.Count == 0)
                    {
                        command = UiThread.RunAsync(() =>
                        {
                            DteProvider.Dte.Debugger.Stop(false);
                            return true;
                        }, ct);
                    }

                    var state = await AwaitTransitionAsync(command, transition, waitMs, confirm, ct).ConfigureAwait(false);

                    if (terminated.Count > 0) state["terminatedProcesses"] = terminated;
                    if (failed.Count > 0)
                    {
                        state["notTerminated"] = failed;
                        if (state["dialog"] == null)
                        {
                            state["hint"] = "Certains processus debogues n'ont pas pu etre termines, un " +
                                "processus eleve par exemple.";
                        }
                    }

                    return state;
                });
        }

        /// <summary>Un processus sous le controle du debogueur.</summary>
        private sealed class DebuggedProcess
        {
            internal int Id;
            internal string Name;
        }

        /// <summary>Processus actuellement debogues, lus sur le thread UI.</summary>
        private static List<DebuggedProcess> DebuggedProcesses()
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var list = new List<DebuggedProcess>();

            var processes = DteProvider.Dte.Debugger.DebuggedProcesses;
            if (processes == null) return list;

            foreach (EnvDTE.Process process in processes)
            {
                try
                {
                    list.Add(new DebuggedProcess { Id = process.ProcessID, Name = process.Name });
                }
                catch (Exception)
                {
                    // Processus deja parti entre l'enumeration et la lecture.
                }
            }

            return list;
        }

        /// <summary>
        /// Terminaison brutale, equivalente a ce que fait la commande Arreter : aucun code de
        /// sortie propre n'est execute dans le programme debogue.
        /// </summary>
        private static bool TryKill(DebuggedProcess target)
        {
            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById(target.Id))
                {
                    process.Kill();
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true; // Deja termine.
            }
            catch (Exception ex)
            {
                ExtensionLog.Error("Terminaison du processus debogue " + target.Id, ex);
                return false;
            }
        }

        private static JObject Describe(DebuggedProcess target)
        {
            return new JObject
            {
                ["pid"] = target.Id,
                ["name"] = target.Name
            };
        }

        private static int Clamp(int waitMs)
        {
            return Math.Min(Math.Max(0, waitMs), MaxWaitMs);
        }

        private const int DialogPollMs = 150;

        /// <summary>
        /// Attend une transition du debogueur. Un depassement de delai n'est pas une erreur :
        /// le programme tourne peut-etre simplement toujours.
        ///
        /// La commande qui provoque la transition est passee sans etre attendue : si Visual Studio
        /// ouvre une boite modale, elle ne rend la main qu'a sa fermeture. On surveille donc en
        /// parallele les boites ouvertes et on en rend compte, au lieu de rester bloque jusqu'au
        /// delai d'UiThread. Avec confirmDialogs, chaque boite est confirmee (Oui, sinon OK) et
        /// l'attente continue.
        /// </summary>
        private static async Task<JObject> AwaitTransitionAsync(Task command, Task<DebugTransition> transition,
            int waitMs, bool confirmDialogs, System.Threading.CancellationToken ct)
        {
            var confirmed = new JArray();
            var deadline = Environment.TickCount + waitMs;

            try
            {
                while (!transition.IsCompleted)
                {
                    var dialog = ModalDialogs.FindTop();
                    if (dialog != null)
                    {
                        // Garde-fou contre une boite qui en rouvre une autre indefiniment.
                        if (!confirmDialogs || confirmed.Count >= 3) return DialogOpenState(dialog, confirmed);

                        var json = dialog.ToJson();
                        var clicked = ModalDialogs.Confirm(dialog);
                        if (clicked == null) return DialogOpenState(dialog, confirmed);

                        json["answered"] = clicked.Label;
                        confirmed.Add(json);
                        ExtensionLog.Info("Boite \"" + dialog.Title + "\" confirmee par \"" + clicked.Label + "\".");
                        continue;
                    }

                    // Commande refusee (pas en mode arret, debogueur absent...) : remonter l'erreur.
                    // Teste apres les boites : une commande expiree a cause d'une boite doit etre
                    // rapportee comme telle, pas comme un delai depasse.
                    // Une commande abandonnee pour cause de boite (ModalDialogException) n'est pas
                    // une erreur ici : la boite vient d'etre traitee, la transition suit.
                    if (command != null && command.IsCompleted)
                    {
                        if (command.IsFaulted && !(command.Exception?.GetBaseException() is ModalDialogException))
                        {
                            await command.ConfigureAwait(false);
                        }
                        command = null;
                    }

                    var remaining = deadline - Environment.TickCount;
                    if (remaining <= 0) break;

                    await Task.WhenAny(transition, Task.Delay(Math.Min(DialogPollMs, remaining), ct)).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                Observe(command);
            }

            JObject state;
            if (transition.IsCompleted)
            {
                var result = await transition.ConfigureAwait(false);
                state = await UiThread.RunAsync(() => BuildState(), ct);
                state["transition"] = result.KindName;
                state["reason"] = result.Reason;
            }
            else
            {
                state = await UiThread.RunAsync(() => BuildState(), ct);
                state["waitTimedOut"] = true;
                state["hint"] = "Le programme s'execute toujours. Poser un point d'arret, ou appeler debug_pause.";
            }

            if (confirmed.Count > 0) state["confirmedDialogs"] = confirmed;
            return state;
        }

        /// <summary>
        /// Reponse quand une boite modale bloque Visual Studio. Ne touche pas au thread UI :
        /// tant que la boite est ouverte, il ne repondrait pas.
        /// </summary>
        private static JObject DialogOpenState(ModalDialogs.Dialog dialog, JArray confirmed)
        {
            var state = new JObject
            {
                ["status"] = "dialog_open",
                ["dialog"] = dialog.ToJson(),
                ["hint"] = "Visual Studio attend une reponse a cette boite de dialogue et ne traitera " +
                           "rien d'autre d'ici la. Y repondre avec answer_dialog, puis appeler debug_state."
            };
            if (confirmed != null && confirmed.Count > 0) state["confirmedDialogs"] = confirmed;
            return state;
        }

        /// <summary>Une commande abandonnee ne doit pas laisser d'exception non observee.</summary>
        private static void Observe(Task command)
        {
            _ = command?.ContinueWith(
                t => ExtensionLog.Error("Commande du debogueur", t.Exception?.GetBaseException()),
                System.Threading.CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
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
