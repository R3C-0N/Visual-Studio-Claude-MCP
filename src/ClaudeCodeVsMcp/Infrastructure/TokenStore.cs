using System;
using System.IO;
using System.Security.Cryptography;

namespace ClaudeCodeVsMcp.Infrastructure
{
    /// <summary>
    /// Jeton partage par TOUTES les instances de Visual Studio de l'utilisateur.
    ///
    /// Un jeton unique (et non un par instance) est un choix delibere : la commande
    /// `claude mcp add` reste valable quelle que soit l'instance qui joue le role de hub,
    /// et le relais entre instances n'a pas a reinjecter un jeton different.
    /// </summary>
    internal static class TokenStore
    {
        private static readonly object Gate = new object();
        private static string _cached;

        internal static string Directory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "claude-vs-mcp");
            }
        }

        internal static string FilePath
        {
            get { return Path.Combine(Directory, "token"); }
        }

        internal static string GetOrCreate()
        {
            lock (Gate)
            {
                if (_cached != null) return _cached;

                try
                {
                    if (File.Exists(FilePath))
                    {
                        var existing = File.ReadAllText(FilePath).Trim();
                        if (existing.Length >= 16)
                        {
                            _cached = existing;
                            return _cached;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ExtensionLog.Warn("Lecture du jeton impossible, regeneration : " + ex.Message);
                }

                _cached = Generate();

                try
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    File.WriteAllText(FilePath, _cached);
                }
                catch (Exception ex)
                {
                    // Le jeton reste valable pour la duree de vie du processus, mais devra
                    // etre relu depuis le pane de sortie a chaque redemarrage.
                    ExtensionLog.Error("Persistance du jeton impossible.", ex);
                }

                return _cached;
            }
        }

        private static string Generate()
        {
            var bytes = new byte[32];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(bytes);
            }
            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        /// <summary>
        /// Comparaison a temps constant : un == classique court-circuite au premier octet
        /// different et fuit la longueur du prefixe correct.
        /// </summary>
        internal static bool Matches(string candidate)
        {
            var expected = GetOrCreate();
            if (candidate == null) return false;
            if (candidate.Length != expected.Length) return false;

            var diff = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                diff |= expected[i] ^ candidate[i];
            }
            return diff == 0;
        }
    }
}
