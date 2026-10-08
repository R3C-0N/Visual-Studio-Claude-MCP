using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
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

        /// <summary>"table" (Liste d'erreurs moderne, avec codes) ou "dte" (repli, sans codes).</summary>
        internal string Source { get; set; }
    }

    /// <summary>
    /// Lecture de la fenetre Liste d'erreurs.
    ///
    /// On lit la liste structuree plutot que le texte du pane de sortie : on obtient
    /// fichier/ligne/colonne/projet exploitables directement, sans analyser du texte
    /// dont le format depend de la verbosite MSBuild et de la langue de l'IDE.
    ///
    /// Source principale : la table de IErrorList (SVsErrorList), seule a exposer le code
    /// d'erreur (CS0103...). ForceUpdateAsync en donne toutes les entrees, avant les filtres
    /// d'affichage de l'utilisateur, qu'on n'a donc plus a toucher. Repli : EnvDTE.ErrorItem,
    /// qui n'a pas de code, si le service est absent ou echoue.
    /// </summary>
    internal static class ErrorListReader
    {
        /// <summary>Entree brute, commune aux deux sources, avant filtrage.</summary>
        private sealed class Entry
        {
            internal string Level;
            internal string Description;
            internal string File;
            internal int Line;
            internal int Column;
            internal string Project;
            internal string Code;
            internal string Tool;
            internal string HelpLink;
        }

        internal static async Task<ErrorListResult> ReadAsync(
            string severity, int maxItems, string projectContains, string fileContains, string codePrefix = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var source = "table";
            List<Entry> entries;
            try
            {
                entries = await ReadTableAsync();
            }
            catch (Exception ex)
            {
                ExtensionLog.Warn("Liste d'erreurs : lecture de la table impossible, repli sur EnvDTE. " + ex.Message);
                entries = null;
            }

            if (entries == null)
            {
                source = "dte";
                entries = ReadDte();
            }

            var result = new ErrorListResult { Items = new JArray(), Source = source };

            foreach (var entry in entries)
            {
                switch (entry.Level)
                {
                    case "error": result.Errors++; break;
                    case "warning": result.Warnings++; break;
                    default: result.Messages++; break;
                }

                if (!MatchesSeverity(entry.Level, severity)) continue;
                if (!Contains(entry.Project, projectContains)) continue;
                if (!Contains(entry.File, fileContains)) continue;
                if (!StartsWith(entry.Code, codePrefix)) continue;

                result.Total++;

                if (result.Items.Count >= maxItems)
                {
                    result.Truncated = true;
                    continue;
                }

                var item = new JObject
                {
                    ["severity"] = entry.Level,
                    ["code"] = string.IsNullOrEmpty(entry.Code) ? null : entry.Code,
                    ["description"] = entry.Description,
                    ["file"] = entry.File,
                    ["line"] = entry.Line,
                    ["column"] = entry.Column,
                    ["project"] = entry.Project
                };
                if (!string.IsNullOrEmpty(entry.Tool)) item["tool"] = entry.Tool;
                if (!string.IsNullOrEmpty(entry.HelpLink)) item["helpLink"] = entry.HelpLink;

                result.Items.Add(item);
            }

            return result;
        }

        /// <summary>Entrees de la table, ou null si le service n'est pas disponible.</summary>
        private static async Task<List<Entry>> ReadTableAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var errorList = ServiceProvider.GlobalProvider.GetService(typeof(SVsErrorList)) as IErrorList;
            var table = errorList?.TableControl;
            if (table == null) return null;

            var update = await table.ForceUpdateAsync();
            var handles = (IEnumerable<ITableEntryHandle>)update?.AllEntries ?? table.Entries;

            var entries = new List<Entry>();
            foreach (var handle in handles)
            {
                entries.Add(new Entry
                {
                    Level = TableLevel(handle),
                    Description = TableString(handle, StandardTableKeyNames.Text),
                    File = TableString(handle, StandardTableKeyNames.DocumentName),
                    // Line et Column sont 0-based dans la table ; EnvDTE et l'editeur sont 1-based.
                    Line = TableInt(handle, StandardTableKeyNames.Line) + 1,
                    Column = TableInt(handle, StandardTableKeyNames.Column) + 1,
                    Project = TableString(handle, StandardTableKeyNames.ProjectName),
                    Code = TableString(handle, StandardTableKeyNames.ErrorCode),
                    Tool = TableString(handle, StandardTableKeyNames.BuildTool),
                    HelpLink = TableString(handle, StandardTableKeyNames.HelpLink)
                });
            }
            return entries;
        }

        private static List<Entry> ReadDte()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var errorList = DteProvider.Dte.ToolWindows.ErrorList;

            // Les filtres d'affichage conditionnent ce que la collection expose : on les force
            // le temps de la lecture, puis on restaure l'etat choisi par l'utilisateur.
            var previousErrors = errorList.ShowErrors;
            var previousWarnings = errorList.ShowWarnings;
            var previousMessages = errorList.ShowMessages;

            var entries = new List<Entry>();

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

                    entries.Add(new Entry
                    {
                        Level = DteLevel(item),
                        Description = SafeString(() => item.Description),
                        File = SafeString(() => item.FileName),
                        Line = SafeInt(() => item.Line),
                        Column = SafeInt(() => item.Column),
                        Project = SafeString(() => item.Project)
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

            return entries;
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

        private static bool StartsWith(string value, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return true;
            return value != null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string TableLevel(ITableEntryHandle handle)
        {
            if (!handle.TryGetValue(StandardTableKeyNames.ErrorSeverity, out var value) || value == null)
                return "message";

            try
            {
                switch ((__VSERRORCATEGORY)Convert.ToInt32(value))
                {
                    case __VSERRORCATEGORY.EC_ERROR: return "error";
                    case __VSERRORCATEGORY.EC_WARNING: return "warning";
                    default: return "message";
                }
            }
            catch (Exception)
            {
                return "message";
            }
        }

        private static string TableString(ITableEntryHandle handle, string key)
        {
            try
            {
                return handle.TryGetValue(key, out var value) && value != null ? value.ToString() : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static int TableInt(ITableEntryHandle handle, string key)
        {
            try
            {
                return handle.TryGetValue(key, out var value) && value != null ? Convert.ToInt32(value) : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static string DteLevel(ErrorItem item)
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
