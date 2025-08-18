using System;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using McpUnity.Tools.Attributes;
using McpUnity.Utils;
using UnityEngine;

namespace McpUnity.Tools
{
    /// <summary>
    /// Wrapper that converts an attributed static method into an MCP tool
    /// </summary>
    public class AttributedMethodTool : McpToolBase
    {
        private readonly MethodInfo _method;
        private readonly object _instance;
        private readonly ParameterInfo[] _parameters;
        
        public string Category { get; }
        
        /// <summary>
        /// Creates a tool wrapper for an attributed method
        /// </summary>
        /// <param name="method">The method to wrap</param>
        /// <param name="instance">Instance to call method on (null for static methods)</param>
        /// <param name="attribute">The McpTool attribute from the method</param>
        public AttributedMethodTool(MethodInfo method, object instance, McpToolAttribute attribute)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
            _instance = instance;
            _parameters = method.GetParameters();
            
            Name = attribute.Name;
            Description = attribute.Description ?? $"Auto-generated tool for {method.DeclaringType?.Name}.{method.Name}";
            IsAsync = attribute.IsAsync;
            Category = attribute.Category;
        }
        
        public override JObject Execute(JObject parameters)
        {
            if (IsAsync)
            {
                return McpUnity.Unity.McpUnitySocketHandler.CreateErrorResponse(
                    "This tool is marked as async and should be executed via ExecuteAsync", 
                    "execution_error"
                );
            }
            
            return ExecuteInternal(parameters);
        }
        
        public override void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            if (!IsAsync)
            {
                // For non-async tools, just execute synchronously
                try
                {
                    var result = ExecuteInternal(parameters);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                return;
            }
            
            // For async tools, execute on the main thread
            if (Application.isPlaying)
            {
                // In play mode, use Unity's main thread dispatcher
                UnityMainThreadDispatcher.Instance().Enqueue(() =>
                {
                    try
                    {
                        var result = ExecuteInternal(parameters);
                        tcs.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                });
            }
            else
            {
                // In edit mode, use EditorApplication.delayCall
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    try
                    {
                        var result = ExecuteInternal(parameters);
                        tcs.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                };
            }
        }
        
        private JObject ExecuteInternal(JObject parameters)
        {
            try
            {
                // Convert JSON parameters to method arguments
                object[] args = ConvertParameters(parameters);
                
                // Invoke the method
                object result = _method.Invoke(_instance, args);
                
                // Convert result to JObject
                return ConvertResult(result);
            }
            catch (TargetParameterCountException ex)
            {
                McpLogger.LogError($"[AttributedMethodTool] Parameter count mismatch for {Name}: {ex.Message}");
                return McpUnity.Unity.McpUnitySocketHandler.CreateErrorResponse(
                    $"Parameter count mismatch: {ex.Message}", 
                    "parameter_error"
                );
            }
            catch (ArgumentException ex)
            {
                McpLogger.LogError($"[AttributedMethodTool] Parameter type mismatch for {Name}: {ex.Message}");
                return McpUnity.Unity.McpUnitySocketHandler.CreateErrorResponse(
                    $"Parameter type mismatch: {ex.Message}", 
                    "parameter_error"
                );
            }
            catch (TargetInvocationException ex)
            {
                var innerEx = ex.InnerException ?? ex;
                McpLogger.LogError($"[AttributedMethodTool] Tool execution failed for {Name}: {innerEx.Message}");
                return McpUnity.Unity.McpUnitySocketHandler.CreateErrorResponse(
                    $"Tool execution failed: {innerEx.Message}", 
                    "execution_error"
                );
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"[AttributedMethodTool] Unexpected error for {Name}: {ex.Message}");
                return McpUnity.Unity.McpUnitySocketHandler.CreateErrorResponse(
                    $"Unexpected error: {ex.Message}", 
                    "unexpected_error"
                );
            }
        }
        
        private object[] ConvertParameters(JObject parameters)
        {
            var args = new object[_parameters.Length];
            
            for (int i = 0; i < _parameters.Length; i++)
            {
                var param = _parameters[i];
                var paramAttr = param.GetCustomAttribute<McpParameterAttribute>();
                
                // Determine JSON property name
                string jsonName = paramAttr?.JsonName ?? param.Name;
                
                // Get value from JSON
                var token = parameters[jsonName];
                
                if (token == null)
                {
                    // Use default value if available
                    if (paramAttr?.DefaultValue != null)
                    {
                        args[i] = paramAttr.DefaultValue;
                    }
                    else if (!paramAttr?.Required == true)
                    {
                        args[i] = GetDefaultValue(param.ParameterType);
                    }
                    else
                    {
                        throw new ArgumentException($"Required parameter '{jsonName}' not provided");
                    }
                }
                else
                {
                    // Convert JSON value to parameter type
                    args[i] = token.ToObject(param.ParameterType);
                }
            }
            
            return args;
        }
        
        private JObject ConvertResult(object result)
        {
            if (result == null)
            {
                return new JObject
                {
                    ["success"] = true,
                    ["result"] = null
                };
            }
            
            // If result is already a JObject, return it directly
            if (result is JObject jObj)
            {
                return jObj;
            }
            
            // Convert to JSON-compatible format
            return new JObject
            {
                ["success"] = true,
                ["result"] = JToken.FromObject(result)
            };
        }
        
        private static object GetDefaultValue(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
    
    /// <summary>
    /// Simple main thread dispatcher for Unity
    /// </summary>
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        private static UnityMainThreadDispatcher _instance;
        private readonly System.Collections.Generic.Queue<System.Action> _executionQueue = new();
        
        public static UnityMainThreadDispatcher Instance()
        {
            if (_instance == null)
            {
                var go = new GameObject("UnityMainThreadDispatcher");
                _instance = go.AddComponent<UnityMainThreadDispatcher>();
                DontDestroyOnLoad(go);
            }
            return _instance;
        }
        
        public void Enqueue(System.Action action)
        {
            lock (_executionQueue)
            {
                _executionQueue.Enqueue(action);
            }
        }
        
        private void Update()
        {
            lock (_executionQueue)
            {
                while (_executionQueue.Count > 0)
                {
                    _executionQueue.Dequeue().Invoke();
                }
            }
        }
    }
}