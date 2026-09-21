using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Server
{
    /// <summary>
    /// Construction des schemas JSON d'entree des outils, pour eviter d'ecrire du JSON a la main.
    /// </summary>
    internal sealed class SchemaBuilder
    {
        private readonly JObject _properties = new JObject();
        private readonly JArray _required = new JArray();

        internal static SchemaBuilder New()
        {
            return new SchemaBuilder();
        }

        internal SchemaBuilder Str(string name, string description, bool required = false, string[] allowed = null, string defaultValue = null)
        {
            var prop = new JObject { ["type"] = "string", ["description"] = description };
            if (allowed != null) prop["enum"] = new JArray(allowed);
            if (defaultValue != null) prop["default"] = defaultValue;
            return Add(name, prop, required);
        }

        internal SchemaBuilder Int(string name, string description, bool required = false, int? defaultValue = null)
        {
            var prop = new JObject { ["type"] = "integer", ["description"] = description };
            if (defaultValue.HasValue) prop["default"] = defaultValue.Value;
            return Add(name, prop, required);
        }

        internal SchemaBuilder Bool(string name, string description, bool required = false, bool? defaultValue = null)
        {
            var prop = new JObject { ["type"] = "boolean", ["description"] = description };
            if (defaultValue.HasValue) prop["default"] = defaultValue.Value;
            return Add(name, prop, required);
        }

        /// <summary>
        /// Parametre de ciblage d'instance, commun a presque tous les outils.
        /// </summary>
        internal SchemaBuilder Instance()
        {
            return Str("instance",
                "Instance de Visual Studio a piloter : PID, ou nom/chemin de solution. " +
                "Facultatif si une seule instance est ouverte, ou si use_instance a deja ete appele.");
        }

        private SchemaBuilder Add(string name, JObject prop, bool required)
        {
            _properties[name] = prop;
            if (required) _required.Add(name);
            return this;
        }

        internal JObject Build()
        {
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = _properties
            };
            if (_required.Count > 0) schema["required"] = _required;
            // Les clients MCP tolerent mieux un schema permissif ; on ne met pas additionalProperties:false.
            return schema;
        }
    }
}
