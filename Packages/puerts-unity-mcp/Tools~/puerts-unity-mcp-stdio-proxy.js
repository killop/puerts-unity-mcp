#!/usr/bin/env node

const fs = require("fs");
const http = require("http");
const https = require("https");
const path = require("path");
const readline = require("readline");

const STATE_DIR_NAME = ".puerts-unity-mcp";
const EXTENSION_DIR_NAME = "puerts-unity-mcp-extension";
const EDITOR_DIR_NAME = "Editor";
const RUNTIME_DIR_NAME = "Runtime";
const EDITOR_TOOLS_DIR_NAME = "editor-tools";
const RUNTIME_TOOLS_DIR_NAME = "runtime-tools";
const SKILLS_DIR_NAME = "skills";
const EDITOR_CONFIG_FILE_NAME = "editor-mcp-config.json";
const LEGACY_EDITOR_CONFIG_FILE_NAME = "config.json";
const PROJECT_EXTENSION_CONFIG_RELATIVE = path.join(EXTENSION_DIR_NAME, EDITOR_CONFIG_FILE_NAME);
const LEGACY_PROJECT_EXTENSION_CONFIG_RELATIVE = path.join(EXTENSION_DIR_NAME, EDITOR_DIR_NAME, LEGACY_EDITOR_CONFIG_FILE_NAME);
const DEFAULT_CONNECT_TIMEOUT_MS = 30000;
const LOCAL_EXTENSION_TOOL_PREFIX = "agent.extension.";
const DEFAULT_MAX_FILE_BYTES = 1024 * 1024;
const AGENT_INSTRUCTIONS =
  "PuerTS Unity MCP controls Unity Editor, Play Mode, and real Player/phone targets. " +
  "Use editor.js.eval for Unity Editor automation; it runs JavaScript in the Editor PuerTS VM and normally does not generate C# or trigger domain reload. " +
  "Use runtime.js.eval for Play Mode, Android, iOS, or standalone Player automation; pass httpUrl or configure selectedTargetUrl when targeting a remote phone/player. " +
  "Write PuerTS JavaScript with CS.UnityEngine/CS.UnityEditor first, return only JSON-serializable values, and do not return Unity objects directly. " +
  "If a wrapped C# type or member is unavailable, use __unity_mcp.typeExists/getStatic/getStaticPath/setStatic/invokeStatic as the reflection fallback. " +
  "For Editor scene/window context, use editor.hierarchy.get or get-hierarchy to export hierarchy JSON, editor.window.screenshot or screenshot to capture EditorWindow PNGs, and editor.window.focus or focus-window to bring Unity forward. " +
  "For phone UI automation, observe before acting with screen.screenshot, runtime.ui.snapshot, runtime.ui.find, and runtime.ui.raycast, then click with runtime.ui.click or input.tap. " +
  "For performance hotspot diagnosis, use the Editor MCP Profiler tools: editor.profiler.targets.list, editor.profiler.connect when needed, then editor.profiler.capture or performance.hotspot.report. These record through the Unity Editor Profiler, can analyze Editor or attached phone/player Profiler data, and write JSON/CSV/Markdown reports under .puerts-unity-mcp/perf-reports. " +
  "Move stable project-specific flows into puerts-unity-mcp-extension/Editor/editor-tools or puerts-unity-mcp-extension/Runtime/runtime-tools instead of repeatedly generating one-off eval scripts.";

let lastRemoteToolNames = null;

function readArg(name, fallback) {
  const index = process.argv.indexOf(name);
  if (index >= 0 && index + 1 < process.argv.length) {
    return process.argv[index + 1];
  }

  return fallback;
}

function readNumberArg(name, fallback) {
  const value = Number(readArg(name, fallback));
  return Number.isFinite(value) && value >= 0 ? value : Number(fallback);
}

function readJsonFile(filePath) {
  if (!filePath || !fs.existsSync(filePath)) {
    return null;
  }

  const content = stripBom(fs.readFileSync(filePath, "utf8"));
  return JSON.parse(content);
}

function trimTrailingSlash(value) {
  return String(value || "").replace(/[\\/]+$/, "");
}

function isNonEmptyString(value) {
  return typeof value === "string" && value.trim().length > 0;
}

function stripBom(value) {
  return String(value || "").replace(/^\uFEFF/, "");
}

function displayHost(bindAddress) {
  if (!bindAddress || bindAddress === "0.0.0.0" || bindAddress === "*" || bindAddress === "+") {
    return "127.0.0.1";
  }

  return bindAddress;
}

function toMcpUrl(httpUrl) {
  const trimmed = trimTrailingSlash(httpUrl);
  if (!trimmed) {
    return "";
  }

  return trimmed.endsWith("/mcp") ? trimmed : `${trimmed}/mcp`;
}

function pathEndsWith(filePath, suffix) {
  return path.normalize(filePath || "").toLowerCase().endsWith(path.normalize(suffix || "").toLowerCase());
}

function isFreshHeartbeat(heartbeat) {
  if (!heartbeat || !isNonEmptyString(heartbeat.lastUpdatedUtc)) {
    return true;
  }

  const timestamp = Date.parse(heartbeat.lastUpdatedUtc);
  if (!Number.isFinite(timestamp)) {
    return true;
  }

  return Date.now() - timestamp < 10 * 60 * 1000;
}

