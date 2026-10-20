using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;

namespace ClaudeCodeVsMcp.Vs
{
    internal enum TestRunKind
    {
        All,
        Names,
        Failed,
        Debug,

        /// <summary>Lancee depuis l'interface de Visual Studio, pas par le MCP.</summary>
        External
    }

    internal sealed class TestRunEnd
    {
        /// <summary>completed, cancelled, not_started (rien n'a tourne : build en echec...), refused, failed.</summary>
        internal string Status { get; set; }

        internal string Detail { get; set; }
        internal int DurationMs { get; set; }
    }

    internal sealed class TestRun
    {
        private readonly TaskCompletionSource<TestRunEnd> _end =
            new TaskCompletionSource<TestRunEnd>(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TestRun(TestRunKind kind, IReadOnlyList<string> targets, IReadOnlyDictionary<Guid, long> baseline)
        {
            Kind = kind;
            Targets = targets;
            Baseline = baseline;
            StartedUtc = DateTime.UtcNow;
        }

        internal TestRunKind Kind { get; }

        /// <summary>Noms complets vises, ou null pour tous les tests.</summary>
        internal IReadOnlyList<string> Targets { get; }

        /// <summary>Dernier ReceivedOrder de chaque test avant le lancement : un test qui n'avance pas n'a pas tourne.</summary>
        internal IReadOnlyDictionary<Guid, long> Baseline { get; }

        internal DateTime StartedUtc { get; }
        internal Task<TestRunEnd> Completion => _end.Task;

        // Ecrits sous le verrou du watcher.
        internal bool ExecutionStarted { get; set; }
        internal bool OperationSetFinished { get; set; }
        internal bool StartTaskDone { get; set; }
        internal bool CancelRequested { get; set; }

        internal bool TryEnd(string status, string detail)
        {
            return _end.TrySetResult(new TestRunEnd
            {
                Status = status,
                Detail = detail,
                DurationMs = (int)(DateTime.UtcNow - StartedUtc).TotalMilliseconds
            });
        }
    }

    /// <summary>
    /// Suit les operations de l'Explorateur de tests (evenement IOperationState.StateChanged) et
    /// en tire l'etat d'une execution : construction, execution, annulation, fin.
    ///
    /// PIEGE : TestOperationStates est un enum de drapeaux et le bit Finished (0x4) se retrouve
    /// dans ChangeDetectionFinished (0x40004, fin du build), DiscoveryFinished, Canceled... Tester ce
    /// bit seul declare l'execution terminee des la fin du build. On compare donc des valeurs
    /// exactes, et une fin n'est retenue qu'apres un debut d'execution (TestExecutionStarting ou
    /// TestExecutionStarted).
    ///
    /// L'evenement arrive sur un thread quelconque : tout l'etat est sous verrou.
    /// </summary>
    internal sealed class TestRunWatcher
    {
        private const int DiscoveryStarted = 0x10001;
        private const int DiscoveryFinished = 0x10004;
        private const int DiscoveryCanceled = 0x10006;
        private const int DiscoveryStarting = 0x10008;
        private const int TestExecutionStarted = 0x20001;
        private const int TestExecutionCanceling = 0x20003;
        private const int TestExecutionFinished = 0x20004;
        private const int TestExecutionCancelAndFinished = 0x20006;
        private const int TestExecutionStarting = 0x20008;
        private const int ChangeDetectionFinished = 0x40004;
        private const int ChangeDetectionStarting = 0x40008;
        private const int OperationSetStarted = 0x100000;
        private const int OperationSetFinished = 0x200000;

        private const int RecentCapacity = 16;

        private readonly object _gate = new object();
        private readonly Queue<string> _recent = new Queue<string>();

        // Reference forte : le delegue est attache a un objet MEF qui vit autant que VS.
        private Delegate _handler;

        private bool _discovering;
        private bool _building;
        private bool _executing;
        private bool _cancelling;

        private TestRun _active;
        private TestRun _last;

