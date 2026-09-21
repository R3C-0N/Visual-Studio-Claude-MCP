using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClaudeCodeVsMcp.Infrastructure;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Discovery
{
    /// <summary>
    /// Registre des instances de Visual Studio, materialise par un fichier JSON par processus
    /// dans %TEMP%\claude-vs-mcp\. C'est ce qui permet a une instance d'enumerer ses voisines
    /// et de relayer un appel vers la bonne.
    /// </summary>
    internal static class InstanceRegistry
    {
        /// <summary>Au-dela, une entree est consideree morte meme si le PID existe encore.</summary>
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

        internal static string Directory
        {
            get { return Path.Combine(Path.GetTempPath(), "claude-vs-mcp"); }
        }

        private static string FileFor(int pid)
        {
            return Path.Combine(Directory, "instance-" + pid + ".json");
        }

        /// <summary>Ecriture atomique : fichier temporaire puis remplacement, pour ne jamais exposer un JSON tronque.</summary>
        internal static void Write(InstanceInfo info)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var target = FileFor(info.Pid);
                var temp = target + ".tmp";
                File.WriteAllText(temp, info.ToJson().ToString(Newtonsoft.Json.Formatting.Indented));

                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
            }
            catch (Exception ex)
            {
                ExtensionLog.Error("Ecriture du fichier de decouverte impossible.", ex);
            }
        }

        internal static void Remove(int pid)
        {
            try
            {
                var file = FileFor(pid);
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex)
            {
                ExtensionLog.Warn("Suppression du fichier de decouverte impossible : " + ex.Message);
            }
        }

        /// <summary>
        /// Enumere les instances vivantes et purge au passage les entrees orphelines
        /// (devenv qui a plante sans passer par Dispose).
        /// </summary>
        internal static List<InstanceInfo> ReadAll()
        {
            var result = new List<InstanceInfo>();
            if (!System.IO.Directory.Exists(Directory)) return result;

            foreach (var file in System.IO.Directory.GetFiles(Directory, "instance-*.json"))
            {
                InstanceInfo info = null;
                try
                {
                    info = InstanceInfo.FromJson(JObject.Parse(File.ReadAllText(file)));
                }
                catch (Exception)
                {
                    // JSON illisible : traite comme une entree morte.
                }

                if (info == null || !IsAlive(info))
                {
                    TryDelete(file);
                    continue;
                }

                result.Add(info);
            }

            return result.OrderBy(i => i.StartedUtc).ToList();
        }

        private static bool IsAlive(InstanceInfo info)
        {
            if (info.Pid <= 0 || info.Port <= 0) return false;

            // Une entree dont le heartbeat est fige signale un processus mort dont le PID a ete recycle.
            if (info.HeartbeatUtc != DateTime.MinValue &&
                DateTime.UtcNow - info.HeartbeatUtc > StaleAfter)
            {
                return false;
            }

            try
            {
                var process = Process.GetProcessById(info.Pid);

                // Trois criteres cumules, car Windows recycle les PID : le processus existe,
                // c'est bien un devenv, et c'est bien CELUI qui a ecrit l'entree.
                if (!string.Equals(process.ProcessName, "devenv", StringComparison.OrdinalIgnoreCase))
                    return false;

                if (info.ProcessStartUtc != DateTime.MinValue)
                {
                    var actualStart = process.StartTime.ToUniversalTime();
                    if (Math.Abs((actualStart - info.ProcessStartUtc).TotalSeconds) > 5) return false;
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void TryDelete(string file)
        {
            try { File.Delete(file); } catch (Exception) { /* concurrence benigne */ }
        }
    }
}
