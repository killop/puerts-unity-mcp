<h1 align="center">PuerTS Unity MCP</h1>
<p align="center">
  <strong>Control Unity Editors, Play Mode, and real mobile games through MCP with dynamic PuerTS JavaScript.</strong>
  <br />
  <em>Android · iOS · IL2CPP · Editor JS · Runtime JS · C# and JS MCP tools · Domain reload recovery</em>
</p>

<p align="center">
  <a href="#quick-start"><img src="https://img.shields.io/badge/Quick_Start-4CAF50?style=for-the-badge" alt="Quick Start" /></a>
  <a href="#agent-puerts-js-guide"><img src="https://img.shields.io/badge/Agent_JS_Guide-1976D2?style=for-the-badge" alt="Agent JS Guide" /></a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Unity-2021.3%2B-black?style=flat&logo=unity&logoColor=white" alt="Unity 2021.3+" />
  <img src="https://img.shields.io/badge/PuerTS-3.0.2-blue?style=flat" alt="PuerTS 3.0.2" />
  <img src="https://img.shields.io/badge/MCP-JSON--RPC-6A5ACD?style=flat" alt="MCP JSON-RPC" />
  <img src="https://img.shields.io/badge/IL2CPP-supported-2E7D32?style=flat" alt="IL2CPP supported" />
</p>

<p align="center">
  English · <a href="README-zh.md">中文</a>
</p>

---

## Features

| Feature | Description |
|---|---|
| Direct phone debugging | Connect an agent directly to an Android, iOS, or standalone Unity Player over HTTP and run PuerTS JavaScript inside the live game. |
| IL2CPP player support | Build tools add PuerTS packages, native plugins, StreamingAssets config, Android permissions, and preservation hints for player builds. |
| Editor JavaScript without domain reload | `editor.js.eval` runs in the Editor PuerTS VM. It does not generate C# files, call `AssetDatabase.Refresh`, or trigger a Unity domain reload. |
| Runtime JavaScript | `runtime.js.eval` targets local Play Mode or a remote Player MCP endpoint, including phones. |
| C# and JavaScript MCP tools | Core tools are C#; project tools can also be loaded from `puerts-unity-mcp-extension/Editor/editor-tools` and `Runtime/runtime-tools`. |
| Domain reload recovery | The Editor endpoint persists operation state, compile results, reload hints, and restarts itself after Unity domain reloads. |

## What It Controls

PuerTS Unity MCP treats every controllable Unity whole as one endpoint.

```text
Agent / MCP client
  |
  | stdio JSON-RPC
  v
Node stdio proxy
  |
  | HTTP JSON-RPC POST /mcp
  v
+----------------------+      direct C# route      +-----------------------+
| Unity Editor MCP     | ------------------------> | Play Mode Runtime MCP |
| endpointKind=editor  |                           | endpointKind=player   |
| C# + Editor PuerTS   |                           | C# + Runtime PuerTS   |
+----------------------+                           +-----------------------+
        |
        | LAN discovery or direct target URL
        v
+----------------------+
| Phone / Player MCP   |
| Android, iOS, build  |
| C# + Runtime PuerTS  |
+----------------------+
```

Editor Play Mode is not a third MCP kind. It is the same Runtime MCP implementation running inside the Unity Editor process.

## Quick Start

### Vendor PuerTS

```bash
node Packages/puerts-unity-mcp/Tools~/vendor-puerts.mjs
```

This downloads and verifies PuerTS `Unity_v3.0.2` Core and V8 packages under `third_party/puerts`.

### Sync Into A Unity Project

```bash
node Packages/puerts-unity-mcp/Tools~/sync-local-package.mjs --unity-project-root <UnityProject>
```

The Unity project receives:

```text
<UnityProject>/puerts-unity-mcp
<UnityProject>/puerts-unity-mcp-extension
<UnityProject>/.puerts-unity-mcp
```

### Register Local UPM Packages

The sync script updates `Packages/manifest.json` with three local dependencies:

```json
{
  "dependencies": {
    "com.tencent.puerts.core": "file:../puerts-unity-mcp/third_party/puerts/unity/upms/core",
    "com.tencent.puerts.v8": "file:../puerts-unity-mcp/third_party/puerts/unity/upms/v8",
    "puerts-unity-mcp": "file:../puerts-unity-mcp/Packages/puerts-unity-mcp"
  }
}
```

### Configure Your Agent

Open the agent setup guide copied into the Unity package:

```text
<UnityProject>/puerts-unity-mcp/Packages/puerts-unity-mcp/setup-for-agent.md
```

For Codex, the MCP server entry looks like this:

```toml
[mcp_servers."puerts-unity-mcp"]
command = "node"
args = [
  "<UnityProject>/puerts-unity-mcp/Packages/puerts-unity-mcp/Tools~/puerts-unity-mcp-stdio-proxy.js",
  "--config",
  "<UnityProject>/puerts-unity-mcp-extension/editor-mcp-config.json"
]
```

## Phone And IL2CPP Builds

For QA phones and real player builds, include the Runtime MCP in the player:

