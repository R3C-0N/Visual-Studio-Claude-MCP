using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Vs
{
    /// <summary>
    /// Modifications de code par Roslyn : renommage et correctifs de l'ampoule.
    ///
    /// Toute modification passe par ApplyAsync, qui resume les changements, compare les erreurs du
    /// compilateur avant et apres, et n'applique (TryApplyChanges) que sans nouvelle erreur, sauf
    /// force. C'est ce garde-fou qui attrape un renommage en conflit.
    /// </summary>
    internal static class RoslynRefactorer
    {
        private const int MaxEdits = 50;
        private const int MaxErrors = 30;

        /// <summary>Correctifs exportes par MEF (livres avec Visual Studio), par langage.</summary>
        private static readonly ConcurrentDictionary<string, IReadOnlyList<CodeFixProvider>> MefProviders =
            new ConcurrentDictionary<string, IReadOnlyList<CodeFixProvider>>(StringComparer.Ordinal);

        /// <summary>Correctifs des paquets d'analyseurs, par chemin d'assembly et langage.</summary>
        private static readonly ConcurrentDictionary<string, IReadOnlyList<CodeFixProvider>> PackageProviders =
            new ConcurrentDictionary<string, IReadOnlyList<CodeFixProvider>>(StringComparer.OrdinalIgnoreCase);

        internal sealed class FixCandidate
        {
            internal CodeAction Action;
            internal string Title;
            internal Diagnostic Diagnostic;
            internal string Provider;
        }

        internal sealed class FixSearch
        {
            internal List<Diagnostic> Diagnostics = new List<Diagnostic>();
            internal List<FixCandidate> Fixes = new List<FixCandidate>();
        }

        // ---------------------------------------------------------------- Renommage

        internal static Task<Solution> RenameAsync(Solution solution, ISymbol symbol, string newName, CancellationToken ct)
        {
            // Surcharge OptionSet : la seule de Roslyn 4.0.1 (VS 17.0, borne basse de l'extension).
            // Obsolete a partir de 4.4 mais toujours presente : la remplacer exigerait d'abandonner
            // VS 17.0-17.3. null = options de la solution.
#pragma warning disable CS0618
            return Renamer.RenameSymbolAsync(solution, symbol, newName, null, ct);
#pragma warning restore CS0618
        }

        // ---------------------------------------------------------------- Application

        /// <summary>
        /// Resume, verifie et (hors preview) applique newSolution. Les documents lies (un fichier
        /// partage par plusieurs cibles d'un projet multi-cible) recoivent le meme texte.
        /// </summary>
        internal static async Task<JObject> ApplyAsync(
            Workspace workspace, Solution oldSolution, Solution newSolution, bool preview, bool force, CancellationToken ct)
        {
            newSolution = await AlignLinkedDocumentsAsync(oldSolution, newSolution, ct).ConfigureAwait(false);

            var changedIds = newSolution.GetChanges(oldSolution).GetProjectChanges()
                .SelectMany(p => p.GetChangedDocuments())
                .ToList();

            if (changedIds.Count == 0)
            {
                throw new McpToolException("NO_CHANGE", "L'operation ne modifie aucun fichier.");
            }

            var documents = new JArray();
            var newErrors = new JArray();
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenErrors = new HashSet<string>(StringComparer.Ordinal);
            var changedPaths = new List<string>();
            var editCount = 0;

            foreach (var id in changedIds)
            {
                var oldDocument = oldSolution.GetDocument(id);
                var newDocument = newSolution.GetDocument(id);
                if (oldDocument == null || newDocument == null) continue;

                // Les erreurs sont verifiees pour chaque cible : un renommage peut casser une seule
                // d'entre elles (code sous #if).
                foreach (var error in await NewErrorsAsync(oldDocument, newDocument, ct).ConfigureAwait(false))
                {
                    var key = (string)error["file"] + ":" + error["line"] + ":" + error["code"] + ":" + error["message"];
                    if (newErrors.Count < MaxErrors && seenErrors.Add(key)) newErrors.Add(error);
                }

                var path = newDocument.FilePath ?? newDocument.Name;
                if (!seenFiles.Add(path)) continue;
                changedPaths.Add(path);

                var summary = await SummarizeAsync(oldDocument, newDocument, MaxEdits - editCount, ct).ConfigureAwait(false);
                editCount += ((JArray)summary["edits"]).Count;
                documents.Add(summary);
            }

            var result = new JObject
            {
                ["documentCount"] = documents.Count,
                ["documents"] = documents,
                ["newErrors"] = newErrors,
                ["applied"] = false
            };

            if (preview)
            {
                result["preview"] = true;
                return result;
            }

            if (newErrors.Count > 0 && !force)
            {
                result["hint"] = "La modification introduit des erreurs de compilation : rien n'a ete applique. " +
                                 "Corriger la demande, ou relancer avec force:true pour appliquer quand meme.";
                return result;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            if (!workspace.TryApplyChanges(newSolution))
            {
                throw new McpToolException("CONFLICT",
                    "La solution a change pendant le calcul (edition, rechargement) : relancer l'operation.");
            }

            result["applied"] = true;

            var dirty = DocumentHelper.DirtyPaths();
            var unsaved = new JArray(changedPaths.Where(p => dirty.Contains(DocumentHelper.Normalize(p))));
            if (unsaved.Count > 0)
            {
                result["unsaved"] = unsaved;
                result["hint"] = "Fichiers modifies dans l'editeur sans etre enregistres : save_documents les ecrit sur disque.";
            }

            return result;
        }

        /// <summary>
        /// Un fichier lie a plusieurs projets (multi-cible) doit avoir le meme texte partout : un
        /// correctif ne modifie que le document sur lequel il a ete calcule.
        /// </summary>
        private static async Task<Solution> AlignLinkedDocumentsAsync(Solution oldSolution, Solution newSolution, CancellationToken ct)
        {
            var changedIds = newSolution.GetChanges(oldSolution).GetProjectChanges()
                .SelectMany(p => p.GetChangedDocuments())
                .ToList();
            var changed = new HashSet<DocumentId>(changedIds);

            foreach (var id in changedIds)
            {
                var document = newSolution.GetDocument(id);
                if (document == null) continue;

                var text = await document.GetTextAsync(ct).ConfigureAwait(false);
                foreach (var linkedId in document.GetLinkedDocumentIds())
                {
                    if (changed.Contains(linkedId)) continue;
                    newSolution = newSolution.WithDocumentText(linkedId, text);
                }
            }
            return newSolution;
        }

        private static async Task<JObject> SummarizeAsync(Document oldDocument, Document newDocument, int budget, CancellationToken ct)
        {
            var oldText = await oldDocument.GetTextAsync(ct).ConfigureAwait(false);
            var newText = await newDocument.GetTextAsync(ct).ConfigureAwait(false);
            var changes = (await newDocument.GetTextChangesAsync(oldDocument, ct).ConfigureAwait(false))
                .OrderBy(c => c.Span.Start)
                .ToList();

            var edits = new JArray();
            var seenLines = new HashSet<int>();
            var delta = 0;

            foreach (var change in changes)
            {
                var oldLine = oldText.Lines.GetLineFromPosition(change.Span.Start);
                var newPosition = Math.Min(change.Span.Start + delta, Math.Max(0, newText.Length - 1));
                delta += (change.NewText ?? string.Empty).Length - change.Span.Length;

                // Plusieurs changements sur une meme ligne (deux occurrences renommees) : une seule entree.
                if (edits.Count >= budget || !seenLines.Add(oldLine.LineNumber)) continue;

                var newLine = newText.Length == 0 ? oldLine : newText.Lines.GetLineFromPosition(newPosition);
                edits.Add(new JObject
                {
                    ["line"] = oldLine.LineNumber + 1,
                    ["before"] = RoslynNavigator.Truncate(oldLine.ToString().Trim()),
                    ["after"] = RoslynNavigator.Truncate(newText.Length == 0 ? string.Empty : newLine.ToString().Trim())
                });
            }

            return new JObject
            {
                ["file"] = newDocument.FilePath,
                ["changes"] = changes.Count,
                ["edits"] = edits,
                ["editsTruncated"] = seenLines.Count > edits.Count
            };
        }

        /// <summary>Erreurs du compilateur presentes dans newDocument et absentes de oldDocument.</summary>
        private static async Task<List<JObject>> NewErrorsAsync(Document oldDocument, Document newDocument, CancellationToken ct)
        {
            var before = await ErrorsAsync(oldDocument, ct).ConfigureAwait(false);
            var after = await ErrorsAsync(newDocument, ct).ConfigureAwait(false);

            // Comparaison par code + message, en multiensemble : une erreur deja presente ne compte
            // pas, meme si les lignes ont bouge.
            var remaining = before
                .GroupBy(d => d.Id + "|" + d.GetMessage())
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var added = new List<JObject>();
            foreach (var diagnostic in after)
            {
                var key = diagnostic.Id + "|" + diagnostic.GetMessage();
                if (remaining.TryGetValue(key, out var count) && count > 0)
                {
                    remaining[key] = count - 1;
                    continue;
                }

                var span = diagnostic.Location.GetLineSpan();
                added.Add(new JObject
                {
                    ["code"] = diagnostic.Id,
                    ["message"] = diagnostic.GetMessage(),
                    ["file"] = span.Path,
                    ["line"] = span.StartLinePosition.Line + 1
                });
            }
            return added;
        }

        private static async Task<IEnumerable<Diagnostic>> ErrorsAsync(Document document, CancellationToken ct)
        {
            var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (model == null) return Enumerable.Empty<Diagnostic>();
            return model.GetDiagnostics(null, ct).Where(d => d.Severity == DiagnosticSeverity.Error && !d.IsSuppressed);
        }

        // ---------------------------------------------------------------- Correctifs

        /// <summary>
        /// Fournisseurs de correctifs pour le langage du projet : ceux livres avec Visual Studio
        /// (exports MEF : ajouter un using, implementer l'interface, IDE*...) et ceux des paquets
        /// d'analyseurs references par le projet.
        /// </summary>
        internal static IReadOnlyList<CodeFixProvider> Providers(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var language = project.Language;
            var providers = new List<CodeFixProvider>(MefProviders.GetOrAdd(language, LoadMefProviders));

            foreach (var reference in project.AnalyzerReferences.OfType<AnalyzerFileReference>())
            {
                var key = reference.FullPath + "|" + language;
                providers.AddRange(PackageProviders.GetOrAdd(key, _ => LoadPackageProviders(reference, language)));
            }
            return providers;
        }

        private static IReadOnlyList<CodeFixProvider> LoadMefProviders(string language)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var list = new List<CodeFixProvider>();
            var componentModel = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
            if (componentModel == null) return list;

            foreach (var export in componentModel.DefaultExportProvider.GetExports<CodeFixProvider, IDictionary<string, object>>())
            {
                if (!SupportsLanguage(export.Metadata, language)) continue;
                try
                {
                    list.Add(export.Value);
                }
                catch (Exception ex)
                {
                    ExtensionLog.Warn("Correctif MEF non instanciable : " + ex.Message);
                }
            }
            return list;
        }

        private static bool SupportsLanguage(IDictionary<string, object> metadata, string language)
        {
            if (metadata == null || !metadata.TryGetValue("Languages", out var value) || value == null) return true;
            if (value is string single) return single == language;
            if (value is IEnumerable<string> many) return many.Contains(language);
            return true;
        }

        /// <summary>
        /// Correctifs d'un paquet d'analyseurs : types derives de CodeFixProvider marques
        /// [ExportCodeFixProvider] pour ce langage, comme les decouvre Visual Studio.
        /// </summary>
        private static IReadOnlyList<CodeFixProvider> LoadPackageProviders(AnalyzerFileReference reference, string language)
        {
            var list = new List<CodeFixProvider>();
            Type[] types;
            try
            {
                types = reference.GetAssembly().GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }
            catch (Exception)
            {
                return list;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || !typeof(CodeFixProvider).IsAssignableFrom(type)) continue;

                var attribute = type.GetCustomAttributes(typeof(ExportCodeFixProviderAttribute), false)
                    .OfType<ExportCodeFixProviderAttribute>()
                    .FirstOrDefault();
                if (attribute == null || !attribute.Languages.Contains(language)) continue;

                try
                {
                    list.Add((CodeFixProvider)Activator.CreateInstance(type));
                }
                catch (Exception)
                {
                    // Pas de constructeur sans parametre : ce correctif n'est pas utilisable hors de son hote.
                }
            }
            return list;
        }

        /// <summary>Diagnostics de la ligne et correctifs proposes pour chacun.</summary>
        internal static async Task<FixSearch> FindFixesAsync(
            Document document, int line, string codePrefix, IReadOnlyList<CodeFixProvider> providers, CancellationToken ct)
        {
            var text = await document.GetTextAsync(ct).ConfigureAwait(false);
            if (line < 1 || line > text.Lines.Count)
            {
                throw McpToolException.InvalidArgs(
                    "Ligne " + line + " hors du fichier (" + text.Lines.Count + " lignes dans le tampon de l'editeur).");
            }

            var span = text.Lines[line - 1].Span;
            var search = new FixSearch();

            foreach (var diagnostic in await DiagnosticsAsync(document, span, ct).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(codePrefix) &&
                    !diagnostic.Id.StartsWith(codePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                search.Diagnostics.Add(diagnostic);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var diagnostic in search.Diagnostics)
            {
                foreach (var provider in providers)
                {
                    if (!SafeFixableIds(provider).Contains(diagnostic.Id)) continue;

                    var actions = new List<CodeAction>();
                    var context = new CodeFixContext(document, diagnostic, (action, _) =>
                    {
                        lock (actions) actions.Add(action);
                    }, ct);

                    try
                    {
                        await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        ExtensionLog.Warn("Correctif " + provider.GetType().FullName + " en echec : " + ex.Message);
                        continue;
                    }

                    foreach (var action in actions)
                    {
                        foreach (var leaf in Flatten(action, null))
                        {
                            if (!seen.Add(diagnostic.Id + "|" + leaf.Value)) continue;
                            search.Fixes.Add(new FixCandidate
                            {
                                Action = leaf.Key,
                                Title = leaf.Value,
                                Diagnostic = diagnostic,
                                Provider = provider.GetType().Name
                            });
                        }
                    }
                }
            }

            return search;
        }

        /// <summary>
        /// Compilateur, puis analyseurs du projet et de l'hote (Visual Studio y place ses analyseurs
        /// IDE*). Un analyseur qui plante est rapporte par Roslyn (AD0001), pas propage.
        /// </summary>
        private static async Task<List<Diagnostic>> DiagnosticsAsync(Document document, TextSpan span, CancellationToken ct)
        {
            var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            var tree = model.SyntaxTree;
            var diagnostics = new List<Diagnostic>(model.GetDiagnostics(span, ct));

            var analyzers = document.Project.AnalyzerReferences
                .Concat(document.Project.Solution.AnalyzerReferences)
                .SelectMany(r => SafeAnalyzers(r, document.Project.Language))
                .GroupBy(a => a.GetType())
                .Select(g => g.First())
                .ToImmutableArray();

            if (analyzers.Length > 0)
            {
                try
                {
                    var compilation = await document.Project.GetCompilationAsync(ct).ConfigureAwait(false);
                    var withAnalyzers = compilation.WithAnalyzers(analyzers, document.Project.AnalyzerOptions, ct);
                    // Pas de surcharge par plage en 4.0.1 pour la syntaxe : arbre entier, filtre ci-dessous.
                    diagnostics.AddRange(await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, ct).ConfigureAwait(false));
                    diagnostics.AddRange(await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, span, ct).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ExtensionLog.Warn("Analyseurs non executes, diagnostics du compilateur seuls : " + ex.Message);
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            return diagnostics
                .Where(d => !d.IsSuppressed && d.Id != "AD0001" &&
                            d.Location.IsInSource && d.Location.SourceTree == tree &&
                            d.Location.SourceSpan.IntersectsWith(span))
                .Where(d => seen.Add(d.Id + "|" + d.Location.SourceSpan))
                .OrderBy(d => d.Severity == DiagnosticSeverity.Error ? 0 : d.Severity == DiagnosticSeverity.Warning ? 1 : 2)
                .ThenBy(d => d.Location.SourceSpan.Start)
                .ToList();
        }

        internal static async Task<Solution> ChangedSolutionAsync(CodeAction action, CancellationToken ct)
        {
            var operations = await action.GetOperationsAsync(ct).ConfigureAwait(false);
            var apply = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
            if (apply == null)
            {
                throw new McpToolException("UNSUPPORTED",
                    "Ce correctif ne produit pas de modification applicable automatiquement.");
            }
            return apply.ChangedSolution;
        }

        internal static JObject DescribeDiagnostic(Diagnostic diagnostic)
        {
            var span = diagnostic.Location.GetLineSpan();
            return new JObject
            {
                ["code"] = diagnostic.Id,
                ["severity"] = diagnostic.Severity.ToString().ToLowerInvariant(),
                ["message"] = diagnostic.GetMessage(),
                ["column"] = span.StartLinePosition.Character + 1
            };
        }

        /// <summary>
        /// Actions finales d'une action, titres composes "Parent > Enfant". NestedActions n'est
        /// public qu'a partir de Roslyn 4.10 ; avant, c'est la propriete interne NestedCodeActions.
        /// </summary>
        private static IEnumerable<KeyValuePair<CodeAction, string>> Flatten(CodeAction action, string prefix)
        {
            var title = string.IsNullOrEmpty(prefix) ? action.Title : prefix + " > " + action.Title;
            var nested = NestedActions(action);

            if (nested.IsDefaultOrEmpty)
            {
                yield return new KeyValuePair<CodeAction, string>(action, title);
                yield break;
            }

            foreach (var child in nested)
            {
                foreach (var leaf in Flatten(child, title)) yield return leaf;
            }
        }

        private static ImmutableArray<CodeAction> NestedActions(CodeAction action)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var type = action.GetType();
            var property = type.GetProperty("NestedActions", flags) ?? type.GetProperty("NestedCodeActions", flags);

            try
            {
                return property?.GetValue(action) is ImmutableArray<CodeAction> nested ? nested : default;
            }
            catch (Exception)
            {
                return default;
            }
        }

        private static ImmutableArray<string> SafeFixableIds(CodeFixProvider provider)
        {
            try
            {
                var ids = provider.FixableDiagnosticIds;
                return ids.IsDefault ? ImmutableArray<string>.Empty : ids;
            }
            catch (Exception)
            {
                return ImmutableArray<string>.Empty;
            }
        }

        private static IEnumerable<DiagnosticAnalyzer> SafeAnalyzers(AnalyzerReference reference, string language)
        {
            try
            {
                return reference.GetAnalyzers(language);
            }
            catch (Exception)
            {
                return Enumerable.Empty<DiagnosticAnalyzer>();
            }
        }
    }
}
