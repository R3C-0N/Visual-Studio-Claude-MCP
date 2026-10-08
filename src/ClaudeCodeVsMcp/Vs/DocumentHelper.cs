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
