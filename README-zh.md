<h1 align="center">PuerTS Unity MCP</h1>
<p align="center">
  <strong>通过 MCP 控制 Unity Editor、Play Mode 和真实手机游戏，并在运行中的游戏里动态执行 PuerTS JavaScript。</strong>
  <br />
  <em>Android · iOS · IL2CPP · Editor JS · Runtime JS · C# 和 JS MCP Tool · Domain Reload 恢复</em>
</p>

<p align="center">
  <a href="#快速开始"><img src="https://img.shields.io/badge/Quick_Start-4CAF50?style=for-the-badge" alt="Quick Start" /></a>
  <a href="#agent-puerts-js-速查"><img src="https://img.shields.io/badge/Agent_JS_Guide-1976D2?style=for-the-badge" alt="Agent JS Guide" /></a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Unity-2021.3%2B-black?style=flat&logo=unity&logoColor=white" alt="Unity 2021.3+" />
  <img src="https://img.shields.io/badge/PuerTS-3.0.2-blue?style=flat" alt="PuerTS 3.0.2" />
  <img src="https://img.shields.io/badge/MCP-JSON--RPC-6A5ACD?style=flat" alt="MCP JSON-RPC" />
  <img src="https://img.shields.io/badge/IL2CPP-supported-2E7D32?style=flat" alt="IL2CPP supported" />
</p>

<p align="center">
  <a href="README.md">English</a> · 中文
</p>

---

## 功能特性

| 能力 | 说明 |
|---|---|
| 手机直连动态调试 | Agent 可以直接连接 Android、iOS 或 standalone Unity Player，在真实运行中的游戏里执行 PuerTS JavaScript。 |
| 支持 IL2CPP Player | 构建脚本会加入 PuerTS 包、native plugin、StreamingAssets 配置、Android 权限库和保留提示，用于手机和 IL2CPP 构建。 |
| Editor 执行 JS 不触发 Domain Reload | `editor.js.eval` 在 Editor PuerTS VM 里执行 JS，不生成 C# 文件，不调用 `AssetDatabase.Refresh`，正常自动化流程不会触发 Unity domain reload。 |
| Runtime 执行 JS | `runtime.js.eval` 可以指向本地 Play Mode，也可以指向远程 Player MCP，包括手机。 |
| C# 和 JS 扩展 MCP Tool | 核心工具用 C# 写，项目工具可以放在 `puerts-unity-mcp-extension/Editor/editor-tools` 和 `Runtime/runtime-tools` 里用 JS 写。 |
| Domain Reload 稳定性 | Editor MCP 会持久化 operation、compile result、reload hint，并在 Unity domain reload 后自动恢复 HTTP endpoint。 |

## 它控制什么

PuerTS Unity MCP 把每个可控制的 Unity 整体都看成一个 endpoint。

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

Editor Play Mode 不是第三种 MCP。它是运行在 Unity Editor 进程内的同一套 Runtime MCP 实现。

## 快速开始

### 拉取 PuerTS 依赖

```bash
node Packages/puerts-unity-mcp/Tools~/vendor-puerts.mjs
```

这个命令会下载并校验 PuerTS `Unity_v3.0.2` Core 和 V8 包，放到 `third_party/puerts`。

### 同步到 Unity 工程

```bash
node Packages/puerts-unity-mcp/Tools~/sync-local-package.mjs --unity-project-root <UnityProject>
```

Unity 工程里会出现：

```text
<UnityProject>/puerts-unity-mcp
<UnityProject>/puerts-unity-mcp-extension
<UnityProject>/.puerts-unity-mcp
```

### 注册本地 UPM 包

同步脚本会在 `Packages/manifest.json` 里加入三个本地依赖：

```json
{
  "dependencies": {
    "com.tencent.puerts.core": "file:../puerts-unity-mcp/third_party/puerts/unity/upms/core",
    "com.tencent.puerts.v8": "file:../puerts-unity-mcp/third_party/puerts/unity/upms/v8",
    "puerts-unity-mcp": "file:../puerts-unity-mcp/Packages/puerts-unity-mcp"
  }
}
```

### 配置 Agent

同步后打开 Unity 工程内的 Agent 配置说明：

```text
<UnityProject>/puerts-unity-mcp/Packages/puerts-unity-mcp/setup-for-agent.md
```

Codex 的 MCP 配置示例：

```toml
[mcp_servers."puerts-unity-mcp"]
command = "node"
args = [
  "<UnityProject>/puerts-unity-mcp/Packages/puerts-unity-mcp/Tools~/puerts-unity-mcp-stdio-proxy.js",
  "--config",
  "<UnityProject>/puerts-unity-mcp-extension/editor-mcp-config.json"
]
```