        /// <summary>Execution en cours, ou null.</summary>
        internal TestRun Active
        {
            get { lock (_gate) { return _active; } }
        }

        /// <summary>Derniere execution, en cours ou terminee, ou null.</summary>
        internal TestRun Last
        {
            get { lock (_gate) { return _last; } }
        }

        internal bool Attached
        {
            get { lock (_gate) { return _handler != null; } }
        }

        /// <summary>idle, discovering, building, running ou cancelling.</summary>
        internal string Phase
        {
            get
            {
                lock (_gate)
                {
                    if (_cancelling) return "cancelling";
                    if (_executing) return "running";
                    if (_building) return "building";
                    if (_discovering) return "discovering";
                    return "idle";
                }
            }
        }

        /// <summary>Derniers evenements recus, pour diagnostiquer un etat inattendu.</summary>
        internal string[] RecentEvents
        {
            get { lock (_gate) { return _recent.ToArray(); } }
        }

        /// <summary>
        /// S'abonne a StateChanged. L'evenement est implemente explicitement sur OperationBroker :
        /// il faut passer par l'EventInfo de l'interface. Le gestionnaire (object, EventArgs) se lie
        /// a EventHandler&lt;OperationStateChangedEventArgs&gt; par contravariance.
        /// </summary>
        internal void Attach(object broker, Type operationStateInterface)
        {
            lock (_gate)
            {
                if (_handler != null) return;
            }

            var stateChanged = operationStateInterface.GetEvent("StateChanged")
                ?? throw new McpToolException("UNSUPPORTED",
                    "IOperationState.StateChanged est introuvable dans cette version de Visual Studio.");

            var callback = typeof(TestRunWatcher).GetMethod(nameof(OnStateChanged), BindingFlags.Instance | BindingFlags.NonPublic);
            var handler = Delegate.CreateDelegate(stateChanged.EventHandlerType, this, callback);
            stateChanged.AddEventHandler(broker, handler);

            lock (_gate)
            {
                _handler = handler;
            }
        }

        /// <summary>
        /// Enregistre une execution AVANT de la lancer : une execution tres courte pourrait sinon
        /// se terminer avant d'etre suivie.
        /// </summary>
        internal TestRun Begin(TestRunKind kind, IReadOnlyList<string> targets, IReadOnlyDictionary<Guid, long> baseline)
        {
            lock (_gate)
            {
                if (_active != null)
                {
                    throw new McpToolException("INVALID_STATE",
                        "Une execution de tests est deja en cours (" + Describe(_active.Kind) +
                        "). Attendre test_status ou l'arreter avec cancel_tests.");
                }

                _active = new TestRun(kind, targets, baseline);
                _last = _active;
                return _active;
            }
        }

        /// <summary>
        /// Suit la tache renvoyee par OperationBroker. Elle sert de second signal de fin, et
        /// revele les lancements refuses (false) ou en erreur.
        /// </summary>
        internal void Track(TestRun run, Task brokerTask)
        {
            if (brokerTask == null)
            {
                End(run, "failed", "OperationBroker n'a renvoye aucune tache.");
                return;
            }

            _ = brokerTask.ContinueWith(t => OnBrokerTaskDone(run, t), TaskScheduler.Default);
        }

        private void OnBrokerTaskDone(TestRun run, Task task)
        {
            if (task.IsFaulted)
            {
                var error = task.Exception?.GetBaseException();
                ExtensionLog.Error("Execution de tests", error);
                End(run, "failed", error?.Message);
                return;
            }

            var accepted = !(task.GetType().GetProperty("Result")?.GetValue(task) is bool result) || result;

            bool end;
            string status;
            lock (_gate)
            {
                run.StartTaskDone = true;
                if (!run.ExecutionStarted && !accepted)
                {
                    end = true;
                    status = "refused";
                }
                else if (!run.ExecutionStarted)
                {
                    // Lancement accepte mais aucune execution vue : fin seulement si l'ensemble
                    // d'operations est deja termine (build en echec, aucun test correspondant).
                    end = run.OperationSetFinished;
                    status = "not_started";
                }
                else
                {
                    end = true;
                    status = run.CancelRequested ? "cancelled" : "completed";
                }
            }

            if (end)
            {
                End(run, status, status == "refused"
                    ? "L'Explorateur de tests a refuse le lancement (une operation est peut-etre deja en cours)."
                    : null);
            }
        }