function resolveUnityProjectPathFromConfig(configPath) {
  const configDirectory = path.dirname(configPath);
  if (path.basename(configDirectory) === STATE_DIR_NAME) {
    return path.dirname(configDirectory);
  }

  if (pathEndsWith(configPath, PROJECT_EXTENSION_CONFIG_RELATIVE)) {
    return path.resolve(path.dirname(configPath), "..");
  }

  if (pathEndsWith(configPath, LEGACY_PROJECT_EXTENSION_CONFIG_RELATIVE)) {
    return path.resolve(path.dirname(configPath), "..", "..");
  }

  return "";
}

function resolveDefaultConfigPath() {
  const extensionConfig = path.resolve(process.cwd(), PROJECT_EXTENSION_CONFIG_RELATIVE);
  if (fs.existsSync(extensionConfig)) {
    return extensionConfig;
  }

  const legacyExtensionConfig = path.resolve(process.cwd(), LEGACY_PROJECT_EXTENSION_CONFIG_RELATIVE);
  if (fs.existsSync(legacyExtensionConfig)) {
    return legacyExtensionConfig;
  }

  return path.resolve(process.cwd(), STATE_DIR_NAME, "config.json");
}

function resolveConfiguredConfigPath() {
  const configured = readArg("--config", process.env.PUERTS_UNITY_MCP_CONFIG || resolveDefaultConfigPath());
  return configured ? path.resolve(configured) : "";
}

function resolveStateRootFromConfig(configPath) {
  const unityProjectPath = resolveUnityProjectPathFromConfig(configPath);
  if (isNonEmptyString(unityProjectPath)) {
    return path.join(unityProjectPath, STATE_DIR_NAME);
  }

  return path.dirname(configPath);
}

function resolveExtensionRoot(configPath, unityProjectPath) {
  const explicit = readArg("--extension-root", process.env.PUERTS_UNITY_MCP_EXTENSION_DIR || "");
  if (isNonEmptyString(explicit)) {
    return path.resolve(explicit);
  }

  if (isNonEmptyString(configPath) && pathEndsWith(configPath, PROJECT_EXTENSION_CONFIG_RELATIVE)) {
    return path.dirname(configPath);
  }

  if (isNonEmptyString(configPath) && pathEndsWith(configPath, LEGACY_PROJECT_EXTENSION_CONFIG_RELATIVE)) {
    return path.resolve(path.dirname(configPath), "..");
  }

  if (isNonEmptyString(unityProjectPath)) {
    return path.join(unityProjectPath, EXTENSION_DIR_NAME);
  }

  return path.resolve(process.cwd(), EXTENSION_DIR_NAME);
}

function resolveProxyContext(requireConfig) {
  const configPath = resolveConfiguredConfigPath();
  if (requireConfig && (!configPath || !fs.existsSync(configPath))) {
    throw new Error(`Unity MCP config not found: ${configPath}`);
  }

  const config = configPath && fs.existsSync(configPath) ? (readJsonFile(configPath) || {}) : {};
  const resolvedProjectPath = configPath && fs.existsSync(configPath) ? resolveUnityProjectPathFromConfig(configPath) : "";
  const unityProjectPath = isNonEmptyString(resolvedProjectPath) ? resolvedProjectPath : process.cwd();
  const stateRoot = configPath && fs.existsSync(configPath)
    ? resolveStateRootFromConfig(configPath)
    : path.join(unityProjectPath, STATE_DIR_NAME);

  return {
    configPath,
    config,
    unityProjectPath,
    stateRoot,
    selector: readTargetSelector(config),
    extensionRoot: resolveExtensionRoot(configPath, unityProjectPath)
  };
}

function readTargetSelector(config) {
  const targetKind = readArg(
    "--target-kind",
    process.env.PUERTS_UNITY_MCP_TARGET_KIND || (config && config.selectedTargetKind) || "editor"
  );
  const targetId = readArg(
    "--target-id",
    process.env.PUERTS_UNITY_MCP_TARGET_ID || (config && config.selectedTargetId) || ""
  );
  const targetName = readArg(
    "--target-name",
    process.env.PUERTS_UNITY_MCP_TARGET_NAME || (config && config.selectedTargetName) || ""
  );
  const targetUrl = readArg(
    "--target-url",
    process.env.PUERTS_UNITY_MCP_TARGET_URL || (config && config.selectedTargetUrl) || ""
  );

  return {
    kind: String(targetKind || "editor").trim().toLowerCase(),
    id: String(targetId || "").trim(),
    name: String(targetName || "").trim(),
    url: String(targetUrl || "").trim()
  };
}

function resolveFromInstances(stateRoot, unityProjectPath) {
  const document = readJsonFile(path.join(stateRoot, "instances.json"));
  const instances = document && Array.isArray(document.instances) ? document.instances : [];
  const normalizedProjectPath = unityProjectPath ? path.resolve(unityProjectPath).toLowerCase() : "";
  const candidates = instances
    .filter(entry => entry && entry.endpointKind === "editor" && isNonEmptyString(entry.httpUrl))
    .filter(entry => !normalizedProjectPath || !entry.projectRoot || path.resolve(entry.projectRoot).toLowerCase() === normalizedProjectPath)
    .filter(isFreshHeartbeat)
    .sort((left, right) => Date.parse(right.lastUpdatedUtc || 0) - Date.parse(left.lastUpdatedUtc || 0));

  if (candidates.length === 0) {
    return "";
  }

  return toMcpUrl(candidates[0].httpUrl);
}

