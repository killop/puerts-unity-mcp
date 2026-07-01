# PuerTS Unity MCP Protocol

## Endpoint Model

```text
                         Agent / MCP client
                                |
                        stdio MCP JSON-RPC
                                |
                         Node stdio proxy
                                |
                    HTTP JSON-RPC POST /mcp
                                |
        +-----------------------+-----------------------+
        |                                               |
  Unity Editor MCP                               Runtime MCP
  endpointKind=editor                            endpointKind=player
  one open Editor project                        one running game whole
  C# + Editor PuerTS VM                          C# + Runtime PuerTS VM
        |                                               ^
        | direct in-process route when Play Mode exists |
        +------------------ Play Mode Runtime Host -----+

Phone / APK / IPA / standalone players expose the same Runtime MCP HTTP endpoint
as Editor Play Mode. The PC agent can connect to them directly without opening
the Unity Editor.
```

Implementation assemblies:

- `PuertsUnityMcp.Editor`: Editor-only UI, build hook, and Editor MCP.
- `PuertsUnityMcp.Runtime`: Runtime MCP implementation shared by Editor Play Mode, Android, iOS, and standalone builds.

There are two endpoint kinds:

- `editor`: controls the Unity Editor as a whole, including Editor APIs and local Play Mode runtime routing
- `player`: controls a running game as a whole, including Editor Play Mode, Android, iOS, and standalone builds

Editor Play Mode is not a third MCP kind. It is the same Runtime MCP implementation running inside the Editor process.

## HTTP Transport

```text
GET  /health
GET  /api/ping
POST /mcp
```

`POST /mcp` accepts synchronous JSON-RPC:

```json
{
  "jsonrpc": "2.0",
  "id": "1",
  "method": "tools/call",
  "params": {
    "name": "runtime.js.eval",
    "arguments": {
      "targetId": "playmode",
      "mode": "expression",
      "code": "1 + 1"
    }
  }
}
```

Supported JSON-RPC methods:

- `initialize`
- `ping`
- `tools/list`
- `tools/call`
- direct tool-name calls, for example `editor.state`

Current implementation constraint: all C# JSON serialization uses Unity `JsonUtility`. There is no third-party JSON dependency and no JSON DOM. For this reason, dynamic schema/result objects are carried as JSON strings:

- `tools/list`: each descriptor has `inputSchemaJson`
- `tools/call`: result has `structuredContentJson`

## Explicit Remote Targeting

Remote Editor and phone/player MCP endpoints are never auto-discovered. There is
no UDP broadcast/multicast, no HTTP subnet scan, and no `name_group` filtering in
the protocol. A PC agent connects to remote Unity by an explicit URL:

- pass `--target-url http://PHONE_OR_PC_IP:PORT` to the stdio proxy
- set `PUERTS_UNITY_MCP_TARGET_URL`
- set `selectedTargetUrl` in `puerts-unity-mcp-extension/editor-mcp-config.json`
- pass `httpUrl` directly to remote-capable tools such as `runtime.js.eval`

The project-local heartbeat cache under `.puerts-unity-mcp` remains for the
local Editor endpoint and optional local Runtime file-command flows, not for LAN
discovery.

## File Command Pump

Inspired by AIBridge, the Editor endpoint has a file exchange queue for domain
reload recovery and local automation. Runtime endpoints can enable the same pump
with `enableFileCommandPump`, but the default Runtime MCP path is HTTP JSON-RPC.

```text
.puerts-unity-mcp/
  editors/{editorId}/
    heartbeat.json
    commands/{commandId}.json
    results/{commandId}.json
  players/{playerId}/
    heartbeat.json        optional enableDiskHeartbeat
    commands/{commandId}.json optional enableFileCommandPump
    results/{commandId}.json  optional enableFileCommandPump
  ops/{operationId}/
    request.json
    state.json
    result.json
```

Command file:

```json
{
  "id": "cmd_001",
  "action": "editor.state",
  "targetId": "editor",
  "params": {}
}
```

Result file:

```json
{
  "id": "cmd_001",
  "success": true,
  "resultJson": "{\"status\":\"ok\"}",
  "completedAtUtc": "2026-06-11T00:00:00.0000000Z",
  "executionTimeMs": 12
}
```

## Reload And Compile Hints

Inspired by uLoop, reload and compile state is exposed through lock files:

```text
Temp/puerts-unity-mcp/domainreload.lock
Temp/puerts-unity-mcp/compiling.lock
Temp/puerts-unity-mcp/serverstarting.lock
Temp/puerts-unity-mcp/compile-results/{requestId}.json
```

The Editor endpoint stops its HTTP listener before domain reload, stores `WasRunning` and port in Editor state, and restarts on `afterAssemblyReload`/startup delay.
Runtime endpoints do not rely on C# domain reload recovery in player builds. In
Editor Play Mode, after a script compile/domain reload, the Runtime Host is
recreated and exposes the same HTTP tool surface again.

## JS VM Split

```text
Unity Editor process
  Editor MCP C# endpoint
    Editor PuerTS VM
      editor.js.eval
      UnityEditor reflection
    Play Mode Runtime Host
      Runtime MCP C# endpoint
      Runtime PuerTS VM
      runtime.js.eval targetId=playmode

Player process
  Runtime MCP C# endpoint
    Runtime PuerTS VM
      runtime.js.eval
      runtime tools
```

Editor JS and Runtime JS are separate PuerTS `ScriptEnv` instances. The Editor endpoint routes to Play Mode by direct C# call to `UnityMcpRuntimeHost.Instance`, not by internal MCP-over-MCP.

## Direct Remote Player Routing

A remote phone Player is called directly with an HTTP URL. The package does not
create persistent manual Player registrations from MCP calls.

```json
{
  "name": "runtime.js.eval",
  "arguments": {
    "httpUrl": "http://192.168.1.55:18991",
    "mode": "expression",
    "code": "__unity_mcp.getStatic('UnityEngine.Application', 'productName')"
  }
}
```

If `selectedTargetUrl` is set to a Player URL, `runtime.targets.list` exposes a
configured direct target and calls can route by `targetId`:

```json
{
  "name": "runtime.js.eval",
  "arguments": {
    "targetId": "android-devkit-01",
    "mode": "expression",
    "code": "__unity_mcp.getStatic('UnityEngine.Application', 'productName')"
  }
}
```
