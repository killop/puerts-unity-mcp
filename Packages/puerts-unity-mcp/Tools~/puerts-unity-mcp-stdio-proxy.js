#!/usr/bin/env node

const fs = require("fs");
const dgram = require("dgram");
const http = require("http");
const https = require("https");
const os = require("os");
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
const DISCOVERY_PROTOCOL = "puerts-unity-mcp.discovery.v1";
const DISCOVERY_PORT = 18992;
const DEFAULT_PLAYER_PORT = 18991;
const DEFAULT_CONNECT_TIMEOUT_MS = 30000;
const DEFAULT_DISCOVERY_TIMEOUT_MS = 3000;
const DEFAULT_HTTP_PROBE_TIMEOUT_MS = 1000;
const HTTP_PROBE_BATCH_SIZE = 64;
const MAX_HTTP_PROBE_HOSTS = 4096;
const LOCAL_EXTENSION_TOOL_PREFIX = "agent.extension.";
const DEFAULT_MAX_FILE_BYTES = 1024 * 1024;
const HEARTBEAT_RETENTION_MS = 10 * 60 * 1000;
const MAX_PLAYER_HEARTBEAT_DIRECTORIES = 64;
const AGENT_INSTRUCTIONS =
  "PuerTS Unity MCP controls Unity Editor, Play Mode, and real Player/phone targets. " +
  "Use editor.js.eval for Unity Editor automation; it runs JavaScript in the Editor PuerTS VM and normally does not generate C# or trigger domain reload. " +
  "Use runtime.js.eval for Play Mode, Android, iOS, or standalone Player automation; pass targetId/httpUrl when targeting a remote phone/player. " +
  "Write PuerTS JavaScript with CS.UnityEngine/CS.UnityEditor first, return only JSON-serializable values, and do not return Unity objects directly. " +
  "If a wrapped C# type or member is unavailable, use __unity_mcp.typeExists/getStatic/getStaticPath/setStatic/invokeStatic as the reflection fallback. " +
  "For phone UI automation, observe before acting with screen.screenshot, runtime.ui.snapshot, runtime.ui.find, and runtime.ui.raycast, then click with runtime.ui.click or input.tap. " +
  "Move stable project-specific flows into puerts-unity-mcp-extension/Editor/editor-tools or puerts-unity-mcp-extension/Runtime/runtime-tools instead of repeatedly generating one-off eval scripts.";

let lastRemoteToolNames = null;
let lastLanDiscoveryDiagnostics = null;

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

function normalizeGroup(value) {
  return isNonEmptyString(value) ? String(value).trim() : "default";
}

function sanitizeId(value) {
  const text = String(value || "default");
  return text.replace(/[^A-Za-z0-9_.-]/g, "_");
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
  const nameGroup = readArg(
    "--name-group",
    process.env.PUERTS_UNITY_MCP_NAME_GROUP || (config && config.name_group) || "default"
  );

  return {
    kind: String(targetKind || "editor").trim().toLowerCase(),
    id: String(targetId || "").trim(),
    name: String(targetName || "").trim(),
    url: String(targetUrl || "").trim(),
    group: normalizeGroup(nameGroup)
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

function listHeartbeatFiles(root) {
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
        stack.push(entryPath);
      } else if (entry.isFile() && entry.name === "heartbeat.json") {
        results.push(entryPath);
      }
    }
  }

  return results;
}

function readHeartbeatCandidates(stateRoot, directoryName) {
  if (directoryName === "players") {
    cleanupPlayerHeartbeatCache(path.join(stateRoot, directoryName));
  }

  return listHeartbeatFiles(path.join(stateRoot, directoryName))
    .map(filePath => {
      try {
        return readJsonFile(filePath);
      } catch {
        return null;
      }
    })
    .filter(Boolean);
}

function heartbeatAgeMs(heartbeat) {
  if (!heartbeat || !isNonEmptyString(heartbeat.lastUpdatedUtc)) {
    return 0;
  }

  const timestamp = Date.parse(heartbeat.lastUpdatedUtc);
  return Number.isFinite(timestamp) ? Date.now() - timestamp : 0;
}