function resolveFromEditorConfig(config, stateRoot) {
  config = config || readJsonFile(path.join(stateRoot, "config.json"));
  if (!config || !Number.isFinite(Number(config.editorPort)) || Number(config.editorPort) <= 0) {
    return "";
  }

  return `http://${displayHost(config.editorBindAddress)}:${Number(config.editorPort)}/mcp`;
}

async function resolveEndpointUrl() {
  const explicitUrl = readArg("--url", process.env.PUERTS_UNITY_MCP_URL || "");
  if (isNonEmptyString(explicitUrl)) {
    return toMcpUrl(explicitUrl);
  }

  const context = resolveProxyContext(true);
  const configPath = context.configPath;
  const config = context.config;
  const stateRoot = context.stateRoot;
  const unityProjectPath = context.unityProjectPath;
  const selector = context.selector;
  if (isNonEmptyString(selector.url)) {
    return toMcpUrl(selector.url);
  }

  if (selector.kind === "player") {
    throw new Error("Player MCP targets require an explicit URL. Set selectedTargetUrl in editor-mcp-config.json, pass --target-url http://PHONE_IP:18991, or set PUERTS_UNITY_MCP_TARGET_URL.");
  }

  const explicitEditorSelection = selector.kind === "editor" && (isNonEmptyString(selector.id) || isNonEmptyString(selector.name));
  if (selector.kind === "editor" && !explicitEditorSelection) {
    const fromInstances = resolveFromInstances(stateRoot, unityProjectPath);
    if (fromInstances) {
      return fromInstances;
    }

    const fromEditorConfig = resolveFromEditorConfig(config, stateRoot);
    if (fromEditorConfig) {
      return fromEditorConfig;
    }

    throw new Error(`No local Unity Editor MCP endpoint found for config ${configPath}. Set selectedTargetId, selectedTargetName, selectedTargetUrl, --target-id, --target-name, or --url to connect to a remote Editor/Player.`);
  }

  if (selector.kind === "editor" && explicitEditorSelection) {
    throw new Error("Remote Editor MCP targets require an explicit URL. Set selectedTargetUrl in editor-mcp-config.json, pass --target-url http://PC_IP:18990, or set PUERTS_UNITY_MCP_TARGET_URL.");
  }

  const fromEditorConfig = resolveFromEditorConfig(config, stateRoot);
  if (fromEditorConfig) {
    return fromEditorConfig;
  }

  throw new Error(`No reachable Unity MCP endpoint found for config ${configPath}`);
}

function postJson(url, body) {
  return new Promise((resolve, reject) => {
    const parsed = new URL(url);
    const client = parsed.protocol === "https:" ? https : http;
    const request = client.request(
      {
        method: "POST",
        protocol: parsed.protocol,
        hostname: parsed.hostname,
        port: parsed.port,
        path: `${parsed.pathname}${parsed.search}`,
        headers: {
          "content-type": "application/json",
          "content-length": Buffer.byteLength(body)
        }
      },
      response => {
        let data = "";
        response.setEncoding("utf8");
        response.on("data", chunk => {
          data += chunk;
        });
        response.on("end", () => {
          if (response.statusCode >= 200 && response.statusCode < 300) {
            resolve(data);
            return;
          }

          reject(new Error(`HTTP ${response.statusCode}: ${data}`));
        });
      }
    );

    request.on("error", reject);
    request.write(body);
    request.end();
  });
}

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function isRetriableConnectError(error) {
  if (!error) {
    return false;
  }

  return error.code === "ECONNREFUSED"
    || error.code === "ECONNRESET"
    || error.code === "ETIMEDOUT"
    || error.code === "EPIPE"
    || /socket hang up/i.test(error.message || "");
}

async function postJsonWithRetry(url, body) {
  const timeoutMs = readNumberArg("--connect-timeout-ms", process.env.PUERTS_UNITY_MCP_CONNECT_TIMEOUT_MS || DEFAULT_CONNECT_TIMEOUT_MS);
  const deadline = Date.now() + timeoutMs;

  while (true) {
    try {
      return await postJson(url, body);
    } catch (error) {
      if (!isRetriableConnectError(error) || Date.now() >= deadline) {
        throw error;
      }

      await sleep(Math.min(500, Math.max(50, deadline - Date.now())));
    }
  }
}

function extractId(line) {
  try {
    const request = JSON.parse(line);
    return Object.prototype.hasOwnProperty.call(request, "id") ? request.id : undefined;
  } catch {
    return undefined;
  }
}

function writeError(id, message) {
  if (id === null || id === undefined) {
    return;
  }

  process.stdout.write(`${JSON.stringify({
    jsonrpc: "2.0",
    id,
    error: {
      code: -32000,
      message
    }
  })}\n`);
}

function objectSchema(properties, required) {
  const schema = {
    type: "object",
    properties: properties || {},
    additionalProperties: false
  };
  if (required && required.length > 0) {
    schema.required = required;
  }

  return schema;
}

