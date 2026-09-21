using System;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVsMcp.Vs
{
    internal sealed class BuildOutcome
    {
        internal bool Succeeded { get; set; }
        internal int FailedProjects { get; set; }
        internal string Scope { get; set; }
        internal string Action { get; set; }
        internal int DurationMs { get; set; }
    }

    /// <summary>
    /// Traduit les evenements de build d'EnvDTE en taches attendables.
    ///
    /// PIEGE MAJEUR : dte.Events.BuildEvents renvoie un NOUVEL objet COM a chaque acces. Sans
    /// reference forte conservee dans un champ, le RCW est collecte par le GC et les evenements
    /// cessent d'arriver -- sans la moindre erreur. C'est la cause numero un des regressions
    /// "ca marchait hier" dans les extensions VS.
    /// </summary>
    internal sealed class BuildWatcher : IDisposable
    {
        private readonly object _gate = new object();

        private BuildEvents _buildEvents;
        private TaskCompletionSource<BuildOutcome> _pending;
        private Task<BuildOutcome> _pendingTask;
        private DateTime _startedUtc;

        /// <summary>
        /// Derniere attente armee, terminee ou non. Permet a un second appel de build de se
        /// raccrocher au build en cours au lieu d'en lancer un autre, et a build_status de
        /// relire le dernier verdict.
        /// </summary>
        internal Task<BuildOutcome> Pending
        {
            get { lock (_gate) { return _pendingTask; } }
        }

        internal DateTime StartedUtc
        {
            get { lock (_gate) { return _startedUtc; } }
        }

        internal void Subscribe(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _buildEvents = dte.Events.BuildEvents; // champ d'instance : ne pas transformer en variable locale.
            _buildEvents.OnBuildDone += OnBuildDone;
        }

        /// <summary>
        /// Arme l'attente AVANT de lancer le build : sinon un build tres court peut se terminer
        /// avant l'abonnement et l'attente expire pour rien.
        /// </summary>
        internal Task<BuildOutcome> Arm()
        {
            lock (_gate)
            {
                // RunContinuationsAsynchronously est indispensable : sans lui, la suite du traitement
                // (serialisation, ecriture HTTP) s'executerait en ligne sur le thread UI de VS.
                _pending = new TaskCompletionSource<BuildOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingTask = _pending.Task;
                _startedUtc = DateTime.UtcNow;
                return _pendingTask;
            }
        }

        private void OnBuildDone(vsBuildScope scope, vsBuildAction action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var failed = 0;
            try
            {
                // LastBuildInfo = nombre de projets en echec. Plus fiable que d'analyser le texte
                // de la fenetre Sortie, dont le format depend de la verbosite et de la langue.
                failed = DteProvider.Dte.Solution.SolutionBuild.LastBuildInfo;
            }
            catch (Exception ex)
            {
                ExtensionLog.Warn("Lecture de LastBuildInfo impossible : " + ex.Message);
            }

            TaskCompletionSource<BuildOutcome> pending;
            lock (_gate)
            {
                pending = _pending;
                _pending = null;
            }

            pending?.TrySetResult(new BuildOutcome
            {
                Succeeded = failed == 0,
                DurationMs = (int)(DateTime.UtcNow - StartedUtc).TotalMilliseconds,
                FailedProjects = failed,
                Scope = scope.ToString(),
                Action = action.ToString()
            });
        }

        internal void Cancel()
        {
            TaskCompletionSource<BuildOutcome> pending;
            lock (_gate)
            {
                pending = _pending;
                _pending = null;
            }
            pending?.TrySetCanceled();
        }

        public void Dispose()
        {
            try
            {
                if (_buildEvents != null)
                {
                    _buildEvents.OnBuildDone -= OnBuildDone;
                    _buildEvents = null;
                }
            }
            catch (Exception)
            {
                // Arret de VS : le desabonnement peut echouer si le RCW est deja libere.
            }
        }
    }
}
