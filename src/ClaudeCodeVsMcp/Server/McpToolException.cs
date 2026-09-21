using System;

namespace ClaudeCodeVsMcp.Server
{
    /// <summary>
    /// Erreur *applicative* d'un outil (pas de solution ouverte, debogueur hors mode Break, ...).
    /// Elle est renvoyee au client en isError:true avec un code lisible, et non en erreur JSON-RPC :
    /// c'est ce format que le modele sait exploiter pour se corriger tout seul.
    /// </summary>
    internal sealed class McpToolException : Exception
    {
        internal string Code { get; }

        internal McpToolException(string code, string message) : base(message)
        {
            Code = code;
        }

        internal static McpToolException NoSolution()
        {
            return new McpToolException("NO_SOLUTION",
                "Aucune solution n'est ouverte dans cette instance de Visual Studio.");
        }

        internal static McpToolException NotInBreakMode(string current)
        {
            return new McpToolException("NOT_IN_BREAK_MODE",
                "Cette operation exige que le debogueur soit arrete sur un point d'arret. Mode actuel : " + current + ".");
        }

        internal static McpToolException NotFound(string what)
        {
            return new McpToolException("NOT_FOUND", what);
        }

        internal static McpToolException InvalidArgs(string message)
        {
            return new McpToolException("INVALID_ARGS", message);
        }
    }
}
