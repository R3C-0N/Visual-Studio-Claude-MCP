using System;
using ClaudeCodeVsMcp.Infrastructure;
using ClaudeCodeVsMcp.Server;
using ClaudeCodeVsMcp.Vs;
using EnvDTE;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Tools
{
    /// <summary>
    /// Inspection de l'etat du programme debogue. Tous ces outils exigent le mode arret :
    /// l'evaluateur d'expressions n'existe pas pendant l'execution.
    /// </summary>
    internal static class InspectTools
    {
        private const int MaxDepth = 4;
        private const int MaxChildrenCap = 200;

        internal static void Register(ToolRegistry registry)
        {
            registry.Add(
                "evaluate",
                "Evalue une expression C# dans le contexte de la frame courante. Exige que le debogueur " +
                "soit arrete. Attention : evaluer peut avoir des effets de bord (appel de proprietes).",
                SchemaBuilder.New()
                    .Instance()
                    .Str("expression", "Expression a evaluer, par exemple 'order.Items.Count'.", required: true)
                    .Int("depth", "Profondeur d'expansion des membres (1 a 4).", defaultValue: 1)
                    .Int("max_children", "Nombre maximum de membres par niveau.", defaultValue: 50)
                    .Int("timeout_ms", "Delai d'evaluation cote debogueur.", defaultValue: 5000)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var expression = Args.RequireStr(args, "expression");
                    var depth = Clamp(Args.Int(args, "depth", 1), 1, MaxDepth);
                    var maxChildren = Clamp(Args.Int(args, "max_children", 50), 1, MaxChildrenCap);
                    var timeout = Clamp(Args.Int(args, "timeout_ms", 5000), 100, 60000);

                    var debugger = DteProvider.RequireBreakMode();
                    var evaluated = debugger.GetExpression(expression, true, timeout);

                    if (evaluated == null)
                    {
                        throw new McpToolException("EVAL_FAILED", "Le debogueur n'a rien renvoye pour : " + expression);
                    }

                    var result = Describe(evaluated, depth, maxChildren);
                    result["expression"] = expression;

                    if (!evaluated.IsValidValue)
                    {
                        // Valeur invalide = expression non compilable ou hors portee. Le message du
                        // debogueur est dans Value : le remonter tel quel aide a corriger l'appel.
                        result["hint"] = "Expression invalide dans ce contexte. Verifier la portee avec get_locals.";
                    }

                    return result;
                }, ct));

            registry.Add(
                "get_locals",
                "Variables locales et arguments de la frame courante. Exige que le debogueur soit arrete.",
                SchemaBuilder.New()
                    .Instance()
                    .Int("frame_index", "Index de la frame dans la pile (0 = frame courante).", defaultValue: 0)
                    .Bool("include_arguments", "Inclure les arguments de la fonction.", defaultValue: true)
                    .Int("depth", "Profondeur d'expansion (1 a 4). Couteux au-dela de 2.", defaultValue: 1)
                    .Int("max_children", "Nombre maximum de membres par niveau.", defaultValue: 50)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var frameIndex = Math.Max(0, Args.Int(args, "frame_index", 0));
                    var includeArguments = Args.Bool(args, "include_arguments", true);
                    var depth = Clamp(Args.Int(args, "depth", 1), 1, MaxDepth);
                    var maxChildren = Clamp(Args.Int(args, "max_children", 50), 1, MaxChildrenCap);

                    var debugger = DteProvider.RequireBreakMode();
                    var frame = SelectFrame(debugger, frameIndex);

                    var locals = new JArray();
                    foreach (Expression local in frame.Locals)
                    {
                        if (locals.Count >= maxChildren) break;
                        locals.Add(Describe(local, depth, maxChildren));
                    }

                    var result = new JObject
                    {
                        ["frame"] = new JObject
                        {
                            ["index"] = frameIndex,
                            ["function"] = Safe(() => frame.FunctionName),
                            ["module"] = Safe(() => frame.Module)
                        },
                        ["locals"] = locals
                    };

                    if (includeArguments)
                    {
                        var arguments = new JArray();
                        foreach (Expression argument in frame.Arguments)
                        {
                            if (arguments.Count >= maxChildren) break;
                            arguments.Add(Describe(argument, depth, maxChildren));
                        }
                        result["arguments"] = arguments;
                    }

                    return result;
                }, ct));

            registry.Add(
                "get_stack",
                "Pile d'appels du thread courant. Exige que le debogueur soit arrete. " +
                "Ne contient ni fichier ni numero de ligne : EnvDTE ne les expose pas sur une frame.",
                SchemaBuilder.New()
                    .Instance()
                    .Int("max_frames", "Nombre maximum de frames retournees.", defaultValue: 30)
                    .Build(),
                (args, ctx, ct) => UiThread.RunAsync(() =>
                {
                    var maxFrames = Clamp(Args.Int(args, "max_frames", 30), 1, 200);
                    var debugger = DteProvider.RequireBreakMode();

                    var frames = new JArray();
                    var index = 0;

                    foreach (StackFrame frame in debugger.CurrentThread.StackFrames)
                    {
                        if (frames.Count >= maxFrames) break;

                        frames.Add(new JObject
                        {
                            ["index"] = index++,
                            ["function"] = Safe(() => frame.FunctionName),
                            ["module"] = Safe(() => frame.Module),
                            ["language"] = Safe(() => frame.Language),
                            ["returnType"] = Safe(() => frame.ReturnType)
                        });
                    }

                    return new JObject
                    {
                        ["thread"] = new JObject
                        {
                            ["id"] = SafeInt(() => debugger.CurrentThread.ID),
                            ["name"] = Safe(() => debugger.CurrentThread.Name)
                        },
                        ["frameCount"] = frames.Count,
                        ["frames"] = frames,
                        ["note"] = "Pour la position source courante, utiliser debug_state."
                    };
                }, ct));
        }

        /// <summary>
        /// Selectionne une frame de la pile. Changer de frame modifie CurrentStackFrame, ce qui est
        /// visible dans l'IDE (navigation de l'editeur) : effet de bord assume, faute d'API sans etat.
        /// </summary>
        private static StackFrame SelectFrame(Debugger debugger, int frameIndex)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            if (frameIndex == 0) return debugger.CurrentStackFrame;

            var index = 0;
            foreach (StackFrame frame in debugger.CurrentThread.StackFrames)
            {
                if (index++ != frameIndex) continue;
                debugger.CurrentStackFrame = frame;
                return frame;
            }

            throw McpToolException.NotFound(
                "La pile ne contient pas de frame d'index " + frameIndex + ". Appeler get_stack.");
        }

        /// <summary>
        /// Serialise une expression, en bornant strictement profondeur et nombre de membres :
        /// un graphe d'objets peut etre cyclique ou enorme, et chaque acces traverse le pont COM.
        /// </summary>
        private static JObject Describe(Expression expression, int depth, int maxChildren)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var result = new JObject
            {
                ["name"] = Safe(() => expression.Name),
                ["value"] = Safe(() => expression.Value),
                ["type"] = Safe(() => expression.Type),
                ["isValid"] = SafeBool(() => expression.IsValidValue)
            };

            if (depth <= 1)
            {
                result["hasMembers"] = SafeBool(() => expression.DataMembers != null && expression.DataMembers.Count > 0);
                return result;
            }

            try
            {
                var members = expression.DataMembers;
                if (members == null || members.Count == 0) return result;

                var children = new JArray();
                var count = 0;

                foreach (Expression member in members)
                {
                    if (count++ >= maxChildren)
                    {
                        result["membersTruncated"] = true;
                        break;
                    }
                    children.Add(Describe(member, depth - 1, maxChildren));
                }

                result["members"] = children;
            }
            catch (Exception)
            {
                // Certaines valeurs refusent l'expansion (proprietes qui levent, objets natifs).
                result["membersUnavailable"] = true;
            }

            return result;
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Min(Math.Max(value, min), max);
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
