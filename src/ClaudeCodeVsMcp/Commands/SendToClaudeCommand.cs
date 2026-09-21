using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Ide;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Vs;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVsMcp.Commands
{
    /// <summary>
    /// Commande « Envoyer a Claude Code », disponible dans le menu contextuel de l'editeur
    /// (Ctrl+Alt+Shift+C) et dans celui de la fenetre Sortie.
    ///
    /// L'interet de passer par une commande plutot que de laisser Claude Code lire la selection
    /// a la demande : le contenu est fige au moment du clic. Entre le moment ou l'utilisateur
    /// selectionne quelque chose et celui ou il formule sa demande, la selection a souvent deja
    /// change.
    /// </summary>
    internal sealed class SendToClaudeCommand
    {
        private static readonly Guid CommandSet = new Guid("F7D83724-4AC7-4935-896D-167AD15219EC");
        private const int CmdIdEditor = 0x0100;
        private const int CmdIdOutput = 0x0101;

        private readonly AsyncPackage _package;

        private SendToClaudeCommand(AsyncPackage package)
        {
            _package = package;
        }

        internal static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var service = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (service == null)
            {
                ExtensionLog.Warn("Service de commandes indisponible : la commande d'envoi ne sera pas proposee.");
                return;
            }

            var command = new SendToClaudeCommand(package);
            command.Register(service, CmdIdEditor, "editor");
            command.Register(service, CmdIdOutput, "output");

            ExtensionLog.Info("Commande « Envoyer a Claude Code » enregistree " +
                              "(menu contextuel de l'editeur et de la fenetre Sortie, Ctrl+Alt+Maj+C).");
        }

        private void Register(OleMenuCommandService service, int commandId, string source)
        {
            var id = new CommandID(CommandSet, commandId);
            var item = new OleMenuCommand((s, e) => Execute(source), id);

            // Le bouton est toujours visible : la decouvrabilite prime, et une commande
            // conditionnellement invisible ne laisse aucune trace quand la condition se
            // trompe. On ne pilote donc que l'activation, et en restant permissif : en cas
            // de doute on laisse la commande active plutot que de la griser a tort.
            item.BeforeQueryStatus += (s, e) =>
            {
                var command = s as OleMenuCommand;
                if (command == null) return;

                ThreadHelper.ThrowIfNotOnUIThread();
                command.Enabled = HasSomethingToSend(source);
            };

            service.AddCommand(item);
        }

        private static bool HasSomethingToSend(string source)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var snapshot = SelectionReader.Read(source);
                return snapshot != null && !snapshot.IsEmpty;
            }
            catch (Exception)
            {
                // BeforeQueryStatus est appele a chaque ouverture de menu : il doit rester muet.
                // On active malgre tout : Execute signalera une selection vide dans le journal,
                // ce qui est plus diagnosticable qu'une commande grisee sans explication.
                return true;
            }
        }

        private void Execute(string source)
        {
            ExtensionLog.Info("Commande d'envoi declenchee (source demandee : " + source + ").");

            _package.JoinableTaskFactory.RunAsync(async delegate
            {
                try
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var snapshot = SelectionReader.Read(source);

                    await TaskScheduler.Default;
                    var outcome = await ClaudeSender.SendAsync(snapshot, CancellationToken.None)
                        .ConfigureAwait(false);

                    ClaudeSender.LogOutcome(outcome);
                }
                catch (Exception ex)
                {
                    ExtensionLog.Error("Envoi de la selection a Claude Code impossible.", ex);
                }
            }).FileAndForget("claude-vs-mcp/send-selection");
        }
    }
}