function isManualHeartbeat(heartbeat) {
  return heartbeat && String(heartbeat.source || "").toLowerCase() === "manual";
}

function safeRemoveDirectory(root, directory) {
  if (!root || !directory) {
    return;
  }

  const normalizedRoot = path.resolve(root);
  const normalizedDirectory = path.resolve(directory);
  if (normalizedDirectory === normalizedRoot || !normalizedDirectory.startsWith(`${normalizedRoot}${path.sep}`)) {
    return;
  }

  try {
    fs.rmSync(normalizedDirectory, { recursive: true, force: true });
  } catch {
  }
}

function cleanupPlayerHeartbeatCache(playersRoot) {
  if (!playersRoot || !fs.existsSync(playersRoot)) {
    return;
  }

  let directories = [];
  try {
    directories = fs.readdirSync(playersRoot, { withFileTypes: true })
      .filter(entry => entry.isDirectory())
      .map(entry => path.join(playersRoot, entry.name));
  } catch {
    return;
  }

  const kept = [];
  for (const directory of directories) {
    const heartbeatPath = path.join(directory, "heartbeat.json");
    let heartbeat = null;
    try {
      heartbeat = fs.existsSync(heartbeatPath) ? readJsonFile(heartbeatPath) : null;
    } catch {
      heartbeat = null;
    }

    const stale = heartbeat ? (!isManualHeartbeat(heartbeat) && heartbeatAgeMs(heartbeat) > HEARTBEAT_RETENTION_MS) : true;
    if (stale) {
      safeRemoveDirectory(playersRoot, directory);
    } else {
      kept.push({ directory, heartbeat });
    }
  }

  const removable = kept
    .filter(entry => !isManualHeartbeat(entry.heartbeat))
    .sort((left, right) => Date.parse(right.heartbeat.lastUpdatedUtc || 0) - Date.parse(left.heartbeat.lastUpdatedUtc || 0));
  for (let i = MAX_PLAYER_HEARTBEAT_DIRECTORIES; i < removable.length; i++) {
    safeRemoveDirectory(playersRoot, removable[i].directory);
  }
}

function endpointMatchesSelector(heartbeat, selector, endpointKind) {
  if (!heartbeat || !isNonEmptyString(heartbeat.httpUrl)) {
    return false;
  }

  if (endpointKind && String(heartbeat.endpointKind || "").toLowerCase() !== endpointKind) {
    return false;
  }

  if (selector && selector.group && normalizeGroup(heartbeat.name_group) !== selector.group) {
    return false;
  }

  if (selector && isNonEmptyString(selector.id) && heartbeat.endpointId !== selector.id) {
    return false;
  }

  if (selector && isNonEmptyString(selector.name)) {
    const names = [heartbeat.name, heartbeat.endpointName, heartbeat.projectName].filter(isNonEmptyString);
    if (!names.some(name => String(name).toLowerCase() === selector.name.toLowerCase())) {
      return false;
    }
  }

  return isFreshHeartbeat(heartbeat);
}

