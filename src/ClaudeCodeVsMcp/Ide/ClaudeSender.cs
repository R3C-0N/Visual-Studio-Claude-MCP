using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Vs;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Ide
{
    /// <summary>
    /// Transmet une selection a Claude Code, par push si l'integration IDE est connectee,
    /// et dans tous les cas par depot dans une file que Claude Code peut venir vider.
    ///
    /// La file n'est pas un simple repli : elle fige le contenu AU MOMENT DU CLIC. Sans elle,
    /// un outil qui relirait la selection plus tard renverrait ce qui est selectionne a cet
    /// instant-la, pas ce que l'utilisateur voulait envoyer.
    /// </summary>
    internal static class ClaudeSender
    {
        private const int MaxPending = 20;

        private static readonly ConcurrentQueue<JObject> Pending = new ConcurrentQueue<JObject>();

        internal static IdeBridge Bridge { get; set; }

        private static string SnippetDirectory
        {
            get { return Path.Combine(Path.GetTempPath(), "claude-vs-mcp", "snippets"); }
        }

        internal static int PendingCount
        {
            get { return Pending.Count; }
        }

        /// <summary>
        /// Envoie une selection. Renvoie un compte rendu destine au journal et a l'utilisateur.
        /// </summary>
        internal static async Task<JObject> SendAsync(SelectionSnapshot snapshot, CancellationToken ct)
        {
            if (snapshot == null || snapshot.IsEmpty)
            {
                return new JObject
                {
                    ["pushed"] = false,
                    ["queued"] = false,
                    ["reason"] = "Selection vide : rien a envoyer."
                };
            }

            var entry = snapshot.ToJson();
            entry["capturedUtc"] = DateTime.UtcNow.ToString("o");
            Enqueue(entry);

            var bridge = Bridge;
            if (bridge == null || !bridge.IsConnected)
            {
                return new JObject
                {
                    ["pushed"] = false,
                    ["queued"] = true,
                    ["pendingCount"] = Pending.Count,
                    ["reason"] = "Claude Code n'est pas connecte a l'integration IDE. " +
                                 "Lancer /ide dans Claude Code, ou recuperer la selection avec take_pending_context."
                };
            }

            // at_mentioned ne transporte qu'un chemin et une plage de lignes : du texte qui ne
            // vit pas dans un fichier (sortie de console) doit d'abord etre materialise.
            var path = snapshot.FilePath;
            int? startLine = null;
            int? endLine = null;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                path = WriteSnippet(snapshot);
                var lineCount = snapshot.Text.Split('\n').Length;
                startLine = 0;
                endLine = Math.Max(0, lineCount - 1);
            }
            else
            {
                // EnvDTE compte les lignes a partir de 1, le protocole IDE a partir de 0.
                startLine = Math.Max(0, snapshot.StartLine - 1);
                endLine = Math.Max(0, snapshot.EndLine - 1);
            }

            var pushed = await bridge.SendAtMentionAsync(path, startLine, endLine, ct).ConfigureAwait(false);

            return new JObject
            {
                ["pushed"] = pushed,
                ["queued"] = true,
                ["path"] = path,
                ["lineStart"] = startLine,
                ["lineEnd"] = endLine,
                ["pendingCount"] = Pending.Count
            };
        }

        /// <summary>Materialise un extrait sans fichier d'origine, pour pouvoir le mentionner.</summary>
        private static string WriteSnippet(SelectionSnapshot snapshot)
        {
            Directory.CreateDirectory(SnippetDirectory);

            var label = Sanitize(snapshot.PaneName ?? snapshot.Source ?? "extrait");
            var name = string.Format("{0:yyyyMMdd-HHmmss}-{1}.txt", DateTime.Now, label);
            var path = Path.Combine(SnippetDirectory, name);

            var header = new StringBuilder();
            header.AppendLine("# Extrait capture depuis Visual Studio");
            header.AppendLine("# Source : " + snapshot.Source +
                              (string.IsNullOrEmpty(snapshot.PaneName) ? string.Empty : " / pane " + snapshot.PaneName));
            header.AppendLine("# Capture : " + DateTime.Now.ToString("u"));
            header.AppendLine();

            File.WriteAllText(path, header + snapshot.Text, Encoding.UTF8);
            return path;
        }

        private static string Sanitize(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(value.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray());
            return cleaned.Length > 40 ? cleaned.Substring(0, 40) : cleaned;
        }

        private static void Enqueue(JObject entry)
        {
            Pending.Enqueue(entry);

            while (Pending.Count > MaxPending)
            {
                JObject dropped;
                Pending.TryDequeue(out dropped);
            }
        }

        /// <summary>Vide la file : les elements ne sont livres qu'une fois.</summary>
        internal static JArray Drain()
        {
            var items = new JArray();
            JObject entry;
            while (Pending.TryDequeue(out entry))
            {
                items.Add(entry);
            }
            return items;
        }

        internal static void LogOutcome(JObject outcome)
        {
            if (outcome == null) return;

            if ((bool?)outcome["pushed"] == true)
            {
                ExtensionLog.Info("Selection poussee vers Claude Code : " + outcome["path"]);
            }
            else
            {
                ExtensionLog.Info("Selection mise en attente pour Claude Code. " + outcome["reason"]);
            }
        }
    }
}