        /// <summary>Note une demande d'annulation ; renvoie l'execution concernee, ou null.</summary>
        internal TestRun RequestCancel()
        {
            lock (_gate)
            {
                if (_active != null) _active.CancelRequested = true;
                return _active;
            }
        }

        /// <summary>Termine l'execution de force, quand l'evenement de fin n'est jamais arrive.</summary>
        internal void ForceEnd(TestRun run, string status, string detail)
        {
            End(run, status, detail);
        }

        private void OnStateChanged(object sender, EventArgs args)
        {
            object raw;
            try
            {
                raw = args?.GetType().GetProperty("State")?.GetValue(args);
            }
            catch (Exception)
            {
                return;
            }
            if (raw == null) return;

            var state = Convert.ToInt32(raw);
            TestRun toEnd = null;
            string endStatus = null;

            lock (_gate)
            {
                Remember(state, raw.ToString());

                switch (state)
                {
                    case DiscoveryStarting:
                    case DiscoveryStarted:
                        _discovering = true;
                        break;

                    case DiscoveryFinished:
                    case DiscoveryCanceled:
                        _discovering = false;
                        break;

                    case ChangeDetectionStarting:
                        _building = true;
                        break;

                    case ChangeDetectionFinished:
                        _building = false;
                        break;

                    case TestExecutionStarting:
                    case TestExecutionStarted:
                        _building = false;
                        _executing = true;
                        if (_active == null)
                        {
                            _active = new TestRun(TestRunKind.External, null, null);
                            _last = _active;
                        }
                        _active.ExecutionStarted = true;
                        break;

                    case TestExecutionCanceling:
                        _cancelling = true;
                        break;

                    case TestExecutionFinished:
                    case TestExecutionCancelAndFinished:
                        _executing = false;
                        _cancelling = false;
                        if (_active != null && _active.ExecutionStarted)
                        {
                            toEnd = _active;
                            endStatus = state == TestExecutionCancelAndFinished || _active.CancelRequested
                                ? "cancelled"
                                : "completed";
                        }
                        break;

                    case OperationSetStarted:
                        // Un ensemble termine avant le notre (decouverte...) ne doit pas compter.
                        if (_active != null) _active.OperationSetFinished = false;
                        break;

                    case OperationSetFinished:
                        _building = false;
                        _executing = false;
                        _cancelling = false;
                        if (_active != null)
                        {
                            _active.OperationSetFinished = true;
                            if (_active.ExecutionStarted)
                            {
                                toEnd = _active;
                                endStatus = _active.CancelRequested ? "cancelled" : "completed";
                            }
                            else if (_active.StartTaskDone)
                            {
                                toEnd = _active;
                                endStatus = "not_started";
                            }
                        }
                        break;
                }
            }

            if (toEnd != null) End(toEnd, endStatus, null);
        }

        private void End(TestRun run, string status, string detail)
        {
            lock (_gate)
            {
                if (_active == run) _active = null;
            }
            run.TryEnd(status, detail);
        }

        private void Remember(int state, string name)
        {
            _recent.Enqueue(DateTime.UtcNow.ToString("HH:mm:ss.fff") + " 0x" + state.ToString("X") + " " + name);
            while (_recent.Count > RecentCapacity) _recent.Dequeue();
        }

        internal static string Describe(TestRunKind kind)
        {
            switch (kind)
            {
                case TestRunKind.All: return "all";
                case TestRunKind.Names: return "selection";
                case TestRunKind.Failed: return "failed";
                case TestRunKind.Debug: return "debug";
                default: return "external";
            }
        }
    }
}