function resolveFromEndpointHeartbeats(stateRoot, directoryName, selector, endpointKind) {
  const candidates = readHeartbeatCandidates(stateRoot, directoryName)
    .filter(entry => endpointMatchesSelector(entry, selector, endpointKind))
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

function isLoopbackOrWildcardHost(host) {
  const value = String(host || "").toLowerCase();
  return value === "127.0.0.1"
    || value === "localhost"
    || value === "::1"
    || value === "0.0.0.0"
    || value === "*"
    || value === "+"
    || value === "::"
    || value === "[::]";
}

function resolveReachableHttpUrl(httpUrl, port, senderAddress) {
  if (!isNonEmptyString(httpUrl)) {
    return isNonEmptyString(senderAddress) && Number(port) > 0 ? `http://${senderAddress}:${Number(port)}` : "";
  }

  let parsed;
  try {
    parsed = new URL(httpUrl);
  } catch {
    return trimTrailingSlash(httpUrl);
  }

  if (!isLoopbackOrWildcardHost(parsed.hostname) || !isNonEmptyString(senderAddress)) {
    return trimTrailingSlash(httpUrl);
  }

  parsed.hostname = senderAddress;
  return trimTrailingSlash(parsed.toString());
}

function heartbeatFromDiscoveryMessage(json, requiredGroup, senderAddress) {
  let message;
  try {
    message = JSON.parse(stripBom(json));
  } catch {
    return null;
  }

  if (!message
    || message.protocol !== DISCOVERY_PROTOCOL
    || String(message.messageType || "").toLowerCase() !== "announce"
    || normalizeGroup(message.name_group) !== normalizeGroup(requiredGroup)
    || !isNonEmptyString(message.endpointId)
    || !isNonEmptyString(message.endpointKind)) {
    return null;
  }

  return {
    endpointId: sanitizeId(message.endpointId),
    endpointKind: message.endpointKind,
    endpointName: message.endpointName || "",
    projectRoot: message.projectRoot || "",
    projectName: message.projectName || "",
    name: message.name || message.endpointName || message.projectName || message.endpointId,
    name_group: normalizeGroup(message.name_group),
    processId: Number(message.processId) || 0,
    httpUrl: resolveReachableHttpUrl(message.httpUrl, message.port, senderAddress),
    port: Number(message.port) || 0,
    unityVersion: message.unityVersion || "",
    platform: message.platform || "",
    isEditor: Boolean(message.isEditor) || String(message.endpointKind || "").toLowerCase() === "editor",
    lastUpdatedUtc: new Date().toISOString(),
    capabilities: message.capabilities || {},
    source: "lan"
  };
}

function writeDiscoveredHeartbeat(stateRoot, heartbeat) {
  if (!stateRoot || !heartbeat || !isNonEmptyString(heartbeat.endpointId)) {
    return;
  }

  const directoryName = String(heartbeat.endpointKind || "").toLowerCase() === "editor" ? "editors" : "players";
  if (directoryName === "players") {
    cleanupPlayerHeartbeatCache(path.join(stateRoot, directoryName));
  }

  const heartbeatDirectory = path.join(stateRoot, directoryName, sanitizeId(heartbeat.endpointId));
  fs.mkdirSync(heartbeatDirectory, { recursive: true });
  fs.writeFileSync(path.join(heartbeatDirectory, "heartbeat.json"), `${JSON.stringify(heartbeat, null, 2)}\n`, "utf8");
}

function buildDiscoveryQuery(selector) {
  return JSON.stringify({
    protocol: DISCOVERY_PROTOCOL,
    messageType: "query",
    name: "stdio-proxy",
    name_group: selector.group,
    endpointId: `stdio-proxy-${process.pid}`,
    endpointKind: "agent"
  });
}

function splitProbeList(value) {
  if (Array.isArray(value)) {
    return value.flatMap(splitProbeList);
  }

  if (!isNonEmptyString(value)) {
    return [];
  }

  return String(value).split(/[,;\n\r\t ]+/).map(entry => entry.trim()).filter(Boolean);
}

function parseIpv4(value) {
  const parts = String(value || "").trim().split(".");
  if (parts.length !== 4) {
    return null;
  }

  const bytes = parts.map(part => Number(part));
  if (bytes.some(part => !Number.isInteger(part) || part < 0 || part > 255)) {
    return null;
  }

  return bytes;
}

function ipv4ToUInt32(value) {
  const bytes = Array.isArray(value) ? value : parseIpv4(value);
  if (!bytes) {
    return null;
  }

  return (((bytes[0] << 24) >>> 0) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]) >>> 0;
}

function ipv4FromUInt32(value) {
  const normalized = value >>> 0;
  return [
    (normalized >>> 24) & 255,
    (normalized >>> 16) & 255,
    (normalized >>> 8) & 255,
    normalized & 255
  ].join(".");
}

function addProbeHost(hosts, seen, host) {
  const value = String(host || "").trim();
  if (!parseIpv4(value) || value === "127.0.0.1" || seen.has(value)) {
    return;
  }

  seen.add(value);
  hosts.push(value);
}

function addProbeHosts(hosts, seen, values) {
  for (const value of splitProbeList(values)) {
    addProbeHost(hosts, seen, value);
  }
}