```bash
node <UnityProject>/puerts-unity-mcp/Packages/puerts-unity-mcp/Tools~/add-pum-to-build.mjs --unity-project-root <UnityProject>
```

Remove it from player builds again with:

```bash
node <UnityProject>/puerts-unity-mcp/Packages/puerts-unity-mcp/Tools~/remove-pum-from-build.mjs --unity-project-root <UnityProject>
```

The add script:

- adds the local PuerTS and PuerTS Unity MCP package dependencies
- copies `puerts-unity-mcp-extension/mobile-mcp-config.json` to `Assets/StreamingAssets/PuertsUnityMcp/mobile-mcp-config.json`
- verifies the upstream PuerTS Android native libraries under `third_party/puerts` and uses the MCP Android permission library bundled under `Packages/puerts-unity-mcp/Runtime/Plugins/Android`
- keeps runtime defaults low-IO for phones

`remove-pum-from-build.mjs` removes the build dependency entries and copied `StreamingAssets` config, but it does not delete the bundled Android plugin files from the package.

The runtime endpoint inside the phone exposes:

```text
GET  /health
POST /mcp
```

An agent can connect without the Unity Editor by using:

```bash
node <UnityProject>/puerts-unity-mcp/Packages/puerts-unity-mcp/Tools~/puerts-unity-mcp-stdio-proxy.js \
  --config <UnityProject>/puerts-unity-mcp-extension/editor-mcp-config.json \
  --target-kind player \
  --target-url http://PHONE_IP:18991
```

LAN discovery uses UDP port `18992` and `name_group`. If UDP broadcast or multicast is blocked by office Wi-Fi, AP isolation, VLAN routing, VPN policy, or firewall rules, configure HTTP fallback:

```json
{
  "selectedTargetKind": "player",
  "name_group": "default",
  "lanHttpProbeHosts": ["192.168.1.55"],
  "lanHttpProbeCidrs": ["192.168.1.0/24"],
  "lanHttpProbeTimeoutMs": 1000
}
```

## Editor JavaScript Without Domain Reload

Use `editor.js.eval` for Editor automation that should stay smooth. It executes inside an existing PuerTS VM and does not create or compile C# scripts.

```json
{
  "name": "editor.js.eval",
  "arguments": {
    "mode": "expression",
    "code": "CS.UnityEditor.EditorApplication.isPlaying"
  }
}
```

This is different from generating temporary C# files. JavaScript eval does not call `AssetDatabase.Refresh`, so normal Editor automation avoids Unity domain reload entirely.

When a domain reload is unavoidable because project C# changed, the Editor MCP records operation state under `.puerts-unity-mcp/ops`, writes compile result hints, and restarts its HTTP endpoint after `afterAssemblyReload`.

## MCP Tool Extension

Core tools are implemented in C# and registered by the Editor or Runtime host. Project tools can be implemented in JavaScript and loaded from the Unity project extension folder.

```text
<UnityProject>/puerts-unity-mcp-extension
  Editor/editor-tools      Editor-side JavaScript MCP tools
  Runtime/runtime-tools    Runtime/player JavaScript MCP tools
  skills                   Project skills for agents
```

Each JavaScript MCP tool has a manifest that points to a module:

```json
{
  "name": "runtime.activeScene",
  "description": "Return the active Unity scene through the runtime PuerTS VM.",
  "modulePath": "active-scene.mjs",
  "functionName": "execute",
  "inputSchema": {
    "type": "object",
    "additionalProperties": true
  }
}
```

Runtime JavaScript tools execute through `runtime.js.eval`, so the same tool model works for Play Mode and real phones.

## Agent PuerTS JS Guide

This section is written for agents that need to generate JavaScript for `editor.js.eval`, `runtime.js.eval`, or project JavaScript MCP tools.

### Choose The Target VM

| Task | Tool | VM |
|---|---|---|
| Unity Editor automation | `editor.js.eval` | Editor PuerTS VM |
| Play Mode runtime automation | `runtime.js.eval` with local Play Mode target | Runtime PuerTS VM |
| Android, iOS, standalone automation | `runtime.js.eval` with `targetId` or `httpUrl` | Runtime PuerTS VM on the player |

Editor and Runtime are separate PuerTS `ScriptEnv` instances. Editor code can use `UnityEditor` APIs. Runtime/player code should use runtime-safe APIs.

### Basic PuerTS Code

Use the PuerTS `CS` global first.

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

For `mode: "expression"`, the expression is returned automatically:

```js
CS.UnityEngine.Application.version
```

For `mode: "script"`, use `return` explicitly:

```js
var go = CS.UnityEngine.GameObject.Find("Canvas");
return {
  found: !!go,
  name: go ? go.name : ""
};
```

### Reflection Fallback

If a C# type is not available through generated wraps, use `__unity_mcp`. This project intentionally supports reflection-first access, which is useful during development and for project-specific IL2CPP investigation.

```js
return __unity_mcp.invokeStatic(
  "UnityEngine.Debug",
  "Log",
  "hello through reflection"
);
```

Common helpers:

```js
__unity_mcp.typeExists("UnityEngine.Application");
__unity_mcp.getStatic("UnityEngine.Application", "productName");
__unity_mcp.getStaticPath("UnityEngine.Screen", "width");
__unity_mcp.setStatic("UnityEngine.Time", "timeScale", 1);
__unity_mcp.invokeStatic("UnityEngine.Debug", "Log", "message");
```

On IL2CPP builds, reflection depends on the type and member surviving stripping. Add link.xml preservation or a project wrapper when a reflected type is stripped.

### Runtime UI Automation Pattern

For black-box phone automation, prefer observation before action:

```js
var root = CS.UnityEngine.GameObject.Find("UICanvas");
return {
  hasUiCanvas: !!root,
  screen: {
    width: CS.UnityEngine.Screen.width,
    height: CS.UnityEngine.Screen.height
  }
};
```

Then use runtime MCP tools such as:

- `screen.screenshot`
- `runtime.ui.snapshot`
- `runtime.ui.find`
- `runtime.ui.raycast`
- `runtime.ui.click`
- `input.tap`

For stable game workflows, put project-specific logic into `puerts-unity-mcp-extension/Runtime/runtime-tools` instead of repeatedly generating one-off eval scripts.

### Return Values

Return JSON-serializable values: strings, numbers, booleans, arrays, and plain objects. Avoid returning raw Unity objects directly.

```js
var camera = CS.UnityEngine.Camera.main;
return {
  hasMainCamera: !!camera,
  cameraName: camera ? camera.name : ""
};
```

## Protocol Surface

HTTP endpoints:

| Endpoint | Purpose |
|---|---|
| `GET /health` | Endpoint metadata, runtime state, capability summary |
| `GET /api/ping` | Lightweight health alias |
| `POST /mcp` | Synchronous JSON-RPC MCP calls |

Main MCP methods:

- `initialize`
- `ping`
- `tools/list`
- `tools/call`

JSON serialization in C# uses Unity `JsonUtility`. The package does not depend on Newtonsoft.Json or any other third-party JSON library.

## Configuration And State

Persistent project configuration:

| Path | Purpose |
|---|---|
| `puerts-unity-mcp-extension/editor-mcp-config.json` | Editor, agent, target selection, LAN discovery config |
| `puerts-unity-mcp-extension/mobile-mcp-config.json` | Runtime/player config copied into builds |
| `Packages/puerts-unity-mcp/Runtime/Plugins/Android` | Bundled MCP Android permission library; PuerTS native libraries come from the upstream UPM packages under `third_party/puerts` |
| `puerts-unity-mcp-extension/Runtime/Plugins/puerts_il2cpp` | Generated PuerTS IL2CPP bridge files for the current Unity project; ignore/regenerate instead of committing as reusable package source |
| `puerts-unity-mcp-extension/Editor/editor-tools` | Project Editor JS MCP tools |
| `puerts-unity-mcp-extension/Runtime/runtime-tools` | Project Runtime JS MCP tools |
| `puerts-unity-mcp-extension/skills` | Project skills for agents |

Temporary state and operation data:

| Path | Purpose |
|---|---|
| `.puerts-unity-mcp/editors/{editorId}/heartbeat.json` | Editor heartbeat |
| `.puerts-unity-mcp/players/{playerId}/heartbeat.json` | Optional player heartbeat |
| `.puerts-unity-mcp/ops/{operationId}` | Persistent operation state/result |
| `.puerts-unity-mcp/temp/compile-results` | Compile result hints |

## Unity Project `.gitignore`

Add these entries to the Unity project `.gitignore`:

```gitignore
# PuerTS Unity MCP runtime state and generated project-local files
.puerts-unity-mcp/
puerts-unity-mcp-extension/Runtime/Generated/
puerts-unity-mcp-extension/Runtime/Plugins/puerts_il2cpp/
```

Do not ignore the whole `puerts-unity-mcp-extension` directory. Project configs, JS tools, and skills under that directory are persistent project assets and may be committed when they are intended to travel with the project. Do not ignore `puerts-unity-mcp/Packages/puerts-unity-mcp/Runtime/Plugins/Android`; that folder contains the MCP Android permission library. Upstream PuerTS `.so` files come from `third_party/puerts`; do not duplicate them inside the MCP package.

`puerts-unity-mcp/third_party/puerts/unity/.gitignore` comes from upstream PuerTS and ignores `*.meta` files Unity may generate for the vendored PuerTS UPM packages. Seeing those files after opening the Unity project is normal; do not commit them. Native plugin `.meta` files that upstream PuerTS already provides remain part of the source tree.

## Directory Structure

```text
puerts-unity-mcp
  Packages/puerts-unity-mcp
    Editor/      Editor MCP endpoint and Unity menus
    Runtime/     Runtime MCP assembly for Editor Play Mode, Android, iOS, and standalone builds
      Plugins/   Runtime native/plugin assets bundled with the package
    Tools~/      Node install, build, sync, and stdio proxy tools
    Tests/       Unity Editor tests
  docs/
    protocol.md
  third_party/puerts/
    Vendored PuerTS UPM packages and native plugins
```

## Notes Before Publishing

No root `LICENSE` file is currently present. Add a license before publishing the repository publicly.
