import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

// Constants for the tool
const toolName = 'capture_screenshot';
const toolDescription = "Captures a screenshot from Unity's Scene or Game view";
const paramsSchema = z.object({
  viewType: z.enum(["scene", "game", "both"]).default("game").describe("Type of view to capture: 'scene', 'game', or 'both'"),
  width: z.number().default(0).describe("Width of the screenshot (0 for current size)"),
  height: z.number().default(0).describe("Height of the screenshot (0 for current size)"),
  saveToFile: z.boolean().default(false).describe("Whether to save the screenshot to a file"),
  filePath: z.string().optional().describe("Path where to save the screenshot (optional, auto-generated if not provided)")
});

/**
 * Creates and registers the Capture Screenshot tool with the MCP server
 * This tool allows capturing screenshots from Unity's Scene or Game view
 * 
 * @param server The MCP server instance to register with
 * @param mcpUnity The McpUnity instance to communicate with Unity
 * @param logger The logger instance for diagnostic information
 */
export function registerCaptureScreenshotTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);
  
  // Register this tool with the MCP server
  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (args, extra): Promise<CallToolResult> => {
      try {
        const { 
          viewType = "game", 
          width = 0, 
          height = 0, 
          saveToFile = false,
          filePath 
        } = args;

        logger.info(`Capturing screenshot: viewType=${viewType}, width=${width}, height=${height}, saveToFile=${saveToFile}`);

        const result = await mcpUnity.sendRequest({
          method: "capture_screenshot",
          params: {
            viewType,
            width,
            height,
            saveToFile,
            filePath
          }
        });

        if (!result.success) {
          logger.error(`Screenshot capture failed: ${result.message}`);
          return {
            content: [
              {
                type: "text",
                text: `Failed to capture screenshot: ${result.message || "Unknown error"}`
              }
            ]
          };
        }

        // Handle the response based on what Unity returned
        if (result.type === "image" && result.data) {
          // Single screenshot - return as MCP image content
          const screenshot = result.data;
          return {
            content: [
              {
                type: "image",
                data: screenshot.data, // This should be the base64 data URL
                mimeType: "image/png"
              },
              {
                type: "text",
                text: `Captured ${screenshot.type} view screenshot (${screenshot.width}x${screenshot.height})`
              }
            ]
          };
        } else if (result.type === "images" && result.data?.screenshots) {
          // Multiple screenshots - return each as separate content
          const screenshots = result.data.screenshots;
          const content: any[] = [];
          
          for (const screenshot of screenshots) {
            content.push({
              type: "image",
              data: screenshot.data,
              mimeType: "image/png"
            });
          }
          
          content.push({
            type: "text",
            text: `Captured ${screenshots.length} screenshots: ${screenshots.map((s: any) => `${s.type} (${s.width}x${s.height})`).join(", ")}`
          });
          
          return { content };
        } else if (result.type === "files" && result.data?.files) {
          // Files saved - return paths
          const files = result.data.files;
          return {
            content: [
              {
                type: "text",
                text: `Screenshots saved to:\n${files.join("\n")}`
              }
            ]
          };
        }

        // Fallback response
        return {
          content: [
            {
              type: "text",
              text: result.message || "Screenshot captured successfully"
            }
          ]
        };
      } catch (error) {
        if (error instanceof McpUnityError) {
          logger.error(`Screenshot capture error: ${error.message}`);
          return {
            content: [
              {
                type: "text",
                text: `Error capturing screenshot: ${error.message}`
              }
            ]
          };
        }
        
        logger.error("Screenshot capture error:", error);
        return {
          content: [
            {
              type: "text",
              text: `Error capturing screenshot: ${error instanceof Error ? error.message : String(error)}`
            }
          ]
        };
      }
    }
  );
  
  logger.info(`Tool ${toolName} registered successfully`);
}