function addProbeHostsForClassC(hosts, seen, localAddress) {
  const bytes = parseIpv4(localAddress);
  if (!bytes) {
    return;
  }

  for (let host = 1; host <= 254; host++) {
    if (host === bytes[3]) {
      continue;
    }

    addProbeHost(hosts, seen, `${bytes[0]}.${bytes[1]}.${bytes[2]}.${host}`);
  }
}

function addProbeHostsForCidr(hosts, seen, cidr) {
  const text = String(cidr || "").trim();
  const parts = text.split("/");
  if (parts.length !== 2) {
    return;
  }

  const addressValue = ipv4ToUInt32(parts[0]);
  const prefixLength = Number(parts[1]);
  if (addressValue === null || !Number.isInteger(prefixLength) || prefixLength < 0 || prefixLength > 32) {
    return;
  }

  const mask = prefixLength === 0 ? 0 : (0xffffffff << (32 - prefixLength)) >>> 0;
  const network = (addressValue & mask) >>> 0;
  const broadcast = (network | (~mask >>> 0)) >>> 0;
  if (broadcast <= network + 1) {
    return;
  }

  const hostCount = broadcast - network - 1;
  if (hostCount > MAX_HTTP_PROBE_HOSTS) {
    process.stderr.write(`[puerts-unity-mcp] LAN HTTP probe CIDR skipped because it is too large: ${text}. Use /20 or smaller ranges.\n`);
    return;
  }

  for (let value = network + 1; value < broadcast; value++) {
    addProbeHost(hosts, seen, ipv4FromUInt32(value));
  }
}

function addProbeCidrs(hosts, seen, values) {
  for (const value of splitProbeList(values)) {
    addProbeHostsForCidr(hosts, seen, value);
  }
}

function buildLanHttpProbeHosts(config) {
  const hosts = [];
  const seen = new Set();
  const interfaces = os.networkInterfaces();
  for (const entries of Object.values(interfaces)) {
    for (const entry of entries || []) {
      if (!entry || entry.internal || entry.family !== "IPv4") {
        continue;
      }

      addProbeHostsForClassC(hosts, seen, entry.address);
    }
  }

  addProbeHosts(hosts, seen, config && config.lanHttpProbeHosts);
  addProbeCidrs(hosts, seen, config && config.lanHttpProbeCidrs);
  addProbeHosts(hosts, seen, readArg("--probe-hosts", process.env.PUERTS_UNITY_MCP_PROBE_HOSTS || ""));
  addProbeCidrs(hosts, seen, readArg("--probe-cidrs", process.env.PUERTS_UNITY_MCP_PROBE_CIDRS || ""));
  return hosts;
}

function buildLanHttpProbeUrls(config) {
  const port = Number(config && config.runtimePort) > 0 ? Number(config.runtimePort) : DEFAULT_PLAYER_PORT;
  return buildLanHttpProbeHosts(config).map(host => `http://${host}:${port}/health`);
}

function getJson(url, timeoutMs) {
  return new Promise(resolve => {
    let parsed;
    try {
      parsed = new URL(url);
    } catch (error) {
      resolve({ ok: false, url, reason: "invalid_url", error: error.message });
      return;
    }

    const client = parsed.protocol === "https:" ? https : http;
    let connected = false;
    const request = client.request(
      {
        method: "GET",
        protocol: parsed.protocol,
        hostname: parsed.hostname,
        port: parsed.port,
        path: `${parsed.pathname}${parsed.search}`,
        timeout: timeoutMs
      },
      response => {
        let data = "";
        response.setEncoding("utf8");
        response.on("data", chunk => {
          data += chunk;
        });
        response.on("end", () => {
          resolve({
            ok: response.statusCode >= 200 && response.statusCode < 300,
            url,
            connected: true,
            statusCode: response.statusCode,
            body: data,
            reason: response.statusCode >= 200 && response.statusCode < 300 ? "" : "http_status"
          });
        });
      }
    );

    request.on("socket", socket => {
      if (!socket) {
        return;
      }

      if (socket.connecting) {
        socket.once("connect", () => {
          connected = true;
        });
      } else {
        connected = true;
      }
    });
    request.on("timeout", () => {
      request.destroy(new Error("timeout"));
    });
    request.on("error", error => {
      resolve({
        ok: false,
        url,
        connected,
        reason: error && error.message === "timeout" ? "timeout" : "network_error",
        error: error ? error.message : ""
      });
    });
    request.end();
  });
}

