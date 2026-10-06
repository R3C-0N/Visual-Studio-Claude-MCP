using System;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Vs;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVsMcp.Infrastructure
{
    /// <summary>
    /// Marshaling vers le thread UI de Visual Studio.
    ///
    /// Tous les appels DTE/EnvDTE DOIVENT passer par ici. Comme on reste dans le processus
    /// devenv.exe et qu'on execute sur son propre thread UI, il n'y a aucun appel COM
    /// inter-apartment : les rejets RPC_E_CALL_REJECTED ne se produisent pas et un
    /// IOleMessageFilter est inutile.
    ///
    /// En contrepartie, si VS est bloque dans une boucle modale (boite de dialogue,
    /// assistant d'exception), le basculement vers le thread UI n'aboutit pas. D'ou le
    /// timeout obligatoire sur chaque appel.
    ///
    /// Une fois sur le thread UI, un appel COM synchrone qui ouvre une boite modale (Build
    /// pendant un debogage, Stop...) ne rend la main qu'a sa fermeture et ne peut pas etre
    /// interrompu. On cesse alors de l'attendre des qu'une boite est constatee : l'appelant
    /// recoit une ModalDialogException qui la decrit, au lieu de bloquer, verrou du dispatcher
    /// compris, jusqu'a ce que quelqu'un y reponde. L'appel abandonne se termine a la fermeture
    /// de la boite.
    /// </summary>
    internal static class UiThread
    {
        internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        private const int DialogPollMs = 200;

        /// <summary>Une boite doit persister ce temps avant qu'on abandonne l'appel.</summary>
        private const int DialogGraceMs = 600;

        internal static async Task<T> RunAsync<T>(Func<Task<T>> body, CancellationToken ct, TimeSpan? timeout = null)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeout ?? DefaultTimeout);
                var token = cts.Token;

                var joinable = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    return await body().ConfigureAwait(true);
                });
                var work = joinable.Task;

                int? dialogSince = null;
                while (!work.IsCompleted)
                {
                    await Task.WhenAny(work, Task.Delay(DialogPollMs, ct)).ConfigureAwait(false);
                    if (work.IsCompleted) break;
                    ct.ThrowIfCancellationRequested();

                    var dialog = ModalDialogs.FindTop();
                    if (dialog == null)
                    {
                        dialogSince = null;
                        continue;
                    }

                    dialogSince = dialogSince ?? Environment.TickCount;
                    if (Environment.TickCount - dialogSince.Value < DialogGraceMs) continue;

                    Abandon(work);
                    throw new ModalDialogException(dialog);
                }

                try
                {
                    return await joinable;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "Visual Studio n'a pas repondu dans le delai imparti. " +
                        (ModalDialogs.Describe() ?? "Aucune boite de dialogue modale detectee : l'IDE est probablement occupe."));
                }
            }
        }

        internal static Task<T> RunAsync<T>(Func<T> body, CancellationToken ct, TimeSpan? timeout = null)
        {
            return RunAsync(() => Task.FromResult(body()), ct, timeout);
        }

        /// <summary>L'appel abandonne finira a la fermeture de la boite : ne pas perdre son erreur.</summary>
        private static void Abandon(Task work)
        {
            _ = work.ContinueWith(
                t => ExtensionLog.Warn("Appel abandonne pour cause de boite modale, termine en erreur : " +
                                       t.Exception?.GetBaseException().Message),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Une boite de dialogue modale bloque l'appel en cours. Renvoyee au client avec le code
    /// DIALOG_OPEN et la description de la boite.
    /// </summary>
    internal sealed class ModalDialogException : Exception
    {
        internal ModalDialogs.Dialog Dialog { get; }

        internal ModalDialogException(ModalDialogs.Dialog dialog)
            : base("Visual Studio attend une reponse a une boite de dialogue et ne traitera rien d'autre " +
                   "d'ici la ; la commande en cours reprendra a sa fermeture. " + ModalDialogs.Describe(dialog))
        {
            Dialog = dialog;
        }
    }
}
