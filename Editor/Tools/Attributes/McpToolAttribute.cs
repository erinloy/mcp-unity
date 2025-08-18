using System;

namespace McpUnity.Tools.Attributes
{
    /// <summary>
    /// Attribute to mark methods as MCP tools that should be automatically discovered and registered.
    /// Decorated methods will be exposed as tools in the MCP Unity server.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class McpToolAttribute : Attribute
    {
        /// <summary>
        /// The name of the tool as used in MCP API calls
        /// </summary>
        public string Name { get; }
        
        /// <summary>
        /// Description of the tool's functionality
        /// </summary>
        public string Description { get; set; }
        
        /// <summary>
        /// Whether the tool should execute asynchronously on the Unity main thread
        /// </summary>
        public bool IsAsync { get; set; } = false;
        
        /// <summary>
        /// Category for organizing tools (optional)
        /// </summary>
        public string Category { get; set; } = "General";
        
        /// <summary>
        /// Creates a new McpTool attribute
        /// </summary>
        /// <param name="name">The name of the tool as used in MCP API calls</param>
        public McpToolAttribute(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }
    }
    
    /// <summary>
    /// Attribute to mark parameters for MCP tools, providing metadata for parameter validation and documentation
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    public class McpParameterAttribute : Attribute
    {
        /// <summary>
        /// Description of what this parameter does
        /// </summary>
        public string Description { get; set; }
        
        /// <summary>
        /// Whether this parameter is required
        /// </summary>
        public bool Required { get; set; } = true;
        
        /// <summary>
        /// Default value for the parameter if not provided
        /// </summary>
        public object DefaultValue { get; set; }
        
        /// <summary>
        /// Name to use in the JSON parameters (if different from parameter name)
        /// </summary>
        public string JsonName { get; set; }
    }
}