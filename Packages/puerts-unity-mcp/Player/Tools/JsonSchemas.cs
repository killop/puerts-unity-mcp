namespace PuertsUnityMcp
{
    public static class JsonSchemas
    {
        public static string Object(params string[] properties)
        {
            if (properties == null || properties.Length == 0)
            {
                return "{\"type\":\"object\",\"additionalProperties\":true}";
            }

            return "{\"type\":\"object\",\"additionalProperties\":true,\"properties\":{" + string.Join(",", properties) + "}}";
        }

        public static string StringProperty(string name, string description = null)
        {
            return Property(name, "string", description);
        }

        public static string NumberProperty(string name, string description = null)
        {
            return Property(name, "number", description);
        }

        public static string BooleanProperty(string name, string description = null)
        {
            return Property(name, "boolean", description);
        }

        private static string Property(string name, string type, string description)
        {
            return UnityJson.ToJsonStringValue(name) + ":{\"type\":" + UnityJson.ToJsonStringValue(type)
                + ",\"description\":" + UnityJson.ToJsonStringValue(description ?? string.Empty) + "}";
        }
    }
}
