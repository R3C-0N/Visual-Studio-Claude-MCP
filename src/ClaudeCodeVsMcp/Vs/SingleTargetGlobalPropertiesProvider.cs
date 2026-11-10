using System;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Build;

namespace ClaudeCodeVsMcp.Vs
{
    /// <summary>
    /// Passe TargetFramework a MSBuild pendant un build lance par le MCP, pour que seul le TFM
    /// choisi soit compile (voir <see cref="SingleTargetBuild"/>).
    ///
    /// Meme principe que StartupProjectSingleTargetGlobalBuildPropertyProvider du systeme de
    /// projet .NET, utilise par F5 : la propriete globale TargetFramework transforme le build
    /// externe (toutes les cibles) en build d'une seule cible. Point d'extension public de CPS ;
    /// le composant est decouvert par MEF grace a l'asset MefComponent du manifeste.
    ///
    /// N'agit que :
    ///  - pendant un build du MCP (<see cref="SingleTargetBuild.Current"/> renseigne) ;
    ///  - pour une configuration multi-cible (dimension TargetFramework presente) ;
    ///  - si le projet cible effectivement ce TFM. Les autres projets se compilent normalement.
    /// Aucun fichier n'est ecrit.
    /// </summary>
    [ExportBuildGlobalPropertiesProvider(designTimeBuildProperties: false)]
    [AppliesTo(".NET")]
    // L'ordre est indispensable : TargetFrameworkGlobalBuildPropertyProvider (Order 10) pose
    // TargetFramework="" sur les configurations multi-cibles pour forcer le build de toutes les
    // cibles, et le fournisseur de F5 (Order 20) le remplace. Le plus grand ordre l'emporte :
    // sans Order, le notre passe en premier et se fait ecraser.
    [Order(100)]
    internal sealed class SingleTargetGlobalPropertiesProvider : StaticGlobalPropertiesProviderBase
    {
        private const string TargetFrameworkProperty = "TargetFramework";

        private static readonly IImmutableDictionary<string, string> Empty =
            ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

        private readonly ConfiguredProject _configuredProject;

        [ImportingConstructor]
        internal SingleTargetGlobalPropertiesProvider(IProjectService projectService, ConfiguredProject configuredProject)
            : base(projectService.Services)
        {
            _configuredProject = configuredProject;
        }

        public override async Task<IImmutableDictionary<string, string>> GetGlobalPropertiesAsync(CancellationToken cancellationToken)
        {
            var framework = SingleTargetBuild.Current;
            if (framework == null) return Empty;

            try
            {
                // Pas de dimension TargetFramework : projet mono-cible, rien a restreindre.
                if (!_configuredProject.ProjectConfiguration.Dimensions.ContainsKey(TargetFrameworkProperty)) return Empty;

                var configurations = _configuredProject.UnconfiguredProject.Services.ProjectConfigurationsService;
                if (configurations == null) return Empty;

                var known = await configurations.GetKnownProjectConfigurationsAsync().ConfigureAwait(false);
                var targets = known
                    .Select(c => c.Dimensions.TryGetValue(TargetFrameworkProperty, out var tfm) ? tfm : null)
                    .Where(tfm => !string.IsNullOrEmpty(tfm));

                if (!targets.Any(tfm => string.Equals(tfm, framework, StringComparison.OrdinalIgnoreCase))) return Empty;

                ExtensionLog.Info("Cible unique " + framework + " : " + System.IO.Path.GetFileName(_configuredProject.UnconfiguredProject.FullPath));
                return Empty.Add(TargetFrameworkProperty, framework);
            }
            catch (Exception ex)
            {
                ExtensionLog.Warn("Cible unique ignoree pour " + _configuredProject.UnconfiguredProject.FullPath + " : " + ex.Message);
                return Empty;
            }
        }
    }
}
