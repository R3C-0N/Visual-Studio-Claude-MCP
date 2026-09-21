using System;
using System.Collections.Generic;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Vs
{
    internal sealed class ErrorListResult
    {
        internal int Total { get; set; }
        internal int Errors { get; set; }
        internal int Warnings { get; set; }
        internal int Messages { get; set; }
        internal JArray Items { get; set; }
        internal bool Truncated { get; set; }
    }

    /// <summary>
    /// Lecture de la fenetre Liste d'erreurs.
    ///
    /// On lit la liste structuree plutot que le texte du pane de sortie : on obtient
    /// fichier/ligne/colonne/projet exploitables directement, sans analyser du texte
    /// dont le format depend de la verbosite MSBuild et de la langue de l'IDE.
    ///
    /// Limite connue : EnvDTE.ErrorItem n'expose PAS le code d'erreur (CS0103, etc.).
    /// Le champ 'code' est donc null. L'obtenir demanderait de passer par
    /// SVsErrorList / IVsTaskList2.EnumTaskItems / IVsTaskItem3.GetColumnValue.
    /// </summary>
    internal static class ErrorListReader
    {
        internal static ErrorListResult Read(string severity, int maxItems, string projectContains, string fileContains)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var errorList = DteProvider.Dte.ToolWindows.ErrorList;

            // Les filtres d'affichage conditionnent ce que la collection expose : on les force
            // le temps de la lecture, puis on restaure l'etat choisi par l'utilisateur.
            var previousErrors = errorList.ShowErrors;
            var previousWarnings = errorList.ShowWarnings;
            var previousMessages = errorList.ShowMessages;

            var result = new ErrorListResult { Items = new JArray() };

            try
            {
                errorList.ShowErrors = true;
                errorList.ShowWarnings = true;
                errorList.ShowMessages = true;

                var items = errorList.ErrorItems;
                var count = items.Count;

                for (var i = 1; i <= count; i++)
                {
                    ErrorItem item;
                    try { item = items.Item(i); }
                    catch (Exception) { continue; }

                    var level = SafeLevel(item);
                    switch (level)
                    {
                        case "error": result.Errors++; break;
                        case "warning": result.Warnings++; break;
                        default: result.Messages++; break;
                    }

                    if (!MatchesSeverity(level, severity)) continue;

                    var project = SafeString(() => item.Project);
                    var file = SafeString(() => item.FileName);

                    if (!Contains(project, projectContains)) continue;
                    if (!Contains(file, fileContains)) continue;

                    result.Total++;

                    if (result.Items.Count >= maxItems)
                    {
                        result.Truncated = true;
                        continue;
                    }

                    result.Items.Add(new JObject
                    {
                        ["severity"] = level,
                        ["description"] = SafeString(() => item.Description),
                        ["file"] = file,
                        ["line"] = SafeInt(() => item.Line),
                        ["column"] = SafeInt(() => item.Column),
                        ["project"] = project,
                        // Non expose par EnvDTE.ErrorItem : voir le commentaire de classe.
                        ["code"] = null
                    });
                }
            }
            finally
            {
                try
                {
                    errorList.ShowErrors = previousErrors;
                    errorList.ShowWarnings = previousWarnings;
                    errorList.ShowMessages = previousMessages;
                }
                catch (Exception)
                {
                    // Restauration cosmetique : ne doit jamais masquer un resultat valide.
                }
            }

            return result;
        }

        private static bool MatchesSeverity(string level, string requested)
        {
            if (string.IsNullOrEmpty(requested) || requested.Equals("all", StringComparison.OrdinalIgnoreCase))
                return true;
            return level.Equals(requested, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Contains(string value, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return true;
            return value != null && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SafeLevel(ErrorItem item)
        {
            try
            {
                switch (item.ErrorLevel)
                {
                    case vsBuildErrorLevel.vsBuildErrorLevelHigh: return "error";
                    case vsBuildErrorLevel.vsBuildErrorLevelMedium: return "warning";
                    default: return "message";
                }
            }
            catch (Exception)
            {
                return "message";
            }
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
