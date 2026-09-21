using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeCodeVsMcp.Infrastructure;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Vs
{
    /// <summary>
    /// Gere les points d'arret, y compris les deux variantes que Visual Studio propose dans son
    /// interface mais qu'EnvDTE n'expose pas :
    ///
    /// - TEMPORAIRE : supprime apres son premier declenchement.
    /// - DEPENDANT : reste desactive jusqu'a ce qu'un autre point d'arret soit atteint.
    ///
    /// Les deux sont emules ici, en s'appuyant sur Debugger.BreakpointLastHit a chaque passage
    /// en mode arret. Ce ne sont donc pas les fonctionnalites natives de l'IDE : elles ne
    /// survivent pas au rechargement de la solution, et ne s'affichent pas comme telles dans la
    /// fenetre Points d'arret. Le comportement observable, lui, est le meme.
    ///
    /// Les tracepoints, en revanche, sont natifs : Breakpoint2.Message plus BreakWhenHit a false.
    /// </summary>
    internal sealed class BreakpointManager
    {
        /// <summary>Identifiants des points d'arret a supprimer apres leur premier declenchement.</summary>
        private readonly ConcurrentDictionary<string, bool> _temporary =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Dependant -> prerequis.</summary>
        private readonly ConcurrentDictionary<string, string> _dependents =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Identifiant stable d'un point d'arret. EnvDTE n'en fournit aucun : on derive
        /// "fichier:ligne", qui est deterministe et directement utilisable par l'appelant
        /// pour exprimer une dependance.
        /// </summary>
        internal static string IdFor(string file, int line)
        {
            var normalized = file ?? string.Empty;
            try { normalized = Path.GetFullPath(normalized); }
            catch (Exception) { /* chemin exotique : on garde tel quel */ }
            return normalized + ":" + line;
        }

        internal static string IdOf(Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return IdFor(breakpoint.File, breakpoint.FileLine); }
            catch (Exception) { return null; }
        }

        internal void MarkTemporary(string id)
        {
            _temporary[id] = true;
        }

        internal void MarkDependent(string dependentId, string prerequisiteId)
        {
            _dependents[dependentId] = prerequisiteId;
        }

        internal void Forget(string id)
        {
            bool ignoredBool;
            string ignoredString;
            _temporary.TryRemove(id, out ignoredBool);
            _dependents.TryRemove(id, out ignoredString);
        }

        internal bool IsTemporary(string id)
        {
            return _temporary.ContainsKey(id);
        }

        internal string PrerequisiteOf(string id)
        {
            string prerequisite;
            return _dependents.TryGetValue(id, out prerequisite) ? prerequisite : null;
        }

        /// <summary>
        /// Appele a chaque passage en mode arret, sur le thread UI. Arme les dependants du point
        /// atteint, puis supprime celui-ci s'il etait temporaire.
        /// </summary>
        internal void OnBreak()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Breakpoint hit;
            try { hit = DteProvider.Dte.Debugger.BreakpointLastHit; }
            catch (Exception) { return; }

            // L'arret peut venir d'un pas a pas ou d'une exception : il n'y a alors aucun
            // point d'arret en cause.
            if (hit == null) return;

            var hitId = IdOf(hit);
            if (hitId == null) return;

            ArmDependentsOf(hitId);
            DeleteIfTemporary(hitId);
        }

        private void ArmDependentsOf(string prerequisiteId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var armed = _dependents
                .Where(pair => string.Equals(pair.Value, prerequisiteId, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToList();

            foreach (var dependentId in armed)
            {
                foreach (var breakpoint in Find(dependentId))
                {
                    try
                    {
                        breakpoint.Enabled = true;
                        ExtensionLog.Info("Point d'arret dependant arme : " + dependentId +
                                          " (prerequis " + prerequisiteId + " atteint).");
                    }
                    catch (Exception ex)
                    {
                        ExtensionLog.Warn("Activation du point d'arret dependant impossible : " + ex.Message);
                    }
                }

                // Une fois arme, il se comporte comme un point d'arret ordinaire.
                string ignored;
                _dependents.TryRemove(dependentId, out ignored);
            }
        }

        private void DeleteIfTemporary(string id)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!IsTemporary(id)) return;

            foreach (var breakpoint in Find(id))
            {
                try
                {
                    breakpoint.Delete();
                    ExtensionLog.Info("Point d'arret temporaire supprime apres declenchement : " + id + ".");
                }
                catch (Exception ex)
                {
                    ExtensionLog.Warn("Suppression du point d'arret temporaire impossible : " + ex.Message);
                }
            }

            bool ignored;
            _temporary.TryRemove(id, out ignored);
        }

        /// <summary>
        /// Remet les dependants en attente a la fin d'une session : c'est le comportement de
        /// Visual Studio, ou une dependance se rearme d'une execution a l'autre.
        /// </summary>
        internal void OnSessionEnded()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (var dependentId in _dependents.Keys.ToList())
            {
                foreach (var breakpoint in Find(dependentId))
                {
                    try { breakpoint.Enabled = false; }
                    catch (Exception) { /* la solution est peut-etre en cours de fermeture */ }
                }
            }
        }

        /// <summary>Retrouve les points d'arret correspondant a un identifiant (un couple fichier/ligne peut en lier plusieurs).</summary>
        internal static IEnumerable<Breakpoint> Find(string id)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var result = new List<Breakpoint>();
            Breakpoints collection;
            try { collection = DteProvider.Dte.Debugger.Breakpoints; }
            catch (Exception) { return result; }

            for (var i = 1; i <= collection.Count; i++)
            {
                Breakpoint breakpoint;
                try { breakpoint = collection.Item(i); }
                catch (Exception) { continue; }

                if (string.Equals(IdOf(breakpoint), id, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(breakpoint);
                }
            }

            return result;
        }

        internal JObject Describe(Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var id = IdOf(breakpoint);
            var result = new JObject
            {
                ["id"] = id,
                ["file"] = Safe(() => breakpoint.File),
                ["line"] = SafeInt(() => breakpoint.FileLine),
                ["column"] = SafeInt(() => breakpoint.FileColumn),
                ["enabled"] = SafeBool(() => breakpoint.Enabled),
                ["condition"] = Empty(Safe(() => breakpoint.Condition)),
                ["conditionType"] = SafeConditionType(breakpoint),
                ["functionName"] = Empty(Safe(() => breakpoint.FunctionName)),
                ["currentHits"] = SafeInt(() => breakpoint.CurrentHits),
                ["hitCountTarget"] = SafeInt(() => breakpoint.HitCountTarget),
                ["hitCountMode"] = SafeHitCountMode(breakpoint),
                ["bound"] = SafeBound(breakpoint)
            };

            var typed = breakpoint as Breakpoint2;
            if (typed != null)
            {
                var message = Safe(() => typed.Message);
                var breaks = SafeBool(() => typed.BreakWhenHit);

                result["message"] = Empty(message);
                result["breaksExecution"] = breaks;
                // Un tracepoint est un point d'arret qui journalise sans interrompre.
                result["kind"] = !string.IsNullOrEmpty(message) && !breaks ? "tracepoint" : "breakpoint";
                result["filter"] = Empty(Safe(() => typed.FilterBy));
            }

            if (id != null)
            {
                if (IsTemporary(id)) result["temporary"] = true;
                var prerequisite = PrerequisiteOf(id);
                if (prerequisite != null) result["dependsOn"] = prerequisite;
            }

            return result;
        }

        private static JToken Empty(string value)
        {
            return string.IsNullOrEmpty(value) ? JValue.CreateNull() : (JToken)value;
        }

        private static string SafeConditionType(Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (string.IsNullOrEmpty(breakpoint.Condition)) return null;
                return breakpoint.ConditionType == dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenChanged
                    ? "when_changed"
                    : "when_true";
            }
            catch (Exception) { return null; }
        }

        private static string SafeHitCountMode(Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                switch (breakpoint.HitCountType)
                {
                    case dbgHitCountType.dbgHitCountTypeEqual: return "equal";
                    case dbgHitCountType.dbgHitCountTypeGreaterOrEqual: return "greater_or_equal";
                    case dbgHitCountType.dbgHitCountTypeMultiple: return "multiple";
                    default: return "none";
                }
            }
            catch (Exception) { return "none"; }
        }

        private static bool SafeBound(Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return breakpoint.Type == dbgBreakpointType.dbgBreakpointTypeBound; }
            catch (Exception) { return false; }
        }

        private static string Safe(Func<string> accessor)
        {
            try { return accessor() ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        private static int SafeInt(Func<int> accessor)
        {
            try { return accessor(); }
            catch (Exception) { return 0; }
        }

        private static bool SafeBool(Func<bool> accessor)
        {
            try { return accessor(); }
            catch (Exception) { return false; }
        }
    }
}
