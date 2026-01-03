using McpUnity.Tools;

namespace McpUnity.Unity
{
    /// <summary>
    /// Extension to register additional Unity MCP tools
    /// Call this from McpUnityServer.RegisterTools() to add new tools
    /// </summary>
    public static class RegisterAdditionalTools
    {
        public static void RegisterTo(System.Collections.Generic.Dictionary<string, McpToolBase> tools)
        {
            // Register UIInspectorTool - Advanced UI inspection and manipulation
            var uiInspectorTool = new UIInspectorTool();
            if (!tools.ContainsKey(uiInspectorTool.Name))
            {
                tools.Add(uiInspectorTool.Name, uiInspectorTool);
            }

            // Register LogSubscriptionTool - Real-time log subscription
            var logSubscriptionTool = new LogSubscriptionTool();
            if (!tools.ContainsKey(logSubscriptionTool.Name))
            {
                tools.Add(logSubscriptionTool.Name, logSubscriptionTool);
            }

            // Register FindGameObjectTool - Search for GameObjects by name pattern
            var findGameObjectTool = new FindGameObjectTool();
            if (!tools.ContainsKey(findGameObjectTool.Name))
            {
                tools.Add(findGameObjectTool.Name, findGameObjectTool);
            }

            // Register CopyFieldValueTool - Copy field values between components (including sprites)
            var copyFieldValueTool = new CopyFieldValueTool();
            if (!tools.ContainsKey(copyFieldValueTool.Name))
            {
                tools.Add(copyFieldValueTool.Name, copyFieldValueTool);
            }
        }
    }
}