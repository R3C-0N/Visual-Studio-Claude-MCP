using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using ClaudeCodeVsMcp.Server;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Vs
{
    /// <summary>
    /// Parcours generique de l'interface WPF de Visual Studio via les AutomationPeer.
    ///
    /// On tourne dans devenv.exe : plutot que d'utiliser un client UI Automation (qui se bloque
    /// quand client et provider partagent le thread UI), on interroge directement les peers WPF.
    /// L'arbre obtenu est celui que verrait un lecteur d'ecran, et les patterns (Invoke, Toggle,
    /// ExpandCollapse, SelectionItem, Value, ScrollItem) permettent d'agir sans simuler la souris.
    ///
    /// Chaque peer rencontre recoit un identifiant entier stable tant que le controle vit ; le
    /// modele fait un snapshot, lit les ids, puis agit dessus. Tout exige le thread UI.
    /// </summary>
    internal static class UiTreeWalker
    {
        internal sealed class SnapshotOptions
        {
            internal string Window;
            internal int? RootId;
            internal int MaxDepth = 12;
            internal int MaxNodes = 1500;
            internal bool InteractiveOnly = true;
            internal string Filter;
            internal bool Bounds;
        }

        internal sealed class SnapshotResult
        {
            internal JToken Tree;
            internal int NodeCount;
            internal bool Truncated;
            internal JArray Candidates;
        }

        private static readonly string[] AllActions =
        {
            "click", "invoke", "toggle", "expand", "collapse", "select", "add_to_selection",
            "set_value", "scroll_into_view", "focus"
        };

        private static readonly object Gate = new object();
        private static readonly Dictionary<int, WeakReference<AutomationPeer>> ById =
            new Dictionary<int, WeakReference<AutomationPeer>>();
        private static readonly ConditionalWeakTable<AutomationPeer, StrongBox<int>> IdByPeer =
            new ConditionalWeakTable<AutomationPeer, StrongBox<int>>();
        private static int _nextId = 1;

        internal static string[] Actions => AllActions;

        // ------------------------------------------------------------------ racines

        /// <summary>Fenetres WPF de niveau superieur du processus (principale + flottantes + popups).</summary>
        internal static List<AutomationPeer> Roots()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var roots = new List<AutomationPeer>();
            var seen = new HashSet<Visual>();

            var app = Application.Current;
            if (app != null)
            {
                foreach (Window window in app.Windows)
                {
                    if (window == null || !seen.Add(window)) continue;
                    var peer = SafePeer(window);
                    if (peer != null) roots.Add(peer);
                }
            }

            foreach (PresentationSource source in PresentationSource.CurrentSources)
            {
                var visual = source?.RootVisual as UIElement;
                if (visual == null || !seen.Add(visual)) continue;
                var peer = SafePeer(visual);
                if (peer != null) roots.Add(peer);
            }

            return roots;
        }

        private static AutomationPeer SafePeer(UIElement element)
        {
            try { return UIElementAutomationPeer.CreatePeerForElement(element); }
            catch (Exception) { return null; }
        }

        internal static JArray DescribeRoots()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var result = new JArray();
            foreach (var peer in Roots())
            {
                var node = Describe(peer, false);
                node.Remove("actions");
                result.Add(node);
            }
            return result;
        }

        // ------------------------------------------------------------------ snapshot

        internal static SnapshotResult Snapshot(SnapshotOptions options)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (options == null) throw new ArgumentNullException(nameof(options));

            var result = new SnapshotResult();
            List<AutomationPeer> starts;

            if (options.RootId.HasValue)
            {
                starts = new List<AutomationPeer> { Resolve(options.RootId.Value) };
            }
            else if (!string.IsNullOrWhiteSpace(options.Window))
            {
                var matches = FindWindow(options.Window);
                if (matches.Count == 0)
                {
                    throw McpToolException.NotFound(
                        "Aucun element d'interface dont le nom contient '" + options.Window + "'. " +
                        "Verifier avec ui_windows que la fenetre est ouverte et visible (un onglet inactif ou en " +
                        "masquage automatique n'a pas de contenu materialise).");
                }
                if (matches.Count > 1)
                {
                    result.Candidates = new JArray(matches.Select(m => Describe(m, false)));
                    return result;
                }
                starts = matches;
            }
            else
            {
                starts = Roots();
            }

            var state = new WalkState(options);
            var trees = new JArray();
            foreach (var start in starts)
            {
                var node = Walk(start, 0, state, isRoot: true);
                if (node != null) trees.Add(node);
            }

            result.NodeCount = state.Visited;
            result.Truncated = state.Truncated;
            result.Tree = trees.Count == 1 ? trees[0] : trees;
            return result;
        }

        private sealed class WalkState
        {
            internal readonly SnapshotOptions Options;
            internal int Visited;
            internal bool Truncated;

            internal WalkState(SnapshotOptions options) { Options = options; }
        }

        /// <summary>
        /// Construit le noeud JSON d'un peer et de sa descendance. Renvoie null si le noeud est
        /// elague (mode interactive_only ou filtre texte). Les conteneurs sans interet propre
        /// sont "aplatis" : leurs enfants remontent dans le parent.
        /// </summary>
        private static JToken Walk(AutomationPeer peer, int depth, WalkState state, bool isRoot)
        {
            var built = WalkInner(peer, depth, state, isRoot);
            if (built == null) return null;
            if (built.Type == JTokenType.Array)
            {
                var arr = (JArray)built;
                if (arr.Count == 0) return null;
                // Racine aplatie : on garde une enveloppe pour que le resultat reste un objet.
                if (isRoot)
                {
                    var wrapper = Describe(peer, state.Options.Bounds);
                    wrapper["children"] = arr;
                    return wrapper;
                }
            }
            return built;
        }

        private static JToken WalkInner(AutomationPeer peer, int depth, WalkState state, bool isRoot)
        {
            state.Visited++;
            if (state.Visited > state.Options.MaxNodes)
            {
                state.Truncated = true;
                return null;
            }

            JObject node;
            try { node = Describe(peer, state.Options.Bounds); }
            catch (Exception ex)
            {
                return new JObject { ["id"] = IdOf(peer), ["error"] = ex.GetType().Name };
            }

            var children = new JArray();
            if (depth < state.Options.MaxDepth)
            {
                List<AutomationPeer> kids = null;
                try
                {
                    peer.ResetChildrenCache();
                    kids = peer.GetChildren();
                }
                catch (Exception) { /* controle en cours de detachement */ }

                if (kids != null)
                {
                    foreach (var kid in kids)
                    {
                        if (kid == null) continue;
                        var child = WalkInner(kid, depth + 1, state, false);
                        if (child == null) continue;
                        if (child.Type == JTokenType.Array)
                        {
                            foreach (var hoisted in (JArray)child) children.Add(hoisted);
                        }
                        else
                        {
                            children.Add(child);
                        }
                        if (state.Truncated) break;
                    }
                }
            }
            else
            {
                try
                {
                    peer.ResetChildrenCache();
                    var kids = peer.GetChildren();
                    if (kids != null && kids.Count > 0)
                    {
                        node["childrenOmitted"] = kids.Count;
                        state.Truncated = true;
                    }
                }
                catch (Exception) { }
            }

            var interesting = IsInteresting(node);
            var matchesFilter = MatchesFilter(node, state.Options.Filter);

            if (!string.IsNullOrEmpty(state.Options.Filter))
            {
                // Filtre : on garde les noeuds qui correspondent et leurs ancetres.
                if (!matchesFilter && children.Count == 0) return null;
            }

            if (state.Options.InteractiveOnly && !interesting && !isRoot)
            {
                // Conteneur muet : ses enfants remontent d'un cran.
                return children;
            }

            if (children.Count > 0) node["children"] = children;
            return node;
        }

        private static bool IsInteresting(JObject node)
        {
            if (node["error"] != null) return true;
            if (node["actions"] is JArray actions && actions.Count > 0) return true;
            if (!string.IsNullOrEmpty((string)node["name"])) return true;
            if (node["value"] != null) return true;
            var type = (string)node["type"];
            return type == "Window" || type == "Edit" || type == "Document";
        }

        private static bool MatchesFilter(JObject node, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return Contains((string)node["name"], filter)
                || Contains((string)node["value"], filter)
                || Contains((string)node["automationId"], filter);
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack)
                && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------------ recherche de fenetre

        private static readonly HashSet<AutomationControlType> ContainerTypes = new HashSet<AutomationControlType>
        {
            AutomationControlType.Window, AutomationControlType.Pane, AutomationControlType.Custom,
            AutomationControlType.Group, AutomationControlType.Document, AutomationControlType.Tab,
            AutomationControlType.ToolBar, AutomationControlType.Tree, AutomationControlType.List,
            AutomationControlType.DataGrid
        };

        /// <summary>
        /// Cherche les elements dont le nom ou l'AutomationId contient le texte. Ne garde que les
        /// correspondances les plus externes, en preferant les conteneurs aux boutons ou onglets
        /// qui portent le meme libelle.
        /// </summary>
        internal static List<AutomationPeer> FindWindow(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var matches = new List<AutomationPeer>();
            var budget = 20000;
            foreach (var root in Roots())
            {
                CollectMatches(root, text, matches, ref budget, 0);
            }

            if (matches.Count <= 1) return matches;

            var containers = matches.Where(m => ContainerTypes.Contains(SafeType(m))).ToList();
            if (containers.Count == 1) return containers;
            if (containers.Count > 1) return containers;
            return matches;
        }

        private static void CollectMatches(AutomationPeer peer, string text, List<AutomationPeer> matches, ref int budget, int depth)
        {
            if (--budget <= 0 || depth > 40) return;

            bool match;
            try
            {
                match = Contains(peer.GetName(), text)
                    || Contains(peer.GetAutomationId(), text);
            }
            catch (Exception) { match = false; }

            if (match)
            {
                // Correspondance la plus externe : on ne descend pas plus loin.
                matches.Add(peer);
                return;
            }

            List<AutomationPeer> kids;
            try
            {
                peer.ResetChildrenCache();
                kids = peer.GetChildren();
            }
            catch (Exception) { return; }

            if (kids == null) return;
            foreach (var kid in kids)
            {
                if (kid == null) continue;
                CollectMatches(kid, text, matches, ref budget, depth + 1);
                if (budget <= 0) return;
            }
        }

        private static AutomationControlType SafeType(AutomationPeer peer)
        {
            try { return peer.GetAutomationControlType(); }
            catch (Exception) { return AutomationControlType.Custom; }
        }

        // ------------------------------------------------------------------ description d'un noeud

        internal static JObject Describe(AutomationPeer peer, bool bounds)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var node = new JObject { ["id"] = IdOf(peer) };

            var type = SafeType(peer);
            node["type"] = type.ToString();

            Put(node, "name", Safe(peer.GetName));
            Put(node, "automationId", Safe(peer.GetAutomationId));
            Put(node, "className", Safe(peer.GetClassName));
            Put(node, "help", Safe(peer.GetHelpText));
            Put(node, "accessKey", Safe(peer.GetAccessKey));
            Put(node, "shortcut", Safe(peer.GetAcceleratorKey));

            var actions = new JArray();

            if (peer.GetPattern(PatternInterface.Value) is IValueProvider value)
            {
                try
                {
                    Put(node, "value", value.Value);
                    if (!value.IsReadOnly) actions.Add("set_value");
                }
                catch (Exception) { }
            }
            if (peer.GetPattern(PatternInterface.RangeValue) is IRangeValueProvider range)
            {
                try { node["value"] = range.Value; } catch (Exception) { }
            }
            if (peer.GetPattern(PatternInterface.Invoke) != null)
            {
                actions.Add("invoke");
            }
            if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
            {
                try { node["toggle"] = toggle.ToggleState.ToString(); } catch (Exception) { }
                actions.Add("toggle");
            }
            if (peer.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider expand)
            {
                try
                {
                    var st = expand.ExpandCollapseState;
                    node["expanded"] = st == ExpandCollapseState.Expanded || st == ExpandCollapseState.PartiallyExpanded;
                    if (st == ExpandCollapseState.LeafNode) node["leaf"] = true;
                }
                catch (Exception) { }
                actions.Add("expand");
                actions.Add("collapse");
            }
            if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider selection)
            {
                try { node["selected"] = selection.IsSelected; } catch (Exception) { }
                actions.Add("select");
                actions.Add("add_to_selection");
            }
            if (peer.GetPattern(PatternInterface.ScrollItem) != null)
            {
                actions.Add("scroll_into_view");
            }
            if (Safe(peer.IsKeyboardFocusable))
            {
                actions.Add("focus");
                if (Safe(peer.HasKeyboardFocus)) node["focused"] = true;
            }

            if (actions.Count > 0) node["actions"] = actions;

            if (!Safe(peer.IsEnabled, true)) node["enabled"] = false;
            if (Safe(peer.IsOffscreen)) node["offscreen"] = true;

            if (bounds)
            {
                try
                {
                    var rect = peer.GetBoundingRectangle();
                    if (!rect.IsEmpty)
                    {
                        node["bounds"] = new JObject
                        {
                            ["x"] = Math.Round(rect.X), ["y"] = Math.Round(rect.Y),
                            ["w"] = Math.Round(rect.Width), ["h"] = Math.Round(rect.Height)
                        };
                    }
                }
                catch (Exception) { }
            }

            return node;
        }

        private static void Put(JObject node, string key, string value)
        {
            if (!string.IsNullOrEmpty(value)) node[key] = value;
        }

        private static string Safe(Func<string> getter)
        {
            try { return getter(); } catch (Exception) { return null; }
        }

        private static bool Safe(Func<bool> getter, bool fallback = false)
        {
            try { return getter(); } catch (Exception) { return fallback; }
        }

        // ------------------------------------------------------------------ identifiants

        internal static int IdOf(AutomationPeer peer)
        {
            lock (Gate)
            {
                if (IdByPeer.TryGetValue(peer, out var box)) return box.Value;

                if (ById.Count > 50000) Prune();

                var id = _nextId++;
                IdByPeer.Add(peer, new StrongBox<int>(id));
                ById[id] = new WeakReference<AutomationPeer>(peer);
                return id;
            }
        }

        private static void Prune()
        {
            var dead = ById.Where(kv => !kv.Value.TryGetTarget(out _)).Select(kv => kv.Key).ToList();
            foreach (var key in dead) ById.Remove(key);
        }

        internal static AutomationPeer Resolve(int id)
        {
            lock (Gate)
            {
                if (ById.TryGetValue(id, out var weak) && weak.TryGetTarget(out var peer)) return peer;
            }
            throw McpToolException.NotFound(
                "Noeud #" + id + " inconnu ou detruit : reprendre un ui_snapshot pour obtenir des ids a jour.");
        }

        // ------------------------------------------------------------------ actions

        internal static void Act(AutomationPeer peer, string action, string value)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!Safe(peer.IsEnabled, true))
            {
                throw McpToolException.InvalidArgs(
                    "Le noeud #" + IdOf(peer) + " (" + Safe(peer.GetName) + ") est desactive : l'action est refusee.");
            }

            switch (action)
            {
                case "click":
                    if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider inv) { inv.Invoke(); return; }
                    if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider tog) { tog.Toggle(); return; }
                    if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider sel) { sel.Select(); return; }
                    if (peer.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider ec)
                    {
                        if (ec.ExpandCollapseState == ExpandCollapseState.Collapsed) ec.Expand(); else ec.Collapse();
                        return;
                    }
                    throw Unsupported(peer, action);

                case "invoke":
                    Require<IInvokeProvider>(peer, PatternInterface.Invoke, action).Invoke();
                    return;

                case "toggle":
                    Require<IToggleProvider>(peer, PatternInterface.Toggle, action).Toggle();
                    return;

                case "expand":
                    Require<IExpandCollapseProvider>(peer, PatternInterface.ExpandCollapse, action).Expand();
                    return;

                case "collapse":
                    Require<IExpandCollapseProvider>(peer, PatternInterface.ExpandCollapse, action).Collapse();
                    return;

                case "select":
                    Require<ISelectionItemProvider>(peer, PatternInterface.SelectionItem, action).Select();
                    return;

                case "add_to_selection":
                    Require<ISelectionItemProvider>(peer, PatternInterface.SelectionItem, action).AddToSelection();
                    return;

                case "set_value":
                {
                    if (value == null)
                        throw McpToolException.InvalidArgs("L'action set_value exige le parametre 'value'.");
                    var provider = Require<IValueProvider>(peer, PatternInterface.Value, action);
                    if (provider.IsReadOnly)
                        throw McpToolException.InvalidArgs("Le noeud #" + IdOf(peer) + " est en lecture seule.");
                    provider.SetValue(value);
                    return;
                }

                case "scroll_into_view":
                    Require<IScrollItemProvider>(peer, PatternInterface.ScrollItem, action).ScrollIntoView();
                    return;

                case "focus":
                    peer.SetFocus();
                    return;

                default:
                    throw McpToolException.InvalidArgs(
                        "Action inconnue '" + action + "'. Actions possibles : " + string.Join(", ", AllActions) + ".");
            }
        }

        private static T Require<T>(AutomationPeer peer, PatternInterface pattern, string action) where T : class
        {
            var provider = peer.GetPattern(pattern) as T;
            if (provider == null) throw Unsupported(peer, action);
            return provider;
        }

        private static McpToolException Unsupported(AutomationPeer peer, string action)
        {
            var node = Describe(peer, false);
            var available = node["actions"] is JArray a && a.Count > 0 ? string.Join(", ", a.Select(t => (string)t)) : "aucune";
            return McpToolException.InvalidArgs(
                "Le noeud #" + IdOf(peer) + " (" + SafeType(peer) + " '" + Safe(peer.GetName) + "') ne supporte pas '" +
                action + "'. Actions disponibles : " + available + ".");
        }
    }
}
