using McpUnity.Unity;
using McpUnity.Utils;
using UnityEditor;
using UnityEditor.Compilation;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for checking Unity's script compilation status
    /// </summary>
    public class CompilationStatusTool : McpToolBase
    {
        public CompilationStatusTool()
        {
            Name = "compilation_status";
            Description = "Get the current script compilation status";
        }

        public override JObject Execute(JObject parameters)
        {
            var assemblies = CompilationPipeline.GetAssemblies();

            var result = new JObject
            {
                ["isCompiling"] = EditorApplication.isCompiling,
                ["assemblyCount"] = assemblies.Length,
                ["message"] = EditorApplication.isCompiling
                    ? "Scripts are currently compiling..."
                    : "Compilation complete."
            };

            return result;
        }
    }
}
