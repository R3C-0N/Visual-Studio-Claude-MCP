using ClaudeCodeVsMcp.Ide;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    internal static class SelectionTools
    {
        internal static void Register(ToolRegistry registry)
        {
            registry.Add(
                "get_selection",
                "Lit ce qui est selectionne a cet instant dans Visual Studio : du code dans l'editeur, " +
                "ou une portion de la fenetre Sortie. Si rien n'est selectionne dans l'editeur, renvoie " +
                "la ligne du curseur.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("source", "Surface a lire. 'auto' deduit de la fenetre active.",
                        allowed: new[] { "auto", "editor", "output" }, defaultValue: "auto")
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(
                    () => SelectionReader.Read(Args.Str(args, "source", "auto")).ToJson(), ct));

            // Volontairement NON global : la file vit dans l'instance qui a capture la selection,
            // donc l'appel doit etre route vers cette instance-la plutot que traite par le hub.
            registry.Add(
                "take_pending_context",
                "Recupere les selections envoyees depuis Visual Studio par la commande " +
                "'Envoyer a Claude Code' et pas encore consommees. La file est videe : chaque " +
                "element n'est livre qu'une fois. A appeler quand l'utilisateur dit avoir envoye " +
                "quelque chose depuis l'IDE.",
                SchemaBuilder.New().Instance().Build(),
                (args, ctx, ct) =>
                {
                    var items = ClaudeSender.Drain();

                    return System.Threading.Tasks.Task.FromResult(new JObject
                    {
                        ["count"] = items.Count,
                        ["items"] = items,
                        ["note"] = items.Count == 0
                            ? "Aucune selection en attente. Utiliser get_selection pour lire la selection courante."
                            : null
                    });
                });
        }
    }
}
