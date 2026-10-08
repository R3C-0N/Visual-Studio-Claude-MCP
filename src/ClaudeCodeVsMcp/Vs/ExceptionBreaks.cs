using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClaudeCodeVsMcp.Server;
using EnvDTE90;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Vs
{
    /// <summary>
    /// Parametres d'exception du debogueur (fenetre Parametres d'exception), via
    /// Debugger3.ExceptionGroups.
    ///
    /// Les noms de groupe sont localises ("Common Language Runtime Exceptions" n'existe pas sur un
    /// IDE francais) : le groupe CLR est donc reconnu a son contenu, celui qui connait
    /// System.Exception, jamais a son nom.
    /// </summary>
    internal static class ExceptionBreaks
    {
        private const string ClrMarker = "System.Exception";

        internal enum BreakWhen
        {
            Thrown,
            UserUnhandled,
            Never
        }

        /// <summary>Resultat de l'application a une exception.</summary>
        internal sealed class Applied
        {
            internal ExceptionSetting Setting;
            internal bool Created;
        }

        internal static ExceptionGroups Groups()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var debugger = DteProvider.Dte.Debugger as Debugger3;
            var groups = debugger?.ExceptionGroups;
            if (groups == null)
            {
                throw new McpToolException("UNSUPPORTED",
                    "Les parametres d'exception ne sont pas accessibles dans cette instance de Visual Studio.");
            }
            return groups;
        }

        /// <summary>
        /// Groupe designe par l'appelant (sous-chaine de son nom, insensible a la casse), ou
        /// groupe CLR par defaut.
        /// </summary>
        internal static ExceptionSettings ResolveGroup(string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var names = new List<string>();

            foreach (ExceptionSettings group in Groups())
            {
                var groupName = SafeName(group);
                names.Add(groupName);

                if (name == null)
                {
                    if (TryItem(group, ClrMarker) != null) return group;
                }
                else if (groupName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return group;
                }
            }

            throw McpToolException.NotFound(
                (name == null
                    ? "Groupe d'exceptions CLR introuvable."
                    : "Aucun groupe d'exceptions ne correspond a '" + name + "'.") +
                " Groupes disponibles : " + string.Join(", ", names) + ".");
        }

        /// <summary>
        /// Applique le reglage a une exception. Un nom court ("NullReferenceException") est
        /// complete s'il designe une seule exception connue du groupe. Un nom complet inconnu est
        /// ajoute comme exception personnalisee : c'est ainsi qu'on cible une exception du projet.
        /// </summary>
        internal static Applied Apply(ExceptionSettings group, string name, BreakWhen when)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var created = false;
            var setting = TryItem(group, name) ?? FindByShortName(group, name);

            if (setting == null)
            {
                if (name.IndexOf('.') < 0)
                {
                    throw McpToolException.NotFound(
                        "Exception '" + name + "' inconnue du groupe " + SafeName(group) + ". Donner le nom " +
                        "complet avec son espace de noms (MonApp.Erreurs.MonException) pour l'ajouter.");
                }

                setting = group.NewException(name, 0);
                created = true;
            }

            group.SetBreakWhenThrown(when == BreakWhen.Thrown, setting);
            group.SetBreakWhenUserUnhandled(when != BreakWhen.Never, setting);

            return new Applied { Setting = setting, Created = created };
        }

        /// <summary>Exceptions du groupe qui interrompent a la levee.</summary>
        internal static JArray ListThrown(ExceptionSettings group)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var list = new JArray();
            foreach (ExceptionSetting setting in group)
            {
                if (SafeBool(() => setting.BreakWhenThrown)) list.Add(Describe(setting));
            }
            return list;
        }

        internal static JObject Describe(ExceptionSetting setting)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return new JObject
            {
                ["name"] = Safe(() => setting.Name),
                ["breakWhenThrown"] = SafeBool(() => setting.BreakWhenThrown),
                ["breakWhenUserUnhandled"] = SafeBool(() => setting.BreakWhenUserUnhandled),
                ["userDefined"] = SafeBool(() => setting.UserDefined)
            };
        }

        internal static string SafeName(ExceptionSettings group)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Safe(() => group.Name);
        }

        /// <summary>Item leve une COMException pour un nom absent : on veut null.</summary>
        private static ExceptionSetting TryItem(ExceptionSettings group, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try { return group.Item(name); }
            catch (COMException) { return null; }
            catch (ArgumentException) { return null; }
        }

        private static ExceptionSetting FindByShortName(ExceptionSettings group, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (name.IndexOf('.') >= 0) return null;

            var suffix = "." + name;
            var matches = new List<ExceptionSetting>();

            foreach (ExceptionSetting setting in group)
            {
                if (Safe(() => setting.Name).EndsWith(suffix, StringComparison.Ordinal)) matches.Add(setting);
            }

            if (matches.Count == 1) return matches[0];
            if (matches.Count == 0) return null;

            var names = new List<string>();
            foreach (var match in matches) names.Add(Safe(() => match.Name));
            throw McpToolException.InvalidArgs(
                "Nom ambigu '" + name + "' : " + string.Join(", ", names) + ". Donner le nom complet.");
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
