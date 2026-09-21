using System;
using System.Collections.Generic;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    /// <summary>
    /// Etat complet souhaite pour un point d'arret.
    ///
    /// Cette indirection existe a cause d'une contrainte d'EnvDTE : Condition, ConditionType,
    /// HitCountTarget et HitCountType sont en LECTURE SEULE. Ils ne peuvent etre fixes qu'a la
    /// creation, via Breakpoints.Add. Modifier une condition impose donc de relire l'etat
    /// courant, de supprimer le point et de le recreer, d'ou ce descripteur commun aux deux
    /// chemins.
    /// </summary>
    internal sealed class BreakpointSpec
    {
        internal string File;
        internal int Line;
        internal int Column = 1;
        internal string Condition = string.Empty;
        internal dbgBreakpointConditionType ConditionType = dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue;
        internal int HitCount;
        internal dbgHitCountType HitCountType = dbgHitCountType.dbgHitCountTypeNone;

        // Proprietes modifiables apres coup.
        internal string Message = string.Empty;
        internal bool BreakWhenHit = true;
        internal string FilterBy = string.Empty;
        internal bool Enabled = true;
    }

    internal static class BreakpointTools
    {
        private static readonly string[] HitCountModes = { "equal", "greater_or_equal", "multiple" };
        private static readonly string[] ConditionTypes = { "when_true", "when_changed" };

        internal static void Register(ToolRegistry registry, BreakpointManager manager)
        {
            registry.Add(
                "set_breakpoint",
                "Pose un point d'arret sur une ligne. Quatre variantes, combinables entre elles :\n" +
                "- ordinaire : interrompt l'execution ;\n" +
                "- conditionnel : 'condition', evaluee dans le contexte de la ligne ;\n" +
                "- tracepoint : 'message' journalise dans le pane Debogage sans interrompre. Le " +
                "message accepte des expressions entre accolades et les pseudo-variables de " +
                "Visual Studio, par exemple \"i = {i}, appele par $CALLER sur le thread $TID\" ;\n" +
                "- temporaire : 'temporary' supprime le point apres son premier declenchement ;\n" +
                "- dependant : 'depends_on' le laisse desactive jusqu'a ce que le point d'arret " +
                "indique soit atteint.\n" +
                "Fonctionne hors session de debogage : le point reste en attente jusqu'au " +
                "chargement du module.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("file", "Chemin du fichier source.", required: true)
                    .Int("line", "Numero de ligne (1-based).", required: true)
                    .Int("column", "Colonne (1-based).", defaultValue: 1)
                    .Str("condition", "Expression conditionnelle, par exemple 'i > 10'.")
                    .Str("condition_type", "Declencher quand la condition est vraie, ou quand sa valeur change.",
                        allowed: ConditionTypes, defaultValue: "when_true")
                    .Str("message", "Message a journaliser. Sa presence fait un tracepoint : le point " +
                                    "n'interrompt plus l'execution, sauf si 'also_break' est vrai.")
                    .Bool("also_break", "Pour un tracepoint : journaliser ET interrompre.", defaultValue: false)
                    .Bool("temporary", "Supprimer le point apres son premier declenchement.", defaultValue: false)
                    .Str("depends_on", "Identifiant du point d'arret prerequis, au format 'chemin:ligne'. " +
                                       "Le point reste desactive jusqu'a ce que le prerequis soit atteint.")
                    .Int("hit_count", "Nombre de passages avant declenchement. 0 desactive ce filtre.", defaultValue: 0)
                    .Str("hit_count_mode", "Comment interpreter 'hit_count'.",
                        allowed: HitCountModes, defaultValue: "greater_or_equal")
                    .Str("filter", "Restreint le point d'arret, par exemple 'ThreadName == \"worker\"'.")
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var spec = SpecFromArgs(args, null);
                    var temporary = Args.Bool(args, "temporary", false);
                    var dependsOn = ResolveDependency(Args.Str(args, "depends_on"));

                    // Un point dependant naît desactive : il s'activera quand son prerequis sera atteint.
                    if (dependsOn != null) spec.Enabled = false;

                    var id = BreakpointManager.IdFor(spec.File, spec.Line);
                    var created = Create(spec);

                    if (temporary) manager.MarkTemporary(id);
                    if (dependsOn != null) manager.MarkDependent(id, dependsOn);

                    var items = new JArray();
                    foreach (var breakpoint in created)
                    {
                        ApplyWritable(breakpoint, spec);
                        items.Add(manager.Describe(breakpoint));
                    }

                    return new JObject
                    {
                        ["id"] = id,
                        ["file"] = spec.File,
                        ["requestedLine"] = spec.Line,
                        ["created"] = items.Count,
                        ["breakpoints"] = items,
                        ["note"] = BuildNote(items.Count, spec.Message, temporary, dependsOn)
                    };
                }, ct));

            registry.Add(
                "list_breakpoints",
                "Liste les points d'arret avec leur nature (ordinaire ou tracepoint), leur condition, " +
                "leur compteur de passages, et les attributs temporaire ou dependant.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("file", "Ne garder que les points d'arret de ce fichier.")
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var filter = Args.Str(args, "file");
                    var items = new JArray();

                    foreach (Breakpoint breakpoint in DteProvider.Dte.Debugger.Breakpoints)
                    {
                        var described = manager.Describe(breakpoint);
                        if (filter != null)
                        {
                            var file = (string)described["file"] ?? string.Empty;
                            if (file.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        }
                        items.Add(described);
                    }

                    return new JObject { ["count"] = items.Count, ["breakpoints"] = items };
                }, ct));

            registry.Add(
                "update_breakpoint",
                "Modifie un point d'arret existant : activation, message de tracepoint, condition, " +
                "compteur de passages ou filtre. Seuls les champs fournis changent.\n" +
                "Note : EnvDTE ne permet pas de modifier une condition ni un compteur de passages " +
                "apres creation. Dans ces cas le point est recree a l'identique avec la nouvelle " +
                "valeur, ce qui est transparent sauf que son compteur de passages repart de zero.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("id", "Identifiant du point d'arret, au format 'chemin:ligne'.", required: true)
                    .Bool("enabled", "Activer ou desactiver.")
                    .Str("condition", "Nouvelle condition. Chaine vide pour la retirer.")
                    .Str("condition_type", "Mode de la condition.", allowed: ConditionTypes)
                    .Str("message", "Nouveau message de tracepoint. Chaine vide pour repasser en point d'arret ordinaire.")
                    .Bool("also_break", "Pour un tracepoint : journaliser ET interrompre.")
                    .Int("hit_count", "Nouveau nombre de passages avant declenchement. 0 retire le filtre.")
                    .Str("hit_count_mode", "Comment interpreter 'hit_count'.", allowed: HitCountModes)
                    .Str("filter", "Nouveau filtre. Chaine vide pour le retirer.")
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var id = NormalizeId(Args.RequireStr(args, "id"));

                    var existing = new List<Breakpoint>(BreakpointManager.Find(id));
                    if (existing.Count == 0)
                    {
                        throw McpToolException.NotFound(
                            "Aucun point d'arret a '" + id + "'. Appeler list_breakpoints pour voir les identifiants.");
                    }

                    var current = ReadSpec(existing[0]);
                    var wanted = SpecFromArgs(args, current);

                    // Condition et compteur de passages sont en lecture seule : les changer
                    // impose de recreer le point d'arret.
                    var needsRecreate =
                        !string.Equals(wanted.Condition, current.Condition, StringComparison.Ordinal) ||
                        wanted.ConditionType != current.ConditionType ||
                        wanted.HitCount != current.HitCount ||
                        wanted.HitCountType != current.HitCountType;

                    var items = new JArray();

                    if (needsRecreate)
                    {
                        // Iteration a l'envers : Delete() compacte la collection.
                        for (var i = existing.Count - 1; i >= 0; i--)
                        {
                            try { existing[i].Delete(); }
                            catch (Exception ex)
                            {
                                throw new McpToolException("BREAKPOINT_UPDATE_FAILED",
                                    "Suppression prealable a la recreation impossible : " + ex.Message);
                            }
                        }

                        foreach (var breakpoint in Create(wanted))
                        {
                            ApplyWritable(breakpoint, wanted);
                            items.Add(manager.Describe(breakpoint));
                        }
                    }
                    else
                    {
                        foreach (var breakpoint in existing)
                        {
                            ApplyWritable(breakpoint, wanted);
                            items.Add(manager.Describe(breakpoint));
                        }
                    }

                    return new JObject
                    {
                        ["id"] = id,
                        ["updated"] = items.Count,
                        ["recreated"] = needsRecreate,
                        ["breakpoints"] = items,
                        ["note"] = needsRecreate
                            ? "Point d'arret recree : EnvDTE interdit de modifier une condition ou un " +
                              "compteur de passages apres creation. Le compteur de passages repart de zero."
                            : null
                    };
                }, ct));

            registry.Add(
                "clear_breakpoints",
                "Supprime des points d'arret : tous, ceux d'un fichier, ou celui d'une ligne precise.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("file", "Fichier dont les points d'arret doivent etre supprimes.")
                    .Int("line", "Ligne precise, a combiner avec 'file'.")
                    .Bool("all", "Supprimer tous les points d'arret de l'instance.", defaultValue: false)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var all = Args.Bool(args, "all", false);
                    var fileArg = Args.Str(args, "file");
                    var line = Args.Int(args, "line", 0);

                    if (!all && fileArg == null)
                    {
                        throw McpToolException.InvalidArgs("Preciser 'file', ou passer all=true.");
                    }

                    var file = fileArg == null ? null : SolutionTools.ResolvePath(fileArg);
                    var breakpoints = DteProvider.Dte.Debugger.Breakpoints;
                    var deleted = 0;

                    // Iteration a l'envers : Delete() compacte la collection, une iteration
                    // croissante sauterait un element sur deux.
                    for (var i = breakpoints.Count; i >= 1; i--)
                    {
                        Breakpoint breakpoint;
                        try { breakpoint = breakpoints.Item(i); }
                        catch (Exception) { continue; }

                        if (!all)
                        {
                            var bpFile = SafeString(() => breakpoint.File);
                            if (!string.Equals(bpFile, file, StringComparison.OrdinalIgnoreCase)) continue;
                            if (line > 0 && SafeInt(() => breakpoint.FileLine) != line) continue;
                        }

                        var id = BreakpointManager.IdOf(breakpoint);

                        try
                        {
                            breakpoint.Delete();
                            deleted++;
                            if (id != null) manager.Forget(id);
                        }
                        catch (Exception ex)
                        {
                            ExtensionLog.Warn("Suppression d'un point d'arret impossible : " + ex.Message);
                        }
                    }

                    return new JObject
                    {
                        ["deleted"] = deleted,
                        ["remaining"] = DteProvider.Dte.Debugger.Breakpoints.Count
                    };
                }, ct));
        }

        /// <summary>
        /// Construit l'etat souhaite. Quand <paramref name="current"/> est fourni, les champs
        /// absents des arguments conservent leur valeur actuelle : une mise a jour ne touche
        /// que ce qui est explicitement demande.
        /// </summary>
        private static BreakpointSpec SpecFromArgs(JObject args, BreakpointSpec current)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var spec = new BreakpointSpec();

            if (current != null)
            {
                spec.File = current.File;
                spec.Line = current.Line;
                spec.Column = current.Column;
                spec.Condition = current.Condition;
                spec.ConditionType = current.ConditionType;
                spec.HitCount = current.HitCount;
                spec.HitCountType = current.HitCountType;
                spec.Message = current.Message;
                spec.BreakWhenHit = current.BreakWhenHit;
                spec.FilterBy = current.FilterBy;
                spec.Enabled = current.Enabled;
            }
            else
            {
                spec.File = SolutionTools.ResolvePath(Args.RequireStr(args, "file"));
                spec.Line = Args.RequireInt(args, "line");
                spec.Column = Math.Max(1, Args.Int(args, "column", 1));
            }

            if (args["condition"] != null)
            {
                spec.Condition = Args.Str(args, "condition", string.Empty) ?? string.Empty;
            }
            if (args["condition_type"] != null)
            {
                spec.ConditionType = Args.Str(args, "condition_type", "when_true") == "when_changed"
                    ? dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenChanged
                    : dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue;
            }

            if (args["hit_count"] != null || args["hit_count_mode"] != null)
            {
                spec.HitCount = Math.Max(0, Args.Int(args, "hit_count", spec.HitCount));
                spec.HitCountType = spec.HitCount > 0
                    ? ParseHitCountMode(Args.Str(args, "hit_count_mode", "greater_or_equal"))
                    : dbgHitCountType.dbgHitCountTypeNone;
            }

            if (args["message"] != null)
            {
                spec.Message = Args.Str(args, "message", string.Empty) ?? string.Empty;
                // Un message fait un tracepoint : il journalise sans interrompre. Retirer le
                // message rend au point d'arret son comportement normal.
                spec.BreakWhenHit = string.IsNullOrEmpty(spec.Message) || Args.Bool(args, "also_break", false);
            }
            else if (args["also_break"] != null)
            {
                spec.BreakWhenHit = Args.Bool(args, "also_break", true);
            }
            else if (current == null)
            {
                spec.BreakWhenHit = true;
            }

            if (args["filter"] != null) spec.FilterBy = Args.Str(args, "filter", string.Empty) ?? string.Empty;
            if (args["enabled"] != null) spec.Enabled = Args.Bool(args, "enabled", true);

            return spec;
        }

        private static BreakpointSpec ReadSpec(Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var spec = new BreakpointSpec
            {
                File = SafeString(() => breakpoint.File),
                Line = SafeInt(() => breakpoint.FileLine),
                Column = Math.Max(1, SafeInt(() => breakpoint.FileColumn)),
                Condition = SafeString(() => breakpoint.Condition),
                HitCount = SafeInt(() => breakpoint.HitCountTarget),
                Enabled = SafeBool(() => breakpoint.Enabled)
            };

            try { spec.ConditionType = breakpoint.ConditionType; } catch (Exception) { }
            try { spec.HitCountType = breakpoint.HitCountType; } catch (Exception) { }

            var typed = breakpoint as Breakpoint2;
            if (typed != null)
            {
                spec.Message = SafeString(() => typed.Message);
                spec.BreakWhenHit = SafeBool(() => typed.BreakWhenHit);
                spec.FilterBy = SafeString(() => typed.FilterBy);
            }

            return spec;
        }

        private static List<Breakpoint> Create(BreakpointSpec spec)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Breakpoints added;
            try
            {
                // Un couple fichier/ligne peut produire PLUSIEURS points d'arret lies
                // (generique instancie plusieurs fois, multi-ciblage) : d'ou la collection.
                added = DteProvider.Dte.Debugger.Breakpoints.Add(
                    string.Empty, spec.File, spec.Line, spec.Column,
                    spec.Condition, spec.ConditionType,
                    string.Empty, string.Empty, 1, string.Empty,
                    spec.HitCount, spec.HitCountType);
            }
            catch (Exception ex)
            {
                throw new McpToolException("BREAKPOINT_FAILED",
                    "Point d'arret refuse sur " + spec.File + ":" + spec.Line + " : " + ex.Message);
            }

            var result = new List<Breakpoint>();
            if (added == null) return result;

            foreach (Breakpoint breakpoint in added) result.Add(breakpoint);
            return result;
        }

        /// <summary>Applique les seules proprietes qu'EnvDTE autorise a modifier apres creation.</summary>
        private static void ApplyWritable(Breakpoint breakpoint, BreakpointSpec spec)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var typed = breakpoint as Breakpoint2;

            if (typed != null)
            {
                if (!string.IsNullOrEmpty(spec.Message) || !spec.BreakWhenHit)
                {
                    try
                    {
                        typed.Message = spec.Message ?? string.Empty;
                        typed.BreakWhenHit = spec.BreakWhenHit;
                    }
                    catch (Exception ex)
                    {
                        throw new McpToolException("TRACEPOINT_FAILED",
                            "Message de tracepoint refuse : " + ex.Message);
                    }
                }

                if (!string.IsNullOrEmpty(spec.FilterBy))
                {
                    try { typed.FilterBy = spec.FilterBy; }
                    catch (Exception ex) { ExtensionLog.Warn("Filtre de point d'arret refuse : " + ex.Message); }
                }
            }
            else if (!string.IsNullOrEmpty(spec.Message))
            {
                throw new McpToolException("TRACEPOINT_UNSUPPORTED",
                    "Les tracepoints exigent l'interface Breakpoint2, indisponible pour ce point d'arret.");
            }

            try { breakpoint.Enabled = spec.Enabled; }
            catch (Exception ex) { ExtensionLog.Warn("Activation du point d'arret refusee : " + ex.Message); }
        }

        private static string ResolveDependency(string dependsOn)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (dependsOn == null) return null;

            var prerequisiteId = NormalizeId(dependsOn);
            foreach (var ignored in BreakpointManager.Find(prerequisiteId)) return prerequisiteId;

            throw McpToolException.NotFound(
                "Aucun point d'arret a '" + prerequisiteId + "' : poser d'abord le prerequis, puis le " +
                "point dependant. Appeler list_breakpoints pour voir les identifiants.");
        }

        private static dbgHitCountType ParseHitCountMode(string mode)
        {
            switch ((mode ?? string.Empty).ToLowerInvariant())
            {
                case "equal": return dbgHitCountType.dbgHitCountTypeEqual;
                case "multiple": return dbgHitCountType.dbgHitCountTypeMultiple;
                default: return dbgHitCountType.dbgHitCountTypeGreaterOrEqual;
            }
        }

        /// <summary>Normalise un identifiant 'chemin:ligne', en tolerant les lettres de lecteur.</summary>
        private static string NormalizeId(string id)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var separator = id.LastIndexOf(':');
            if (separator <= 0)
            {
                throw McpToolException.InvalidArgs(
                    "Identifiant attendu au format 'chemin:ligne', recu : '" + id + "'.");
            }

            int line;
            if (!int.TryParse(id.Substring(separator + 1), out line))
            {
                throw McpToolException.InvalidArgs(
                    "Numero de ligne illisible dans l'identifiant '" + id + "'.");
            }

            return BreakpointManager.IdFor(SolutionTools.ResolvePath(id.Substring(0, separator)), line);
        }

        private static JToken BuildNote(int count, string message, bool temporary, string dependsOn)
        {
            if (count == 0)
            {
                return "Aucun point d'arret cree : verifier que le fichier appartient a un projet charge.";
            }

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(message)) parts.Add("tracepoint : le message part dans le pane de sortie Debogage");
            if (temporary) parts.Add("temporaire : supprime apres son premier declenchement (emule par l'extension)");
            if (dependsOn != null) parts.Add("dependant : desactive jusqu'a ce que " + dependsOn + " soit atteint (emule par l'extension)");

            return parts.Count == 0 ? JValue.CreateNull() : (JToken)string.Join(" ; ", parts);
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

        private static bool SafeBool(Func<bool> accessor)
        {
            try { return accessor(); }
            catch (Exception) { return false; }
        }
    }
}
