using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using EnvDteWindow = EnvDTE.Window;

namespace ClaudeCodeVsMcp.Tools
{
    /// <summary>
    /// Pilotage generique de l'interface : pour toute fenetre sans outil dedie (Explorateur de
    /// tests, Gestionnaire de packages, ...), on lit l'arbre des controles puis on agit par id.
    /// </summary>
    internal static class UiTools
    {
        internal static void Register(ToolRegistry registry)
        {
            registry.Add(
                "ui_windows",
                "Liste les fenetres de Visual Studio (outils, documents) avec leur legende et leur visibilite, " +
                "ainsi que les fenetres WPF racines. Sert a trouver le texte a passer au parametre 'window' de ui_snapshot.",
                SchemaBuilder.New()
                    .Instance()
                    .Bool("visible_only", "Ne lister que les fenetres visibles.", defaultValue: true)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var visibleOnly = Args.Bool(args, "visible_only", true);
                    var windows = new JArray();

                    foreach (EnvDteWindow window in DteProvider.Dte.Windows)
                    {
                        try
                        {
                            var visible = window.Visible;
                            if (visibleOnly && !visible) continue;
                            var entry = new JObject
                            {
                                ["caption"] = window.Caption,
                                ["kind"] = window.Kind,
                                ["visible"] = visible
                            };
                            if (window.Document != null) entry["document"] = window.Document.FullName;
                            if (!string.IsNullOrEmpty(window.ObjectKind)) entry["objectKind"] = window.ObjectKind;
                            windows.Add(entry);
                        }
                        catch (Exception) { /* certaines fenetres levent sur Caption/Visible */ }
                    }

                    return new JObject
                    {
                        ["count"] = windows.Count,
                        ["windows"] = windows,
                        ["wpfRoots"] = UiTreeWalker.DescribeRoots(),
                        ["note"] = "Passer une partie de la legende (ex. 'Test Explorer' ou 'Explorateur de tests') " +
                                   "au parametre 'window' de ui_snapshot."
                    };
                }, ct));

