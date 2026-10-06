using System.Linq;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    /// <summary>
    /// Boites de dialogue modales. Ces outils n'utilisent que Win32, jamais le thread UI que la
    /// boite bloque, et echappent au verrou du dispatcher : l'appel qui a ouvert la boite le
    /// detient peut-etre encore.
    /// </summary>
    internal static class DialogTools
    {
        internal static void Register(ToolRegistry registry)
        {
            registry.Add(
                "get_dialog",
                "Boites de dialogue modales ouvertes dans Visual Studio : titre, message et boutons. " +
                "Tant qu'une boite est ouverte, Visual Studio ne traite plus aucune commande ; un outil " +
                "qui depasse son delai est souvent bloque par l'une d'elles.",
                SchemaBuilder.New().Instance().Build(),
                (args, ctx, ct) =>
                {
                    var dialogs = ModalDialogs.FindAll();
                    return Task.FromResult(new JObject
                    {
                        ["count"] = dialogs.Count,
                        ["dialogs"] = new JArray(dialogs.Select(d => (object)d.ToJson()).ToArray())
                    });
                },
                ungated: true);

            registry.Add(
                "answer_dialog",
                "Repond a la boite de dialogue modale au premier plan en cliquant un de ses boutons.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("button",
                        "Libelle du bouton tel que renvoye par get_dialog ('Oui', 'Non'...), ou reponse " +
                        "standard independante de la langue : yes, no, ok, cancel, abort, retry, ignore.")
                    .Int("id", "Identifiant du bouton renvoye par get_dialog, a la place de 'button'.")
                    .Build(),
                (args, ctx, ct) =>
                {
                    var dialog = ModalDialogs.FindTop();
                    if (dialog == null)
                    {
                        throw new McpToolException("NO_DIALOG", "Aucune boite de dialogue modale n'est ouverte.");
                    }

                    var label = Args.Str(args, "button");
                    var id = args["id"] != null ? Args.Int(args, "id", 0) : (int?)null;
                    if (label == null && id == null)
                    {
                        throw McpToolException.InvalidArgs("Preciser 'button' ou 'id'.");
                    }

                    var button = ModalDialogs.Resolve(dialog, label, id);
                    if (button == null)
                    {
                        throw new McpToolException("UNKNOWN_BUTTON",
                            "Aucun bouton ne correspond. Boite ouverte : " + dialog.ToJson().ToString(Newtonsoft.Json.Formatting.None));
                    }

                    var closed = ModalDialogs.Click(dialog, button);
                    ExtensionLog.Info("answer_dialog : \"" + dialog.Title + "\" -> \"" + button.Label + "\"" +
                                      (closed ? "." : ", boite toujours ouverte."));

                    var result = new JObject
                    {
                        ["dialog"] = dialog.ToJson(),
                        ["answered"] = button.Label,
                        ["closed"] = closed
                    };

                    var next = ModalDialogs.FindTop();
                    if (next != null) result["nextDialog"] = next.ToJson();
                    if (!closed)
                    {
                        result["hint"] = "La boite ne s'est pas fermee : ce bouton n'existe peut-etre pas. " +
                                         "Essayer une autre reponse standard.";
                    }

                    return Task.FromResult(result);
                },
                ungated: true);
        }
    }
}