function heartbeatFromHealthJson(body, healthUrl, selector) {
  let health;
  try {
    health = JSON.parse(stripBom(body));
  } catch (error) {
    return { reason: "invalid_json", error: error.message };
  }

  if (!health || !isNonEmptyString(health.endpointId)) {
    return { reason: "missing_endpoint" };
  }

  if (health.isEditor) {
    return { reason: "editor_endpoint", endpointKind: health.endpointKind || "editor" };
  }

  const group = normalizeGroup(health.name_group || (selector && selector.group));
  if (selector && selector.group && group !== selector.group) {
    return { reason: "name_group_mismatch", name_group: group };
  }

  const endpointKind = isNonEmptyString(health.endpointKind) ? String(health.endpointKind) : "player";
  if (String(endpointKind).toLowerCase() === "editor") {
    return { reason: "editor_endpoint", endpointKind };
  }

  const parsed = new URL(healthUrl);
  const baseUrl = `${parsed.protocol}//${parsed.hostname}${parsed.port ? `:${parsed.port}` : ""}`;
  const httpUrl = resolveReachableHttpUrl(health.httpUrl || baseUrl, health.httpPort || Number(parsed.port), parsed.hostname);
  const heartbeat = {
    endpointId: sanitizeId(health.endpointId),
    endpointKind,
    endpointName: health.endpointName || "",
    projectRoot: health.projectRoot || "",
    projectName: health.productName || "",
    name: health.endpointName || health.productName || health.endpointId,
    name_group: group,
    processId: 0,
    httpUrl,
    port: Number(health.httpPort) || Number(parsed.port) || DEFAULT_PLAYER_PORT,
    unityVersion: health.unityVersion || "",
    platform: health.platform || "",
    isEditor: false,
    lastUpdatedUtc: new Date().toISOString(),
    capabilities: health.capabilities || {},
    source: "lan"
  };
  return { heartbeat };
}

async function probeHealthUrl(healthUrl, selector, timeoutMs) {
  const response = await getJson(healthUrl, timeoutMs);
  if (!response.ok) {
    return {
      healthUrl,
      tcpReachable: Boolean(response.connected) || response.reason === "http_status",
      reason: response.reason || "network_error",
      statusCode: response.statusCode || 0,
      error: response.error || ""
    };
  }

  const parsed = heartbeatFromHealthJson(response.body, healthUrl, selector);
  if (!parsed.heartbeat) {
    return {
      healthUrl,
      tcpReachable: true,
      healthOk: true,
      reason: parsed.reason || "rejected",
      name_group: parsed.name_group || "",
      endpointKind: parsed.endpointKind || "",
      error: parsed.error || ""
    };
  }

  return {
    healthUrl,
    tcpReachable: true,
    healthOk: true,
    heartbeat: parsed.heartbeat,
    reason: "accepted"
  };
}

