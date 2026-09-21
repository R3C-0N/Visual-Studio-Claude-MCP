using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Server
{
    internal sealed class ToolDefinition
    {
        internal string Name { get; set; }
        internal string Description { get; set; }
        internal JObject InputSchema { get; set; }

        /// <summary>
        /// Un outil global s'execute toujours sur l'instance qui recoit la requete et n'est
        /// jamais relaye vers une autre instance (list_instances, use_instance, ping).
        /// </summary>
        internal bool IsGlobal { get; set; }

        internal Func<JObject, RequestContext, CancellationToken, Task<JObject>> Handler { get; set; }
    }

    internal sealed class ToolRegistry
    {
        private readonly Dictionary<string, ToolDefinition> _tools =
            new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);

        internal void Add(string name, string description, JObject inputSchema,
            Func<JObject, RequestContext, CancellationToken, Task<JObject>> handler, bool isGlobal = false)
        {
            _tools[name] = new ToolDefinition
            {
                Name = name,
                Description = description,
                InputSchema = inputSchema,
                Handler = handler,
                IsGlobal = isGlobal
            };
        }

        internal ToolDefinition Find(string name)
        {
            ToolDefinition tool;
            return _tools.TryGetValue(name ?? string.Empty, out tool) ? tool : null;
        }

        internal JArray Describe()
        {
            var array = new JArray();
            foreach (var tool in _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                array.Add(new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["inputSchema"] = tool.InputSchema
                });
            }
            return array;
        }
    }
}