function localProxyToolDescriptors() {
  return [
    {
      name: "agent.extension.info",
      description: "Return local puerts-unity-mcp-extension filesystem paths resolved by the stdio proxy.",
      inputSchema: objectSchema()
    },
    {
      name: "agent.extension.files.list",
      description: "List files under the local puerts-unity-mcp-extension directory.",
      inputSchema: objectSchema({
        subdir: { type: "string", description: "Optional extension-relative directory." },
        recursive: { type: "boolean", description: "Scan recursively. Defaults to true." },
        maxFiles: { type: "number", description: "Maximum files to return." }
      })
    },
    {
      name: "agent.extension.file.read",
      description: "Read one UTF-8 text file under the local puerts-unity-mcp-extension directory.",
      inputSchema: objectSchema({
        relativePath: { type: "string" },
        path: { type: "string" },
        maxBytes: { type: "number" }
      })
    },
    {
      name: "agent.extension.scriptTools.list",
      description: "List JavaScript MCP tool manifests from the local extension directory.",
      inputSchema: objectSchema({
        scope: { type: "string", description: "editor, runtime, or all." }
      })
    },
    {
      name: "agent.extension.skills.list",
      description: "List skill documents from the local extension skills directory.",
      inputSchema: objectSchema()
    },
    {
      name: "agent.extension.skill.load",
      description: "Load one skill document from the local extension skills directory by name or relative path.",
      inputSchema: objectSchema({
        name: { type: "string" },
        relativePath: { type: "string" },
        path: { type: "string" }
      })
    }
  ];
}

function isLocalProxyToolName(name) {
  return String(name || "").startsWith(LOCAL_EXTENSION_TOOL_PREFIX);
}

function isPathInside(childPath, rootPath) {
  const root = path.resolve(rootPath || ".");
  const child = path.resolve(childPath || ".");
  const relative = path.relative(root, child);
  return relative === "" || (!!relative && !relative.startsWith("..") && !path.isAbsolute(relative));
}

function resolveUnderRoot(root, relativePath) {
  const resolvedRoot = path.resolve(root || ".");
  const resolvedPath = path.resolve(resolvedRoot, relativePath || ".");
  if (!isPathInside(resolvedPath, resolvedRoot)) {
    throw new Error(`Path escapes extension root: ${relativePath}`);
  }

  return resolvedPath;
}

function relativeToRoot(root, filePath) {
  return path.relative(path.resolve(root), path.resolve(filePath)).replace(/\\/g, "/");
}

function walkFiles(root, recursive, predicate) {
  if (!root || !fs.existsSync(root)) {
    return [];
  }

  const results = [];
  const stack = [root];
  while (stack.length > 0) {
    const current = stack.pop();
    let entries = [];
    try {
      entries = fs.readdirSync(current, { withFileTypes: true });
    } catch {
      continue;
    }

    for (const entry of entries) {
      const entryPath = path.join(current, entry.name);
      if (entry.isDirectory()) {
        if (recursive) {
          stack.push(entryPath);
        }
        continue;
      }

      if (entry.isFile() && (!predicate || predicate(entryPath))) {
        results.push(entryPath);
      }
    }
  }

  results.sort((left, right) => left.localeCompare(right));
  return results;
}

function listExtensionFiles(args) {
  const context = resolveProxyContext(false);
  const base = resolveUnderRoot(context.extensionRoot, args && args.subdir ? args.subdir : ".");
  const recursive = !args || args.recursive !== false;
  const maxFiles = Math.max(1, Math.min(Number(args && args.maxFiles) || 200, 2000));
  const files = walkFiles(base, recursive)
    .slice(0, maxFiles)
    .map(filePath => {
      const stat = fs.statSync(filePath);
      return {
        relativePath: relativeToRoot(context.extensionRoot, filePath),
        size: stat.size,
        lastModifiedUtc: stat.mtime.toISOString()
      };
    });

  return {
    action: "agent.extension.files.list",
    extensionRoot: context.extensionRoot,
    directoryRoot: base,
    recursive,
    count: files.length,
    files
  };
}

function readExtensionFile(args) {
  const context = resolveProxyContext(false);
  const relativePath = args && (args.relativePath || args.path);
  if (!isNonEmptyString(relativePath)) {
    throw new Error("agent.extension.file.read requires relativePath.");
  }

  const filePath = resolveUnderRoot(context.extensionRoot, relativePath);
  if (!fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
    throw new Error(`Extension file not found: ${relativePath}`);
  }

  const maxBytes = Math.max(1, Number(args && args.maxBytes) || DEFAULT_MAX_FILE_BYTES);
  const stat = fs.statSync(filePath);
  if (stat.size > maxBytes) {
    throw new Error(`Extension file is larger than maxBytes: ${stat.size} > ${maxBytes}`);
  }

  return {
    action: "agent.extension.file.read",
    extensionRoot: context.extensionRoot,
    relativePath: relativeToRoot(context.extensionRoot, filePath),
    size: stat.size,
    lastModifiedUtc: stat.mtime.toISOString(),
    content: stripBom(fs.readFileSync(filePath, "utf8"))
  };
}

function selectedLocalScriptScope(context) {
  const explicit = readArg("--extension-tool-scope", process.env.PUERTS_UNITY_MCP_EXTENSION_TOOL_SCOPE || "");
  const normalized = String(explicit || "").trim().toLowerCase();
  if (normalized === "editor" || normalized === "runtime") {
    return normalized;
  }

  return context && context.selector && context.selector.kind === "player" ? "runtime" : "editor";
}

function scriptToolRoot(extensionRoot, scope) {
  return scope === "runtime"
    ? path.join(extensionRoot, RUNTIME_DIR_NAME, RUNTIME_TOOLS_DIR_NAME)
    : path.join(extensionRoot, EDITOR_DIR_NAME, EDITOR_TOOLS_DIR_NAME);
}

