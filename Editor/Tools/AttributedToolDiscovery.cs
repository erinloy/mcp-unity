using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using McpUnity.Tools.Attributes;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    /// <summary>
    /// Discovers and registers tools from methods decorated with McpTool attributes
    /// </summary>
    public static class AttributedToolDiscovery
    {
        /// <summary>
        /// Discovers all methods decorated with McpTool attributes in loaded assemblies
        /// </summary>
        /// <returns>Collection of AttributedMethodTool instances</returns>
        public static IEnumerable<AttributedMethodTool> DiscoverAttributedTools()
        {
            var tools = new List<AttributedMethodTool>();
            
            try
            {
                // Get all loaded assemblies in the current domain
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                
                foreach (var assembly in assemblies)
                {
                    try
                    {
                        // Skip system assemblies to improve performance
                        if (IsSystemAssembly(assembly))
                            continue;
                            
                        var discoveredTools = DiscoverToolsInAssembly(assembly);
                        tools.AddRange(discoveredTools);
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogWarning($"[AttributedToolDiscovery] Failed to scan assembly {assembly.FullName}: {ex.Message}");
                    }
                }
                
                McpLogger.LogInfo($"[AttributedToolDiscovery] Discovered {tools.Count} attributed tools across {assemblies.Length} assemblies");
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"[AttributedToolDiscovery] Failed to discover attributed tools: {ex.Message}");
            }
            
            return tools;
        }
        
        /// <summary>
        /// Discovers tools in a specific assembly
        /// </summary>
        /// <param name="assembly">Assembly to scan</param>
        /// <returns>Collection of AttributedMethodTool instances from the assembly</returns>
        public static IEnumerable<AttributedMethodTool> DiscoverToolsInAssembly(Assembly assembly)
        {
            var tools = new List<AttributedMethodTool>();
            
            try
            {
                // Get all types in the assembly
                var types = assembly.GetTypes();
                
                foreach (var type in types)
                {
                    try
                    {
                        var typeMethods = DiscoverToolsInType(type);
                        tools.AddRange(typeMethods);
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogWarning($"[AttributedToolDiscovery] Failed to scan type {type.FullName}: {ex.Message}");
                    }
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                McpLogger.LogWarning($"[AttributedToolDiscovery] Type load exception in assembly {assembly.FullName}: {ex.Message}");
                
                // Try to process the types that did load successfully
                var loadedTypes = ex.Types.Where(t => t != null);
                foreach (var type in loadedTypes)
                {
                    try
                    {
                        var typeMethods = DiscoverToolsInType(type);
                        tools.AddRange(typeMethods);
                    }
                    catch (Exception typeEx)
                    {
                        McpLogger.LogWarning($"[AttributedToolDiscovery] Failed to scan loaded type {type?.FullName}: {typeEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogWarning($"[AttributedToolDiscovery] Failed to get types from assembly {assembly.FullName}: {ex.Message}");
            }
            
            return tools;
        }
        
        /// <summary>
        /// Discovers tools in a specific type
        /// </summary>
        /// <param name="type">Type to scan for attributed methods</param>
        /// <returns>Collection of AttributedMethodTool instances from the type</returns>
        public static IEnumerable<AttributedMethodTool> DiscoverToolsInType(Type type)
        {
            var tools = new List<AttributedMethodTool>();
            
            try
            {
                // Get all methods in the type (public and non-public, static and instance)
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                
                foreach (var method in methods)
                {
                    try
                    {
                        var attribute = method.GetCustomAttribute<McpToolAttribute>();
                        if (attribute != null)
                        {
                            var tool = CreateAttributedTool(method, attribute);
                            if (tool != null)
                            {
                                tools.Add(tool);
                                McpLogger.LogInfo($"[AttributedToolDiscovery] Discovered tool '{attribute.Name}' from {type.FullName}.{method.Name}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogWarning($"[AttributedToolDiscovery] Failed to process method {type.FullName}.{method.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogWarning($"[AttributedToolDiscovery] Failed to get methods from type {type.FullName}: {ex.Message}");
            }
            
            return tools;
        }
        
        /// <summary>
        /// Creates an AttributedMethodTool from a method and its attribute
        /// </summary>
        /// <param name="method">The method to wrap</param>
        /// <param name="attribute">The McpTool attribute</param>
        /// <returns>AttributedMethodTool instance, or null if creation failed</returns>
        private static AttributedMethodTool CreateAttributedTool(MethodInfo method, McpToolAttribute attribute)
        {
            try
            {
                // Validate the method signature
                if (!ValidateMethodSignature(method))
                {
                    McpLogger.LogWarning($"[AttributedToolDiscovery] Method {method.DeclaringType?.FullName}.{method.Name} has invalid signature for MCP tool");
                    return null;
                }
                
                // For static methods, instance is null
                object instance = null;
                
                // For instance methods, we need to create an instance
                if (!method.IsStatic)
                {
                    try
                    {
                        // Try to create instance using parameterless constructor
                        instance = Activator.CreateInstance(method.DeclaringType);
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogWarning($"[AttributedToolDiscovery] Cannot create instance of {method.DeclaringType?.FullName} for method {method.Name}: {ex.Message}");
                        return null;
                    }
                }
                
                return new AttributedMethodTool(method, instance, attribute);
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"[AttributedToolDiscovery] Failed to create tool for method {method.DeclaringType?.FullName}.{method.Name}: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Validates that a method has a compatible signature for MCP tools
        /// </summary>
        /// <param name="method">Method to validate</param>
        /// <returns>True if method signature is valid</returns>
        private static bool ValidateMethodSignature(MethodInfo method)
        {
            // Method must be public
            if (!method.IsPublic)
            {
                return false;
            }
            
            // Return type should be compatible with JSON serialization
            // Allow: void, basic types, JObject, custom objects
            var returnType = method.ReturnType;
            if (!IsJsonCompatibleType(returnType))
            {
                McpLogger.LogWarning($"[AttributedToolDiscovery] Method {method.DeclaringType?.FullName}.{method.Name} has unsupported return type: {returnType.FullName}");
                return false;
            }
            
            // Parameters should be JSON-compatible
            var parameters = method.GetParameters();
            foreach (var param in parameters)
            {
                if (!IsJsonCompatibleType(param.ParameterType))
                {
                    McpLogger.LogWarning($"[AttributedToolDiscovery] Method {method.DeclaringType?.FullName}.{method.Name} has unsupported parameter type: {param.ParameterType.FullName}");
                    return false;
                }
            }
            
            return true;
        }
        
        /// <summary>
        /// Checks if a type is compatible with JSON serialization
        /// </summary>
        /// <param name="type">Type to check</param>
        /// <returns>True if type can be serialized/deserialized from JSON</returns>
        private static bool IsJsonCompatibleType(Type type)
        {
            // Handle void
            if (type == typeof(void))
                return true;
                
            // Handle nullable types
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                return IsJsonCompatibleType(type.GetGenericArguments()[0]);
            }
            
            // Basic JSON types
            if (type.IsPrimitive || 
                type == typeof(string) || 
                type == typeof(decimal) || 
                type == typeof(DateTime) || 
                type == typeof(DateTimeOffset) ||
                type == typeof(Guid))
            {
                return true;
            }
            
            // Newtonsoft.Json types
            if (type.Namespace?.StartsWith("Newtonsoft.Json") == true)
            {
                return true;
            }
            
            // Arrays and collections of compatible types
            if (type.IsArray)
            {
                return IsJsonCompatibleType(type.GetElementType());
            }
            
            if (type.IsGenericType)
            {
                var genericTypeDef = type.GetGenericTypeDefinition();
                if (genericTypeDef == typeof(List<>) || 
                    genericTypeDef == typeof(IList<>) ||
                    genericTypeDef == typeof(IEnumerable<>) ||
                    genericTypeDef == typeof(Dictionary<,>) ||
                    genericTypeDef == typeof(IDictionary<,>))
                {
                    return type.GetGenericArguments().All(IsJsonCompatibleType);
                }
            }
            
            // Custom classes/structs should generally work with JSON.NET
            // We'll be permissive here and let JSON.NET handle the conversion
            return !type.IsPointer && !type.IsByRef;
        }
        
        /// <summary>
        /// Determines if an assembly is a system assembly that should be skipped
        /// </summary>
        /// <param name="assembly">Assembly to check</param>
        /// <returns>True if assembly should be skipped</returns>
        private static bool IsSystemAssembly(Assembly assembly)
        {
            if (assembly == null) return true;
            
            var assemblyName = assembly.FullName;
            if (string.IsNullOrEmpty(assemblyName)) return true;
            
            // Skip well-known system assemblies
            var systemPrefixes = new[]
            {
                "mscorlib",
                "System.",
                "Microsoft.",
                "netstandard",
                "Newtonsoft.Json",
                "Unity.", // Skip Unity engine assemblies unless they contain user code
                "UnityEngine",
                "UnityEditor"
            };
            
            // Don't skip assemblies that might contain user Unity code
            var userUnityAssemblies = new[]
            {
                "Assembly-CSharp",
                "Assembly-CSharp-Editor"
            };
            
            foreach (var userAssembly in userUnityAssemblies)
            {
                if (assemblyName.StartsWith(userAssembly, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            
            foreach (var prefix in systemPrefixes)
            {
                if (assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            
            return false;
        }
    }
}