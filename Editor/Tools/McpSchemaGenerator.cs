using System;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Generates basic MCP InputSchema for tools
    /// </summary>
    public static class McpSchemaGenerator
    {
        /// <summary>
        /// Generate a basic InputSchema for tools that don't define their own
        /// </summary>
        public static JObject GenerateSchema(Type toolType)
        {
            // Return a basic schema structure
            // Tools should override InputSchema in their constructor for specific schemas
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject(),
                ["additionalProperties"] = true
            };
            
            return schema;
        }
    }
}