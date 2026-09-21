using System;
using System.Linq;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    internal static class OutputTools
    {
        internal static void Register(ToolRegistry registry)
        {
            registry.Add(
                "list_output_panes",
                "Liste les panes de la fenetre Sortie. Utile car leurs noms sont localises : sur un " +
                "Visual Studio en francais, le pane de compilation s'appelle 'Generer'.",
                SchemaBuilder.New().Instance().Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var panes = OutputPaneReader.AllPanes().Select(OutputPaneReader.SafeName).ToArray();

                    return new JObject
                    {
                        ["count"] = panes.Length,
                        ["panes"] = new JArray(panes),
                        ["aliases"] = new JArray("build", "debug", "general"),
                        ["note"] = "Utiliser de preference les alias, resolus par GUID donc insensibles a la langue."
                    };
                }, ct));

            registry.Add(
                "get_output_pane",
                "Lit le contenu d'un pane de la fenetre Sortie. Par defaut la fin du pane de compilation.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("pane", "Alias ('build', 'debug', 'general') ou nom exact d'un pane.", defaultValue: "build")
                    .Int("max_chars", "Nombre maximum de caracteres retournes.", defaultValue: 20000)
                    .Bool("tail", "Retourner la fin du pane plutot que le debut.", defaultValue: true)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var name = Args.Str(args, "pane", "build");
                    var maxChars = Math.Min(Math.Max(100, Args.Int(args, "max_chars", 20000)), 200000);
                    var tail = Args.Bool(args, "tail", true);

                    var pane = OutputPaneReader.Resolve(name);
                    var text = OutputPaneReader.ReadText(pane, maxChars, tail);

                    return new JObject
                    {
                        ["pane"] = OutputPaneReader.SafeName(pane),
                        ["requested"] = name,
                        ["chars"] = text.Length,
                        ["truncated"] = text.Length >= maxChars,
                        ["tail"] = tail,
                        ["text"] = text
                    };
                }, ct));
        }
    }
}
