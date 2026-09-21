using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Commands;
using ClaudeCodeVsMcp.Discovery;
using ClaudeCodeVsMcp.Ide;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Tools;
using ClaudeCodeVsMcp.Vs;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
// EnvDTE definit aussi un type Process : on desambigue une fois pour tout le fichier.
using Process = System.Diagnostics.Process;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVsMcp
{
    /// <summary>
    /// Point d'entree de l'extension : demarre le serveur MCP et publie cette instance de
    /// Visual Studio dans le registre partage.
    ///
    /// Chargement automatique sur ShellInitialized ET SolutionExists. ShellInitialized est le
    /// declencheur principal : le serveur doit repondre meme sans solution ouverte, sinon une
    /// instance vide serait invisible de list_instances et Claude Code ne pourrait pas lui
    /// demander d'ouvrir quoi que ce soit. SolutionExists sert de ceinture-bretelles, l'ordre
    /// des contextes variant selon les versions.
    ///
    /// BackgroundLoad est obligatoire : Visual Studio penalise ou refuse le chargement
    /// synchrone d'une extension.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    public sealed class ClaudeCodeVsMcpPackage : AsyncPackage
    {
        public const string PackageGuidString = "23677659-B917-4169-8644-FEB1608590A3";

        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

        private readonly BuildWatcher _buildWatcher = new BuildWatcher();
        private readonly DebugWatcher _debugWatcher = new DebugWatcher();
        private readonly SessionStore _sessions = new SessionStore();

        // Les objets d'evenements COM DOIVENT rester references : sans champ, le GC les collecte
        // et les evenements cessent d'arriver silencieusement.
        private SolutionEvents _solutionEvents;

        private DTE2 _dte;
        private McpHttpServer _server;
        private IdeBridge _ideBridge;
        private Timer _heartbeat;
        private DateTime _processStartUtc;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // --- Partie thread UI : services VS, DTE, abonnements COM ---
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var outputWindow = await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
            ExtensionLog.Initialize(outputWindow);
            ExtensionLog.Info("Chargement du pont MCP " + McpDispatcher.ServerVersion + "...");

            _dte = await GetServiceAsync(typeof(SDTE)) as DTE2;
            if (_dte == null)
            {
                ExtensionLog.Error("DTE indisponible : le pont MCP ne peut pas demarrer.");
                return;
            }

            DteProvider.Initialize(_dte);
            _buildWatcher.Subscribe(_dte);
            _debugWatcher.Subscribe(_dte);

            _solutionEvents = _dte.Events.SolutionEvents; // champ obligatoire, cf. remarque ci-dessus
            _solutionEvents.Opened += OnSolutionChanged;
            _solutionEvents.AfterClosing += OnSolutionChanged;

            try
            {
                _processStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            }
            catch (Exception)
            {
                _processStartUtc = DateTime.UtcNow;
            }

            // --- Retour en arriere-plan : rien de ce qui suit ne doit occuper le thread UI ---
            await TaskScheduler.Default;

            var registry = ToolInstaller.Build(_sessions, _buildWatcher, _debugWatcher, DescribeServer);
            var dispatcher = new McpDispatcher(registry, _sessions);

            _server = new McpHttpServer(dispatcher, ResolveHubPort());
            _server.HubStatusChanged += PublishInstance;

            if (!_server.Start())
            {
                ExtensionLog.Error("Le serveur MCP n'a pas pu demarrer.");
                return;
            }

            PublishInstance();
            _heartbeat = new Timer(_ => PublishInstance(), null, HeartbeatInterval, HeartbeatInterval);

            // Second transport : WebSocket, seul moyen de POUSSER vers Claude Code.
            // Son echec n'est pas fatal, le pont HTTP reste pleinement fonctionnel.
            _ideBridge = new IdeBridge(dispatcher);
            ClaudeSender.Bridge = _ideBridge;
            _ideBridge.Start(Process.GetCurrentProcess().Id, IdeName, await GetWorkspaceFoldersAsync());

            await SendToClaudeCommand.InitializeAsync(this);

            LogRegistrationHelp();
        }

        /// <summary>Le port du hub peut etre surcharge pour contourner un conflit avec un autre logiciel.</summary>
        private static int ResolveHubPort()
        {
            var raw = Environment.GetEnvironmentVariable("CLAUDE_VS_MCP_HUB_PORT");
            int port;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out port) && port > 0 && port < 65536)
            {
                return port;
            }
            return PortAllocator.DefaultHubPort;
        }

        private void OnSolutionChanged()
        {
            // Declenche sur le thread UI : on republie le registre d'instances et, pour l'IDE,
            // le lockfile, dont workspaceFolders sert a Claude Code pour choisir la bonne session.
            PublishInstance();

            if (_ideBridge == null) return;

            JoinableTaskFactory.RunAsync(async delegate
            {
                var folders = await GetWorkspaceFoldersAsync();
                _ideBridge.UpdateWorkspace(Process.GetCurrentProcess().Id, IdeName, folders);
            }).FileAndForget("claude-vs-mcp/update-ide-workspace");
        }

        /// <summary>
        /// Publie ou rafraichit l'entree de cette instance dans le registre partage.
        /// Lit la solution courante sur le thread UI, puis ecrit le fichier en arriere-plan.
        /// </summary>
        private void PublishInstance()
        {
            if (_server == null) return;

            JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();

                    var solutionPath = DteProvider.SolutionPath;
                    var version = SafeVersion();

                    await TaskScheduler.Default;

                    InstanceRegistry.Write(new InstanceInfo
                    {
                        Pid = Process.GetCurrentProcess().Id,
                        Port = _server.InstancePort,
                        IsHub = _server.IsHub,
                        VsVersion = version,
                        SolutionPath = solutionPath,
                        SolutionName = string.IsNullOrEmpty(solutionPath)
                            ? null
                            : Path.GetFileNameWithoutExtension(solutionPath),
                        StartedUtc = DateTime.UtcNow,
                        ProcessStartUtc = _processStartUtc,
                        HeartbeatUtc = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    ExtensionLog.Error("Publication de l'instance dans le registre impossible.", ex);
                }
            }).FileAndForget("claude-vs-mcp/publish-instance");
        }

        private string SafeVersion()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { return _dte?.Version ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        /// <summary>Nom affiche par Claude Code dans la liste des IDE joignables (/ide).</summary>
        private string IdeName
        {
            get { return "Visual Studio"; }
        }

        /// <summary>
        /// Repertoires que Claude Code compare a son repertoire de travail pour associer une
        /// session a cet IDE : le dossier de la solution ouverte.
        /// </summary>
        private async Task<string[]> GetWorkspaceFoldersAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var solutionPath = DteProvider.SolutionPath;
            await TaskScheduler.Default;

            if (string.IsNullOrEmpty(solutionPath)) return new string[0];

            var directory = Path.GetDirectoryName(solutionPath);
            return string.IsNullOrEmpty(directory) ? new string[0] : new[] { directory };
        }

        private JObject DescribeServer()
        {
            return new JObject
            {
                ["idePort"] = _ideBridge?.Port ?? 0,
                ["ideConnected"] = _ideBridge?.IsConnected ?? false,
                ["pendingSelections"] = ClaudeSender.PendingCount,
                ["instancePort"] = _server?.InstancePort ?? 0,
                ["hubPort"] = _server?.HubPort ?? 0,
                ["isHub"] = _server?.IsHub ?? false,
                ["registryDirectory"] = InstanceRegistry.Directory,
                ["tokenFile"] = TokenStore.FilePath
            };
        }

        /// <summary>
        /// Affiche la commande d'enregistrement prete a copier. Le jeton y figure en clair :
        /// il est deja lisible dans %APPDATA% par l'utilisateur, et sans cela la configuration
        /// initiale devient inutilement penible.
        /// </summary>
        private void LogRegistrationHelp()
        {
            var url = "http://127.0.0.1:" + (_server?.HubPort ?? PortAllocator.DefaultHubPort) + "/mcp";

            ExtensionLog.Info("Serveur MCP demarre. Port d'instance " + _server?.InstancePort +
                              ", role de hub : " + (_server?.IsHub == true ? "oui" : "non") + ".");
            ExtensionLog.Info("Enregistrement dans Claude Code (une seule fois, quelle que soit l'instance) :");
            ExtensionLog.Info("  claude mcp add --transport http visual-studio " + url +
                              " --header \"Authorization: Bearer " + TokenStore.GetOrCreate() + "\"");

            if (_ideBridge != null && _ideBridge.Port != 0)
            {
                ExtensionLog.Info("Integration IDE active (port " + _ideBridge.Port + "). Taper /ide dans " +
                                  "Claude Code pour s'y connecter et activer l'envoi depuis l'editeur.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            // Appele sur le thread UI a la fermeture de Visual Studio : rien de bloquant ici.
            if (disposing)
            {
                try { _heartbeat?.Dispose(); } catch (Exception) { }

                try
                {
                    if (_solutionEvents != null)
                    {
                        _solutionEvents.Opened -= OnSolutionChanged;
                        _solutionEvents.AfterClosing -= OnSolutionChanged;
                        _solutionEvents = null;
                    }
                }
                catch (Exception) { }

                try { _ideBridge?.Dispose(); } catch (Exception) { }
                try { _server?.Dispose(); } catch (Exception) { }
                try { _buildWatcher.Dispose(); } catch (Exception) { }
                try { _debugWatcher.Dispose(); } catch (Exception) { }

                try { InstanceRegistry.Remove(Process.GetCurrentProcess().Id); } catch (Exception) { }
            }

            base.Dispose(disposing);
        }
    }
}
