using System;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Vs
{
    /// <summary>
    /// Description des frames d'une pile d'appels. StackFrame2 (EnvDTE90a) porte fichier, ligne et
    /// UserCode ; une frame sans symboles renvoie un fichier vide et la ligne 0.
    /// </summary>
    internal static class StackHelper
    {
        /// <summary>
        /// Frames d'une pile. L'index reste celui de la pile reelle, meme quand on filtre : c'est
        /// lui que get_locals attend.
        /// </summary>
        internal static JArray DescribeFrames(StackFrames frames, int maxFrames, bool userCodeOnly)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var result = new JArray();
            if (frames == null) return result;

            var index = 0;
            foreach (StackFrame frame in frames)
            {
                if (result.Count >= maxFrames) break;

                var frameIndex = index++;
                var described = Describe(frame, frameIndex);

                // Les annotations ("[Deadlocked...]") restent : elles disent pourquoi le thread attend.
                if (userCodeOnly && !(bool)described["userCode"] && described["annotation"] == null) continue;

                result.Add(described);
            }
            return result;
        }

        internal static JObject Describe(StackFrame frame, int index)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var frame2 = frame as EnvDTE90a.StackFrame2;
            var function = Safe(() => frame.FunctionName);
            var module = Safe(() => frame.Module);

            // Frame synthetique ajoutee par Visual Studio ("[Deadlocked, ...]", "[Waiting on lock
            // owned by Thread 0x...]", "[External Code]") : ni code, ni module.
            if (function.StartsWith("[", StringComparison.Ordinal) && module.Length == 0)
            {
                return new JObject
                {
                    ["index"] = index,
                    ["function"] = function,
                    ["annotation"] = true,
                    ["userCode"] = false
                };
            }

            var file = frame2 == null ? string.Empty : Safe(() => frame2.FileName);
            var line = frame2 == null ? 0 : SafeInt(() => (int)frame2.LineNumber);

            return new JObject
            {
                ["index"] = index,
                ["function"] = function,
                ["file"] = file.Length > 0 ? (JToken)file : null,
                ["line"] = line > 0 ? (JToken)line : null,
                ["userCode"] = frame2 != null && SafeBool(() => frame2.UserCode),
                ["module"] = module,
                ["language"] = Safe(() => frame.Language),
                ["returnType"] = Safe(() => frame.ReturnType)
            };
        }

        /// <summary>Premiere frame du code utilisateur qui porte un fichier, ou null.</summary>
        internal static JObject FirstUserFrame(StackFrames frames, int maxScan)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (frames == null) return null;

            var index = 0;
            foreach (StackFrame frame in frames)
            {
                if (index >= maxScan) break;

                var described = Describe(frame, index++);
                if ((bool)described["userCode"] && described["file"] != null && described["file"].Type != JTokenType.Null)
                {
                    return described;
                }
            }
            return null;
        }

        /// <summary>Position exacte de la frame courante, ou null sans symboles.</summary>
        internal static JObject CurrentPosition(Debugger debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            EnvDTE90a.StackFrame2 frame2;
            try { frame2 = debugger.CurrentStackFrame as EnvDTE90a.StackFrame2; }
            catch (Exception) { return null; }
            if (frame2 == null) return null;

            var file = Safe(() => frame2.FileName);
            var line = SafeInt(() => (int)frame2.LineNumber);
            if (file.Length == 0 || line <= 0) return null;

            return new JObject
            {
                ["file"] = file,
                ["line"] = line,
                ["approximate"] = false
            };
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
