using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    /// <summary>
    /// Refactorings Roslyn : renommage sur toute la solution et correctifs de l'ampoule. Plus surs
    /// qu'une edition de texte : homonymes, surcharges et fichiers lies sont traites par le
    /// compilateur, et rien n'est applique si la modification introduit une erreur.
    /// </summary>
    internal static class RefactorTools
    {
        /// <summary>Renommage et analyseurs peuvent etre longs sur une grosse solution.</summary>
        private static readonly TimeSpan RefactorTimeout = TimeSpan.FromSeconds(120);

        internal static void Register(ToolRegistry registry)
        {
            registry.Add(
                "rename_symbol",
                "Renomme un symbole (type, membre, variable, parametre) dans toute la solution, comme F2 " +
                "dans Visual Studio. Commencer par preview:true. Rien n'est applique si le renommage " +
                "introduit des erreurs de compilation (newErrors), sauf force:true. " + NavigationToolsHelp,
                NavigationTools.Target(SchemaBuilder.New().Instance())
                    .Str("new_name", "Nouveau nom.", required: true)
                    .Bool("preview", "Decrire les modifications sans les appliquer.", defaultValue: false)
                    .Bool("force", "Appliquer meme si de nouvelles erreurs apparaissent.", defaultValue: false)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(async () =>
                {
                    var newName = Args.RequireStr(args, "new_name").Trim();
                    var preview = Args.Bool(args, "preview", false);
                    var force = Args.Bool(args, "force", false);

                    var workspace = RoslynNavigator.Workspace();
                    var solution = workspace.CurrentSolution;
                    var symbol = await NavigationTools.ResolveAsync(solution, args, ct);

                    if (!symbol.Locations.Any(l => l.IsInSource))
                    {
                        throw McpToolException.InvalidArgs(
                            "'" + symbol.Name + "' est defini dans un assembly compile, pas dans la solution : " +
                            "il ne peut pas etre renomme.");
                    }
                    if (symbol.Name == newName)
                    {
                        throw McpToolException.InvalidArgs("Le symbole s'appelle deja '" + newName + "'.");
                    }

                    var renamed = await RoslynRefactorer.RenameAsync(solution, symbol, newName, ct);
                    var result = await RoslynRefactorer.ApplyAsync(workspace, solution, renamed, preview, force, ct);

                    result["symbol"] = RoslynNavigator.DescribeSymbol(symbol);
                    result["newName"] = newName;
                    return result;
                }, ct, RefactorTimeout));

            registry.Add(
                "apply_code_fix",
                "Correctifs de l'ampoule pour une ligne : ajouter un using, implementer une interface, " +
                "corriger un avertissement d'analyseur... Sans 'fix', liste les diagnostics de la ligne et " +
                "les correctifs proposes ; avec 'fix', applique celui choisi. Commencer par preview:true. " +
                "Rien n'est applique si le correctif introduit des erreurs, sauf force:true.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("file", "Fichier. Absolu, ou relatif au repertoire de la solution.", required: true)
                    .Int("line", "Ligne du diagnostic (1-based).", required: true)
                    .Str("code", "Ne garder que les diagnostics dont le code commence par ce texte, par exemple 'CS0246'.")
                    .Str("fix", "Correctif a appliquer : son index dans la liste, ou une partie unique de son titre.")
                    .Bool("preview", "Decrire les modifications sans les appliquer.", defaultValue: false)
                    .Bool("force", "Appliquer meme si de nouvelles erreurs apparaissent.", defaultValue: false)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(async () =>
                {
                    var path = SolutionTools.ResolvePath(Args.RequireStr(args, "file"));
                    var line = Args.RequireInt(args, "line");
                    var fix = Args.Str(args, "fix");

                    var workspace = RoslynNavigator.Workspace();
                    var solution = workspace.CurrentSolution;
                    var document = RoslynNavigator.FindDocument(solution, path);
                    var providers = RoslynRefactorer.Providers(document.Project);

                    var search = await RoslynRefactorer.FindFixesAsync(document, line, Args.Str(args, "code"), providers, ct);

                    if (string.IsNullOrWhiteSpace(fix))
                    {
                        var listing = new JObject
                        {
                            ["file"] = path,
                            ["line"] = line,
                            ["diagnostics"] = new JArray(search.Diagnostics.Select(RoslynRefactorer.DescribeDiagnostic)),
                            ["fixes"] = new JArray(search.Fixes.Select((f, i) => new JObject
                            {
                                ["index"] = i,
                                ["title"] = f.Title,
                                ["code"] = f.Diagnostic.Id
                            }))
                        };

                        if (search.Diagnostics.Count == 0)
                        {
                            listing["hint"] = "Aucun diagnostic sur cette ligne. Les refactorings de l'ampoule " +
                                              "sans diagnostic (extraire une methode...) ne sont pas proposes.";
                        }
                        else if (search.Fixes.Count == 0)
                        {
                            listing["hint"] = "Aucun correctif automatique pour ces diagnostics.";
                        }
                        else
                        {
                            listing["hint"] = "Rappeler avec fix (index ou titre), de preference avec preview:true d'abord.";
                        }
                        return listing;
                    }

                    var chosen = Choose(search.Fixes, fix);
                    var changed = await RoslynRefactorer.ChangedSolutionAsync(chosen.Action, ct);
                    var result = await RoslynRefactorer.ApplyAsync(
                        workspace, solution, changed, Args.Bool(args, "preview", false), Args.Bool(args, "force", false), ct);

                    result["fix"] = new JObject
                    {
                        ["title"] = chosen.Title,
                        ["code"] = chosen.Diagnostic.Id,
                        ["provider"] = chosen.Provider
                    };
                    return result;
                }, ct, RefactorTimeout));
        }

        private const string NavigationToolsHelp =
            "Designer le symbole par sa position (file + line + symbol) ou par son nom (name).";

        /// <summary>Correctif designe par son index, son titre exact, ou une partie unique de son titre.</summary>
        private static RoslynRefactorer.FixCandidate Choose(List<RoslynRefactorer.FixCandidate> fixes, string fix)
        {
            if (fixes.Count == 0)
            {
                throw McpToolException.NotFound("Aucun correctif disponible sur cette ligne. Appeler sans 'fix' pour voir les diagnostics.");
            }

            if (int.TryParse(fix.Trim(), out var index))
            {
                if (index >= 0 && index < fixes.Count) return fixes[index];
                throw McpToolException.InvalidArgs("Index " + index + " hors de la liste (0 a " + (fixes.Count - 1) + ").");
            }

            var exact = fixes.Where(f => string.Equals(f.Title, fix.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];

            var partial = fixes.Where(f => f.Title.IndexOf(fix.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (partial.Count == 1) return partial[0];

            var candidates = (partial.Count > 0 ? partial : fixes).Take(15).Select(f => fixes.IndexOf(f) + ": " + f.Title);
            throw McpToolException.InvalidArgs(
                (partial.Count == 0 ? "Aucun correctif ne correspond a '" : "Plusieurs correctifs correspondent a '") +
                fix + "' : " + string.Join(" ; ", candidates) + ".");
        }
    }
}
