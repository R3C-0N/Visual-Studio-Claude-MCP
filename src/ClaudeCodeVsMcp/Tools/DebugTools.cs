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
                                () => ErrorListReader.ReadAsync("error", 20, null, null), ct);

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
                "run_to_line",
                "Execute jusqu'a une ligne (equivalent Ctrl+F10), sans poser de point d'arret. En mode " +
                "conception, demarre le debogage. Un point d'arret rencontre avant arrete plus tot : la " +
                "reponse donne la position reelle.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("file", "Fichier. Absolu, ou relatif au repertoire de la solution.", required: true)
                    .Int("line", "Ligne cible (1-based).", required: true)
                    .Int("wait_ms", "Attente maximale de l'arret.", defaultValue: 30000)
                    .Build(),
                async (args, ctx, ct) =>
                {
                    var waitMs = Clamp(Args.Int(args, "wait_ms", 30000));
                    var transition = watcher.Arm();

                    var command = UiThread.RunAsync(() =>
                    {
                        var debugger = DteProvider.Dte.Debugger;
                        if (debugger.CurrentMode == dbgDebugMode.dbgRunMode)
                        {
                            throw new McpToolException("INVALID_STATE",
                                "Le programme s'execute : appeler debug_pause avant run_to_line.");
                        }

                        DocumentHelper.OpenAt(SolutionTools.ResolvePath(Args.RequireStr(args, "file")),
                            Args.RequireInt(args, "line"), 1);
                        debugger.RunToCursor(false);
                        return true;
                    }, ct);

                    return await AwaitTransitionAsync(command, transition, waitMs, false, ct).ConfigureAwait(false);
                });

            registry.Add(
                "set_next_statement",
                "Deplace l'instruction suivante sur une ligne de la fonction courante, sans rien executer. " +
                "Exige que le debogueur soit arrete. Les lignes sautees ne s'executent pas, celles rejouees " +
                "s'executent une seconde fois : utile pour retenter un bloc apres avoir change une variable.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("file", "Fichier de la fonction courante. Absolu, ou relatif a la solution.", required: true)
                    .Int("line", "Ligne de la prochaine instruction (1-based).", required: true)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var debugger = DteProvider.RequireBreakMode();

                    DocumentHelper.OpenAt(SolutionTools.ResolvePath(Args.RequireStr(args, "file")),
                        Args.RequireInt(args, "line"), 1);

                    try
                    {
                        debugger.SetNextStatement();
                    }
                    catch (System.Runtime.InteropServices.COMException ex)
                    {
                        throw new McpToolException("INVALID_TARGET",
                            "Le debogueur refuse cette ligne (hors de la fonction courante, ou sans code) : " +
                            ex.Message);
                    }

                    return BuildState();
                }, ct));

            registry.Add(
                "debug_attach",
                "Attache le debogueur a un processus deja lance (service Windows, w3wp, application " +
                "demarree hors de Visual Studio), designe par PID ou par nom. debug_detach s'en detache " +
                "sans le terminer.",
                SchemaBuilder.New()
                    .Instance()
                    .Int("pid", "Identifiant du processus.")
                    .Str("name", "A defaut de PID : nom du processus ou partie de son nom, par exemple 'w3wp'.")
                    .Str("engine", "Moteur de debogage (partie de son nom : 'Managed', 'Native'...). Par defaut, detection automatique.")
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var target = FindLocalProcess(Args.Int(args, "pid", 0), Args.Str(args, "name"));
                    var engine = Args.Str(args, "engine");

                    try
                    {
                        if (string.IsNullOrWhiteSpace(engine))
                        {
                            target.Attach();
                        }
                        else
                        {
                            ((EnvDTE80.Process2)target).Attach2(FindEngine(engine));
                        }
                    }
                    catch (System.Runtime.InteropServices.COMException ex)
                    {
                        throw new McpToolException("ATTACH_FAILED",
                            "Attachement refuse : " + ex.Message + " Un processus eleve (service, IIS) exige " +
                            "un Visual Studio lance en administrateur.");
                    }

                    var state = BuildState();
                    state["attached"] = DescribeLocal(target);
                    return state;
                }, ct, TimeSpan.FromSeconds(60)));

            registry.Add(
                "debug_detach",
                "Detache le debogueur de tous les processus debogues, qui continuent de s'executer.",
                SchemaBuilder.New()
                    .Instance()
                    .Int("wait_ms", "Attente du retour en mode conception.", defaultValue: 10000)
                    .Build(),
                async (args, ctx, ct) =>
                {
                    var waitMs = Clamp(Args.Int(args, "wait_ms", 10000));
                    var detached = new JArray();
                    var transition = watcher.Arm();

                    // DetachAll rend la main avant que le debogueur ait quitte la session : on attend
                    // le passage en mode conception plutot que de rendre un etat intermediaire.
                    var command = UiThread.RunAsync(() =>
                    {
                        var debugger = DteProvider.Dte.Debugger;
                        if (debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                        {
                            throw new McpToolException("INVALID_STATE", "Aucune session de debogage en cours.");
                        }

                        foreach (var process in DebuggedProcesses()) detached.Add(Describe(process));
                        debugger.DetachAll();
                        return true;
                    }, ct);

                    var state = await AwaitTransitionAsync(command, transition, waitMs, false, ct).ConfigureAwait(false);
                    state["detached"] = detached;
                    return state;
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
        /// Processus local designe par PID, ou par nom (partie du nom de fichier, insensible a la
        /// casse). Un nom exact (sans extension) l'emporte sur les correspondances partielles.
        /// </summary>
        private static EnvDTE.Process FindLocalProcess(int pid, string name)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (pid <= 0 && string.IsNullOrWhiteSpace(name))
            {
                throw McpToolException.InvalidArgs("Donner 'pid' ou 'name'.");
            }

            var matches = new List<EnvDTE.Process>();
            var exact = new List<EnvDTE.Process>();

            foreach (EnvDTE.Process process in DteProvider.Dte.Debugger.LocalProcesses)
            {
                int id;
                string fileName;
                try
                {
                    id = process.ProcessID;
                    fileName = System.IO.Path.GetFileName(process.Name ?? string.Empty);
                }
                catch (Exception)
                {
                    continue; // Processus termine entre l'enumeration et la lecture.
                }

                if (pid > 0)
                {
                    if (id == pid) return process;
                    continue;
                }

                if (fileName.IndexOf(name.Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;

                matches.Add(process);
                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(fileName), name.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, name.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    exact.Add(process);
                }
            }

            if (pid > 0) throw McpToolException.NotFound("Aucun processus local de PID " + pid + ".");
            if (exact.Count == 1) return exact[0];
            if (matches.Count == 1) return matches[0];
            if (matches.Count == 0) throw McpToolException.NotFound("Aucun processus local ne correspond a '" + name + "'.");

            var candidates = new List<string>();
            foreach (var process in matches)
            {
                if (candidates.Count >= 15) break;
                var described = DescribeLocal(process);
                candidates.Add(described["name"] + " (pid " + described["pid"] + ")");
            }
            throw McpToolException.InvalidArgs(
                matches.Count + " processus correspondent a '" + name + "' : " + string.Join(", ", candidates) +
                ". Donner le 'pid'.");
        }

        private static JObject DescribeLocal(EnvDTE.Process process)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var process2 = process as EnvDTE80.Process2;
            return new JObject
            {
                ["pid"] = SafeInt(() => process.ProcessID),
                ["name"] = System.IO.Path.GetFileName(SafeString(() => process.Name)),
                ["path"] = SafeString(() => process.Name),
                ["user"] = process2 == null ? string.Empty : SafeString(() => process2.UserName)
            };
        }

        /// <summary>
        /// Moteur de debogage du transport par defaut dont le nom contient le texte donne. Les noms
        /// sont localises : on liste ceux disponibles en cas d'echec.
        /// </summary>
        private static EnvDTE80.Engine FindEngine(string text)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var debugger = DteProvider.Dte.Debugger as EnvDTE80.Debugger2;
            var transport = debugger?.Transports?.Item("Default");
            if (transport == null)
            {
                throw new McpToolException("UNSUPPORTED", "Transport de debogage par defaut introuvable.");
            }

            var matches = new List<EnvDTE80.Engine>();
            var names = new List<string>();

            foreach (EnvDTE80.Engine engine in transport.Engines)
            {
                var engineName = SafeString(() => engine.Name);
                names.Add(engineName);
                if (engineName.IndexOf(text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) matches.Add(engine);
            }

            if (matches.Count == 1) return matches[0];

            throw McpToolException.InvalidArgs(
                (matches.Count == 0 ? "Aucun moteur ne correspond a '" : "Plusieurs moteurs correspondent a '") +
                text + "'. Moteurs disponibles : " + string.Join(" ; ", names) + ".");
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

            // La commande vient d'etre emise et n'a pas encore abouti : la fenetre active est encore
            // celle de l'utilisateur. Visual Studio se met au premier plan a l'arret ; on la lui rend.
            var foreground = ForegroundWindow.Capture();

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
                state = await UiThread.RunAsync(() =>
                {
                    var built = BuildState();

                    // La raison transmise par Visual Studio n'est pas fiable (une pause peut arriver
                    // marquee ExceptionThrown) : on ne l'annonce que si $exception existe vraiment.
                    if (result.Reason.IndexOf("Exception", StringComparison.Ordinal) >= 0 && HasCurrentException())
                    {
                        built["hint"] = "Arret sur une exception : get_exception la decrit.";
                    }
                    return built;
                }, ct);
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
            ForegroundWindow.Restore(foreground);
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

            // StackFrame2 donne la position exacte quand les symboles sont charges. A defaut, on lit
            // le curseur du document actif, que Visual Studio place sur l'instruction courante a
            // l'arret : approximation qui suit la navigation de l'utilisateur.
            state["location"] = (JToken)StackHelper.CurrentPosition(debugger) ?? CurrentLocation();
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

        /// <summary>Vrai si le debogueur arrete expose une exception courante ($exception).</summary>
        private static bool HasCurrentException()
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var debugger = DteProvider.Dte.Debugger;
                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode) return false;

                var exception = debugger.GetExpression("$exception", false, 1000);
                return exception != null && exception.IsValidValue && exception.Value != "null";
            }
            catch (Exception)
            {
                return false;
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