function isToolManifestFile(filePath) {
  const fileName = path.basename(filePath).toLowerCase();
  if (fileName.endsWith(".tool") || fileName.endsWith(".tool.json")) {
    return true;
  }

  if (!fileName.endsWith(".json")) {
    return false;
  }

  try {
    return fs.readFileSync(filePath, "utf8").toLowerCase().includes("\"modulepath\"");
  } catch {
    return false;
  }
}

function stripToolManifestSuffix(filePath) {
  let baseName = path.basename(filePath);
  if (baseName.toLowerCase().endsWith(".tool.json")) {
    return baseName.slice(0, -".tool.json".length);
  }

  if (baseName.toLowerCase().endsWith(".tool")) {
    return baseName.slice(0, -".tool".length);
  }

  return baseName.replace(/\.[^.]+$/, "");
}

function parseSchema(manifest) {
  if (manifest && manifest.inputSchema && typeof manifest.inputSchema === "object") {
    return manifest.inputSchema;
  }

  if (manifest && isNonEmptyString(manifest.inputSchemaJson)) {
    try {
      return JSON.parse(manifest.inputSchemaJson);
    } catch {
      return { type: "object", additionalProperties: true };
    }
  }

  return { type: "object", additionalProperties: true };
}

function normalizeScriptToolManifest(directoryRoot, manifestPath, manifest, scope, extensionRoot) {
  if (!manifest || manifest.disabled || !isNonEmptyString(manifest.name)) {
    return null;
  }

  const manifestDirectory = path.dirname(manifestPath);
  const modulePath = isNonEmptyString(manifest.modulePath)
    ? (path.isAbsolute(manifest.modulePath) ? manifest.modulePath : path.join(manifestDirectory, manifest.modulePath))
    : path.join(manifestDirectory, `${stripToolManifestSuffix(manifestPath)}.mjs`);

  return {
    name: String(manifest.name).trim(),
    description: isNonEmptyString(manifest.description)
      ? String(manifest.description)
      : "Project JavaScript MCP tool loaded from puerts-unity-mcp-extension.",
    inputSchema: parseSchema(manifest),
    inputSchemaJson: isNonEmptyString(manifest.inputSchemaJson) ? manifest.inputSchemaJson : JSON.stringify(parseSchema(manifest)),
    modulePath: path.resolve(modulePath),
    functionName: isNonEmptyString(manifest.functionName) ? String(manifest.functionName) : "execute",
    manifestPath: path.resolve(manifestPath),
    relativeManifestPath: relativeToRoot(extensionRoot, manifestPath),
    relativeModulePath: relativeToRoot(extensionRoot, modulePath),
    directoryRoot,
    scope
  };
}

function loadScriptToolManifests(extensionRoot, scope) {
  const directoryRoot = scriptToolRoot(extensionRoot, scope);
  const manifests = [];
  const files = walkFiles(directoryRoot, true, isToolManifestFile);
  for (const filePath of files) {
    try {
      const manifest = JSON.parse(stripBom(fs.readFileSync(filePath, "utf8")));
      const normalized = normalizeScriptToolManifest(directoryRoot, filePath, manifest, scope, extensionRoot);
      if (normalized) {
        manifests.push(normalized);
      }
    } catch (error) {
      process.stderr.write(`[puerts-unity-mcp] Failed to parse local script tool manifest ${filePath}: ${error.message}\n`);
    }
  }

  manifests.sort((left, right) => left.name.localeCompare(right.name));
  return manifests;
}

function loadScriptToolManifestsForRequest(args) {
  const context = resolveProxyContext(false);
  const requested = String((args && args.scope) || "all").toLowerCase();
  const scopes = requested === "editor" || requested === "runtime"
    ? [requested]
    : ["editor", "runtime"];
  const tools = [];
  for (const scope of scopes) {
    tools.push(...loadScriptToolManifests(context.extensionRoot, scope));
  }

  return {
    action: "agent.extension.scriptTools.list",
    extensionRoot: context.extensionRoot,
    count: tools.length,
    tools
  };
}

function activeLocalScriptToolManifests(context) {
  const scope = selectedLocalScriptScope(context);
  return loadScriptToolManifests(context.extensionRoot, scope);
}

function findActiveLocalScriptTool(name, context) {
  if (!isNonEmptyString(name)) {
    return null;
  }

  const active = activeLocalScriptToolManifests(context);
  for (const manifest of active) {
    if (manifest.name === name) {
      return manifest;
    }
  }

  return null;
}

function isSkillFile(filePath) {
  const extension = path.extname(filePath).toLowerCase();
  return extension === ".md" || extension === ".txt";
}

function trimYamlScalar(value) {
  const text = String(value || "").trim();
  if (text.length >= 2
    && ((text[0] === "\"" && text[text.length - 1] === "\"")
      || (text[0] === "'" && text[text.length - 1] === "'"))) {
    return text.slice(1, -1);
  }

  return text;
}

