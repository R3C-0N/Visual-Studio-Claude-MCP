using System;
using System.Threading;
using System.Threading.Tasks;
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
    /// Limite connue : le timeout protege la mise en file d'attente. Une fois sur le thread
    /// UI, un appel COM synchrone qui bloque ne peut pas etre interrompu.
    /// </summary>
    internal static class UiThread
    {
        internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        internal static async Task<T> RunAsync<T>(Func<Task<T>> body, CancellationToken ct, TimeSpan? timeout = null)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeout ?? DefaultTimeout);
                var token = cts.Token;

                try
                {
                    return await ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                        return await body().ConfigureAwait(true);
                    });
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "Visual Studio n'a pas repondu dans le delai imparti. " +
                        "Une boite de dialogue modale est peut-etre ouverte, ou l'IDE est occupe.");
                }
            }
        }

        internal static Task<T> RunAsync<T>(Func<T> body, CancellationToken ct, TimeSpan? timeout = null)
        {
            return RunAsync(() => Task.FromResult(body()), ct, timeout);
        }
    }
}
