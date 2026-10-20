using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Vs
{
    /// <summary>
    /// Navigation semantique par Roslyn, sur la solution deja chargee et analysee par l'IDE
    /// (VisualStudioWorkspace) : pas de second chargement MSBuild, et le texte vu est celui des
    /// tampons de l'editeur, modifications non enregistrees comprises.
    ///
    /// Seuls C# et VB ont un modele Roslyn : un fichier C++ n'apparait pas dans l'espace de travail.
    ///
    /// Hors GetWorkspace, rien ici ne touche au DTE : les methodes asynchrones peuvent reprendre
    /// sur un thread du pool (ConfigureAwait(false)).
    /// </summary>
    internal static class RoslynNavigator
    {
        private const int MaxTextLength = 200;

        /// <summary>Nom qualifie sans parametres : MonNs.MaClasse.Methode. Sert a comparer avec 'name'.</summary>
        private static readonly SymbolDisplayFormat QualifiedFormat = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

        /// <summary>Idem avec les parametres de type, pour l'affichage : Repository&lt;TEntity&gt;.</summary>
        private static readonly SymbolDisplayFormat GenericQualifiedFormat = QualifiedFormat
            .WithGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeParameters);

        internal static Solution CurrentSolution()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Workspace().CurrentSolution;
        }

        internal static VisualStudioWorkspace Workspace()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            DteProvider.RequireSolution();

            VisualStudioWorkspace workspace = null;
            try
            {
                var componentModel = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
                workspace = componentModel?.GetService<VisualStudioWorkspace>();
            }
            catch (Exception)
            {
                // GetService leve si l'export MEF est absent : meme traitement que null.
            }

            if (workspace == null)
            {
                throw new McpToolException("UNSUPPORTED",
                    "L'espace de travail Roslyn n'est pas disponible dans cette instance de Visual Studio.");
            }
            return workspace;
        }

        /// <summary>
        /// Symbole designe soit par une position (file + line + symbol ou column), soit par un nom
        /// simple ou qualifie (name).
        /// </summary>
        internal static async Task<ISymbol> ResolveSymbolAsync(
            Solution solution, string file, int line, string symbolText, int column, string name, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(file))
            {
                return await ResolveAtPositionAsync(solution, file, line, symbolText, column, ct).ConfigureAwait(false);
            }
            if (!string.IsNullOrEmpty(name))
            {
                return await ResolveByNameAsync(solution, name, ct).ConfigureAwait(false);
            }

            throw McpToolException.InvalidArgs(
                "Designer le symbole par 'file' + 'line' + 'symbol' (ou 'column'), ou par 'name'.");
        }

        private static async Task<ISymbol> ResolveAtPositionAsync(
            Solution solution, string path, int line, string symbolText, int column, CancellationToken ct)
        {
            var document = FindDocument(solution, path);
            var text = await document.GetTextAsync(ct).ConfigureAwait(false);

            if (line < 1 || line > text.Lines.Count)
            {
                throw McpToolException.InvalidArgs(
                    "Ligne " + line + " hors du fichier (" + text.Lines.Count + " lignes dans le tampon de l'editeur).");
            }

            var textLine = text.Lines[line - 1];
            var lineText = textLine.ToString();
            int offset;

            if (column > 0)
            {
                offset = Math.Min(column - 1, Math.Max(0, lineText.Length - 1));
            }
            else if (!string.IsNullOrEmpty(symbolText))
            {
                offset = FindIdentifier(lineText, symbolText);
                if (offset < 0)
                {
                    throw McpToolException.NotFound(
                        "'" + symbolText + "' absent de la ligne " + line + " : " + Truncate(lineText.Trim()));
                }
            }
            else
            {
                throw McpToolException.InvalidArgs("Avec 'file' et 'line', donner aussi 'symbol' ou 'column'.");
            }

            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, textLine.Start + offset, ct)
                .ConfigureAwait(false);

            if (symbol == null)
            {
                throw McpToolException.NotFound(
                    "Aucun symbole a la ligne " + line + ", colonne " + (offset + 1) + " : " + Truncate(lineText.Trim()));
            }
            return symbol;
        }

        private static async Task<ISymbol> ResolveByNameAsync(Solution solution, string name, CancellationToken ct)
        {
            var qualified = name.Trim();
            var paren = qualified.IndexOf('(');
            if (paren >= 0) qualified = qualified.Substring(0, paren);

            var simple = qualified.Substring(qualified.LastIndexOf('.') + 1);

            var declarations = await SymbolFinder.FindSourceDeclarationsAsync(
                solution, n => n == simple, SymbolFilter.TypeAndMember, ct).ConfigureAwait(false);

            var matches = Distinct(declarations.Where(s =>
            {
                var display = s.ToDisplayString(QualifiedFormat);
                return display == qualified || display.EndsWith("." + qualified, StringComparison.Ordinal);
            })).ToList();

            if (matches.Count == 1) return matches[0];

            if (matches.Count == 0)
            {
                throw McpToolException.NotFound(
                    "Aucune declaration '" + name + "' dans la solution. find_symbol cherche par motif.");
            }

            var candidates = matches.Take(10).Select(s =>
            {
                var location = s.Locations.FirstOrDefault(l => l.IsInSource);
                var where = location == null ? string.Empty
                    : " (" + location.GetLineSpan().Path + ":" + (location.GetLineSpan().StartLinePosition.Line + 1) + ")";
                return s.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) + where;
            });

            throw McpToolException.InvalidArgs(
                "Nom ambigu '" + name + "', " + matches.Count + " symboles : " + string.Join(" ; ", candidates) +
                ". Qualifier le nom, ou designer le symbole par file + line + symbol.");
        }

        internal static Document FindDocument(Solution solution, string path)
        {
            var ids = solution.GetDocumentIdsWithFilePath(path);
            if (ids.IsDefaultOrEmpty)
            {
                throw McpToolException.NotFound(
                    "Fichier absent de l'espace de travail Roslyn : " + path + ". Seuls les fichiers C# et VB " +
                    "des projets charges de la solution sont analyses.");
            }
            return solution.GetDocument(ids[0]);
        }

        /// <summary>Symboles distincts : un projet multi-cible declare le meme symbole plusieurs fois.</summary>
        internal static IEnumerable<ISymbol> Distinct(IEnumerable<ISymbol> symbols)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var symbol in symbols)
            {
                var location = symbol.Locations.FirstOrDefault();
                var key = symbol.Kind + "|" + symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) + "|" +
                          (location != null && location.IsInSource
                              ? location.SourceTree.FilePath + ":" + location.SourceSpan.Start
                              : string.Empty);
                if (seen.Add(key)) yield return symbol;
            }
        }

        internal static JObject DescribeSymbol(ISymbol symbol)
        {
            var result = new JObject
            {
                ["name"] = symbol.Name,
                ["kind"] = KindOf(symbol),
                ["display"] = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            };

            if (symbol.ContainingType != null)
            {
                result["containingType"] = symbol.ContainingType.ToDisplayString(GenericQualifiedFormat);
            }
            else if (symbol.ContainingNamespace != null && !symbol.ContainingNamespace.IsGlobalNamespace)
            {
                result["namespace"] = symbol.ContainingNamespace.ToDisplayString();
            }
            return result;
        }

        /// <summary>
        /// Position source (fichier, ligne et colonne 1-based, texte de la ligne) ou, pour un symbole
        /// compile, l'assembly qui le contient.
        /// </summary>
        internal static async Task<JObject> DescribeLocationAsync(
            Location location, Solution solution, ISet<string> dirtyPaths, CancellationToken ct)
        {
            if (location.IsInSource)
            {
                var span = location.GetLineSpan();
                var line = span.StartLinePosition.Line;
                var text = await location.SourceTree.GetTextAsync(ct).ConfigureAwait(false);

                var result = new JObject
                {
                    ["file"] = span.Path,
                    ["line"] = line + 1,
                    ["column"] = span.StartLinePosition.Character + 1,
                    ["text"] = line < text.Lines.Count ? Truncate(text.Lines[line].ToString().Trim()) : string.Empty
                };

                var project = solution.GetDocument(location.SourceTree)?.Project?.Name;
                if (!string.IsNullOrEmpty(project)) result["project"] = project;

                if (dirtyPaths != null && dirtyPaths.Contains(DocumentHelper.Normalize(span.Path)))
                {
                    // Le tampon differe du disque : les lignes peuvent ne pas correspondre au fichier lu.
                    result["unsaved"] = true;
                }
                return result;
            }

            return new JObject
            {
                ["metadata"] = true,
                ["assembly"] = location.MetadataModule?.ContainingAssembly?.Name
            };
        }

        internal static string KindOf(ISymbol symbol)
        {
            switch (symbol)
            {
                case INamedTypeSymbol type:
                    return type.TypeKind.ToString().ToLowerInvariant();
                case IMethodSymbol method:
                    return method.MethodKind == MethodKind.Constructor || method.MethodKind == MethodKind.StaticConstructor
                        ? "constructor"
                        : "method";
                default:
                    return symbol.Kind.ToString().ToLowerInvariant();
            }
        }

        /// <summary>
        /// Position de l'identifiant dans la ligne, mot entier. "Classe.Membre" vise le dernier segment.
        /// </summary>
        private static int FindIdentifier(string lineText, string symbolText)
        {
            var target = symbolText.Trim();
            var start = 0;

            while (true)
            {
                var index = lineText.IndexOf(target, start, StringComparison.Ordinal);
                if (index < 0) return -1;

                var end = index + target.Length;
                var before = index == 0 || !IsIdentifierChar(lineText[index - 1]);
                var after = end >= lineText.Length || !IsIdentifierChar(lineText[end]);

                if (before && after) return index + target.LastIndexOf('.') + 1;
                start = index + 1;
            }
        }

        private static bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        internal static string Truncate(string value)
        {
            return value.Length <= MaxTextLength ? value : value.Substring(0, MaxTextLength) + "...";
        }
    }
}