## 手机和 IL2CPP 构建

QA 手机和真实 Player 构建需要把 Runtime MCP 编进包：

```bash
node <UnityProject>/puerts-unity-mcp/Packages/puerts-unity-mcp/Tools~/add-pum-to-build.mjs --unity-project-root <UnityProject>
```

从 Player 构建里移除：

```bash
node <UnityProject>/puerts-unity-mcp/Packages/puerts-unity-mcp/Tools~/remove-pum-from-build.mjs --unity-project-root <UnityProject>
```

`add-pum-to-build.mjs` 会做这些事：

- 添加本地 PuerTS 和 PuerTS Unity MCP package 依赖
- 把 `puerts-unity-mcp-extension/mobile-mcp-config.json` 复制到 `Assets/StreamingAssets/PuertsUnityMcp/mobile-mcp-config.json`
- 确认 `Packages/puerts-unity-mcp/Plugins/Android` 下随包提供的 Android native libraries 和权限库存在
- 使用适合手机的低 IO 默认配置

`remove-pum-from-build.mjs` 会移除构建依赖和复制到 `StreamingAssets` 的配置，但不会删除 package 自带的 Android plugin 文件。

手机里的 Runtime MCP 会暴露：

```text
GET  /health
POST /mcp
```

Agent 不开 Unity Editor 也可以直接连手机：

```bash
node <UnityProject>/puerts-unity-mcp/Packages/puerts-unity-mcp/Tools~/puerts-unity-mcp-stdio-proxy.js \
  --config <UnityProject>/puerts-unity-mcp-extension/editor-mcp-config.json \
  --target-kind player \
  --target-url http://PHONE_IP:18991
```

LAN discovery 使用 UDP `18992` 和 `name_group`。如果办公 Wi-Fi、AP 隔离、跨 VLAN、VPN 策略或防火墙禁掉 UDP broadcast/multicast，可以使用 HTTP fallback：

```json
{
  "selectedTargetKind": "player",
  "name_group": "default",
  "lanHttpProbeHosts": ["192.168.1.55"],
  "lanHttpProbeCidrs": ["192.168.1.0/24"],
  "lanHttpProbeTimeoutMs": 1000
}
```

## Editor JS 不触发 Domain Reload

使用 `editor.js.eval` 做 Editor 自动化。它在现有 Editor PuerTS VM 中执行 JS，不创建 C# 脚本，也不触发编译。

```json
{
  "name": "editor.js.eval",
  "arguments": {
    "mode": "expression",
    "code": "CS.UnityEditor.EditorApplication.isPlaying"
  }
}
```

这和临时生成 C# 文件完全不同。JS eval 不调用 `AssetDatabase.Refresh`，所以常规 Editor 自动化流程不会触发 Unity domain reload，操作会更流畅。

如果项目 C# 修改导致 domain reload 无法避免，Editor MCP 会把 operation 状态写到 `.puerts-unity-mcp/ops`，保存 compile result hint，并在 `afterAssemblyReload` 后恢复 HTTP endpoint。

## MCP Tool 扩展

核心工具由 C# 注册。项目工具可以用 JS 写，放在 Unity 工程的 extension 目录里。

```text
<UnityProject>/puerts-unity-mcp-extension
  Editor/editor-tools      Editor 侧 JavaScript MCP tools
  Runtime/runtime-tools    Runtime / Player 侧 JavaScript MCP tools
  skills                   给 Agent 使用的项目技能
```

每个 JavaScript MCP tool 使用一个 manifest 指向模块：

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

Runtime JS tool 会通过 `runtime.js.eval` 执行，所以同一套工具模型可以同时用于 Play Mode 和真实手机。

## Agent PuerTS JS 速查

这一节是专门写给 Agent 的，用于生成 `editor.js.eval`、`runtime.js.eval` 或项目 JavaScript MCP tool 的代码。

### 选择目标 VM

| 任务 | Tool | VM |
|---|---|---|
| Unity Editor 自动化 | `editor.js.eval` | Editor PuerTS VM |
| Play Mode Runtime 自动化 | `runtime.js.eval` 指向本地 Play Mode target | Runtime PuerTS VM |
| Android、iOS、standalone 自动化 | `runtime.js.eval` 指向 `targetId` 或 `httpUrl` | 手机或 Player 里的 Runtime PuerTS VM |

Editor 和 Runtime 是两个独立的 PuerTS `ScriptEnv`。Editor 代码可以使用 `UnityEditor` API。Runtime 和手机代码应使用运行时安全的 API。

