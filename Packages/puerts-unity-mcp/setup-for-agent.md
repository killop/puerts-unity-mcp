# PuerTS Unity MCP Agent Setup

This package does not install agent configuration files into the Unity project root.
Configure your agent workspace manually, or let the agent edit its own config files outside the Unity project.

## Unity Project Paths

Use these paths after the package has been synced into a Unity project root:

- MCP stdio proxy: `<UnityProject>/puerts-unity-mcp/Packages/puerts-unity-mcp/Tools~/puerts-unity-mcp-stdio-proxy.js`
- Editor config: `<UnityProject>/puerts-unity-mcp-extension/editor-mcp-config.json`
- Mobile/player config: `<UnityProject>/puerts-unity-mcp-extension/mobile-mcp-config.json`
- Editor JavaScript tools: `<UnityProject>/puerts-unity-mcp-extension/Editor/editor-tools`
- Runtime JavaScript tools: `<UnityProject>/puerts-unity-mcp-extension/Runtime/runtime-tools`
- Skills: `<UnityProject>/puerts-unity-mcp-extension/skills`
- Runtime state: `<UnityProject>/.puerts-unity-mcp`

The Unity project root should not contain generated `.mcp.json`, `.cursor`, `.codex`, `.claude`, `*-plugin`, or root `skills` directories from this package.

## Unity Project `.gitignore`

Ensure the Unity project `.gitignore` contains:

```gitignore
# PuerTS Unity MCP runtime state and generated project-local files
.puerts-unity-mcp/
Assets/puerts-unity-mcp/Runtime/Generated/
Assets/puerts-unity-mcp/Runtime/Generated/Plugins/puerts_il2cpp/
```

Do not ignore the whole `puerts-unity-mcp-extension` directory. Project configs, JavaScript MCP tools, and skills under that directory are persistent project assets. Do not ignore `puerts-unity-mcp/Packages/puerts-unity-mcp/Runtime/Plugins/Android`; that folder contains the MCP Android permission library. Upstream PuerTS `.so` files come from `third_party/puerts`, not from the MCP package.

The vendored PuerTS directory has its own upstream `.gitignore` at `puerts-unity-mcp/third_party/puerts/unity/.gitignore`. It ignores Unity-generated `*.meta` files for PuerTS packages, so those local files may appear after Unity opens the project but should not be committed.

## Codex

Add this to the agent workspace Codex config, replacing `<UnityProject>` with the absolute Unity project path:

```toml
[mcp_servers."puerts-unity-mcp"]
command = "node"
args = [
  "<UnityProject>/puerts-unity-mcp/Packages/puerts-unity-mcp/Tools~/puerts-unity-mcp-stdio-proxy.js",
  "--config",
  "<UnityProject>/puerts-unity-mcp-extension/editor-mcp-config.json"
]
```

## Claude / Cursor JSON MCP

Add this to the agent workspace MCP JSON config, replacing `<UnityProject>` with the absolute Unity project path:

```json
{
  "mcpServers": {
    "puerts-unity-mcp": {
      "command": "node",
      "args": [
        "<UnityProject>/puerts-unity-mcp/Packages/puerts-unity-mcp/Tools~/puerts-unity-mcp-stdio-proxy.js",
        "--config",
        "<UnityProject>/puerts-unity-mcp-extension/editor-mcp-config.json"
      ]
    }
  }
}
```

## Direct Phone / Player Mode

To connect directly to a phone or standalone Player MCP without opening Unity Editor:

1. Keep the agent working directory at the Unity project root, or pass `--extension-root <UnityProject>/puerts-unity-mcp-extension`.
2. Keep `runtimeBindAddress` as `0.0.0.0`.
3. Keep `name_group` the same between the PC agent config and the player runtime config.
4. Set `selectedTargetKind` to `player` in `<UnityProject>/puerts-unity-mcp-extension/editor-mcp-config.json`, or pass `--target-kind player`.
5. Use LAN discovery or pass a target URL to the stdio proxy.

