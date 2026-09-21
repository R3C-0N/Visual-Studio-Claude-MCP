using System;
using System.Collections.Generic;
using ClaudeCodeVsMcp.Server;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Vs
{
    internal static class SolutionHelper
    {
        private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

        /// <summary>
        /// Enumere les vrais projets de la solution.
        /// Les dossiers de solution ne sont pas des projets : il faut descendre dedans via
        /// ProjectItems[i].SubProject, sinon les projets imbriques sont invisibles.
        /// </summary>
        internal static IEnumerable<Project> AllProjects(Solution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (Project project in solution.Projects)
            {
                foreach (var found in Flatten(project))
                {
                    yield return found;
                }
            }
        }

        private static IEnumerable<Project> Flatten(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (project == null) yield break;

            string kind;
            try { kind = project.Kind; }
            catch (Exception) { yield break; }

            if (!string.Equals(kind, SolutionFolderKind, StringComparison.OrdinalIgnoreCase))
            {
                yield return project;
                yield break;
            }

            ProjectItems items;
            try { items = project.ProjectItems; }
            catch (Exception) { yield break; }
            if (items == null) yield break;

            for (var i = 1; i <= items.Count; i++)
            {
                Project sub = null;
                try { sub = items.Item(i)?.SubProject; }
                catch (Exception) { /* element non projet */ }

                if (sub == null) continue;

                foreach (var found in Flatten(sub))
                {
                    yield return found;
                }
            }
        }

        internal static Project FindProject(Solution solution, string nameOrUniqueName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Project partial = null;
            var partialCount = 0;

            foreach (var project in AllProjects(solution))
            {
                var name = Safe(() => project.Name);
                var unique = Safe(() => project.UniqueName);

                if (string.Equals(unique, nameOrUniqueName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, nameOrUniqueName, StringComparison.OrdinalIgnoreCase))
                {
                    return project;
                }

                if (name.IndexOf(nameOrUniqueName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    partial = project;
                    partialCount++;
                }
            }

            if (partialCount == 1) return partial;

            throw McpToolException.NotFound(partialCount > 1
                ? "Plusieurs projets correspondent a '" + nameOrUniqueName + "'. Utiliser le uniqueName exact."
                : "Aucun projet nomme '" + nameOrUniqueName + "' dans la solution.");
        }

        internal static JObject Describe(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return new JObject
            {
                ["name"] = Safe(() => project.Name),
                ["uniqueName"] = Safe(() => project.UniqueName),
                // FullName leve pour certains types de projets : d'ou l'acces protege.
                ["fullName"] = Safe(() => project.FullName),
                ["kind"] = Safe(() => project.Kind)
            };
        }

        internal static string Safe(Func<string> accessor)
        {
            try { return accessor() ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        /// <summary>Configuration active, sous la forme "Debug|Any CPU".</summary>
        internal static string ActiveConfiguration(Solution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var configuration = solution.SolutionBuild?.ActiveConfiguration;
                if (configuration == null) return string.Empty;

                var platform = string.Empty;
                var typed = configuration as EnvDTE80.SolutionConfiguration2;
                if (typed != null) platform = typed.PlatformName ?? string.Empty;

                return string.IsNullOrEmpty(platform) ? configuration.Name : configuration.Name + "|" + platform;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
