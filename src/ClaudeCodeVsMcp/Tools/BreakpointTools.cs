using System;
using System.Collections.Generic;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using EnvDTE;
using EnvDTE80;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    internal static class BreakpointTools
    {
        internal static void Register(ToolRegistry registry)
        {
            registry.Add(
                "set_breakpoint",
                "Pose un point d'arret sur une ligne, avec condition optionnelle. Fonctionne aussi hors " +
                "session de debogage : le point reste en attente jusqu'au chargement du module.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("file", "Chemin du fichier source.", required: true)
                    .Int("line", "Numero de ligne (1-based).", required: true)
                    .Int("column", "Colonne (1-based).", defaultValue: 1)
                    .Str("condition", "Expression conditionnelle, par exemple 'i > 10'.")
                    .Str("condition_type", "Mode de la condition.",
                        allowed: new[] { "when_true", "when_changed" }, defaultValue: "when_true")
                    .Int("hit_count", "Declencher seulement a partir de ce nombre de passages.", defaultValue: 0)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var file = SolutionTools.ResolvePath(Args.RequireStr(args, "file"));
                    var line = Args.RequireInt(args, "line");
                    var column = Math.Max(1, Args.Int(args, "column", 1));
                    var condition = Args.Str(args, "condition", string.Empty);
                    var hitCount = Args.Int(args, "hit_count", 0);

                    var conditionType = Args.Str(args, "condition_type", "when_true") == "when_changed"
                        ? dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenChanged
                        : dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue;

                    var hitCountType = hitCount > 0
                        ? dbgHitCountType.dbgHitCountTypeGreaterOrEqual
                        : dbgHitCountType.dbgHitCountTypeNone;

                    Breakpoints created;
                    try
                    {
                        // Un couple fichier/ligne peut produire PLUSIEURS points d'arret lies
                        // (generique instancie plusieurs fois, multi-ciblage) : d'ou la collection.
                        created = DteProvider.Dte.Debugger.Breakpoints.Add(
                            string.Empty, file, line, column, condition, conditionType,
                            string.Empty, string.Empty, 1, string.Empty, hitCount, hitCountType);
                    }
                    catch (Exception ex)
                    {
                        throw new McpToolException("BREAKPOINT_FAILED",
                            "Point d'arret refuse sur " + file + ":" + line + " : " + ex.Message);
                    }

                    var items = new JArray();
                    if (created != null)
                    {
                        foreach (Breakpoint breakpoint in created)
                        {
                            items.Add(Describe(breakpoint));
                        }
                    }

                    return new JObject
                    {
                        ["file"] = file,
                        ["requestedLine"] = line,
                        ["created"] = items.Count,
                        ["breakpoints"] = items,
                        ["note"] = items.Count == 0
                            ? "Aucun point d'arret cree : verifier que le fichier appartient a un projet charge."
                            : null
                    };
                }, ct));

            registry.Add(
                "list_breakpoints",
                "Liste les points d'arret definis dans l'instance.",
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
                        var described = Describe(breakpoint);
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
                            var bpFile = Safe(() => breakpoint.File);
                            if (!string.Equals(bpFile, file, StringComparison.OrdinalIgnoreCase)) continue;
                            if (line > 0 && SafeInt(() => breakpoint.FileLine) != line) continue;
                        }

                        try
                        {
                            breakpoint.Delete();
                            deleted++;
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

        private static JObject Describe(Breakpoint breakpoint)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var result = new JObject
            {
                ["file"] = Safe(() => breakpoint.File),
                ["line"] = SafeInt(() => breakpoint.FileLine),
                ["column"] = SafeInt(() => breakpoint.FileColumn),
                ["enabled"] = SafeBool(() => breakpoint.Enabled),
                ["condition"] = Safe(() => breakpoint.Condition),
                ["functionName"] = Safe(() => breakpoint.FunctionName)
            };

            // Breakpoint2 apporte le compteur de passages ; absent sur certaines implementations.
            var typed = breakpoint as Breakpoint2;
            if (typed != null)
            {
                result["currentHits"] = SafeInt(() => typed.CurrentHits);
                result["hitCountTarget"] = SafeInt(() => typed.HitCountTarget);
            }

            return result;
        }

        private static string Safe(Func<string> accessor)
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
