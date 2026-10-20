using System;
using System.Collections.Generic;
using System.IO;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Vs
{
    /// <summary>
    /// Documents ouverts dans l'IDE. Un document modifie et non enregistre diverge du disque :
    /// un fichier que Claude reecrit en parallele produit alors un conflit, ou une boite
    /// "fichier modifie a l'exterieur" qui fige l'automatisation.
    /// </summary>
    internal static class DocumentHelper
    {
        internal static List<Document> DirtyDocuments()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dirty = new List<Document>();
            foreach (Document document in DteProvider.Dte.Documents)
            {
                if (!SafeBool(() => document.Saved)) dirty.Add(document);
            }
            return dirty;
        }

        /// <summary>Document ouvert sur ce chemin, ou null.</summary>
        internal static Document FindOpen(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (Document document in DteProvider.Dte.Documents)
            {
                if (SamePath(Safe(() => document.FullName), path)) return document;
            }
            return null;
        }

        /// <summary>Chemins (normalises) des documents modifies non enregistres.</summary>
        internal static HashSet<string> DirtyPaths()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var document in DirtyDocuments())
            {
                var path = Normalize(Safe(() => document.FullName));
                if (path.Length > 0) paths.Add(path);
            }
            return paths;
        }

        /// <summary>
        /// Ouvre le fichier, active sa fenetre et place le curseur. RunToCursor et
        /// SetNextStatement agissent sur ce curseur : c'est leur seul moyen de recevoir une ligne.
        /// </summary>
        internal static void OpenAt(string path, int line, int column)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!File.Exists(path))
            {
                throw ClaudeCodeVsMcp.Server.McpToolException.NotFound("Fichier introuvable : " + path);
            }

            // Activer la fenetre amene Visual Studio au premier plan : ce que l'utilisateur tapait
            // ailleurs a ce moment atterrirait dans l'editeur. On rend donc le premier plan a la
            // fenetre qui l'avait, si elle n'appartient pas a Visual Studio.
            var previous = ForegroundWindow.Capture();

            try
            {
                var window = DteProvider.Dte.ItemOperations.OpenFile(path, Constants.vsViewKindTextView);
                window?.Activate();

                var selection = window?.Document?.Selection as TextSelection;
                if (selection == null)
                {
                    throw new ClaudeCodeVsMcp.Server.McpToolException("UNSUPPORTED",
                        "Le fichier ne s'ouvre pas dans un editeur de texte : " + path);
                }

                selection.MoveToLineAndOffset(Math.Max(1, line), Math.Max(1, column));

                // Colonne 1 = indentation : RunToCursor et SetNextStatement n'y voient "aucun code".
                if (column <= 1) selection.StartOfLine(vsStartOfLineOptions.vsStartOfLineOptionsFirstText);
            }
            finally
            {
                ForegroundWindow.Restore(previous);
            }
        }

        internal static JObject Describe(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return new JObject
            {
                ["path"] = Safe(() => document.FullName),
                ["project"] = Safe(() => document.ProjectItem?.ContainingProject?.Name),
                ["saved"] = SafeBool(() => document.Saved),
                ["readOnly"] = SafeBool(() => document.ReadOnly)
            };
        }

        internal static bool SamePath(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }

        internal static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try { return Path.GetFullPath(path); }
            catch (Exception) { return path; }
        }

        private static string Safe(Func<string> accessor)
        {
            try { return accessor() ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        private static bool SafeBool(Func<bool> accessor)
        {
            try { return accessor(); }
            catch (Exception) { return false; }
        }
    }
}
