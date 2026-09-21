using System;
using System.IO;
using System.Linq;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using EnvDTE;
using EnvDTE80;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    internal static class SolutionTools
    {
        internal static void Register(ToolRegistry registry)
        {
            registry.Add(
                "solution_info",
                "Etat de la solution ouverte : chemin, configuration active, projets, projet de demarrage " +
                "et mode du debogueur. Point de depart naturel avant toute autre operation.",
                SchemaBuilder.New().Instance().Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var dte = DteProvider.Dte;

                    if (!DteProvider.IsSolutionOpen)
                    {
                        return new JObject
                        {
                            ["isOpen"] = false,
                            ["debugMode"] = DteProvider.DebugModeName(dte.Debugger.CurrentMode),
                            ["hint"] = "Aucune solution ouverte dans cette instance de Visual Studio."
                        };
                    }

                    var solution = dte.Solution;
                    var projects = SolutionHelper.AllProjects(solution).Select(SolutionHelper.Describe).ToList();

                    return new JObject
                    {
                        ["isOpen"] = true,
                        ["solutionPath"] = solution.FullName,
                        ["solutionName"] = Path.GetFileNameWithoutExtension(solution.FullName),
                        ["activeConfiguration"] = SolutionHelper.ActiveConfiguration(solution),
                        ["configurations"] = new JArray(AvailableConfigurations(solution)),
                        ["startupProjects"] = new JArray(StartupProjects(solution)),
                        ["debugMode"] = DteProvider.DebugModeName(dte.Debugger.CurrentMode),
                        ["projectCount"] = projects.Count,
                        ["projects"] = new JArray(projects)
                    };
                }, ct));

            registry.Add(
                "list_projects",
                "Liste les projets de la solution, en descendant dans les dossiers de solution.",
                SchemaBuilder.New().Instance().Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var solution = DteProvider.RequireSolution();
                    var projects = SolutionHelper.AllProjects(solution).Select(SolutionHelper.Describe).ToList();

                    return new JObject
                    {
                        ["count"] = projects.Count,
                        ["projects"] = new JArray(projects)
                    };
                }, ct));

            registry.Add(
                "open_file",
                "Ouvre un fichier dans l'editeur et positionne eventuellement le curseur sur une ligne. " +
                "Fonctionne aussi pour un fichier qui n'appartient pas a la solution.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("path", "Chemin du fichier. Absolu, ou relatif au repertoire de la solution.", required: true)
                    .Int("line", "Ligne sur laquelle placer le curseur (1-based).")
                    .Int("column", "Colonne (1-based).", defaultValue: 1)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var path = ResolvePath(Args.RequireStr(args, "path"));
                    var line = Args.Int(args, "line", 0);
                    var column = Args.Int(args, "column", 1);

                    if (!File.Exists(path))
                    {
                        throw McpToolException.NotFound("Fichier introuvable : " + path);
                    }

                    var window = DteProvider.Dte.ItemOperations.OpenFile(path, Constants.vsViewKindTextView);
                    window?.Activate();

                    if (line > 0)
                    {
                        var selection = window?.Document?.Selection as TextSelection;
                        selection?.MoveToLineAndOffset(line, Math.Max(1, column));
                    }

                    return new JObject
                    {
                        ["path"] = path,
                        ["line"] = line > 0 ? (JToken)line : null
                    };
                }, ct));

            registry.Add(
                "set_startup_project",
                "Definit le projet de demarrage utilise par debug_start.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("project", "Nom ou uniqueName du projet.", required: true)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var solution = DteProvider.RequireSolution();
                    var project = SolutionHelper.FindProject(solution, Args.RequireStr(args, "project"));
                    var uniqueName = project.UniqueName;

                    // StartupProjects attend un tableau d'objets contenant les uniqueName.
                    solution.SolutionBuild.StartupProjects = new object[] { uniqueName };

                    return new JObject { ["startupProject"] = uniqueName };
                }, ct));

            registry.Add(
                "set_configuration",
                "Active une configuration de solution, par exemple 'Release' ou 'Debug|x64'. " +
                "Appeler solution_info pour connaitre les valeurs disponibles.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("configuration", "Nom de la configuration, avec plateforme optionnelle apres '|'.", required: true)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var solution = DteProvider.RequireSolution();
                    var requested = Args.RequireStr(args, "configuration");

                    var builds = solution.SolutionBuild.SolutionConfigurations;
                    for (var i = 1; i <= builds.Count; i++)
                    {
                        var configuration = builds.Item(i);
                        var platform = (configuration as SolutionConfiguration2)?.PlatformName ?? string.Empty;
                        var full = string.IsNullOrEmpty(platform) ? configuration.Name : configuration.Name + "|" + platform;

                        if (string.Equals(full, requested, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(configuration.Name, requested, StringComparison.OrdinalIgnoreCase))
                        {
                            configuration.Activate();
                            return new JObject { ["activeConfiguration"] = full };
                        }
                    }

                    throw McpToolException.NotFound(
                        "Configuration '" + requested + "' introuvable. Disponibles : " +
                        string.Join(", ", AvailableConfigurations(solution)));
                }, ct));
        }

        private static string[] AvailableConfigurations(Solution solution)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var result = new System.Collections.Generic.List<string>();
                var builds = solution.SolutionBuild.SolutionConfigurations;
                for (var i = 1; i <= builds.Count; i++)
                {
                    var configuration = builds.Item(i);
                    var platform = (configuration as SolutionConfiguration2)?.PlatformName ?? string.Empty;
                    result.Add(string.IsNullOrEmpty(platform) ? configuration.Name : configuration.Name + "|" + platform);
                }
                return result.ToArray();
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        private static string[] StartupProjects(Solution solution)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var startup = solution.SolutionBuild.StartupProjects as Array;
                if (startup == null) return new string[0];

                return startup.Cast<object>().Select(o => o?.ToString() ?? string.Empty).ToArray();
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        /// <summary>Resout un chemin relatif par rapport au repertoire de la solution.</summary>
        internal static string ResolvePath(string path)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);

            var solutionPath = DteProvider.SolutionPath;
            if (string.IsNullOrEmpty(solutionPath)) return Path.GetFullPath(path);

            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(solutionPath) ?? string.Empty, path));
        }
    }
}