function parseSkillDocument(skillsRoot, extensionRoot, filePath) {
  const text = stripBom(fs.readFileSync(filePath, "utf8"));
  const lines = text.split(/\r?\n/);
  if (lines.length < 3 || lines[0].trim() !== "---") {
    return null;
  }

  let end = -1;
  for (let i = 1; i < lines.length; i++) {
    if (lines[i].trim() === "---") {
      end = i;
      break;
    }
  }

  if (end < 0) {
    return null;
  }

  const skill = {
    name: "",
    description: "",
    assetName: relativeToRoot(skillsRoot, filePath),
    filePath: path.resolve(filePath),
    relativePath: relativeToRoot(extensionRoot, filePath),
    content: lines.slice(end + 1).join("\n").replace(/^\s+/, "")
  };

  for (const line of lines.slice(1, end)) {
    const match = /^([^:]+):\s*(.*)$/.exec(line);
    if (!match) {
      continue;
    }

    const key = match[1].trim().toLowerCase();
    if (key === "name") {
      skill.name = trimYamlScalar(match[2]);
    } else if (key === "description") {
      skill.description = trimYamlScalar(match[2]);
    }
  }

  return isNonEmptyString(skill.name) ? skill : null;
}

function loadSkillsFromExtension(extensionRoot) {
  const skillsRoot = path.join(extensionRoot, SKILLS_DIR_NAME);
  const skills = [];
  const files = walkFiles(skillsRoot, true, isSkillFile);
  for (const filePath of files) {
    try {
      const skill = parseSkillDocument(skillsRoot, extensionRoot, filePath);
      if (skill) {
        skills.push(skill);
      }
    } catch (error) {
      process.stderr.write(`[puerts-unity-mcp] Failed to parse local skill ${filePath}: ${error.message}\n`);
    }
  }

  skills.sort((left, right) => left.name.localeCompare(right.name));
  return { skillsRoot, skills };
}

function listExtensionSkills() {
  const context = resolveProxyContext(false);
  const loaded = loadSkillsFromExtension(context.extensionRoot);
  return {
    action: "agent.extension.skills.list",
    extensionRoot: context.extensionRoot,
    directoryRoot: loaded.skillsRoot,
    count: loaded.skills.length,
    skills: loaded.skills
  };
}

function loadExtensionSkill(args) {
  const context = resolveProxyContext(false);
  const loaded = loadSkillsFromExtension(context.extensionRoot);
  const requestedName = args && args.name;
  const requestedPath = args && (args.relativePath || args.path);
  let skill = null;
  if (isNonEmptyString(requestedName)) {
    skill = loaded.skills.find(entry => entry.name === requestedName) || null;
  } else if (isNonEmptyString(requestedPath)) {
    const targetPath = resolveUnderRoot(context.extensionRoot, requestedPath);
    skill = loaded.skills.find(entry => path.resolve(entry.filePath).toLowerCase() === path.resolve(targetPath).toLowerCase()) || null;
  } else {
    throw new Error("agent.extension.skill.load requires name or relativePath.");
  }

  return {
    action: "agent.extension.skill.load",
    extensionRoot: context.extensionRoot,
    directoryRoot: loaded.skillsRoot,
    success: !!skill,
    error: skill ? "" : "Skill not found.",
    skill
  };
}

function localExtensionInfo() {
  const context = resolveProxyContext(false);
  return {
    action: "agent.extension.info",
    configPath: context.configPath,
    configExists: !!(context.configPath && fs.existsSync(context.configPath)),
    unityProjectPath: context.unityProjectPath,
    stateRoot: context.stateRoot,
    extensionRoot: context.extensionRoot,
    selectedTargetKind: context.selector.kind,
    selectedTargetId: context.selector.id,
    selectedTargetName: context.selector.name,
    selectedTargetUrl: context.selector.url,
    activeScriptToolScope: selectedLocalScriptScope(context),
    editorToolsRoot: scriptToolRoot(context.extensionRoot, "editor"),
    runtimeToolsRoot: scriptToolRoot(context.extensionRoot, "runtime"),
    skillsRoot: path.join(context.extensionRoot, SKILLS_DIR_NAME)
  };
}

function readRequestArguments(request) {
  if (!request || !request.params) {
    return {};
  }

  if (request.method === "tools/call") {
    return request.params.arguments && typeof request.params.arguments === "object" ? request.params.arguments : {};
  }

  if (request.params.arguments && typeof request.params.arguments === "object") {
    return request.params.arguments;
  }

  return typeof request.params === "object" ? request.params : {};
}

function localToolNameFromRequest(request) {
  if (!request || !isNonEmptyString(request.method)) {
    return "";
  }

  if (request.method === "tools/call" && request.params && isNonEmptyString(request.params.name)) {
    return request.params.name;
  }

  return request.method;
}

