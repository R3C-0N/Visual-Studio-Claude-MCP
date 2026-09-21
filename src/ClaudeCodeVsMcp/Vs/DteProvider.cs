using System;
using EnvDTE;
using EnvDTE80;
using ClaudeCodeVsMcp.Server;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVsMcp.Vs
{
    /// <summary>
    /// Point d'acces unique a l'automation Visual Studio.
    /// Toutes les proprietes exigent le thread UI : les appelants passent par UiThread.RunAsync.
    /// </summary>
    internal static class DteProvider
    {
        private static DTE2 _dte;

        internal static void Initialize(DTE2 dte)
        {
            _dte = dte;
        }

        internal static DTE2 Dte
        {
            get
            {
                if (_dte == null)
                {
                    throw new InvalidOperationException(
                        "L'automation Visual Studio n'est pas initialisee (le package n'a pas fini de charger).");
                }
                return _dte;
            }
        }

        /// <summary>Renvoie la solution ouverte, ou leve une erreur d'outil exploitable par le modele.</summary>
        internal static Solution RequireSolution()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var solution = Dte.Solution;
            if (solution == null || !solution.IsOpen)
            {
                throw McpToolException.NoSolution();
            }
            return solution;
        }

        internal static bool IsSolutionOpen
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var solution = _dte?.Solution;
                return solution != null && solution.IsOpen;
            }
        }

        /// <summary>
        /// Chemin de la solution ouverte, ou null. Solution.FullName leve pour certains etats
        /// transitoires, d'ou le try/catch.
        /// </summary>
        internal static string SolutionPath
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    var solution = _dte?.Solution;
                    if (solution == null || !solution.IsOpen) return null;
                    var path = solution.FullName;
                    return string.IsNullOrEmpty(path) ? null : path;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        internal static string DebugModeName(dbgDebugMode mode)
        {
            switch (mode)
            {
                case dbgDebugMode.dbgBreakMode: return "Break";
                case dbgDebugMode.dbgRunMode: return "Run";
                case dbgDebugMode.dbgDesignMode: return "Design";
                default: return mode.ToString();
            }
        }

        /// <summary>Exige que le debogueur soit arrete : evaluate, get_locals et get_stack en dependent.</summary>
        internal static Debugger RequireBreakMode()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var debugger = Dte.Debugger;
            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                throw McpToolException.NotInBreakMode(
                    debugger == null ? "indisponible" : DebugModeName(debugger.CurrentMode));
            }
            return debugger;
        }
    }
}
