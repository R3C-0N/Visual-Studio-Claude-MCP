using System;
using System.Collections.Generic;
using System.IO;
using ClaudeCodeVsMcp.Infrastructure;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Ide
{
    /// <summary>
    /// Fichier de decouverte lu par Claude Code pour trouver les IDE joignables.
    ///
    /// Le format reproduit celui de l'extension Visual Studio Code officielle :
    /// ~/.claude/ide/&lt;port&gt;.lock contenant pid, workspaceFolders, ideName, transport,
    /// runningInWindows et authToken.
    ///
    /// C'est un protocole INTERNE a Claude Code, non documente publiquement. Il peut changer
    /// sans preavis d'une version a l'autre : le pont HTTP reste le chemin nominal, celui-ci
    /// n'ajoute que le confort du push.
    /// </summary>
    internal static class IdeLockFile
    {
        internal static string Directory
        {
            get
            {
                // Claude Code autorise le deplacement de son repertoire de configuration.
                var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                var root = string.IsNullOrEmpty(configured)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                    : configured;

                return Path.Combine(root, "ide");
            }
        }

        private static string PathFor(int port)
        {
            return Path.Combine(Directory, port + ".lock");
        }

        internal static void Write(int port, string authToken, int pid, string ideName, IEnumerable<string> workspaceFolders)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                var payload = new JObject
                {
                    ["pid"] = pid,
                    ["workspaceFolders"] = new JArray(workspaceFolders ?? new string[0]),
                    ["ideName"] = ideName,
                    ["transport"] = "ws",
                    ["runningInWindows"] = true,
                    ["authToken"] = authToken
                };

                File.WriteAllText(PathFor(port), payload.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex)
            {
                ExtensionLog.Error("Ecriture du fichier de decouverte IDE impossible.", ex);
            }
        }

        /// <summary>
        /// Supprime les lockfiles laisses par des devenv disparus. Sans cela, /ide propose des
        /// instances mortes et Claude Code tente de s'y connecter.
        /// </summary>
        internal static void PurgeStale()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory)) return;

                foreach (var file in System.IO.Directory.GetFiles(Directory, "*.lock"))
                {
                    int pid;
                    try
                    {
                        var content = JObject.Parse(File.ReadAllText(file));
                        pid = (int?)content["pid"] ?? 0;
                        // On ne touche qu'aux entrees ecrites par cette extension.
                        if ((string)content["ideName"] != "Visual Studio") continue;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (pid <= 0 || IsDead(pid))
                    {
                        try { File.Delete(file); } catch (Exception) { }
                    }
                }
            }
            catch (Exception ex)
            {
                ExtensionLog.Warn("Purge des lockfiles IDE impossible : " + ex.Message);
            }
        }

        private static bool IsDead(int pid)
        {
            try
            {
                var process = System.Diagnostics.Process.GetProcessById(pid);
                return !string.Equals(process.ProcessName, "devenv", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return true;
            }
        }

        internal static void Remove(int port)
        {
            try
            {
                var file = PathFor(port);
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex)
            {
                ExtensionLog.Warn("Suppression du fichier de decouverte IDE impossible : " + ex.Message);
            }
        }
    }
}
