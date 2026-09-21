using System;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Server
{
    /// <summary>Lecture tolerante des arguments d'outil.</summary>
    internal static class Args
    {
        internal static string Str(JObject args, string name, string fallback = null)
        {
            var token = args?[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            var value = token.Type == JTokenType.String ? (string)token : token.ToString();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        internal static string RequireStr(JObject args, string name)
        {
            var value = Str(args, name);
            if (value == null)
                throw McpToolException.InvalidArgs("Le parametre requis '" + name + "' est absent.");
            return value;
        }

        internal static int Int(JObject args, string name, int fallback)
        {
            var token = args?[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try { return (int)token; }
            catch (Exception)
            {
                throw McpToolException.InvalidArgs("Le parametre '" + name + "' doit etre un entier.");
            }
        }

        internal static int RequireInt(JObject args, string name)
        {
            if (args?[name] == null || args[name].Type == JTokenType.Null)
                throw McpToolException.InvalidArgs("Le parametre requis '" + name + "' est absent.");
            return Int(args, name, 0);
        }

        internal static bool Bool(JObject args, string name, bool fallback)
        {
            var token = args?[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try { return (bool)token; }
            catch (Exception)
            {
                throw McpToolException.InvalidArgs("Le parametre '" + name + "' doit etre un booleen.");
            }
        }
    }
}
