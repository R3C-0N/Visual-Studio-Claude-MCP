using System;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVsMcp.Infrastructure
{
    /// <summary>
    /// Pane de sortie "Claude MCP" pour le diagnostic de l'extension.
    /// Toutes les ecritures passent par OutputStringThreadSafe : appelable depuis n'importe quel thread.
    /// </summary>
    internal static class ExtensionLog
    {
        private static readonly Guid PaneGuid = new Guid("23677659-B917-4169-8644-FEB1608590A4");
        private static IVsOutputWindowPane _pane;
        private static readonly object Gate = new object();

        /// <summary>
        /// Le pane de sortie n'est lisible que depuis l'IDE. Le meme journal est ecrit dans un
        /// fichier pour pouvoir diagnostiquer de l'exterieur, notamment quand le package se
        /// charge mal ou qu'une commande ne repond pas.
        /// </summary>
        internal static string FilePath
        {
            get
            {
                return Path.Combine(Path.GetTempPath(), "claude-vs-mcp",
                    "extension-" + Process.GetCurrentProcess().Id + ".log");
            }
        }

        /// <summary>Doit etre appele depuis le thread UI, une seule fois, au chargement du package.</summary>
        internal static void Initialize(IVsOutputWindow outputWindow)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (outputWindow == null) return;

            var guid = PaneGuid;
            outputWindow.CreatePane(ref guid, "Claude MCP", fInitVisible: 1, fClearWithSolution: 0);
            outputWindow.GetPane(ref guid, out var pane);
            lock (Gate) { _pane = pane; }
        }

        internal static void Info(string message) => Write("INFO ", message);
        internal static void Warn(string message) => Write("WARN ", message);

        internal static void Error(string message, Exception ex = null)
        {
            Write("ERROR", ex == null ? message : message + " :: " + ex);
        }

        private static void WriteToFile(string line)
        {
            try
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, line);
            }
            catch (Exception)
            {
                // Le journal ne doit jamais faire tomber une operation metier.
            }
        }

        private static void Write(string level, string message)
        {
            var line = string.Format("[{0:HH:mm:ss}] {1} {2}{3}",
                DateTime.Now, level, message, Environment.NewLine);

            Debug.Write("ClaudeCodeVsMcp " + line);
            WriteToFile(line);

            IVsOutputWindowPane pane;
            lock (Gate) { pane = _pane; }
            if (pane == null) return;

            try
            {
                pane.OutputStringThreadSafe(line);
            }
            catch (Exception)
            {
                // Le diagnostic ne doit jamais faire tomber une operation metier.
            }
        }
    }
}