            registry.Add(
                "ui_snapshot",
                "Renvoie l'arbre des controles WPF de Visual Studio en JSON : chaque noeud porte un 'id' a passer a " +
                "ui_action, son type, son nom, sa valeur, son etat (toggle, expanded, selected) et les actions possibles. " +
                "Cibler une fenetre avec 'window' (partie de sa legende) ou un sous-arbre avec 'root_id'. " +
                "Les listes virtualisees ne montrent que les elements affiches : deplier (expand) ou faire defiler " +
                "(scroll_into_view) puis refaire un snapshot. Les ids restent valides tant que le controle existe.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("window", "Texte contenu dans la legende ou l'AutomationId de la fenetre a inspecter (ex. 'Test Explorer'). " +
                                   "Sans 'window' ni 'root_id', toute l'interface est parcourue.")
                    .Int("root_id", "Id d'un noeud d'un snapshot precedent, pour n'inspecter que sa descendance.")
                    .Int("max_depth", "Profondeur maximale de descente.", defaultValue: 12)
                    .Int("max_nodes", "Nombre maximal de noeuds visites.", defaultValue: 1500)
                    .Bool("interactive_only", "N'inclure que les noeuds nommes, valorises ou actionnables ; les conteneurs muets sont aplatis.", defaultValue: true)
                    .Str("filter", "Ne garder que les noeuds dont le nom, la valeur ou l'AutomationId contient ce texte (et leurs ancetres).")
                    .Bool("bounds", "Inclure la position et la taille a l'ecran de chaque noeud.", defaultValue: false)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync<JObject>(async () =>
                {
                    var options = new UiTreeWalker.SnapshotOptions
                    {
                        Window = Args.Str(args, "window"),
                        MaxDepth = Math.Min(Math.Max(1, Args.Int(args, "max_depth", 12)), 60),
                        MaxNodes = Math.Min(Math.Max(10, Args.Int(args, "max_nodes", 1500)), 20000),
                        InteractiveOnly = Args.Bool(args, "interactive_only", true),
                        Filter = Args.Str(args, "filter"),
                        Bounds = Args.Bool(args, "bounds", false)
                    };
                    if (args?["root_id"] != null && args["root_id"].Type != JTokenType.Null)
                        options.RootId = Args.Int(args, "root_id", 0);

                    string activated = null;
                    if (options.Window != null && !options.RootId.HasValue)
                    {
                        activated = ActivateDteWindow(options.Window);
                        // Laisser WPF materialiser le contenu de la fenetre qu'on vient d'activer.
                        if (activated != null) await Task.Delay(150).ConfigureAwait(true);
                    }

                    var snapshot = UiTreeWalker.Snapshot(options);

                    var result = new JObject();
                    if (activated != null) result["activated"] = activated;
                    if (snapshot.Candidates != null)
                    {
                        result["ok"] = false;
                        result["code"] = "AMBIGUOUS_WINDOW";
                        result["error"] = "Plusieurs elements correspondent a '" + options.Window +
                                          "'. Relancer avec root_id sur le bon candidat.";
                        result["candidates"] = snapshot.Candidates;
                        return result;
                    }

                    result["nodeCount"] = snapshot.NodeCount;
                    result["truncated"] = snapshot.Truncated;
                    result["actions"] = new JArray(UiTreeWalker.Actions);
                    result["tree"] = snapshot.Tree ?? new JObject();
                    return result;
                }, ct));

            registry.Add(
                "ui_action",
                "Agit sur un controle designe par l'id d'un ui_snapshot : click (invoke, sinon toggle, select ou expand), " +
                "invoke, toggle, expand, collapse, select, add_to_selection, set_value (avec 'value'), scroll_into_view, focus. " +
                "Renvoie l'etat du noeud apres l'action. Si l'action ouvre une boite de dialogue modale, l'appel expire.",
                SchemaBuilder.New()
                    .Instance()
                    .Int("id", "Id du noeud, tel que renvoye par ui_snapshot.", required: true)
                    .Str("action", "Action a executer.", required: true, allowed: UiTreeWalker.Actions)
                    .Str("value", "Texte a ecrire, pour set_value.")
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync<JObject>(async () =>
                {
                    var id = Args.RequireInt(args, "id");
                    var action = Args.RequireStr(args, "action");
                    var value = Args.Str(args, "value");

                    var peer = UiTreeWalker.Resolve(id);
                    var before = UiTreeWalker.Describe(peer, false);

                    UiTreeWalker.Act(peer, action, value);

                    // Laisser les liaisons WPF se propager avant de relire l'etat.
                    await Task.Delay(100).ConfigureAwait(true);

                    JObject after;
                    try { after = UiTreeWalker.Describe(peer, false); }
                    catch (Exception) { after = new JObject { ["id"] = id, ["gone"] = true }; }

                    return new JObject
                    {
                        ["id"] = id,
                        ["action"] = action,
                        ["before"] = before,
                        ["after"] = after
                    };
                }, ct));

            registry.Add(
                "execute_command",
                "Execute une commande nommee de Visual Studio (ex. 'View.TestExplorer', 'TestExplorer.RunAllTests', " +
                "'Edit.FormatDocument'). Couvre ce que les menus et raccourcis font sans passer par l'interface. " +
                "Si la commande ouvre une boite de dialogue modale, l'appel expire apres 30 s.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("command", "Nom canonique de la commande (Menu.Commande).", required: true)
                    .Str("args", "Arguments de la commande, comme dans la fenetre Commande.")
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var command = Args.RequireStr(args, "command");
                    var commandArgs = Args.Str(args, "args", "");

                    EnvDTE.Command found;
                    try { found = DteProvider.Dte.Commands.Item(command); }
                    catch (Exception)
                    {
                        throw McpToolException.NotFound("Commande inconnue : '" + command + "'.");
                    }

                    var available = found.IsAvailable;
                    if (!available)
                    {
                        throw new McpToolException("COMMAND_UNAVAILABLE",
                            "La commande '" + command + "' existe mais n'est pas disponible dans le contexte actuel.");
                    }

                    try
                    {
                        DteProvider.Dte.ExecuteCommand(command, commandArgs);
                    }
                    catch (COMException ex)
                    {
                        throw new McpToolException("COMMAND_FAILED",
                            "Echec de '" + command + "' : " + ex.Message);
                    }

                    return new JObject
                    {
                        ["command"] = command,
                        ["args"] = commandArgs,
                        ["executed"] = true
                    };
                }, ct));
        }

        /// <summary>
        /// Active la fenetre DTE dont la legende contient le texte, pour qu'un onglet inactif ou
        /// une fenetre en masquage automatique ait un contenu WPF materialise. Renvoie la legende
        /// activee, ou null.
        /// </summary>
        private static string ActivateDteWindow(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var candidates = new List<EnvDteWindow>();
            foreach (EnvDteWindow window in DteProvider.Dte.Windows)
            {
                try
                {
                    var caption = window.Caption;
                    if (!string.IsNullOrEmpty(caption) &&
                        caption.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        candidates.Add(window);
                    }
                }
                catch (Exception) { }
            }

            if (candidates.Count != 1) return null;

            try
            {
                var window = candidates[0];
                if (!window.Visible) window.Visible = true;
                window.Activate();
                return window.Caption;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
