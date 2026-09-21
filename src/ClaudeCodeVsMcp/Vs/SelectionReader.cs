using System;
using ClaudeCodeVsMcp.Server;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Vs
{
    /// <summary>Selection capturee a un instant donne, dans l'editeur ou dans la fenetre Sortie.</summary>
    internal sealed class SelectionSnapshot
    {
        /// <summary>"editor" ou "output".</summary>
        internal string Source { get; set; }

        /// <summary>Chemin du fichier pour l'editeur ; null pour un pane de sortie.</summary>
        internal string FilePath { get; set; }

        internal string PaneName { get; set; }
        internal string Text { get; set; }

        /// <summary>Lignes 1-based, convention EnvDTE.</summary>
        internal int StartLine { get; set; }
        internal int EndLine { get; set; }
        internal int StartColumn { get; set; }
        internal int EndColumn { get; set; }

        internal bool IsEmpty
        {
            get { return string.IsNullOrEmpty(Text); }
        }

        internal JObject ToJson()
        {
            return new JObject
            {
                ["source"] = Source,
                ["filePath"] = FilePath,
                ["pane"] = PaneName,
                ["startLine"] = StartLine,
                ["endLine"] = EndLine,
                ["startColumn"] = StartColumn,
                ["endColumn"] = EndColumn,
                ["isEmpty"] = IsEmpty,
                ["chars"] = Text?.Length ?? 0,
                ["text"] = Text
            };
        }
    }

    internal static class SelectionReader
    {
        /// <summary>
        /// Lit la selection courante.
        /// </summary>
        /// <param name="source">"editor", "output", ou "auto" pour deduire de la fenetre active.</param>
        internal static SelectionSnapshot Read(string source)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var wanted = (source ?? "auto").Trim().ToLowerInvariant();
            if (wanted == "auto") wanted = DetectActiveSurface();

            return wanted == "output" ? ReadOutput() : ReadEditor();
        }

        /// <summary>Deduit la surface visee de la fenetre active de l'IDE.</summary>
        private static string DetectActiveSurface()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var active = DteProvider.Dte.ActiveWindow;
                if (active != null &&
                    string.Equals(active.ObjectKind, Constants.vsWindowKindOutput, StringComparison.OrdinalIgnoreCase))
                {
                    return "output";
                }
            }
            catch (Exception)
            {
                // Fenetre active indisponible : l'editeur reste le choix le plus probable.
            }

            return "editor";
        }

        private static SelectionSnapshot ReadEditor()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var document = DteProvider.Dte.ActiveDocument;
            if (document == null)
            {
                throw McpToolException.NotFound(
                    "Aucun document actif dans l'editeur. Ouvrir un fichier, ou preciser source='output'.");
            }

            var selection = document.Selection as TextSelection;
            if (selection == null)
            {
                throw McpToolException.NotFound(
                    "Le document actif n'est pas un editeur de texte (concepteur, diagramme...).");
            }

            var text = selection.Text ?? string.Empty;

            // Selection vide : on remonte la ligne du curseur plutot que rien, c'est presque
            // toujours ce que l'appelant veut voir.
            var startLine = selection.TopLine;
            var endLine = selection.BottomLine;

            if (text.Length == 0)
            {
                try
                {
                    var point = selection.ActivePoint;
                    startLine = endLine = point.Line;
                    var line = selection.ActivePoint.CreateEditPoint();
                    text = line.GetLines(startLine, startLine + 1) ?? string.Empty;
                }
                catch (Exception)
                {
                    text = string.Empty;
                }
            }

            return new SelectionSnapshot
            {
                Source = "editor",
                FilePath = SafeFullName(document),
                Text = text,
                StartLine = startLine,
                EndLine = endLine,
                StartColumn = SafeColumn(() => selection.TopPoint.DisplayColumn),
                EndColumn = SafeColumn(() => selection.BottomPoint.DisplayColumn)
            };
        }

        private static SelectionSnapshot ReadOutput()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            OutputWindowPane pane;
            try
            {
                pane = DteProvider.Dte.ToolWindows.OutputWindow.ActivePane;
            }
            catch (Exception)
            {
                pane = null;
            }

            if (pane == null)
            {
                throw McpToolException.NotFound(
                    "Aucun pane actif dans la fenetre Sortie. L'ouvrir, ou utiliser get_output_pane.");
            }

            var selection = pane.TextDocument?.Selection;
            var text = selection?.Text ?? string.Empty;

            return new SelectionSnapshot
            {
                Source = "output",
                PaneName = OutputPaneReader.SafeName(pane),
                Text = text,
                StartLine = selection?.TopLine ?? 0,
                EndLine = selection?.BottomLine ?? 0,
                StartColumn = 1,
                EndColumn = 1
            };
        }

        private static string SafeFullName(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return document.FullName; }
            catch (Exception) { return null; }
        }

        private static int SafeColumn(Func<int> accessor)
        {
            try { return accessor(); }
            catch (Exception) { return 1; }
        }
    }
}
