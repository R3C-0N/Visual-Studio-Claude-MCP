using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using EnvDTE;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVsMcp.Vs
{
    /// <summary>Cible retenue pour un build, et d'ou elle vient.</summary>
    internal sealed class TargetFrameworkChoice
    {
        /// <summary>TFM a compiler, ou null pour toutes les cibles.</summary>
        internal string Framework { get; set; }

        /// <summary>session (set_configuration), ide (liste deroulante de la barre d'outils) ou all.</summary>
        internal string Source { get; set; }
    }

    /// <summary>
    /// Cible unique pendant les builds lances par le MCP.
    ///
    /// Visual Studio sait deja ne compiler que la cible active, mais seulement pour un build
    /// declenche par F5 et pour l'unique projet de demarrage (StartupProjectSingleTargetGlobalBuildPropertyProvider
    /// du systeme de projet .NET). La commande Generer, que le MCP utilise, compile toutes les cibles.
    /// <see cref="SingleTargetGlobalPropertiesProvider"/> comble ce manque : tant que
    /// <see cref="Current"/> est renseigne, il passe TargetFramework a MSBuild pour chaque projet
    /// multi-cible qui la contient. Rien n'est ecrit sur disque.
    /// </summary>
    internal static class SingleTargetBuild
    {
        /// <summary>Valeur de set_configuration qui revient a la cible active de l'IDE.</summary>
        internal const string Ide = "ide";

        /// <summary>Valeur de set_configuration qui compile toutes les cibles.</summary>
        internal const string All = "all";

        private static volatile string _session;
        private static volatile string _current;

        /// <summary>
        /// Reglage de session : un TFM, <see cref="All"/>, ou null pour suivre la cible active de l'IDE.
        /// Garde en memoire uniquement : changer la cible active de l'IDE ecrirait ActiveDebugFramework
        /// dans le .csproj.user, ce qu'on s'interdit.
        /// </summary>
        internal static string Session
        {
            get => _session;
            set => _session = string.IsNullOrWhiteSpace(value) || string.Equals(value, Ide, StringComparison.OrdinalIgnoreCase)
                ? null
                : value.Trim();
        }

        /// <summary>Cible du build MCP en cours, null hors d'un build MCP. Lue par le fournisseur.</summary>
        internal static string Current => _current;

        internal static void Begin(string framework) => _current = framework;

        internal static void End() => _current = null;

        /// <summary>Cible a utiliser pour le prochain build : reglage de session, sinon cible active de l'IDE.</summary>
        internal static async Task<TargetFrameworkChoice> ResolveAsync(Solution solution)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var session = Session;
            if (string.Equals(session, All, StringComparison.OrdinalIgnoreCase))
            {
                return new TargetFrameworkChoice { Framework = null, Source = "session" };
            }
            if (session != null)
            {
                return new TargetFrameworkChoice { Framework = session, Source = "session" };
            }

            var ide = await ReadIdeActiveFrameworkAsync(solution);
            return ide == null
                ? new TargetFrameworkChoice { Framework = null, Source = "all" }
                : new TargetFrameworkChoice { Framework = ide, Source = "ide" };
        }

        /// <summary>
        /// Cible active de l'IDE (liste deroulante de la barre d'outils) pour l'unique projet de
        /// demarrage. Null si le projet de demarrage n'est pas multi-cible ou n'est pas un projet CPS.
        /// Lecture seule.
        /// </summary>
        internal static async Task<string> ReadIdeActiveFrameworkAsync(Solution solution)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            try
            {
                var startup = (solution.SolutionBuild.StartupProjects as object[])?.OfType<string>().ToArray();
                if (startup == null || startup.Length != 1)
                {
                    ExtensionLog.Info("Pas d'unique projet de demarrage : toutes les cibles seront compilees.");
                    return null;
                }

                var project = SolutionHelper.AllProjects(solution)
                    .FirstOrDefault(p => string.Equals(SolutionHelper.Safe(() => p.UniqueName), startup[0], StringComparison.OrdinalIgnoreCase));
                var unconfigured = GetUnconfiguredProject(project);
                if (unconfigured == null)
                {
                    ExtensionLog.Info("Projet de demarrage hors systeme de projet SDK : toutes les cibles seront compilees.");
                    return null;
                }

                // Service du systeme de projet .NET : interface publique, mais dans une assembly
                // (Microsoft.VisualStudio.ProjectSystem.Managed) qui n'a pas de package de
                // compilation. On l'atteint donc par reflexion, avec un repli sur le .csproj.user.
                // Ordre : choix enregistre selon le service, puis selon le .csproj.user, et
                // seulement ensuite la premiere cible (ce qu'affiche la liste sans choix enregistre).
                // 1. Propriete MSBuild evaluee ActiveDebugFramework (ecrite par la liste deroulante
                //    dans le .csproj.user) : API publique de CPS, la plus fiable a l'essai. Le
                //    service IActiveDebugFrameworkServices renvoyait une chaine vide alors que
                //    MSBuild voyait bien la valeur.
                var evaluated = await ReadEvaluatedActiveFrameworkAsync(unconfigured);
                if (!string.IsNullOrEmpty(evaluated)) return evaluated;

                var fromService = await ReadActiveFrameworkFromServiceAsync(unconfigured);
                if (!string.IsNullOrEmpty(fromService.Active)) return fromService.Active;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var fromUserFile = ReadActiveFrameworkFromUserFile(project);
                if (!string.IsNullOrEmpty(fromUserFile)) return fromUserFile;

                if (fromService.First == null)
                {
                    ExtensionLog.Info("Projet de demarrage mono-cible : pas de cible unique a appliquer.");
                }
                return fromService.First;
            }
            catch (Exception ex)
            {
                ExtensionLog.Warn("Lecture de la cible active de l'IDE impossible : " + ex.Message);
                return null;
            }
        }

        internal static UnconfiguredProject GetUnconfiguredProject(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project == null) return null;

            var context = project as IVsBrowseObjectContext;
            if (context == null)
            {
                try { context = project.Object as IVsBrowseObjectContext; } catch (Exception) { }
            }
            return context?.UnconfiguredProject;
        }

        private static async Task<ServiceFrameworks> ReadActiveFrameworkFromServiceAsync(UnconfiguredProject unconfigured)
        {
            // Type.GetType ne trouve pas une assembly chargee depuis un dossier d'extension :
            // on la cherche parmi celles deja chargees (le systeme de projet l'est des qu'un
            // projet SDK est ouvert).
            var serviceType = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name == "Microsoft.VisualStudio.ProjectSystem.Managed")
                .Select(a => a.GetType("Microsoft.VisualStudio.ProjectSystem.Debug.IActiveDebugFrameworkServices", throwOnError: false))
                .FirstOrDefault(t => t != null);
            if (serviceType == null)
            {
                ExtensionLog.Warn("IActiveDebugFrameworkServices introuvable : repli sur le .csproj.user.");
                return new ServiceFrameworks();
            }

            // GetExportedValues<T>() existe dans les deux MEF (System.ComponentModel.Composition et
            // VS MEF) ; GetExportedValueOrDefault, non.
            var exportProvider = unconfigured.Services.ExportProvider;
            var getExported = exportProvider.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetExportedValues" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            if (getExported == null)
            {
                ExtensionLog.Warn("GetExportedValues<T> absent de " + exportProvider.GetType().FullName + " : repli sur le .csproj.user.");
                return new ServiceFrameworks();
            }

            var services = getExported.MakeGenericMethod(serviceType).Invoke(exportProvider, null) as System.Collections.IEnumerable;
            var service = services?.Cast<object>().FirstOrDefault();
            if (service == null)
            {
                ExtensionLog.Warn("IActiveDebugFrameworkServices non exporte pour ce projet : repli sur le .csproj.user.");
                return new ServiceFrameworks();
            }

            var result = new ServiceFrameworks();
            if (serviceType.GetMethod("GetActiveDebuggingFrameworkPropertyAsync")?.Invoke(service, null) is Task<string> active)
            {
                var value = await active;
                if (!string.IsNullOrWhiteSpace(value)) result.Active = value.Trim();
            }

            // Sans choix enregistre, la liste deroulante affiche la premiere cible de TargetFrameworks.
            if (serviceType.GetMethod("GetProjectFrameworksAsync")?.Invoke(service, null) is Task<System.Collections.Generic.List<string>> frameworks)
            {
                var list = await frameworks;
                if (list != null && list.Count > 1) result.First = list[0];
            }
            return result;
        }

        private static async Task<string> ReadEvaluatedActiveFrameworkAsync(UnconfiguredProject unconfigured)
        {
            try
            {
                var configured = await unconfigured.GetSuggestedConfiguredProjectAsync();
                var properties = configured?.Services.ProjectPropertiesProvider?.GetCommonProperties();
                if (properties == null) return null;

                var value = await properties.GetEvaluatedPropertyValueAsync("ActiveDebugFramework");
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            catch (Exception ex)
            {
                ExtensionLog.Warn("Lecture de ActiveDebugFramework evaluee impossible : " + ex.Message);
                return null;
            }
        }

        private sealed class ServiceFrameworks
        {
            internal string Active { get; set; }
            internal string First { get; set; }
        }

        private static string ReadActiveFrameworkFromUserFile(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var solution = (IVsSolution)ServiceProvider.GlobalProvider.GetService(typeof(SVsSolution));
            if (solution == null) return null;
            if (solution.GetProjectOfUniqueName(project.UniqueName, out var hierarchy) != 0) return null;
            if (!(hierarchy is IVsBuildPropertyStorage storage)) return null;

            return storage.GetPropertyValue("ActiveDebugFramework", null, (uint)_PersistStorageType.PST_USER_FILE, out var value) == 0
                && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;
        }
    }
}
