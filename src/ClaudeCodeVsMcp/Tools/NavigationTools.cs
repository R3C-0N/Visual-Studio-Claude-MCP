using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using Constants = EnvDTE.Constants;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using TextSelection = EnvDTE.TextSelection;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    /// <summary>
    /// Navigation semantique (Roslyn) : declarations, definitions, references. Exact la ou une
    /// recherche de texte confond surcharges, homonymes et implementations d'interface.
    ///
    /// Les corps sont asynchrones : les recherches Roslyn tournent hors du thread UI, et le
    /// thread UI n'est repris que pour le DTE (documents non enregistres, ouverture d'editeur).
    /// </summary>
    internal static class NavigationTools
    {
        /// <summary>Une recherche de references sur une grosse solution depasse les 30 s par defaut.</summary>
        private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(60);

        internal static void Register(ToolRegistry registry)
        {
            registry.Add(
                "find_symbol",
                "Cherche des declarations (types, methodes, proprietes...) dans le code C#/VB de la solution, " +
                "par nom ou motif. Le motif tolere les majuscules de camelCase : 'ExBr' trouve ExceptionBreaks.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("query", "Nom ou motif du symbole.", required: true)
                    .Str("kind", "Nature des symboles.", allowed: new[] { "all", "type", "method", "property", "field", "event" }, defaultValue: "all")
                    .Str("project", "Ne garder que les projets dont le nom contient ce texte.")
                    .Int("max_results", "Nombre maximum de resultats.", defaultValue: 50)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(async () =>
                {
                    var query = Args.RequireStr(args, "query");
                    var kind = Args.Str(args, "kind", "all").ToLowerInvariant();
                    var project = Args.Str(args, "project");
                    var maxResults = Clamp(Args.Int(args, "max_results", 50), 1, 500);

                    var solution = RoslynNavigator.CurrentSolution();
                    var dirty = DocumentHelper.DirtyPaths();

                    var filter = kind == "type" ? SymbolFilter.Type
                        : kind == "all" ? SymbolFilter.TypeAndMember
                        : SymbolFilter.Member;

                    var found = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(solution, query, filter, ct);

                    var symbols = new JArray();
                    var total = 0;

                    foreach (var symbol in RoslynNavigator.Distinct(found))
                    {
                        if (!MatchesKind(symbol, kind)) continue;

                        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                        if (location == null) continue;

                        var description = RoslynNavigator.DescribeSymbol(symbol);
                        description["location"] = await RoslynNavigator.DescribeLocationAsync(location, solution, dirty, ct);

                        if (!string.IsNullOrEmpty(project) &&
                            ((string)description["location"]["project"] ?? string.Empty)
                                .IndexOf(project, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        total++;
                        if (symbols.Count < maxResults) symbols.Add(description);
                    }

                    return new JObject
                    {
                        ["query"] = query,
                        ["total"] = total,
                        ["truncated"] = total > symbols.Count,
                        ["symbols"] = symbols
                    };
                }, ct, SearchTimeout));

            registry.Add(
                "go_to_definition",
                "Definition(s) d'un symbole : toutes les declarations d'un type partiel, ou l'assembly pour un " +
                "symbole compile (framework, NuGet). " + TargetHelp,
                Target(SchemaBuilder.New().Instance())
                    .Bool("open", "Ouvrir aussi la premiere definition dans l'editeur.", defaultValue: false)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(async () =>
                {
                    var solution = RoslynNavigator.CurrentSolution();
                    var dirty = DocumentHelper.DirtyPaths();
                    var symbol = await ResolveAsync(solution, args, ct);

                    // Une methode generique construite ou une extension reduite pointe vers sa definition.
                    var definition = symbol.OriginalDefinition ?? symbol;
                    if (!definition.Locations.Any(l => l.IsInSource))
                    {
                        var source = await SymbolFinder.FindSourceDefinitionAsync(definition, solution, ct);
                        if (source != null) definition = source;
                    }

                    var definitions = new JArray();
                    foreach (var location in definition.Locations)
                    {
                        definitions.Add(await RoslynNavigator.DescribeLocationAsync(location, solution, dirty, ct));
                    }

                    var result = new JObject
                    {
                        ["symbol"] = RoslynNavigator.DescribeSymbol(definition),
                        ["inSource"] = definition.Locations.Any(l => l.IsInSource),
                        ["definitions"] = definitions
                    };

                    if (Args.Bool(args, "open", false))
                    {
                        var first = definitions.OfType<JObject>().FirstOrDefault(d => d["file"] != null);
                        if (first != null)
                        {
                            Open((string)first["file"], (int)first["line"], (int)first["column"]);
                            result["opened"] = first["file"];
                        }
                    }

                    return result;
                }, ct, SearchTimeout));

            registry.Add(
                "find_references",
                "Toutes les references a un symbole dans la solution, par analyse semantique (Roslyn) : les " +
                "homonymes sont exclus, les surcharges distinguees, les implementations et substitutions " +
                "incluses. " + TargetHelp,
                Target(SchemaBuilder.New().Instance())
                    .Bool("include_definition", "Inclure aussi les declarations du symbole.", defaultValue: false)
                    .Int("max_results", "Nombre maximum de references retournees.", defaultValue: 200)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(async () =>
                {
                    var includeDefinition = Args.Bool(args, "include_definition", false);
                    var maxResults = Clamp(Args.Int(args, "max_results", 200), 1, 2000);

                    var solution = RoslynNavigator.CurrentSolution();
                    var dirty = DocumentHelper.DirtyPaths();
                    var symbol = await ResolveAsync(solution, args, ct);

                    var referenced = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);

                    // Positions dedoublonnees : une meme reference peut etre rattachee a plusieurs
                    // symboles en cascade (membre d'interface et son implementation).
                    var locations = new Dictionary<string, Location>(StringComparer.OrdinalIgnoreCase);
                    foreach (var group in referenced)
                    {
                        if (includeDefinition)
                        {
                            foreach (var location in group.Definition.Locations.Where(l => l.IsInSource))
                            {
                                locations[Key(location)] = location;
                            }
                        }

                        foreach (var reference in group.Locations)
                        {
                            if (reference.IsImplicit || !reference.Location.IsInSource) continue;
                            locations[Key(reference.Location)] = reference.Location;
                        }
                    }

                    var ordered = locations.Values
                        .OrderBy(l => l.SourceTree.FilePath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(l => l.SourceSpan.Start)
                        .ToList();

                    var references = new JArray();
                    foreach (var location in ordered.Take(maxResults))
                    {
                        references.Add(await RoslynNavigator.DescribeLocationAsync(location, solution, dirty, ct));
                    }

                    return new JObject
                    {
                        ["symbol"] = RoslynNavigator.DescribeSymbol(symbol),
                        ["total"] = ordered.Count,
                        ["truncated"] = ordered.Count > references.Count,
                        ["references"] = references
                    };
                }, ct, SearchTimeout));
        }

        private const string TargetHelp =
            "Designer le symbole par sa position (file + line + symbol, le texte de l'identifiant sur cette " +
            "ligne) ou par son nom (name, simple ou qualifie).";

        private static SchemaBuilder Target(SchemaBuilder builder)
        {
            return builder
                .Str("file", "Fichier contenant une occurrence du symbole. Absolu, ou relatif a la solution.")
                .Int("line", "Ligne de cette occurrence (1-based).")
                .Str("symbol", "Identifiant tel qu'ecrit sur la ligne, par exemple 'Apply' ou 'ExceptionBreaks.Apply'.")
                .Int("column", "Colonne (1-based), a la place de 'symbol'.")
                .Str("name", "A defaut de position : nom simple ou qualifie, par exemple 'ExceptionBreaks.Apply'.");
        }

        private static async Task<ISymbol> ResolveAsync(Solution solution, JObject args, CancellationToken ct)
        {
            // ResolvePath lit le chemin de la solution dans le DTE.
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            var file = Args.Str(args, "file");
            if (!string.IsNullOrEmpty(file)) file = SolutionTools.ResolvePath(file);

            return await RoslynNavigator.ResolveSymbolAsync(
                solution,
                file,
                Args.Int(args, "line", 0),
                Args.Str(args, "symbol"),
                Args.Int(args, "column", 0),
                Args.Str(args, "name"),
                ct);
        }

        private static bool MatchesKind(ISymbol symbol, string kind)
        {
            switch (kind)
            {
                case "type": return symbol is INamedTypeSymbol;
                case "method": return symbol is IMethodSymbol;
                case "property": return symbol is IPropertySymbol;
                case "field": return symbol is IFieldSymbol;
                case "event": return symbol is IEventSymbol;
                default: return !(symbol is INamespaceSymbol);
            }
        }

        private static string Key(Location location)
        {
            return location.SourceTree.FilePath + ":" + location.SourceSpan.Start;
        }

        private static void Open(string path, int line, int column)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var window = DteProvider.Dte.ItemOperations.OpenFile(path, Constants.vsViewKindTextView);
            window?.Activate();
            (window?.Document?.Selection as TextSelection)?.MoveToLineAndOffset(line, Math.Max(1, column));
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Min(Math.Max(value, min), max);
        }
    }
}
