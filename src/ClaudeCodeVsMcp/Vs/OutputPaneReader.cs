using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeCodeVsMcp.Server;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVsMcp.Vs
{
    /// <summary>
    /// Lecture des panes de la fenetre Sortie.
    ///
    /// IMPORTANT : les panes integres sont LOCALISES. Sur une installation francaise, le pane
    /// de build s'appelle "Generer" et non "Build". Toute resolution par nom casserait donc
    /// selon la langue de l'IDE : on resout par GUID, et le nom n'est utilise qu'en dernier
    /// recours pour les panes tiers (NuGet, tests, extensions...).
    /// </summary>
    internal static class OutputPaneReader
    {
        // GUID des panes integres. Ecrits en dur plutot que via VSConstants pour ne dependre
        // d'aucun nom de constante : ces valeurs sont stables depuis VS 2002.
        private static readonly Guid BuildPane = new Guid("1BD8A850-02D1-11d1-BEE7-00A0C913D1F8");
        private static readonly Guid DebugPane = new Guid("FC076020-078A-11D1-A7DF-00A0C9110051");
        private static readonly Guid GeneralPane = new Guid("3C24D581-5591-4884-A571-9FE89915CD64");

        internal static IEnumerable<OutputWindowPane> AllPanes()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var window = DteProvider.Dte.ToolWindows.OutputWindow;
            for (var i = 1; i <= window.OutputWindowPanes.Count; i++)
            {
                OutputWindowPane pane = null;
                try { pane = window.OutputWindowPanes.Item(i); }
                catch (Exception) { /* pane disparu entre-temps */ }

                if (pane != null) yield return pane;
            }
        }

        /// <summary>
        /// Resout un pane depuis un alias stable ("build", "debug", "general") ou un nom exact.
        /// </summary>
        internal static OutputWindowPane Resolve(string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var wanted = (name ?? "build").Trim();
            var panes = AllPanes().ToList();

            Guid? targetGuid = null;
            if (wanted.Equals("build", StringComparison.OrdinalIgnoreCase)) targetGuid = BuildPane;
            else if (wanted.Equals("debug", StringComparison.OrdinalIgnoreCase)) targetGuid = DebugPane;
            else if (wanted.Equals("general", StringComparison.OrdinalIgnoreCase)) targetGuid = GeneralPane;

            if (targetGuid.HasValue)
            {
                var byGuid = panes.FirstOrDefault(p => GuidOf(p) == targetGuid.Value);
                if (byGuid != null) return byGuid;

                throw McpToolException.NotFound(
                    "Le pane de sortie '" + wanted + "' n'existe pas encore. Il est cree par Visual Studio " +
                    "a la premiere utilisation (lancer un build ou un debogage d'abord).");
            }

            var byName = panes.FirstOrDefault(p => SafeName(p).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                         ?? panes.FirstOrDefault(p => SafeName(p).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0);

            if (byName == null)
            {
                throw McpToolException.NotFound(
                    "Aucun pane de sortie nomme '" + wanted + "'. Panes disponibles : " +
                    string.Join(", ", panes.Select(SafeName)));
            }

            return byName;
        }

        internal static string SafeName(OutputWindowPane pane)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return pane.Name ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        private static Guid GuidOf(OutputWindowPane pane)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                Guid parsed;
                return Guid.TryParse(pane.Guid, out parsed) ? parsed : Guid.Empty;
            }
            catch (Exception)
            {
                return Guid.Empty;
            }
        }

        /// <summary>
        /// Lit le contenu d'un pane. Couteux sur gros volume (tout le buffer traverse le pont COM),
        /// d'ou le plafonnement systematique par l'appelant.
        ///
        /// Limites connues, a documenter cote utilisateur : le buffer du pane est borne par VS,
        /// le detail du pane de build depend de la verbosite MSBuild configuree, et la sortie
        /// d'une application console lancee dans une console externe n'y apparait pas du tout.
        /// </summary>
        internal static string ReadText(OutputWindowPane pane, int maxChars, bool tail)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string text;
            try
            {
                // EditPoint plutot que Selection : lire via la selection deplacerait le curseur de
                // l'utilisateur dans le pane et laisserait tout le texte surligne.
                var document = pane.TextDocument;
                text = document.StartPoint.CreateEditPoint().GetText(document.EndPoint) ?? string.Empty;
            }
            catch (Exception ex)
            {
                throw new McpToolException("OUTPUT_READ_FAILED",
                    "Lecture du pane de sortie impossible : " + ex.Message);
            }

            if (maxChars <= 0 || text.Length <= maxChars) return text;

            return tail
                ? text.Substring(text.Length - maxChars)
                : text.Substring(0, maxChars);
        }
    }
}