The stdio proxy reads local extension files through the system filesystem. When it is connected directly to a phone, it still exposes local `agent.extension.*` tools and can register JavaScript tool manifests from `puerts-unity-mcp-extension/Runtime/runtime-tools`; those scripts execute through the phone's `runtime.js.eval` tool.

Runtime MCP uses low-IO defaults for phones: `enableFileCommandPump`, `enableDiskHeartbeat`, `enableDiscoveredEndpointCache`, and `enableAotMissLog` default to `false`; `screen.screenshot` defaults to in-memory PNG base64 with `screenshotWriteMode: "memory"`.

For player builds, use the package tools instead of Unity Scripting Define Symbols. `add-pum-to-build.mjs` adds the PuerTS Unity MCP package dependencies, copies `<UnityProject>/puerts-unity-mcp-extension/mobile-mcp-config.json` into `Assets/StreamingAssets/PuertsUnityMcp/mobile-mcp-config.json`, verifies the upstream PuerTS Android native libraries under `third_party/puerts`, and uses the MCP permission library bundled under `Packages/puerts-unity-mcp/Runtime/Plugins/Android`. `remove-pum-from-build.mjs` removes the build dependency entries and copied `StreamingAssets` config again, but keeps the bundled MCP Android permission library in the package.

PuerTS generated C# files are generated per Unity project under `<UnityProject>/Assets/puerts-unity-mcp/Runtime/Generated`; the upstream PuerTS IL2CPP bridge path is derived from that and lands under `<UnityProject>/Assets/puerts-unity-mcp/Runtime/Generated/Plugins/puerts_il2cpp`. Treat those folders as generated output: ignore them in reusable package source and regenerate them for the current Unity/IL2CPP environment.

The Editor MCP can still route to local Play Mode runtime targets when the Unity Editor is open.

## PuerTS JavaScript Guide For Agents

Use `editor.js.eval` for Unity Editor automation and `runtime.js.eval` for Play Mode, Android, iOS, or standalone Player automation. Editor and Runtime are separate PuerTS `ScriptEnv` instances: Editor code may use `UnityEditor`, while phone/player code should stay on runtime-safe Unity APIs.

Use PuerTS `CS` first:

```js
CS.UnityEngine.Debug.Log("hello from PuerTS Unity MCP");

var productName = CS.UnityEngine.Application.productName;
var sceneName = CS.UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

return {
  ok: true,
  productName: productName,
  sceneName: sceneName
};
```

With `mode: "expression"`, the expression result is returned automatically:

```js
CS.UnityEngine.Application.version
```

With `mode: "script"`, return plain JSON-serializable data explicitly:

```js
var camera = CS.UnityEngine.Camera.main;
return {
  hasMainCamera: !!camera,
  cameraName: camera ? camera.name : ""
};
```

If a C# type is not available through generated PuerTS wraps, use the reflection helper injected as `__unity_mcp`:

```js
__unity_mcp.typeExists("UnityEngine.Application");
__unity_mcp.getStatic("UnityEngine.Application", "productName");
__unity_mcp.getStaticPath("UnityEngine.Screen", "width");
__unity_mcp.setStatic("UnityEngine.Time", "timeScale", 1);
__unity_mcp.invokeStatic("UnityEngine.Debug", "Log", "message from reflection");
```

For phone UI automation, observe first, then act. Useful runtime tools include `screen.screenshot`, `runtime.ui.snapshot`, `runtime.ui.find`, `runtime.ui.raycast`, `runtime.ui.click`, and `input.tap`. Stable game-specific flows should be moved into `puerts-unity-mcp-extension/Runtime/runtime-tools` instead of repeatedly generating one-off eval scripts.

In IL2CPP builds, reflection only works for types and members that survive stripping. If a reflected type is missing, preserve it with link.xml or add a small project wrapper.
