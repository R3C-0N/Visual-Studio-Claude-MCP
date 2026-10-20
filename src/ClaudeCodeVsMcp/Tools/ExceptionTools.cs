using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using EnvDTE;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    /// <summary>
    /// Debogage des exceptions : choisir celles qui interrompent l'execution, et decrire celle sur
    /// laquelle le debogueur est arrete.
    /// </summary>
    internal static class ExceptionTools
    {
        private const int MaxInnerCap = 10;
        private const int MaxAggregated = 10;
        private const int MaxFrames = 50;

        /// <summary>
        /// Une ligne de StackTrace : "at Methode() in C:\chemin\Fichier.cs:line 42". Les mots
        /// "at", "in" et "line" suivent la culture du programme debogue ("a ... dans ...:ligne"),
        /// d'ou des \S+ a leur place ; seul le chemin absolu sert d'ancre.
        /// </summary>
        private static readonly Regex FrameLine = new Regex(
            @"^\s*\S+\s+(?<method>.+?)(?:\s+\S+\s+(?<file>(?:[A-Za-z]:\\|\\\\|/).+?):\S+\s+(?<line>\d+))?\s*$",
            RegexOptions.Compiled);

        internal static void Register(ToolRegistry registry, DebugWatcher watcher)
        {
            registry.Add(
                "break_on_exception",
                "Choisit les exceptions qui interrompent l'execution (fenetre Parametres d'exception). " +
                "'thrown' arrete des la levee, meme si un catch la traite ensuite : c'est ce qui permet de " +
                "trouver l'origine d'une exception avalee ou re-levee. 'user_unhandled' arrete quand elle " +
                "sort du code utilisateur sans etre traitee (exige Uniquement mon code). Sans 'exception', " +
                "liste celles qui arretent a la levee sans rien changer. Valable pour les sessions en cours " +
                "et a venir. Une fois arrete, get_exception decrit l'exception.",
                SchemaBuilder.New()
                    .Instance()
                    .Str("exception",
                        "Nom complet de l'exception (System.InvalidOperationException), ou nom court s'il est " +
                        "sans ambiguite. Plusieurs noms separes par des virgules. Un nom complet inconnu, " +
                        "comme une exception du projet, est ajoute au groupe.")
                    .Str("break_when", "Quand interrompre.",
                        allowed: new[] { "thrown", "user_unhandled", "never" }, defaultValue: "thrown")
                    .Str("group",
                        "Groupe d'exceptions, par une partie de son nom (\"C++\", \"Win32\"...). Par defaut, " +
                        "les exceptions .NET.")
                    .Bool("reset_all",
                        "Retablir les reglages par defaut de Visual Studio pour toutes les exceptions, tous " +
                        "groupes confondus, avant d'appliquer le reste.",
                        defaultValue: false)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var names = Args.Str(args, "exception");
                    var when = ParseWhen(Args.Str(args, "break_when", "thrown"));
                    var groupName = Args.Str(args, "group");

                    if (Args.Bool(args, "reset_all", false))
                    {
                        ExceptionBreaks.Groups().ResetAll();
                        ExtensionLog.Info("Parametres d'exception retablis par defaut.");
                    }

                    var group = ExceptionBreaks.ResolveGroup(groupName);
                    var result = new JObject { ["group"] = ExceptionBreaks.SafeName(group) };

                    if (names != null)
                    {
                        var applied = new JArray();
                        foreach (var raw in names.Split(','))
                        {
                            var name = raw.Trim();
                            if (name.Length == 0) continue;

                            var outcome = ExceptionBreaks.Apply(group, name, when);
                            var json = ExceptionBreaks.Describe(outcome.Setting);
                            if (outcome.Created)
                            {
                                json["created"] = true;
                                json["note"] = "Exception absente du groupe, ajoutee comme personnalisee : " +
                                               "verifier que le nom complet est exact, sinon elle n'arretera jamais.";
                            }
                            applied.Add(json);
                        }
                        result["applied"] = applied;
                    }

                    result["breakWhenThrown"] = ExceptionBreaks.ListThrown(group);
                    return result;
                }, ct));

            registry.Add(
                "get_exception",
                "Decrit l'exception sur laquelle le debogueur est arrete : type, message, HResult, " +
                "pile d'appels avec fichiers et lignes, chaine des InnerException et exceptions " +
                "agregees. Exige un arret sur exception (voir break_on_exception). Evalue des " +
                "proprietes de l'exception, donc du code du programme debogue.",
                SchemaBuilder.New()
                    .Instance()
                    .Int("max_inner", "Profondeur maximale de la chaine InnerException (0 a 10).", defaultValue: 5)
                    .Int("timeout_ms", "Delai d'evaluation de chaque propriete.", defaultValue: 5000)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var maxInner = Math.Min(Math.Max(Args.Int(args, "max_inner", 5), 0), MaxInnerCap);
                    var timeout = Math.Min(Math.Max(Args.Int(args, "timeout_ms", 5000), 100), 60000);

                    var debugger = DteProvider.RequireBreakMode();
                    var signaled = watcher.LastException;

                    var root = debugger.GetExpression("$exception", true, timeout);
                    var managed = root != null && root.IsValidValue && !IsNull(root.Value);

                    if (!managed && signaled == null)
                    {
                        throw new McpToolException("NO_EXCEPTION",
                            "Le debogueur n'est pas arrete sur une exception. Appeler break_on_exception " +
                            "puis relancer l'execution jusqu'a la levee.");
                    }

                    var result = new JObject();
                    if (signaled != null)
                    {
                        result["stoppedAt"] = signaled.Unhandled ? "unhandled" : "thrown";
                    }
                    else
                    {
                        // Les evenements d'exception d'EnvDTE ne sont pas toujours emis (Visual Studio
                        // 2026) : a defaut, la raison du dernier arret.
                        var reason = SafeReason(debugger);
                        if (reason == dbgEventReason.dbgEventReasonExceptionNotHandled) result["stoppedAt"] = "unhandled";
                        else if (reason == dbgEventReason.dbgEventReasonExceptionThrown) result["stoppedAt"] = "thrown";
                    }

                    if (!managed)
                    {
                        // Exception native ou SEH : pas d'objet a inspecter, seulement ce que le
                        // debogueur a annonce.
                        result["managed"] = false;
                        result["type"] = signaled.Name;
                        result["code"] = "0x" + signaled.Code.ToString("X8", CultureInfo.InvariantCulture);
                        result["description"] = signaled.Description;
                        result["hint"] = "Pour la position, debug_state ; pour la pile, get_stack.";
                        return result;
                    }

                    result["managed"] = true;

                    var exception = DescribeLevel(debugger, "$exception", root, timeout, true);
                    foreach (var property in exception.Properties()) result[property.Name] = property.Value;

                    var inner = new JArray();
                    var path = "$exception";
                    for (var level = 0; level < maxInner; level++)
                    {
                        path += ".InnerException";
                        var expression = debugger.GetExpression(path, true, timeout);
                        if (expression == null || !expression.IsValidValue || IsNull(expression.Value)) break;
                        inner.Add(DescribeLevel(debugger, path, expression, timeout, false));
                    }
                    if (inner.Count > 0) result["innerExceptions"] = inner;

                    return result;
                }, ct));
        }

        private static ExceptionBreaks.BreakWhen ParseWhen(string value)
        {
            switch (value.ToLowerInvariant())
            {
                case "thrown": return ExceptionBreaks.BreakWhen.Thrown;
                case "user_unhandled": return ExceptionBreaks.BreakWhen.UserUnhandled;
                case "never": return ExceptionBreaks.BreakWhen.Never;
                default:
                    throw McpToolException.InvalidArgs(
                        "break_when doit valoir 'thrown', 'user_unhandled' ou 'never'.");
            }
        }

        /// <summary>
        /// Une exception de la chaine. La pile complete n'est donnee que pour la premiere : les
        /// suivantes n'en gardent que l'origine, ce qui suffit a situer chaque maillon.
        /// </summary>
        private static JObject DescribeLevel(Debugger debugger, string path, Expression expression,
            int timeout, bool fullStack)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var type = RuntimeType(Safe(() => expression.Type));
            var result = new JObject
            {
                ["type"] = type,
                ["message"] = ReadString(debugger, path + ".Message", timeout)
            };

            var hresult = ReadInt(debugger, path + ".HResult", timeout);
            if (hresult.HasValue)
            {
                result["hresult"] = "0x" + hresult.Value.ToString("X8", CultureInfo.InvariantCulture);
            }

            var source = ReadString(debugger, path + ".Source", timeout);
            if (!string.IsNullOrEmpty(source)) result["source"] = source;

            var frames = ParseStackTrace(ReadString(debugger, path + ".StackTrace", timeout));
            var origin = FirstWithFile(frames);
            if (origin != null) result["origin"] = origin;
            if (fullStack && frames.Count > 0) result["stackTrace"] = frames;

            if (type.EndsWith("AggregateException", StringComparison.Ordinal))
            {
                var aggregated = DescribeAggregated(debugger, path, timeout);
                if (aggregated.Count > 0) result["aggregated"] = aggregated;
            }

            return result;
        }

        /// <summary>
        /// AggregateException : InnerException ne donne que la premiere, les autres ne sont
        /// visibles que dans InnerExceptions.
        /// </summary>
        private static JArray DescribeAggregated(Debugger debugger, string path, int timeout)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var list = new JArray();
            var count = ReadInt(debugger, path + ".InnerExceptions.Count", timeout) ?? 0;

            for (var i = 0; i < Math.Min(count, MaxAggregated); i++)
            {
                var itemPath = path + ".InnerExceptions[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                var item = debugger.GetExpression(itemPath, true, timeout);
                if (item == null || !item.IsValidValue) continue;

                var json = new JObject
                {
                    ["type"] = RuntimeType(Safe(() => item.Type)),
                    ["message"] = ReadString(debugger, itemPath + ".Message", timeout)
                };
                var origin = FirstWithFile(ParseStackTrace(ReadString(debugger, itemPath + ".StackTrace", timeout)));
                if (origin != null) json["origin"] = origin;
                list.Add(json);
            }

            if (count > MaxAggregated) list.Add(new JObject { ["truncated"] = count - MaxAggregated });
            return list;
        }

        private static JArray ParseStackTrace(string stackTrace)
        {
            var frames = new JArray();
            if (string.IsNullOrEmpty(stackTrace)) return frames;

            foreach (var raw in stackTrace.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0) continue;
                if (frames.Count >= MaxFrames)
                {
                    frames.Add(new JObject { ["truncated"] = true });
                    break;
                }

                var match = FrameLine.Match(line);
                if (!match.Success || line.TrimStart().StartsWith("---", StringComparison.Ordinal))
                {
                    // Separateurs "--- End of stack trace from previous location ---" et autres.
                    frames.Add(new JObject { ["text"] = line.Trim() });
                    continue;
                }

                var frame = new JObject { ["method"] = match.Groups["method"].Value };
                if (match.Groups["file"].Success)
                {
                    frame["file"] = match.Groups["file"].Value;
                    frame["line"] = int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture);
                }
                frames.Add(frame);
            }

            return frames;
        }

        /// <summary>Premiere frame situee dans un fichier : le point de levee dans le code du projet.</summary>
        private static JObject FirstWithFile(JArray frames)
        {
            foreach (var token in frames)
            {
                if (token is JObject frame && frame["file"] != null) return frame;
            }
            return null;
        }

        /// <summary>
        /// Le debogueur affiche "TypeDeclare {TypeReel}" quand les deux different : seul le type
        /// reel interesse.
        /// </summary>
        private static string RuntimeType(string type)
        {
            var open = type.IndexOf('{');
            var close = type.LastIndexOf('}');
            return open >= 0 && close > open ? type.Substring(open + 1, close - open - 1).Trim() : type.Trim();
        }

        private static string ReadString(Debugger debugger, string path, int timeout)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var expression = debugger.GetExpression(path, true, timeout);
                if (expression == null || !expression.IsValidValue) return null;
                var value = expression.Value;
                return IsNull(value) ? null : Unquote(value);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Lit un entier affiche en decimal ou, si l'affichage hexadecimal est actif, en hexa.</summary>
        private static int? ReadInt(Debugger debugger, string path, int timeout)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var expression = debugger.GetExpression(path, true, timeout);
                if (expression == null || !expression.IsValidValue) return null;

                var value = (expression.Value ?? string.Empty).Trim();
                if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                    uint.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                {
                    return unchecked((int)hex);
                }
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                    ? number
                    : (int?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsNull(string value)
        {
            return value == null || value.Trim() == "null";
        }

        private static dbgEventReason SafeReason(Debugger debugger)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            try { return debugger.LastBreakReason; }
            catch (Exception) { return dbgEventReason.dbgEventReasonNone; }
        }

        /// <summary>
        /// Une chaine evaluee arrive entre guillemets et echappee a la C# : on rend le texte brut.
        /// </summary>
        private static string Unquote(string value)
        {
            if (value.Length < 2 || value[0] != '"' || value[value.Length - 1] != '"') return value;

            var builder = new StringBuilder(value.Length);
            for (var i = 1; i < value.Length - 1; i++)
            {
                var c = value[i];
                if (c != '\\' || i + 1 >= value.Length - 1)
                {
                    builder.Append(c);
                    continue;
                }

                var next = value[++i];
                switch (next)
                {
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case '0': builder.Append('\0'); break;
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    default: builder.Append('\\').Append(next); break;
                }
            }
            return builder.ToString();
        }

        private static string Safe(Func<string> accessor)
        {
            try { return accessor() ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }
    }
}