### 基础 PuerTS 写法

优先使用 PuerTS 的 `CS` 全局对象。

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

`mode: "expression"` 会自动返回表达式：

```js
CS.UnityEngine.Application.version
```

`mode: "script"` 需要显式 `return`：

```js
var go = CS.UnityEngine.GameObject.Find("Canvas");
return {
  found: !!go,
  name: go ? go.name : ""
};
```

### 反射 fallback

如果某个 C# 类型没有生成 wrap，使用 `__unity_mcp`。这个项目当前走 reflection-first，适合开发阶段和项目特有的 IL2CPP 排查。

```js
return __unity_mcp.invokeStatic(
  "UnityEngine.Debug",
  "Log",
  "hello through reflection"
);
```

常用 helper：

```js
__unity_mcp.typeExists("UnityEngine.Application");
__unity_mcp.getStatic("UnityEngine.Application", "productName");
__unity_mcp.getStaticPath("UnityEngine.Screen", "width");
__unity_mcp.setStatic("UnityEngine.Time", "timeScale", 1);
__unity_mcp.invokeStatic("UnityEngine.Debug", "Log", "message");
```

在 IL2CPP 包里，反射取决于类型和成员是否被 stripping。遇到被裁剪的类型时，用 link.xml 或项目包装类补保留。

### 手机 UI 自动化模式

黑盒自动玩手机游戏时，先观察，再操作。

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

然后组合 runtime MCP 工具：

- `screen.screenshot`
- `runtime.ui.snapshot`
- `runtime.ui.find`
- `runtime.ui.raycast`
- `runtime.ui.click`
- `input.tap`

稳定的项目流程不要一直生成一次性 eval 脚本，应该沉淀到 `puerts-unity-mcp-extension/Runtime/runtime-tools`。

### 返回值规则

返回 JSON 可序列化数据：字符串、数字、布尔值、数组和普通对象。不要直接返回 Unity 对象。

```js
var camera = CS.UnityEngine.Camera.main;
return {
  hasMainCamera: !!camera,
  cameraName: camera ? camera.name : ""
};
```

## 协议表面

HTTP endpoints：

| Endpoint | 用途 |
|---|---|
| `GET /health` | endpoint 元数据、运行状态、能力摘要 |
| `GET /api/ping` | 轻量 health alias |
| `POST /mcp` | 同步 JSON-RPC MCP 调用 |

主要 MCP methods：

- `initialize`
- `ping`
- `tools/list`
- `tools/call`

C# 侧 JSON 序列化只使用 Unity `JsonUtility`。项目不依赖 Newtonsoft.Json 或其他第三方 JSON 库。

## 配置和状态目录

持久项目配置：

| 路径 | 用途 |
|---|---|
| `puerts-unity-mcp-extension/editor-mcp-config.json` | Editor、Agent、target 选择、LAN discovery 配置 |
| `puerts-unity-mcp-extension/mobile-mcp-config.json` | Runtime / Player 配置，会复制进构建 |
| `Packages/puerts-unity-mcp/Plugins/Android` | 随包提供的 Android PuerTS native libraries 和 MCP 权限库 |
| `puerts-unity-mcp-extension/Editor/editor-tools` | 项目 Editor JS MCP tools |
| `puerts-unity-mcp-extension/Runtime/runtime-tools` | 项目 Runtime JS MCP tools |
| `puerts-unity-mcp-extension/skills` | 给 Agent 使用的项目技能 |

临时状态和 operation 数据：

| 路径 | 用途 |
|---|---|
| `.puerts-unity-mcp/editors/{editorId}/heartbeat.json` | Editor heartbeat |
| `.puerts-unity-mcp/players/{playerId}/heartbeat.json` | 可选 Player heartbeat |
| `.puerts-unity-mcp/ops/{operationId}` | 持久 operation 状态和结果 |
| `.puerts-unity-mcp/temp/compile-results` | 编译结果提示 |

## 目录结构

```text
puerts-unity-mcp
  Packages/puerts-unity-mcp
    Editor/      Editor MCP endpoint 和 Unity 菜单
    Runtime/     Editor Play Mode 使用的 Runtime MCP assembly
    Player/      Android、iOS 和 standalone 构建使用的非 Editor Player MCP assembly
    Tools~/      Node 安装、构建、同步和 stdio proxy 工具
    Tests/       Unity Editor tests
  docs/
    protocol.md
  third_party/puerts/
    Vendored PuerTS UPM packages 和 native plugins
```

## 发布前注意

当前根目录没有 `LICENSE` 文件。公开发布前建议补一个 license。
