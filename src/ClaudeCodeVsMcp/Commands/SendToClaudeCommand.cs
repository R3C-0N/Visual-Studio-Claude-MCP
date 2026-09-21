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

            // Ni conditionnellement invisible, ni conditionnellement desactivee : une commande
            // grisee ne fait rien et n'ecrit rien, ce qui est indiscernable d'un bug de
            // routage ou d'enregistrement. Elle reste donc toujours active, et Execute
            // journalise explicitement le cas « rien a envoyer ».
            service.AddCommand(item);
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

                    if (snapshot == null || snapshot.IsEmpty)
                    {
                        ExtensionLog.Warn("Rien a envoyer : aucune selection dans " +
                                          (source == "output" ? "la fenetre Sortie" : "l'editeur") +
                                          ". Selectionner du texte puis relancer la commande.");
                        return;
                    }

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
