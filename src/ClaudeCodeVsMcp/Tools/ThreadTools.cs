using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using EnvDTE;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    /// <summary>
    /// Threads du programme debogue : liste, changement de thread courant, piles de tous les
    /// threads. Indispensable pour un interblocage ou un pool de threads sature, invisibles depuis
    /// la seule pile du thread courant.
    ///
    /// Les piles sont celles des threads systeme : une tache async en attente n'en a pas, elle
    /// n'apparait donc pas.
    /// </summary>
    internal static class ThreadTools
    {
        internal static void Register(ToolRegistry registry)
        {
            registry.Add(
                "list_threads",
                "Threads du programme debogue, avec la premiere frame du code utilisateur de chacun. " +
                "Exige que le debogueur soit arrete.",
                SchemaBuilder.New().Instance().Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var debugger = DteProvider.RequireBreakMode();
                    var currentId = CurrentThreadId(debugger);

                    var threads = new JArray();
                    foreach (var thread in Threads(debugger))
                    {
                        var described = Describe(thread, currentId);
                        described["top"] = StackHelper.FirstUserFrame(SafeFrames(thread), 30);
                        threads.Add(described);
                    }

                    return new JObject
                    {
                        ["count"] = threads.Count,
                        ["currentThreadId"] = currentId,
                        ["threads"] = threads
                    };
                }, ct));

            registry.Add(
                "switch_thread",
                "Change le thread courant : get_stack, get_locals et evaluate s'appliquent ensuite a ce " +
                "thread. Exige que le debogueur soit arrete.",
                SchemaBuilder.New()
                    .Instance()
                    .Int("thread_id", "Identifiant du thread (list_threads).", required: true)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var threadId = Args.RequireInt(args, "thread_id");
                    var debugger = DteProvider.RequireBreakMode();

                    var target = Threads(debugger).FirstOrDefault(t => SafeInt(() => t.ID) == threadId);
                    if (target == null)
                    {
                        throw McpToolException.NotFound("Aucun thread d'identifiant " + threadId + ". Appeler list_threads.");
                    }

                    debugger.CurrentThread = target;

                    var result = Describe(target, threadId);
                    result["position"] = StackHelper.CurrentPosition(debugger);
                    result["top"] = StackHelper.FirstUserFrame(SafeFrames(target), 30);
                    return result;
                }, ct));

            registry.Add(
                "get_all_stacks",
                "Piles d'appels de tous les threads, regroupees quand elles sont identiques (comme la " +
                "fenetre Piles paralleles) : le moyen le plus direct de voir un interblocage ou des " +
                "threads tous bloques au meme endroit. Exige que le debogueur soit arrete.",
                SchemaBuilder.New()
                    .Instance()
                    .Int("max_frames", "Nombre maximum de frames par pile.", defaultValue: 15)
                    .Bool("user_code_only", "Ne garder que les frames du code utilisateur.", defaultValue: false)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var maxFrames = Math.Min(Math.Max(Args.Int(args, "max_frames", 15), 1), 100);
                    var userCodeOnly = Args.Bool(args, "user_code_only", false);
                    var debugger = DteProvider.RequireBreakMode();
                    var currentId = CurrentThreadId(debugger);

                    // Cle = suite des fonctions : deux threads bloques au meme endroit par le meme
                    // chemin forment un seul groupe.
                    var groups = new Dictionary<string, JObject>(StringComparer.Ordinal);
                    var order = new List<string>();
                    var threadCount = 0;

                    foreach (var thread in Threads(debugger))
                    {
                        threadCount++;
                        var frames = StackHelper.DescribeFrames(SafeFrames(thread), maxFrames, userCodeOnly);
                        var key = string.Join("\n", frames.Select(f => (string)f["function"]));

                        if (!groups.TryGetValue(key, out var group))
                        {
                            group = new JObject { ["threads"] = new JArray(), ["frames"] = frames };
                            groups[key] = group;
                            order.Add(key);
                        }

                        var id = SafeInt(() => thread.ID);
                        var name = Safe(() => thread.Name);
                        var entry = new JObject { ["id"] = id };
                        if (name.Length > 0) entry["name"] = name;
                        if (id == currentId) entry["current"] = true;
                        ((JArray)group["threads"]).Add(entry);
                    }

                    // Les groupes les plus peuples d'abord ; a egalite, l'ordre des threads.
                    var sorted = order
                        .Select((key, index) => new { Group = groups[key], Index = index })
                        .OrderByDescending(g => ((JArray)g.Group["threads"]).Count)
                        .ThenBy(g => g.Index)
                        .Select(g => g.Group);

                    return new JObject
                    {
                        ["threadCount"] = threadCount,
                        ["groupCount"] = groups.Count,
                        ["groups"] = new JArray(sorted),
                        ["note"] = "Piles des threads systeme : une tache async en attente n'apparait pas."
                    };
                }, ct));
        }

        /// <summary>Threads du programme courant. Un thread mort entre deux lectures est ignore.</summary>
        private static List<Thread> Threads(Debugger debugger)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var list = new List<Thread>();
            Threads threads;
            try { threads = debugger.CurrentProgram?.Threads; }
            catch (Exception) { threads = null; }

            if (threads == null)
            {
                throw new McpToolException("UNSUPPORTED", "Le programme debogue n'expose pas ses threads.");
            }

            foreach (Thread thread in threads)
            {
                if (SafeBool(() => thread.IsAlive)) list.Add(thread);
            }
            return list;
        }

        private static JObject Describe(Thread thread, int currentId)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var id = SafeInt(() => thread.ID);
            return new JObject
            {
                ["id"] = id,
                ["name"] = Safe(() => thread.Name),
                ["location"] = Safe(() => thread.Location),
                ["current"] = id == currentId,
                ["frozen"] = SafeBool(() => thread.IsFrozen),
                ["priority"] = Safe(() => thread.Priority)
            };
        }

        private static StackFrames SafeFrames(Thread thread)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            try { return thread.StackFrames; }
            catch (Exception) { return null; }
        }

        private static int CurrentThreadId(Debugger debugger)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            return SafeInt(() => debugger.CurrentThread?.ID ?? 0);
        }

        private static string Safe(Func<string> accessor)
        {
            try { return accessor() ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        private static int SafeInt(Func<int> accessor)
        {
            try { return accessor(); }
            catch (Exception) { return 0; }
        }

        private static bool SafeBool(Func<bool> accessor)
        {
            try { return accessor(); }
            catch (Exception) { return false; }
        }
    }
}
