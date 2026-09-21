using Newtonsoft.Json.Linq;

namespace ClaudeCodeVsMcp.Server
{
    internal static class JsonRpcErrors
    {
        internal const int ParseError = -32700;
        internal const int InvalidRequest = -32600;
        internal const int MethodNotFound = -32601;
        internal const int InvalidParams = -32602;
        internal const int InternalError = -32603;
        internal const int Unauthorized = -32001;
    }

    /// <summary>Une requete ou notification JSON-RPC 2.0 entrante.</summary>
    internal sealed class JsonRpcRequest
    {
        internal JToken Id { get; private set; }
        internal string Method { get; private set; }
        internal JObject Params { get; private set; }

        /// <summary>Une notification n'a pas d'id : elle ne doit jamais recevoir de reponse.</summary>
        internal bool IsNotification
        {
            get { return Id == null || Id.Type == JTokenType.Null; }
        }

        internal static JsonRpcRequest Parse(JObject obj)
        {
            return new JsonRpcRequest
            {
                Id = obj["id"],
                Method = (string)obj["method"],
                Params = obj["params"] as JObject ?? new JObject()
            };
        }
    }

    internal static class JsonRpcResponse
    {
        internal static JObject Success(JToken id, JToken result)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["result"] = result ?? new JObject()
            };
        }

        internal static JObject Error(JToken id, int code, string message, JToken data = null)
        {
            var err = new JObject
            {
                ["code"] = code,
                ["message"] = message
            };
            if (data != null) err["data"] = data;

            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = err
            };
        }
    }
}