async function probeLanPlayerEndpoint(stateRoot, selector, config) {
  const timeoutMs = Math.min(10000, readNumberArg(
    "--http-probe-timeout-ms",
    process.env.PUERTS_UNITY_MCP_HTTP_PROBE_TIMEOUT_MS || (config && config.lanHttpProbeTimeoutMs) || DEFAULT_HTTP_PROBE_TIMEOUT_MS
  ));
  if (timeoutMs <= 0) {
    return "";
  }

  const urls = buildLanHttpProbeUrls(config);
  const stats = {
    httpProbeAttempted: true,
    httpProbeCandidates: urls.length,
    httpProbeTimeoutMs: timeoutMs,
    httpProbeTcpReachable: 0,
    httpProbeHealthOk: 0,
    httpProbeFound: 0,
    httpProbeRejected: 0,
    httpProbeTimeoutOrError: 0,
    firstReachableUrl: "",
    firstRejectedReason: "",
    hint: ""
  };
  const matches = [];

  for (let offset = 0; offset < urls.length; offset += HTTP_PROBE_BATCH_SIZE) {
    const batch = urls.slice(offset, offset + HTTP_PROBE_BATCH_SIZE);
    const results = await Promise.all(batch.map(url => probeHealthUrl(url, selector, timeoutMs)));
    for (const result of results) {
      if (result.tcpReachable) {
        stats.httpProbeTcpReachable++;
        if (!stats.firstReachableUrl) {
          stats.firstReachableUrl = result.healthUrl;
        }
      }

      if (result.healthOk) {
        stats.httpProbeHealthOk++;
      }

      if (result.heartbeat) {
        writeDiscoveredHeartbeat(stateRoot, result.heartbeat);
        if (endpointMatchesSelector(result.heartbeat, selector, "player")) {
          stats.httpProbeFound++;
          matches.push(result.heartbeat);
        } else {
          stats.httpProbeRejected++;
          if (!stats.firstRejectedReason) {
            stats.firstRejectedReason = "selector_mismatch";
          }
        }
      } else if (result.reason && result.reason !== "network_error" && result.reason !== "timeout") {
        stats.httpProbeRejected++;
        if (!stats.firstRejectedReason) {
          stats.firstRejectedReason = result.reason;
        }
      } else {
        stats.httpProbeTimeoutOrError++;
      }
    }
  }

  if (matches.length === 0) {
    stats.hint = stats.httpProbeTcpReachable > 0
      ? "TCP reached at least one host, but no matching Player MCP /health response was accepted. Check mobile MCP startup, port, name_group, and main-thread responsiveness."
      : "No TCP/HTTP Player MCP response was found. UDP may be blocked by firewall, AP isolation, VLAN routing, or network policy; configure lanHttpProbeHosts/lanHttpProbeCidrs or pass --target-url for direct HTTP.";
  }

  lastLanDiscoveryDiagnostics = Object.assign({}, lastLanDiscoveryDiagnostics || {}, stats);
  const fresh = matches
    .sort((left, right) => Date.parse(right.lastUpdatedUtc || 0) - Date.parse(left.lastUpdatedUtc || 0));
  return fresh.length === 0 ? "" : toMcpUrl(fresh[0].httpUrl);
}

function discoverLanPlayerEndpoint(stateRoot, selector) {
  const timeoutMs = readNumberArg(
    "--discovery-timeout-ms",
    process.env.PUERTS_UNITY_MCP_DISCOVERY_TIMEOUT_MS || DEFAULT_DISCOVERY_TIMEOUT_MS
  );
  if (timeoutMs <= 0) {
    lastLanDiscoveryDiagnostics = Object.assign({}, lastLanDiscoveryDiagnostics || {}, {
      udpAttempted: false,
      udpReason: "timeout_disabled"
    });
    return Promise.resolve("");
  }

  return new Promise(resolve => {
    const found = [];
    const socket = dgram.createSocket({ type: "udp4", reuseAddr: true });
    let finished = false;
    let timer = null;
    let udpError = "";

    const finish = () => {
      if (finished) {
        return;
      }

      finished = true;
      if (timer) {
        clearTimeout(timer);
      }

      try {
        socket.close();
      } catch {
      }

      const fresh = found
        .filter(entry => endpointMatchesSelector(entry, selector, "player"))
        .sort((left, right) => Date.parse(right.lastUpdatedUtc || 0) - Date.parse(left.lastUpdatedUtc || 0));
      lastLanDiscoveryDiagnostics = Object.assign({}, lastLanDiscoveryDiagnostics || {}, {
        udpAttempted: true,
        udpPort: DISCOVERY_PORT,
        udpTimeoutMs: timeoutMs,
        udpFound: fresh.length,
        udpError
      });
      resolve(fresh.length === 0 ? "" : toMcpUrl(fresh[0].httpUrl));
    };

    socket.on("error", error => {
      udpError = error ? error.message : "socket_error";
      finish();
    });
    socket.on("message", (message, remote) => {
      const heartbeat = heartbeatFromDiscoveryMessage(message.toString("utf8"), selector.group, remote.address);
      if (!heartbeat) {
        return;
      }

      writeDiscoveredHeartbeat(stateRoot, heartbeat);
      if (endpointMatchesSelector(heartbeat, selector, "player")) {
        found.push(heartbeat);
        if (isNonEmptyString(selector.id)) {
          finish();
        }
      }
    });

    socket.bind(DISCOVERY_PORT, "0.0.0.0", () => {
      try {
        socket.setBroadcast(true);
        const payload = Buffer.from(buildDiscoveryQuery(selector), "utf8");
        socket.send(payload, 0, payload.length, DISCOVERY_PORT, "255.255.255.255");
      } catch {
        finish();
      }
    });

    timer = setTimeout(finish, timeoutMs);
  });
}

