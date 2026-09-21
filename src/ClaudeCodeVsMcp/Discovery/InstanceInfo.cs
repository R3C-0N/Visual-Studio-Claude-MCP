using System;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Discovery
{
    /// <summary>Description d'une instance de Visual Studio exposant le serveur MCP.</summary>
    internal sealed class InstanceInfo
    {
        internal int Pid { get; set; }
        internal int Port { get; set; }
        internal bool IsHub { get; set; }
        internal string VsVersion { get; set; }
        internal string SolutionPath { get; set; }
        internal string SolutionName { get; set; }
        internal DateTime StartedUtc { get; set; }

        /// <summary>Date de demarrage de devenv.exe : discrimine un PID recycle par un autre devenv.</summary>
        internal DateTime ProcessStartUtc { get; set; }
        internal DateTime HeartbeatUtc { get; set; }

        internal string Url
        {
            get { return "http://127.0.0.1:" + Port + "/mcp"; }
        }

        internal JObject ToJson()
        {
            return new JObject
            {
                ["schema"] = 1,
                ["pid"] = Pid,
                ["port"] = Port,
                ["url"] = Url,
                ["isHub"] = IsHub,
                ["vsVersion"] = VsVersion ?? string.Empty,
                ["solutionPath"] = SolutionPath ?? string.Empty,
                ["solutionName"] = SolutionName ?? string.Empty,
                ["startedUtc"] = StartedUtc.ToString("o"),
                ["processStartUtc"] = ProcessStartUtc.ToString("o"),
                ["heartbeatUtc"] = HeartbeatUtc.ToString("o")
            };
        }

        internal static InstanceInfo FromJson(JObject o)
        {
            return new InstanceInfo
            {
                Pid = (int?)o["pid"] ?? 0,
                Port = (int?)o["port"] ?? 0,
                IsHub = (bool?)o["isHub"] ?? false,
                VsVersion = (string)o["vsVersion"],
                SolutionPath = (string)o["solutionPath"],
                SolutionName = (string)o["solutionName"],
                StartedUtc = ParseDate(o["startedUtc"]),
                ProcessStartUtc = ParseDate(o["processStartUtc"]),
                HeartbeatUtc = ParseDate(o["heartbeatUtc"])
            };
        }

        private static DateTime ParseDate(JToken token)
        {
            DateTime value;
            var text = (string)token;
            return DateTime.TryParse(text, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out value)
                ? value
                : DateTime.MinValue;
        }
    }
}
