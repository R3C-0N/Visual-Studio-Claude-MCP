using System.Reflection;
using System.Runtime.InteropServices;

// AssemblyVersion et AssemblyFileVersion ne sont PAS ici : elles sont generees au build
// depuis source.extension.vsixmanifest, seule source de verite de la version.
[assembly: AssemblyTitle("Claude Code MCP Bridge")]
[assembly: AssemblyDescription("Serveur MCP local exposant Visual Studio a Claude Code.")]
[assembly: AssemblyProduct("ClaudeCodeVsMcp")]
[assembly: ComVisible(false)]
