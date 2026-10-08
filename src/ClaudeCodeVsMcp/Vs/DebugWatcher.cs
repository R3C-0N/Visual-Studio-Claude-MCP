using System;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVsMcp.Vs
{
    internal enum DebugTransitionKind
    {
        Break,
        Run,
        Design
    }

    internal sealed class DebugTransition
    {
        internal DebugTransitionKind Kind { get; set; }
        internal string Reason { get; set; }

        internal string KindName
        {
            get
            {
                switch (Kind)
                {
                    case DebugTransitionKind.Break: return "Break";
                    case DebugTransitionKind.Run: return "Run";
                    default: return "Design";
                }
            }
        }
    }

    /// <summary>
    /// Exception signalee par le debogueur juste avant l'arret qu'elle provoque. Seule source
    /// d'information quand $exception n'est pas evaluable (code natif, exception SEH).
    /// </summary>
    internal sealed class DebuggerException
    {
        internal string Type { get; set; }
        internal string Name { get; set; }
        internal int Code { get; set; }
        internal string Description { get; set; }

        /// <summary>true : non geree par le code utilisateur ; false : arret a la levee.</summary>
        internal bool Unhandled { get; set; }
    }

    /// <summary>
    /// Traduit les transitions du debogueur en taches attendables.
    ///
    /// Comme pour BuildWatcher, DebuggerEvents doit etre conserve dans un champ d'instance
    /// sous peine de voir les evenements disparaitre silencieusement apres un passage du GC.
    /// </summary>
    internal sealed class DebugWatcher : IDisposable
    {
        private readonly object _gate = new object();

        private DebuggerEvents _debuggerEvents;
        private TaskCompletionSource<DebugTransition> _pending;

        /// <summary>
        /// Declenche sur le thread UI a chaque passage en mode arret, apres l'achevement de
        /// l'attente. Permet a BreakpointManager d'appliquer les semantiques temporaire et
        /// dependante, qu'EnvDTE n'implemente pas.
        /// </summary>
        internal event Action Broke;

        /// <summary>Declenche sur le thread UI quand la session de debogage se termine.</summary>
        internal event Action SessionEnded;

        /// <summary>
        /// Derniere exception ayant arrete le debogueur, ou null. Effacee a la reprise : elle ne
        /// decrit que l'arret en cours. Lue et ecrite sur le thread UI uniquement.
        /// </summary>
        internal DebuggerException LastException { get; private set; }

        internal void Subscribe(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _debuggerEvents = dte.Events.DebuggerEvents; // champ d'instance obligatoire.
            _debuggerEvents.OnEnterBreakMode += OnEnterBreakMode;
            _debuggerEvents.OnEnterRunMode += OnEnterRunMode;
            _debuggerEvents.OnEnterDesignMode += OnEnterDesignMode;
            _debuggerEvents.OnExceptionThrown += OnExceptionThrown;
            _debuggerEvents.OnExceptionNotHandled += OnExceptionNotHandled;
        }

        /// <summary>
        /// Arme l'attente AVANT d'emettre Go/Step, sinon un arret immediat serait manque.
        /// </summary>
        internal Task<DebugTransition> Arm()
        {
            lock (_gate)
            {
                _pending = new TaskCompletionSource<DebugTransition>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _pending.Task;
            }
        }

        /// <summary>
        /// La signature expose 'ref dbgExecutionAction' : ne JAMAIS y ecrire, cela modifierait
        /// le comportement du debogueur (reprise automatique, par exemple).
        /// </summary>
        private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction executionAction)
        {
            Complete(DebugTransitionKind.Break, reason.ToString());
            Raise(Broke, "Broke");
        }

        private void OnEnterRunMode(dbgEventReason reason)
        {
            LastException = null;
            // On ne complete pas sur le passage en Run : l'appelant attend un arret ou la fin.
        }

        /// <summary>
        /// Le retour en mode Design signifie que le programme s'est termine. Completer ici evite
        /// de laisser l'appelant attendre jusqu'au timeout pour un programme deja fini.
        /// </summary>
        private void OnEnterDesignMode(dbgEventReason reason)
        {
            LastException = null;
            Complete(DebugTransitionKind.Design, reason.ToString());
            Raise(SessionEnded, "SessionEnded");
        }

        /// <summary>
        /// Meme regle que pour OnEnterBreakMode : ne JAMAIS ecrire dans 'ref dbgExceptionAction',
        /// cela deciderait a la place de l'utilisateur de reprendre ou d'ignorer l'exception.
        /// </summary>
        private void OnExceptionThrown(string exceptionType, string name, int code, string description,
            ref dbgExceptionAction exceptionAction)
        {
            Record(exceptionType, name, code, description, false);
        }

        private void OnExceptionNotHandled(string exceptionType, string name, int code, string description,
            ref dbgExceptionAction exceptionAction)
        {
            Record(exceptionType, name, code, description, true);
        }

        private void Record(string exceptionType, string name, int code, string description, bool unhandled)
        {
            LastException = new DebuggerException
            {
                Type = exceptionType,
                Name = name,
                Code = code,
                Description = description,
                Unhandled = unhandled
            };
        }

        /// <summary>
        /// Un abonne qui leve ne doit pas remonter dans un callback COM du debogueur : cela
        /// desabonnerait le sink et ferait disparaitre silencieusement tous les evenements
        /// suivants.
        /// </summary>
        private static void Raise(Action handler, string name)
        {
            if (handler == null) return;
            try { handler(); }
            catch (Exception ex) { ClaudeCodeVsMcp.Infrastructure.ExtensionLog.Error("Evenement debogueur " + name, ex); }
        }

        private void Complete(DebugTransitionKind kind, string reason)
        {
            TaskCompletionSource<DebugTransition> pending;
            lock (_gate)
            {
                pending = _pending;
                _pending = null;
            }

            pending?.TrySetResult(new DebugTransition { Kind = kind, Reason = reason });
        }

        public void Dispose()
        {
            try
            {
                if (_debuggerEvents != null)
                {
                    _debuggerEvents.OnEnterBreakMode -= OnEnterBreakMode;
                    _debuggerEvents.OnEnterRunMode -= OnEnterRunMode;
                    _debuggerEvents.OnEnterDesignMode -= OnEnterDesignMode;
                    _debuggerEvents.OnExceptionThrown -= OnExceptionThrown;
                    _debuggerEvents.OnExceptionNotHandled -= OnExceptionNotHandled;
                    _debuggerEvents = null;
                }
            }
            catch (Exception)
            {
                // Arret de VS.
            }
        }
    }
}