function transformJavaScriptModule(source, modulePath) {
  if (/^\s*import\s/m.test(source)) {
    throw new Error(`Local proxy script tool execution does not support static import statements yet: ${modulePath}`);
  }

  let code = source;
  code = code.replace(/export\s+default\s+async\s+function\s*([A-Za-z_$][\w$]*)?\s*\(/g, (match, name) =>
    `exports.default = async function ${name || ""}(`);
  code = code.replace(/export\s+default\s+function\s*([A-Za-z_$][\w$]*)?\s*\(/g, (match, name) =>
    `exports.default = function ${name || ""}(`);
  code = code.replace(/export\s+async\s+function\s+([A-Za-z_$][\w$]*)\s*\(/g, (match, name) =>
    `exports.${name} = async function ${name}(`);
  code = code.replace(/export\s+function\s+([A-Za-z_$][\w$]*)\s*\(/g, (match, name) =>
    `exports.${name} = function ${name}(`);
  code = code.replace(/export\s+class\s+([A-Za-z_$][\w$]*)/g, (match, name) =>
    `exports.${name} = class ${name}`);
  code = code.replace(/export\s+(const|let|var)\s+([A-Za-z_$][\w$]*)\s*=/g, (match, declaration, name) =>
    `exports.${name} =`);
  code = code.replace(/export\s*\{([^}]+)\}\s*;?/g, (match, names) => {
    return names.split(",")
      .map(part => part.trim())
      .filter(Boolean)
      .map(part => {
        const pieces = part.split(/\s+as\s+/i).map(value => value.trim()).filter(Boolean);
        const localName = pieces[0];
        const exportedName = pieces[1] || pieces[0];
        return `exports.${exportedName} = ${localName};`;
      })
      .join("\n");
  });

  return code;
}

function buildLocalScriptToolEvalCode(manifest, args, context) {
  if (!fs.existsSync(manifest.modulePath)) {
    throw new Error(`Local script tool module not found: ${manifest.relativeModulePath}`);
  }

  const moduleSource = transformJavaScriptModule(stripBom(fs.readFileSync(manifest.modulePath, "utf8")), manifest.modulePath);
  const argsJson = JSON.stringify(args || {});
  const scriptContext = {
    endpointKind: manifest.scope === "runtime" ? "player" : "editor",
    endpointId: "agent-local-extension",
    endpointName: "agent-local-extension",
    toolName: manifest.name,
    modulePath: manifest.modulePath,
    projectRoot: context.unityProjectPath,
    stateRoot: context.stateRoot,
    extensionRoot: context.extensionRoot,
    executionSource: "stdio-proxy"
  };
  const contextJson = JSON.stringify(scriptContext);
  const functionName = manifest.functionName || "execute";
  return [
    "const __module = { exports: {} };",
    "const exports = __module.exports;",
    "(function(exports, module, __unity_mcp) {",
    moduleSource,
    "})(exports, __module, globalThis.__unity_mcp);",
    `const __fn = __module.exports[${JSON.stringify(functionName)}] || (${JSON.stringify(functionName)} === "default" ? __module.exports.default : null);`,
    `if (typeof __fn !== "function") { throw new Error(${JSON.stringify(`Script tool function not found: ${functionName}`)}); }`,
    `const __result = __fn(${JSON.stringify(argsJson)}, ${JSON.stringify(contextJson)});`,
    "if (typeof __result === 'string') {",
    "  try { return JSON.parse(__result); } catch (error) { return { value: __result }; }",
    "}",
    "if (typeof __result === 'undefined') { return {}; }",
    "return __result === null ? {} : __result;"
  ].join("\n");
}

function buildRemoteToolCallBody(id, toolName, args) {
  return JSON.stringify({
    jsonrpc: "2.0",
    id,
    method: "tools/call",
    params: {
      name: toolName,
      arguments: args || {}
    }
  });
}

function parseJsonResponseText(responseText, endpointUrl) {
  const body = stripBom(responseText).trim();
  if (!body) {
    throw new Error(`Empty response from Unity MCP endpoint: ${endpointUrl}`);
  }

  return normalizeMcpResponse(JSON.parse(body));
}

function unwrapEvalStructuredContent(structuredContent) {
  if (!structuredContent || typeof structuredContent !== "object" || !Object.prototype.hasOwnProperty.call(structuredContent, "kind")) {
    return structuredContent || {};
  }

  if (structuredContent.kind === "undefined" || structuredContent.kind === "null") {
    return {};
  }

  if (structuredContent.kind === "string") {
    const text = typeof structuredContent.value === "string" ? structuredContent.value : structuredContent.stringValue;
    if (!isNonEmptyString(text)) {
      return {};
    }

    try {
      return JSON.parse(text);
    } catch {
      return { value: text };
    }
  }

  if (Object.prototype.hasOwnProperty.call(structuredContent, "value")) {
    return structuredContent.value === null || typeof structuredContent.value === "undefined" ? {} : structuredContent.value;
  }

  return structuredContent;
}

async function executeLocalScriptTool(manifest, args, endpointUrl, context) {
  const evalTool = manifest.scope === "runtime" ? "runtime.js.eval" : "editor.js.eval";
  const code = buildLocalScriptToolEvalCode(manifest, args, context);
  const responseText = await postJsonWithRetry(endpointUrl, buildRemoteToolCallBody(`agent-extension-${Date.now()}`, evalTool, {
    code,
    mode: "script",
    chunkName: `mcp://agent-extension/${manifest.scope}/${manifest.name}.mjs`
  }));
  const response = parseJsonResponseText(responseText, endpointUrl);
  if (response.error && response.error.message) {
    throw new Error(response.error.message);
  }

  return unwrapEvalStructuredContent(response.result && response.result.structuredContent);
}

function buildToolCallResponse(id, structuredContent) {
  const safeStructured = structuredContent === undefined ? null : structuredContent;
  return {
    jsonrpc: "2.0",
    id,
    result: {
      content: [
        {
          type: "text",
          text: JSON.stringify(safeStructured)
        }
      ],
      structuredContent: safeStructured,
      isError: false
    }
  };
}

function writeJsonRpcObject(response) {
  if (!response || response.id === undefined) {
    return;
  }

  process.stdout.write(`${JSON.stringify(response)}\n`);
}

function localToolResult(name, args) {
  switch (name) {
    case "agent.extension.info":
      return localExtensionInfo();
    case "agent.extension.files.list":
      return listExtensionFiles(args);
    case "agent.extension.file.read":
      return readExtensionFile(args);
    case "agent.extension.scriptTools.list":
      return loadScriptToolManifestsForRequest(args);
    case "agent.extension.skills.list":
      return listExtensionSkills();
    case "agent.extension.skill.load":
      return loadExtensionSkill(args);
    default:
      throw new Error(`Unknown local extension tool: ${name}`);
  }
}

function appendLocalToolsToListResponse(response, context, remoteToolNames) {
  if (!response.result || !Array.isArray(response.result.tools)) {
    response.result = { tools: [] };
  }

  const known = new Set(response.result.tools.map(tool => tool && tool.name).filter(Boolean));
  for (const descriptor of localProxyToolDescriptors()) {
    if (!known.has(descriptor.name)) {
      response.result.tools.push(descriptor);
      known.add(descriptor.name);
    }
  }

  const localScriptTools = activeLocalScriptToolManifests(context);
  for (const manifest of localScriptTools) {
    if (known.has(manifest.name)) {
      continue;
    }

    response.result.tools.push({
      name: manifest.name,
      description: manifest.description,
      inputSchema: manifest.inputSchema
    });
    known.add(manifest.name);
  }

  lastRemoteToolNames = remoteToolNames || new Set();
  return response;
}

function normalizeMcpResponse(response) {
  if (!response || Array.isArray(response) || !response.result) {
    return response;
  }

  if (response.result.serverInfo && !response.result.instructions) {
    response.result.instructions = AGENT_INSTRUCTIONS;
  }

  if (Array.isArray(response.result.tools)) {
    response.result.tools = response.result.tools.map(tool => {
      if (!tool || typeof tool !== "object") {
        return tool;
      }

      if (!tool.inputSchema && typeof tool.inputSchemaJson === "string") {
        try {
          tool.inputSchema = JSON.parse(tool.inputSchemaJson);
        } catch {
          tool.inputSchema = { type: "object", additionalProperties: true };
        }
      }

      delete tool.inputSchemaJson;
      return tool;
    });
  }

  if (typeof response.result.structuredContentJson === "string" && !response.result.structuredContent) {
    try {
      response.result.structuredContent = JSON.parse(response.result.structuredContentJson);
    } catch {
      response.result.structuredContent = { value: response.result.structuredContentJson };
    }
  }

  delete response.result.structuredContentJson;
  delete response.result.valueJson;
  return response;
}

function writeResponse(id, endpointUrl, response) {
  const body = stripBom(response).trim();
  if (!body) {
    writeError(id, `Empty response from Unity MCP endpoint: ${endpointUrl}`);
    return;
  }

  let parsed;
  try {
    parsed = JSON.parse(body);
  } catch (error) {
    process.stderr.write(`[puerts-unity-mcp] Invalid JSON response from ${endpointUrl}: ${error.message}\n`);
    writeError(id, `Invalid JSON response from Unity MCP endpoint: ${error.message}`);
    return;
  }

  if (id === undefined) {
    return;
  }

  parsed = normalizeMcpResponse(parsed);

  if (id !== null && id !== undefined && parsed && !Array.isArray(parsed) && Object.prototype.hasOwnProperty.call(parsed, "id")) {
    parsed.id = id;
  }

  process.stdout.write(`${JSON.stringify(parsed)}\n`);
}

const input = readline.createInterface({
  input: process.stdin,
  crlfDelay: Infinity
});

input.on("line", async line => {
  const body = line.trim();
  if (!body) {
    return;
  }

  const id = extractId(body);
  try {
    let request = null;
    try {
      request = JSON.parse(body);
    } catch {
      request = null;
    }

    if (request && request.method === "tools/list") {
      const context = resolveProxyContext(false);
      let response = { jsonrpc: "2.0", id, result: { tools: [] } };
      let remoteToolNames = new Set();
      try {
        const endpointUrl = await resolveEndpointUrl();
        response = parseJsonResponseText(await postJsonWithRetry(endpointUrl, body), endpointUrl);
        response.id = id;
        if (response.result && Array.isArray(response.result.tools)) {
          remoteToolNames = new Set(response.result.tools.map(tool => tool && tool.name).filter(Boolean));
        }
      } catch (error) {
        process.stderr.write(`[puerts-unity-mcp] ${error.message}\n`);
      }

      writeJsonRpcObject(appendLocalToolsToListResponse(response, context, remoteToolNames));
      return;
    }

    if (request) {
      const toolName = localToolNameFromRequest(request);
      const args = readRequestArguments(request);
      if (isLocalProxyToolName(toolName)) {
        writeJsonRpcObject(buildToolCallResponse(id, localToolResult(toolName, args)));
        return;
      }

      const context = resolveProxyContext(false);
      const localManifest = findActiveLocalScriptTool(toolName, context);
      if (localManifest && (!lastRemoteToolNames || !lastRemoteToolNames.has(toolName))) {
        const endpointUrl = await resolveEndpointUrl();
        writeJsonRpcObject(buildToolCallResponse(id, await executeLocalScriptTool(localManifest, args, endpointUrl, context)));
        return;
      }
    }

    const endpointUrl = await resolveEndpointUrl();
    const response = await postJsonWithRetry(endpointUrl, body);
    writeResponse(id, endpointUrl, response);
  } catch (error) {
    process.stderr.write(`[puerts-unity-mcp] ${error.message}\n`);
    writeError(id, error.message);
  }
});
