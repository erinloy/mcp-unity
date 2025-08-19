# MCP Unity Editor - C# Implementation

This is a C# implementation of MCP Unity, forked from the excellent work at https://github.com/CoderGamester/mcp-unity.

**This fork's repository**: https://github.com/erinloy/mcp-unity

## Implementation Details

- **Language**: Pure C#
- **Server Location**: `Editor/DirectMcp/unity-mcp.exe`
- **Communication**: stdio protocol
- **Configuration**: Auto-detected from Unity project root
- **Settings**: `ProjectSettings/McpUnitySettings.json`

---

# MCP Unity Editor Documentation

[![](https://badge.mcpx.dev?status=on 'MCP Enabled')](https://modelcontextprotocol.io/introduction)
[![](https://img.shields.io/badge/Unity-000000?style=flat&logo=unity&logoColor=white 'Unity')](https://unity.com/releases/editor/archive)
[![](https://img.shields.io/badge/C%23-239120?style=flat&logo=csharp&logoColor=white 'C#')](https://docs.microsoft.com/en-us/dotnet/csharp/)
[![](https://img.shields.io/badge/License-MIT-red.svg 'MIT License')](https://opensource.org/licenses/MIT)

| [English](README.md) | [🇨🇳简体中文](README_zh-CN.md) | [🇯🇵日本語](README-ja.md) |
|----------------------|---------------------------------|----------------------|

```                                                                        
                              ,/(/.   *(/,                                  
                          */(((((/.   *((((((*.                             
                     .*((((((((((/.   *((((((((((/.                         
                 ./((((((((((((((/    *((((((((((((((/,                     
             ,/(((((((((((((/*.           */(((((((((((((/*.                
            ,%%#((/((((((*                    ,/(((((/(#&@@(                
            ,%%##%%##((((((/*.             ,/((((/(#&@@@@@@(                
            ,%%######%%##((/(((/*.    .*/(((//(%@@@@@@@@@@@(                
            ,%%####%#(%%#%%##((/((((((((//#&@@@@@@&@@@@@@@@(                
            ,%%####%(    /#%#%%%##(//(#@@@@@@@%,   #@@@@@@@(                
            ,%%####%(        *#%###%@@@@@@(        #@@@@@@@(                
            ,%%####%(           #%#%@@@@,          #@@@@@@@(                
            ,%%##%%%(           #%#%@@@@,          #@@@@@@@(                
            ,%%%#*              #%#%@@@@,             *%@@@(                
            .,      ,/##*.      #%#%@@@@,     ./&@#*      *`                
                ,/#%#####%%#/,  #%#%@@@@, ,/&@@@@@@@@@&\.                    
                 `*#########%%%%###%@@@@@@@@@@@@@@@@@@&*´                   
                    `*%%###########%@@@@@@@@@@@@@@&*´                        
                        `*%%%######%@@@@@@@@@@&*´                            
                            `*#%%##%@@@@@&*´                                 
                               `*%#%@&*´                                     
                                                       
     ███╗   ███╗ ██████╗██████╗         ██╗   ██╗███╗   ██╗██╗████████╗██╗   ██╗
     ████╗ ████║██╔════╝██╔══██╗        ██║   ██║████╗  ██║██║╚══██╔══╝╚██╗ ██╔╝
     ██╔████╔██║██║     ██████╔╝        ██║   ██║██╔██╗ ██║██║   ██║    ╚████╔╝ 
     ██║╚██╔╝██║██║     ██╔═══╝         ██║   ██║██║╚██╗██║██║   ██║     ╚██╔╝  
     ██║ ╚═╝ ██║╚██████╗██║             ╚██████╔╝██║ ╚████║██║   ██║      ██║   
     ╚═╝     ╚═╝ ╚═════╝╚═╝              ╚═════╝ ╚═╝  ╚═══╝╚═╝   ╚═╝      ╚═╝   
```       

MCP Unity is an implementation of the Model Context Protocol for Unity Editor, allowing AI assistants to interact with your Unity projects. This package provides a bridge between Unity and a Node.js server that implements the MCP protocol, enabling AI agents like Claude, Windsurf, and Cursor to execute operations within the Unity Editor.

<a href="https://glama.ai/mcp/servers/@CoderGamester/mcp-unity">
  <img width="400" height="200" src="https://glama.ai/mcp/servers/@CoderGamester/mcp-unity/badge" alt="Unity MCP server" />
</a>

## Features

### IDE Integration - Package Cache Access

MCP Unity provides automatic integration with VSCode-like IDEs (Visual Studio Code, Cursor, Windsurf) by adding the Unity `Library/PackedCache` folder to your workspace. This feature:

- Improves code intelligence for Unity packages
- Enables better autocompletion and type information for Unity packages
- Helps AI coding assistants understand your project's dependencies

### MCP Server Tools

The following tools are available for manipulating and querying Unity scenes and GameObjects via MCP:

- `execute_menu_item`: Executes Unity menu items (functions tagged with the MenuItem attribute)
  > **Example prompt:** "Execute the menu item 'GameObject/Create Empty' to create a new empty GameObject"

- `select_gameobject`: Selects game objects in the Unity hierarchy by path or instance ID
  > **Example prompt:** "Select the Main Camera object in my scene"

- `update_gameobject`: Updates a GameObject's core properties (name, tag, layer, active/static state), or creates the GameObject if it does not exist
  > **Example prompt:** "Set the Player object's tag to 'Enemy' and make it inactive"

- `update_component`: Updates component fields on a GameObject or adds it to the GameObject if it does not contain the component
  > **Example prompt:** "Add a Rigidbody component to the Player object and set its mass to 5"

- `add_package`: Installs new packages in the Unity Package Manager
  > **Example prompt:** "Add the TextMeshPro package to my project"

- `run_tests`: Runs tests using the Unity Test Runner
  > **Example prompt:** "Run all the EditMode tests in my project"

- `send_console_log`: Send a console log to Unity
  > **Example prompt:** "Send a console log to Unity Editor"

- `add_asset_to_scene`: Adds an asset from the AssetDatabase to the Unity scene
  > **Example prompt:** "Add the Player prefab from my project to the current scene"

- `create_prefab`: Creates a prefab with optional MonoBehaviour script and serialized field values
  > **Example prompt:** "Create a prefab named 'Player' from the 'PlayerController' script"

### MCP Server Resources

- `unity://menu-items`: Retrieves a list of all available menu items in the Unity Editor to facilitate `execute_menu_item` tool
  > **Example prompt:** "Show me all available menu items related to GameObject creation"

- `unity://scenes-hierarchy`: Retrieves a list of all game objects in the current Unity scene hierarchy
  > **Example prompt:** "Show me the current scenes hierarchy structure"

- `unity://gameobject/{id}`: Retrieves detailed information about a specific GameObject by instance ID or object path in the scene hierarchy, including all GameObject components with it's serialized properties and fields
  > **Example prompt:** "Get me detailed information about the Player GameObject"

- `unity://logs`: Retrieves a list of all logs from the Unity console
  > **Example prompt:** "Show me the recent error messages from the Unity console"

- `unity://packages`: Retrieves information about installed and available packages from the Unity Package Manager
  > **Example prompt:** "List all the packages currently installed in my Unity project"

- `unity://assets`: Retrieves information about assets in the Unity Asset Database
  > **Example prompt:** "Find all texture assets in my project"

- `unity://tests/{testMode}`: Retrieves information about tests in the Unity Test Runner
  > **Example prompt:** "List all available tests in my Unity project"

## Requirements
- Unity 2022.3 or later

> [!IMPORTANT]
> **Project Path Cannot Contain Spaces**
>
> It is crucial that the file path to your Unity project **does not contain any spaces**.
> If your project path includes spaces, the MCP Client (e.g., Cursor, Claude, Windsurf) will fail to connect to the MCP Unity server.
>
> **Examples:**
> -   ✅ **Works:** `C:\Users\YourUser\Documents\UnityProjects\MyAwesomeGame`
> -   ❌ **Fails:** `C:\Users\Your User\Documents\Unity Projects\My Awesome Game`
>
> Please ensure your project is located in a path without spaces before proceeding with the installation.

## <a name="install-server"></a>Installation

### Step 1: Install Unity MCP Server package via Unity Package Manager
1. Open the Unity Package Manager (Window > Package Manager)
2. Click the "+" button in the top-left corner
3. Select "Add package from git URL..."
4. Enter: `https://github.com/erinloy/mcp-unity.git`
5. Click "Add"

![package manager](https://github.com/user-attachments/assets/a72bfca4-ae52-48e7-a876-e99c701b0497)

### Step 2: Configure AI LLM Client (C# Server)

<details open>
<summary><span style="font-size: 1.1em; font-weight: bold;">Option 1: Configure using Unity Editor</span></summary>

1. Open the Unity Editor
2. Navigate to Tools > MCP Unity > Server Window
3. Click on the "Configure" button for your AI LLM client as shown in the image below

![image](docs/configure.jpg)

4. Confirm the configuration installation with the given popup

![image](https://github.com/user-attachments/assets/b1f05d33-3694-4256-a57b-8556005021ba)

</details>

<details>
<summary><span style="font-size: 1.1em; font-weight: bold;">Option 2: Configure Manually</span></summary>

Open the MCP configuration file of your AI client (e.g. claude_desktop_config.json in Claude Desktop) and add:

> Replace `ABSOLUTE/PATH/TO` with the absolute path to your Unity project's mcp-unity installation.

```json
{
   "mcpServers": {
       "unity-mcp": {
          "command": "ABSOLUTE/PATH/TO/mcp-unity/Editor/DirectMcp/unity-mcp.exe"
       }
   }
}
```

The server automatically detects the Unity project root.

</details>

## <a name="start-server"></a>Start Unity Editor MCP Server

1. Configure your MCP client (Claude, Cursor, etc.) with the path to `unity-mcp.exe`
2. The server starts automatically when your MCP client connects
3. The executable launches Unity in batch mode to handle MCP requests

## Configuration

Configuration is stored in `ProjectSettings/McpUnitySettings.json`:

- **Auto-detection**: The server automatically finds your Unity project root
- **Communication**: Uses stdio protocol
- **Settings**: Managed through the configuration file

## <a name="debug-server"></a>Debugging the Server

<details>
<summary><span style="font-size: 1.1em; font-weight: bold;">Debugging</span></summary>

The server can be debugged using standard C# debugging tools:

1. **Unity Console**: Server logs appear in the Unity console
2. **C# Debugger**: Use your IDE's debugger to set breakpoints in the server code
3. **Compilation**: Changes to C# code are compiled by Unity

**To debug the server:**
1. Open the project in your C# IDE (Visual Studio, Rider, etc.)
2. Set breakpoints in the `Editor/DirectMcp/` files
3. Attach the debugger to Unity
4. The server will hit breakpoints when MCP clients make requests

</details>
   
<details>
<summary><span style="font-size: 1.1em; font-weight: bold;">MCP Inspector</span></summary>

Debug the server with [@modelcontextprotocol/inspector](https://github.com/modelcontextprotocol/inspector):
   ```powershell
   npx @modelcontextprotocol/inspector "path/to/unity-mcp.exe"
   ```

</details>

<details>
<summary><span style="font-size: 1.1em; font-weight: bold;">Logging</span></summary>

Logs are automatically written to:
- Unity Console (when running in Editor)
- Standard error stream (when running as subprocess)

Check the Unity console or your MCP client's debug output for server logs.

</details>

## Frequently Asked Questions

<details>
<summary><span style="font-size: 1.1em; font-weight: bold;">What is MCP Unity?</span></summary>

MCP Unity is a powerful bridge that connects your Unity Editor environment to AI assistants LLM tools using the Model Context Protocol (MCP).

In essence, MCP Unity:
-   Exposes Unity Editor functionalities (like creating objects, modifying components, running tests, etc.) as "tools" and "resources" that an AI can understand and use.
-   Runs a WebSocket server inside Unity and a Node.js server (acting as a WebSocket client to Unity) that implements the MCP. This allows AI assistants to send commands to Unity and receive information back.
-   Enables you to use natural language prompts with your AI assistant to perform complex tasks within your Unity project, significantly speeding up development workflows.

</details>

<details>
<summary><span style="font-size: 1.1em; font-weight: bold;">Why use MCP Unity?</span></summary>

MCP Unity offers several compelling advantages for developers, artists, and project managers:

-   **Accelerated Development:** Automate repetitive tasks, generate boilerplate code, and manage assets using AI prompts. This frees up your time to focus on creative and complex problem-solving.
-   **Enhanced Productivity:** Interact with Unity Editor features without needing to manually click through menus or write scripts for simple operations. Your AI assistant becomes a direct extension of your capabilities within Unity.
-   **Improved Accessibility:** Allows users who are less familiar with the deep intricacies of the Unity Editor or C# scripting to still make meaningful contributions and modifications to a project through AI guidance.
-   **Seamless Integration:** Designed to work with various AI assistants and IDEs that support MCP, providing a consistent way to leverage AI across your development toolkit.
-   **Extensibility:** The protocol and the toolset can be expanded. You can define new tools and resources to expose more of your project-specific or Unity's functionality to AI.
-   **Collaborative Potential:** Facilitates a new way of collaborating where AI can assist in tasks traditionally done by team members, or help in onboarding new developers by guiding them through project structures and operations.

</details>

<details>
<summary><span style="font-size: 1.1em; font-weight: bold;">How does MCP Unity compare with the upcoming Unity 6.2 AI features?</span></summary>

Unity 6.2 is set to introduce new built-in AI tools, including the previous Unity Muse (for generative AI capabilities like texture and animation generation) and Unity Sentis (for running neural networks in Unity runtime). As Unity 6.2 is not yet fully released, this comparison is based on publicly available information and anticipated functionalities:

-   **Focus:**
    -   **MCP Unity:** Primarily focuses on **Editor automation and interaction**. It allows external AI (like LLM-based coding assistants) to *control and query the Unity Editor itself* to manipulate scenes, assets, and project settings. It's about augmenting the *developer's workflow* within the Editor.
    -   **Unity 6.2 AI:**
        -   Aims at in-Editor content creation (generating textures, sprites, animations, behaviors, scripts) and AI-powered assistance for common tasks, directly integrated into the Unity Editor interface.
        -   A fine-tuned model to ask any question about Unity's documentation and API structure, with customized examples more accurate to Unity's environment.
        -   Adds the functionality to run AI model inference, allowing developers to deploy and run pre-trained neural networks *within your game or application* for features like NPC behavior, image recognition, etc.

-   **Use Cases:**
    -   **MCP Unity:** "Create a new 3D object, name it 'Player', add a Rigidbody, and set its mass to 10." "Run all Play Mode tests." "Ask to fix the error on the console log." "Execute the custom menu item 'Prepare build for iOS' and fix any errors that may occur."
    -   **Unity 6.2 AI:** "Generate a sci-fi texture for this material." "Update all trees position in the scene to be placed inside of terrain zones tagged with 'forest'." "Create a walking animation for this character." "Generate 2D sprites to complete the character." "Ask details about the error on the console log."

-   **Complementary, Not Mutually Exclusive:**
    MCP Unity and Unity's native AI tools can be seen as complementary. You might use MCP Unity with your AI coding assistant to set up a scene or batch-modify assets, and then use Unity AI tools to generate a specific texture, or to create animations, or 2D sprites for one of those assets. MCP Unity provides a flexible, protocol-based way to interact with the Editor, which can be powerful for developers who want to integrate with a broader range of external AI services or build custom automation workflows.

</details>

<details>
<summary><span style="font-size: 1.1em; font-weight: bold;">What MCP hosts and IDEs currently support MCP Unity?</span></summary>

MCP Unity is designed to work with any AI assistant or development environment that can act as an MCP client. The ecosystem is growing, but current known integrations or compatible platforms include:
-  Windsurf
-  Cursor
-  GitHub Copilot
-  Claude Desktop

</details>

<details>
<summary><span style="font-size: 1.1em; font-weight: bold;">Can I extend MCP Unity with custom tools for my project?</span></summary>

Yes, absolutely! One of the significant benefits of the MCP Unity architecture is its extensibility.
-   **In Unity (C#):** You can create new C# classes that inherit from `McpToolBase` (or a similar base for resources) to expose custom Unity Editor functionality. These tools would then be registered in `McpUnityServer.cs`. For example, you could write a tool to automate a specific asset import pipeline unique to your project.
-   **In Node.js Server (TypeScript):** You would then define the corresponding TypeScript tool handler in the `Server/src/tools/` directory, including its Zod schema for inputs/outputs, and register it in `Server/src/index.ts`. This Node.js part will forward the request to your new C# tool in Unity.

This allows you to tailor the AI's capabilities to the specific needs and workflows of your game or application.

</details>

<details>
<summary><span style="font-size: 1.1em; font-weight: bold;">Is MCP Unity free to use?</span></summary>

Yes, MCP Unity is an open-source project distributed under the MIT License. You are free to use, modify, and distribute it according to the license terms.

</details>

<details>
<summary><span style="font-size: 1.1em; font-weight: bold;">Why am I unable to connect to MCP Unity?</span></summary>

- Ensure the WebSocket server is running (check the Server Window in Unity)
- Send a console log message from MCP client to force a reconnection between MCP client and Unity server
- Change the port number in the Unity Editor MCP Server window. (Tools > MCP Unity > Server Window)

</details>

<details>
<summary><span style="font-size: 1.1em; font-weight: bold;">Why won't the MCP Unity server start?</span></summary>

- Check the Unity Console for error messages
- Ensure Node.js is properly installed and accessible in your PATH
- Verify that all dependencies are installed in the Server directory

</details>

<details>
<summary><span style="font-size: 1.1em; font-weight: bold;">Why do I get a connection failed error when running Play Mode tests?</span></summary>

The `run_tests` tool returns the following response:
```
Error:
Connection failed: Unknown error
```

This error occurs because the bridge connection is lost when the domain reloads upon switching to Play Mode.  
The workaround is to turn off **Reload Domain** in **Edit > Project Settings > Editor > "Enter Play Mode Settings"**.

</details>

## Support & Feedback

If you have any questions or need support, please open an [issue](https://github.com/CoderGamester/mcp-unity/issues) on this repository or alternative you can reach out on:
- Linkedin: [![](https://img.shields.io/badge/LinkedIn-0077B5?style=flat&logo=linkedin&logoColor=white 'LinkedIn')](https://www.linkedin.com/in/miguel-tomas/)
- Discord: gamester7178
- Email: game.gamester@gmail.com

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request or open an Issue with your request.

**Commit your changes** following the [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) format.

## License

This project is under [MIT License](License.md)

## Acknowledgements

- [Model Context Protocol](https://modelcontextprotocol.io)
- [Unity Technologies](https://unity.com)
- [Node.js](https://nodejs.org)
- [WebSocket-Sharp](https://github.com/sta/websocket-sharp)