async function resolvePlayerEndpoint(stateRoot, selector, config) {
  const fromState = resolveFromEndpointHeartbeats(stateRoot, "players", selector, "player");
  if (fromState) {
    return fromState;
  }

  if (!config || config.lanDiscoveryEnabled !== false) {
    const fromUdp = await discoverLanPlayerEndpoint(stateRoot, selector);
    if (fromUdp) {
      return fromUdp;
    }
  } else {
    lastLanDiscoveryDiagnostics = Object.assign({}, lastLanDiscoveryDiagnostics || {}, {
      udpAttempted: false,
      udpReason: "lanDiscoveryEnabled=false"
    });
  }

  return await probeLanPlayerEndpoint(stateRoot, selector, config);
}

function formatLanDiscoveryDiagnostics() {
  const diagnostics = lastLanDiscoveryDiagnostics;
  if (!diagnostics) {
    return "";
  }

  const parts = [];
  if (Object.prototype.hasOwnProperty.call(diagnostics, "udpAttempted")) {
    parts.push(`udpAttempted=${diagnostics.udpAttempted}`);
  }

  if (Object.prototype.hasOwnProperty.call(diagnostics, "udpFound")) {
    parts.push(`udpFound=${diagnostics.udpFound}`);
  }

  if (diagnostics.udpError) {
    parts.push(`udpError=${diagnostics.udpError}`);
  }

  if (Object.prototype.hasOwnProperty.call(diagnostics, "httpProbeCandidates")) {
    parts.push(`httpProbeCandidates=${diagnostics.httpProbeCandidates}`);
  }

  if (Object.prototype.hasOwnProperty.call(diagnostics, "httpProbeTcpReachable")) {
    parts.push(`httpProbeTcpReachable=${diagnostics.httpProbeTcpReachable}`);
  }

  if (Object.prototype.hasOwnProperty.call(diagnostics, "httpProbeHealthOk")) {
    parts.push(`httpProbeHealthOk=${diagnostics.httpProbeHealthOk}`);
  }

  if (Object.prototype.hasOwnProperty.call(diagnostics, "httpProbeFound")) {
    parts.push(`httpProbeFound=${diagnostics.httpProbeFound}`);
  }

  if (diagnostics.firstReachableUrl) {
    parts.push(`firstReachableUrl=${diagnostics.firstReachableUrl}`);
  }

  if (diagnostics.firstRejectedReason) {
    parts.push(`firstRejectedReason=${diagnostics.firstRejectedReason}`);
  }

  if (diagnostics.hint) {
    parts.push(`hint=${diagnostics.hint}`);
  }

  return parts.length === 0 ? "" : ` Discovery diagnostics: ${parts.join(", ")}`;
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
    const playerEndpoint = await resolvePlayerEndpoint(stateRoot, selector, config);
    if (playerEndpoint) {
      return playerEndpoint;
    }

    throw new Error(`No reachable Player MCP endpoint found for name_group=${selector.group}${selector.id ? ` targetId=${selector.id}` : ""}.${formatLanDiscoveryDiagnostics()}`);
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
    const selectedEditor = resolveFromEndpointHeartbeats(stateRoot, "editors", selector, "editor");
    if (selectedEditor) {
      return selectedEditor;
    }

    throw new Error(`No reachable Editor MCP endpoint found for name_group=${selector.group}${selector.id ? ` targetId=${selector.id}` : ""}${selector.name ? ` targetName=${selector.name}` : ""}.`);
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
    name_group: context.selector.group,
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
