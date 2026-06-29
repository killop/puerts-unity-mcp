import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import https from "node:https";
import crypto from "node:crypto";
import { spawn, spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

export const packageKeys = ["com.tencent.puerts.core", "com.tencent.puerts.v8", "puerts-unity-mcp"];
const androidNativeLibraries = [
  { packageName: "core", fileName: "libPuertsCore.so" },
  { packageName: "core", fileName: "libWSPPAddon.so" },
  { packageName: "v8", fileName: "libPapiV8.so" }
];
const androidAbiCpuNames = new Map([
  ["arm64-v8a", "ARM64"],
  ["armeabi-v7a", "ARMv7"],
  ["x86_64", "X86_64"]
]);

export function getToolRoots(importMetaUrl) {
  const toolsRoot = path.dirname(fileURLToPath(importMetaUrl));
  const packageRoot = path.resolve(toolsRoot, "..");
  const repoRoot = path.resolve(packageRoot, "..", "..");
  return { toolsRoot, packageRoot, repoRoot };
}

export function parseArgs(argv = process.argv.slice(2)) {
  const result = { _: [] };
  for (let index = 0; index < argv.length; index++) {
    const token = argv[index];
    if (!token.startsWith("-")) {
      result._.push(token);
      continue;
    }

    const trimmed = token.replace(/^-+/, "");
    const equals = trimmed.indexOf("=");
    let key = equals >= 0 ? trimmed.slice(0, equals) : trimmed;
    let value = equals >= 0 ? trimmed.slice(equals + 1) : true;
    if (equals < 0 && index + 1 < argv.length && !argv[index + 1].startsWith("-")) {
      value = argv[index + 1];
      index++;
    }

    key = normalizeArgKey(key);
    result[key] = value;
  }

  return result;
}

export function getArg(args, names, defaultValue = "") {
  for (const name of names) {
    const key = normalizeArgKey(name);
    if (Object.prototype.hasOwnProperty.call(args, key)) {
      return args[key];
    }
  }

  return defaultValue;
}

export function getBoolArg(args, names, defaultValue = false) {
  const value = getArg(args, names, undefined);
  if (value === undefined) {
    return defaultValue;
  }

  if (value === true) {
    return true;
  }

  return /^(1|true|yes|on)$/i.test(String(value));
}

export function getIntArg(args, names, defaultValue) {
  const value = getArg(args, names, undefined);
  if (value === undefined || value === true || value === "") {
    return defaultValue;
  }

  const parsed = Number.parseInt(String(value), 10);
  return Number.isFinite(parsed) ? parsed : defaultValue;
}

function normalizeArgKey(value) {
  return String(value)
    .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
    .replace(/_/g, "-")
    .toLowerCase();
}

export function resolveUnityProjectRoot(args, toolsRoot) {
  const provided = getArg(args, ["unity-project-root", "UnityProjectRoot"], "");
  if (provided && provided !== true) {
    return path.resolve(String(provided));
  }

  const root = path.resolve(toolsRoot, "..", "..", "..", "..");
  if (!fs.existsSync(path.join(root, "Packages", "manifest.json"))) {
    throw new Error(`UnityProjectRoot was not provided and could not be inferred from ${toolsRoot}`);
  }

  return root;
}

export function assertUnityProjectRoot(projectRoot) {
  const manifestPath = path.join(projectRoot, "Packages", "manifest.json");
  if (!fs.existsSync(manifestPath)) {
    throw new Error(`Unity manifest not found: ${manifestPath}`);
  }
}

export function toUnityFilePath(value) {
  return `file:${path.resolve(value).replace(/\\/g, "/")}`;
}

export function resolveDependencyPrefix(options = {}) {
  if (options.packageRoot) {
    return toUnityFilePath(options.packageRoot);
  }

  return `file:../${options.localPackageDirectoryName || "puerts-unity-mcp"}`;
}

export function addPumToBuild(projectRoot, options = {}) {
  assertUnityProjectRoot(projectRoot);
  updateManifestAdd(projectRoot, options);
  copyMobileConfig(projectRoot);
  removeLegacyProjectAndroidPluginArtifacts(projectRoot);
  removeLegacyProjectGeneratedPluginArtifacts(projectRoot);
  copyAndroidNativeLibraries(projectRoot, options);
  if (!options.skipAndroidPermissions) {
    writeAndroidPermissions(projectRoot, options);
  }

  console.log("PuerTS Unity MCP is enabled for player builds.");
}

export function removePumFromBuild(projectRoot, options = {}) {
  assertUnityProjectRoot(projectRoot);
  updateManifestRemove(projectRoot);
  removePathInsideProject(projectRoot, path.join("Assets", "StreamingAssets", "PuertsUnityMcp", "mobile-mcp-config.json"));
  removeEmptyDirectory(projectRoot, path.join("Assets", "StreamingAssets", "PuertsUnityMcp"));
  removeLegacyProjectAndroidPluginArtifacts(projectRoot);
  removeLegacyProjectGeneratedPluginArtifacts(projectRoot);
  if (options.removeState) {
    removePathInsideProject(projectRoot, ".puerts-unity-mcp");
  }

  console.log("PuerTS Unity MCP is removed from player builds.");
}

export function syncLocalPackage(options) {
  const unityRoot = path.resolve(options.unityProjectRoot);
  const localPackageDirectoryName = options.localPackageDirectoryName || "puerts-unity-mcp";
  const direction = options.direction || "push";
  const toolsRoot = options.toolsRoot;
  const packageRoot = path.resolve(toolsRoot, "..");
  const repoRoot = path.resolve(packageRoot, "..", "..");
  const unityManifest = path.join(unityRoot, "Packages", "manifest.json");
  const unityAssets = path.join(unityRoot, "Assets");
  const localPackageRoot = path.resolve(unityRoot, localPackageDirectoryName);

  if (!fs.existsSync(unityAssets) || !fs.existsSync(unityManifest)) {
    throw new Error(`Unity project root is invalid: ${unityRoot}`);
  }

  assertPathInside(unityRoot, localPackageRoot);
  if (path.basename(localPackageRoot) !== localPackageDirectoryName) {
    throw new Error(`Unexpected local package directory: ${localPackageRoot}`);
  }

  if (direction === "push") {
    copyDirectoryMirror(repoRoot, localPackageRoot, new Set([".tmp", ".mcp_data", ".codex"]));
    if (!options.skipManifestUpdate) {
      addPumToBuild(unityRoot, { localPackageDirectoryName });
      if (options.enablePackageTests) {
        addPackageTestable(unityManifest);
      }
    }

    console.log(`Pushed package to ${localPackageRoot}`);
    return;
  }

  copyDirectoryOverlay(localPackageRoot, repoRoot);
  console.log(`Pulled package from ${localPackageRoot}`);
  console.log("Pull overlays files only; it does not delete files that were removed from the Unity-local package.");
}

export function installToUnityProject(options) {
  const unityProjectRoot = path.resolve(options.unityProjectRoot);
  const toolsRoot = options.toolsRoot;
  const packageRoot = options.packageRoot
    ? path.resolve(options.packageRoot)
    : path.resolve(toolsRoot, "..", "..", "..");
  const localPackageDirectoryName = options.localPackageDirectoryName || "puerts-unity-mcp";
  const manifestPath = path.join(unityProjectRoot, "Packages", "manifest.json");
  if (!fs.existsSync(manifestPath)) {
    throw new Error(`Unity manifest not found: ${manifestPath}`);
  }

  if (options.syncLocalPackage) {
    syncLocalPackage({
      unityProjectRoot,
      direction: "push",
      localPackageDirectoryName,
      enablePackageTests: options.enablePackageTests,
      toolsRoot
    });
    return;
  }

  if (options.useProjectLocalPackage) {
    addPumToBuild(unityProjectRoot, { localPackageDirectoryName });
  } else {
    addPumToBuild(unityProjectRoot, { packageRoot });
  }

  if (options.enablePackageTests) {
    addPackageTestable(manifestPath);
  }

  console.log(`Installed PuerTS Unity MCP dependencies into ${manifestPath}`);
}

export async function vendorPuerts(options) {
  const repoRoot = options.repoRoot;
  const version = options.version || "3.0.2";
  const source = path.resolve(options.source || path.join(repoRoot, "..", "puerts"));
  const destination = path.resolve(options.destination || path.join(repoRoot, "third_party", "puerts"));
  const downloadDirectory = path.resolve(options.downloadDirectory || path.join(repoRoot, ".puerts-unity-mcp", "downloads"));
  ensureDirectory(path.dirname(destination));

  if (fs.existsSync(source)) {
    removeDirectorySafe(destination);
    copyDirectoryOverlay(source, destination);
    removeDirectorySafe(path.join(destination, ".git"));
  } else {
    ensureDirectory(path.join(destination, "unity", "upms"));
  }

  ensureDirectory(downloadDirectory);
  const upmsRoot = path.join(destination, "unity", "upms");
  ensureDirectory(upmsRoot);
  await expandReleasePackage("Core", version, upmsRoot, downloadDirectory);
  await expandReleasePackage("V8", version, upmsRoot, downloadDirectory);
  removeBlockedAssemblyFilter(destination);
  assertRequiredNativeFiles(destination);
  console.log(`Vendored PuerTS Unity_v${version} into ${destination}`);
}

export async function runUnityMcpSmokeTest(options) {
  const projectRoot = path.resolve(options.unityProjectRoot);
  assertUnityProjectRoot(projectRoot);
  const unityExe = options.unityExe;
  if (!unityExe || !fs.existsSync(unityExe)) {
    throw new Error(`Unity executable not found: ${unityExe}`);
  }

  const startupTimeoutSeconds = options.startupTimeoutSeconds || 300;
  const playModeTimeoutSeconds = options.playModeTimeoutSeconds || 360;
  const domainReloadTimeoutSeconds = options.domainReloadTimeoutSeconds || 360;
  const logFile = options.logFile || path.join(projectRoot, ".puerts-unity-mcp", "logs", "unity-mcp-test.log");
  ensureDirectory(path.dirname(logFile));
  if (fs.existsSync(logFile)) {
    fs.rmSync(logFile, { force: true });
  }

  const unity = spawn(unityExe, ["-batchmode", "-nographics", "-projectPath", projectRoot, "-logFile", logFile], {
    detached: false,
    stdio: "ignore",
    windowsHide: true
  });

  const results = [];
  try {
    const endpoint = await findEndpoint(projectRoot, unity, startupTimeoutSeconds, "editor");
    results.push(pass("health", { baseUrl: endpoint.baseUrl, endpointId: endpoint.health.endpointId }));

    const initialize = await invokeMcp(endpoint.baseUrl, { jsonrpc: "2.0", id: "init", method: "initialize", params: { protocolVersion: "test-protocol" } });
    assertCondition(initialize.result?.serverInfo?.endpointName === path.basename(projectRoot), "initialize returned unexpected endpoint name");
    results.push(pass("initialize", initialize.result.serverInfo));

    const tools = await invokeMcp(endpoint.baseUrl, { jsonrpc: "2.0", id: "tools", method: "tools/list", params: {} });
    const toolNames = (tools.result?.tools || []).map((tool) => tool.name);
    for (const name of ["editor.js.eval", "runtime.js.eval", "runtime.tool.call", "editor.playmode.set", "editor.buildSettings.startupScene", "targets.list"]) {
      assertCondition(toolNames.includes(name), `tools/list did not include ${name}`);
    }

    results.push(pass("tools/list", { count: toolNames.length, names: toolNames }));
    const state = await callTool(endpoint.baseUrl, "state", "editor.state", {});
    assertCondition(state.endpointName === path.basename(projectRoot), "editor.state returned unexpected endpoint");
    results.push(pass("editor.state", { ready: state.ready, isPlaying: state.isPlaying }));

    const editorEval = await callTool(endpoint.baseUrl, "editorEval", "editor.js.eval", {
      code: "1 + 2",
      mode: "expression",
      chunkName: "test-editor-eval"
    }, 60);
    assertCondition(editorEval.kind === "number" && Number(editorEval.value) === 3, "editor.js.eval returned unexpected result");
    results.push(pass("editor.js.eval", editorEval));

    if (options.includeDomainReload) {
      await runDomainReloadSmoke(projectRoot, unity, endpoint, domainReloadTimeoutSeconds, results);
    }

    if (!options.skipPlayMode) {
      const enter = await callTool(endpoint.baseUrl, "enter", "editor.playmode.set", { state: "enter" }, 30);
      assertCondition(enter.targetIsPlaying === true, "editor.playmode.set enter did not request Play Mode");
      results.push(pass("editor.playmode.set enter", enter));

      const playMode = await waitForPlayModeTarget(projectRoot, unity, playModeTimeoutSeconds);
      results.push(pass("runtime.targets.list local-playmode", playMode.targets));
      const runtimeEval = await callTool(playMode.baseUrl, "runtimeEval", "runtime.js.eval", {
        targetId: "playmode",
        code: "2 * 3",
        mode: "expression",
        chunkName: "test-runtime-eval"
      }, 90);
      assertCondition(runtimeEval.kind === "number" && Number(runtimeEval.value) === 6, "runtime.js.eval returned unexpected result");
      results.push(pass("runtime.js.eval playmode", runtimeEval));
      const runtimeEndpoint = await findEndpoint(projectRoot, unity, 45, "player");
      results.push(pass("runtime endpoint direct health", { baseUrl: runtimeEndpoint.baseUrl, endpointId: runtimeEndpoint.health.endpointId }));
      await callTool(playMode.baseUrl, "exit", "editor.playmode.set", { state: "exit" }, 30);
      results.push(pass("editor.playmode.set exit", {}));
    }

    console.log(JSON.stringify({ passed: true, projectRoot, logFile, unityProcessId: unity.pid, results }, null, 2));
  } finally {
    if (!options.keepUnityAlive && !unity.killed) {
      unity.kill("SIGTERM");
      await sleep(1000);
      if (!unity.killed) {
        unity.kill("SIGKILL");
      }
    }
  }
}

function updateManifestAdd(projectRoot, options) {
  const manifestPath = path.join(projectRoot, "Packages", "manifest.json");
  const file = readFileLines(manifestPath);
  const lines = removePackageDependencyLines(file.lines);
  const dependenciesStart = findDependenciesStart(lines);
  if (dependenciesStart < 0) {
    throw new Error(`Could not find dependencies object in ${manifestPath}`);
  }

  const existingCount = countDependencyEntries(lines, dependenciesStart);
  const dependencyLines = newPackageDependencyLines(existingCount > 0, options);
  const updated = [];
  for (let index = 0; index < lines.length; index++) {
    updated.push(lines[index]);
    if (index === dependenciesStart) {
      updated.push(...dependencyLines);
    }
  }

  writeFileLines(manifestPath, updated, file.newLine);
  console.log(`Updated ${manifestPath}`);
}

function updateManifestRemove(projectRoot) {
  const manifestPath = path.join(projectRoot, "Packages", "manifest.json");
  const file = readFileLines(manifestPath);
  const lines = repairDependencyTrailingComma(removePackageDependencyLines(file.lines));
  writeFileLines(manifestPath, lines, file.newLine);
  console.log(`Updated ${manifestPath}`);
}

function newPackageDependencyLines(hasExistingDependencies, options) {
  const prefix = resolveDependencyPrefix(options);
  const entries = [
    ["com.tencent.puerts.core", `${prefix}/third_party/puerts/unity/upms/core`],
    ["com.tencent.puerts.v8", `${prefix}/third_party/puerts/unity/upms/v8`],
    ["puerts-unity-mcp", `${prefix}/Packages/puerts-unity-mcp`]
  ];

  return entries.map(([key, value], index) => {
    const isLastInserted = index === entries.length - 1;
    const comma = hasExistingDependencies || !isLastInserted ? "," : "";
    return `    "${key}": "${value}"${comma}`;
  });
}

function copyMobileConfig(projectRoot) {
  const source = path.join(projectRoot, "puerts-unity-mcp-extension", "mobile-mcp-config.json");
  if (!fs.existsSync(source)) {
    ensureDirectory(path.dirname(source));
    fs.writeFileSync(source, `${JSON.stringify(defaultMobileConfig(), null, 2)}${os.EOL}`, "utf8");
    console.log(`Created default mobile MCP config at ${source}`);
  }

  const destination = path.join(projectRoot, "Assets", "StreamingAssets", "PuertsUnityMcp", "mobile-mcp-config.json");
  ensureDirectory(path.dirname(destination));
  fs.copyFileSync(source, destination);
  writeTextIfMissing(path.join(projectRoot, "Assets", "StreamingAssets", "PuertsUnityMcp.meta"), [
    "fileFormatVersion: 2",
    "guid: fedd773bed9449942bd9f4f860d880d4",
    "folderAsset: yes",
    "DefaultImporter:",
    "  externalObjects: {}",
    "  userData:",
    "  assetBundleName:",
    "  assetBundleVariant:",
    ""
  ].join("\n"));
  writeTextIfMissing(`${destination}.meta`, [
    "fileFormatVersion: 2",
    "guid: 0513931682045c74db8b27676af54eb9",
    "DefaultImporter:",
    "  externalObjects: {}",
    "  userData:",
    "  assetBundleName:",
    "  assetBundleVariant:",
    ""
  ].join("\n"));
  console.log(`Copied ${source} to ${destination}`);
}

function copyAndroidNativeLibraries(projectRoot, options) {
  const repoRoot = resolvePumRepoRoot(projectRoot, options);
  const packageRoot = resolvePumPackageRoot(projectRoot, options);
  const copied = [];
  const bundled = [];
  for (const [abi, cpuName] of androidAbiCpuNames.entries()) {
    for (const library of androidNativeLibraries) {
      const destination = path.join(packageRoot, "Plugins", "Android", "libs", abi, library.fileName);
      if (fs.existsSync(destination) && fs.existsSync(`${destination}.meta`)) {
        bundled.push(path.relative(projectRoot, destination).replace(/\\/g, "/"));
        continue;
      }

      const source = path.join(
        repoRoot,
        "third_party",
        "puerts",
        "unity",
        "upms",
        library.packageName,
        "Plugins",
        "Android",
        "libs",
        abi,
        library.fileName
      );
      if (!fs.existsSync(source)) {
        continue;
      }

      ensureDirectory(path.dirname(destination));
      fs.copyFileSync(source, destination);
      fs.writeFileSync(`${destination}.meta`, buildAndroidNativePluginMeta(projectRoot, destination, cpuName), "utf8");
      copied.push(path.relative(projectRoot, destination).replace(/\\/g, "/"));
    }
  }

  const requiredArm64 = ["libPuertsCore.so", "libPapiV8.so"];
  for (const fileName of requiredArm64) {
    const target = path.join(packageRoot, "Plugins", "Android", "libs", "arm64-v8a", fileName);
    if (!fs.existsSync(target)) {
      throw new Error(`Required Android PuerTS native library was not copied: ${target}`);
    }
  }

  var message = `Copied Android PuerTS native libraries: ${copied.length > 0 ? copied.join(", ") : "none"}`;
  if (bundled.length > 0) {
    message += `. Already bundled: ${bundled.join(", ")}`;
  }

  console.log(message);
}

function removeAndroidNativeLibraries(projectRoot, options = {}) {
  const packageRoot = resolvePumPackageRoot(projectRoot, options);
  for (const abi of androidAbiCpuNames.keys()) {
    for (const library of androidNativeLibraries) {
      removePathByAbsolutePath(projectRoot, path.join(packageRoot, "Plugins", "Android", "libs", abi, library.fileName));
    }

    removeEmptyDirectoryByAbsolutePath(projectRoot, path.join(packageRoot, "Plugins", "Android", "libs", abi));
  }

  removeEmptyDirectoryByAbsolutePath(projectRoot, path.join(packageRoot, "Plugins", "Android", "libs"));
  removeEmptyDirectoryByAbsolutePath(projectRoot, path.join(packageRoot, "Plugins", "Android"));
  removeEmptyDirectoryByAbsolutePath(projectRoot, path.join(packageRoot, "Plugins"));
}

function resolvePumRepoRoot(projectRoot, options = {}) {
  if (options.packageRoot && options.packageRoot !== true) {
    return path.resolve(String(options.packageRoot));
  }

  return path.resolve(projectRoot, options.localPackageDirectoryName || "puerts-unity-mcp");
}

function resolvePumPackageRoot(projectRoot, options = {}) {
  return path.join(resolvePumRepoRoot(projectRoot, options), "Packages", "puerts-unity-mcp");
}

function buildAndroidNativePluginMeta(projectRoot, assetPath, cpuName) {
  const relativeAssetPath = path.relative(projectRoot, assetPath).replace(/\\/g, "/");
  const guid = stableGuid(`puerts-unity-mcp:${relativeAssetPath}`);
  return [
    "fileFormatVersion: 2",
    `guid: ${guid}`,
    "PluginImporter:",
    "  externalObjects: {}",
    "  serializedVersion: 2",
    "  iconMap: {}",
    "  executionOrder: {}",
    "  defineConstraints: []",
    "  isPreloaded: 0",
    "  isOverridable: 1",
    "  isExplicitlyReferenced: 0",
    "  validateReferences: 1",
    "  platformData:",
    "  - first:",
    "      : Any",
    "    second:",
    "      enabled: 0",
    "      settings:",
    "        Exclude Android: 0",
    "        Exclude Editor: 1",
    "        Exclude Linux64: 1",
    "        Exclude OSXUniversal: 1",
    "        Exclude WebGL: 1",
    "        Exclude Win: 1",
    "        Exclude Win64: 1",
    "  - first:",
    "      Android: Android",
    "    second:",
    "      enabled: 1",
    "      settings:",
    "        AndroidSharedLibraryType: Executable",
    `        CPU: ${cpuName}`,
    "  - first:",
    "      Any:",
    "    second:",
    "      enabled: 0",
    "      settings: {}",
    "  - first:",
    "      Editor: Editor",
    "    second:",
    "      enabled: 0",
    "      settings:",
    "        CPU: AnyCPU",
    "        DefaultValueInitialized: true",
    "        OS: AnyOS",
    "  userData:",
    "  assetBundleName:",
    "  assetBundleVariant:",
    ""
  ].join("\n");
}

function stableGuid(value) {
  return crypto.createHash("md5").update(value).digest("hex");
}

function writeAndroidPermissions(projectRoot, options) {
  const packageRoot = resolvePumPackageRoot(projectRoot, options);
  const androidRoot = path.join(packageRoot, "Plugins", "Android", "puerts-unity-mcp.androidlib");
  const manifestPath = path.join(androidRoot, "AndroidManifest.xml");
  const projectPropertiesPath = path.join(androidRoot, "project.properties");
  if (fs.existsSync(manifestPath) && fs.existsSync(projectPropertiesPath) && fs.existsSync(`${androidRoot}.meta`)) {
    console.log(`Android MCP permission library is already bundled at ${androidRoot}`);
    return;
  }

  ensureDirectory(androidRoot);
  const manifest = [
    '<manifest xmlns:android="http://schemas.android.com/apk/res/android">',
    '  <uses-permission android:name="android.permission.INTERNET" />',
    '  <uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />',
    '  <uses-permission android:name="android.permission.ACCESS_WIFI_STATE" />',
    '  <uses-permission android:name="android.permission.CHANGE_WIFI_MULTICAST_STATE" />',
    "</manifest>",
    ""
  ].join("\n");
  fs.writeFileSync(manifestPath, manifest, "utf8");
  fs.writeFileSync(projectPropertiesPath, "target=android-35\n", "utf8");
  writeTextIfMissing(`${androidRoot}.meta`, [
    "fileFormatVersion: 2",
    "guid: fee75e8788b69b94e94d42d7f459a053",
    "PluginImporter:",
    "  externalObjects: {}",
    "  serializedVersion: 2",
    "  iconMap: {}",
    "  executionOrder: {}",
    "  defineConstraints: []",
    "  isPreloaded: 0",
    "  isOverridable: 0",
    "  isExplicitlyReferenced: 0",
    "  validateReferences: 1",
    "  platformData:",
    "  - first:",
    "      Android: Android",
    "    second:",
    "      enabled: 1",
    "      settings: {}",
    "  - first:",
    "      Any:",
    "    second:",
    "      enabled: 0",
    "      settings: {}",
    "  - first:",
    "      Editor: Editor",
    "    second:",
    "      enabled: 0",
    "      settings:",
    "        DefaultValueInitialized: true",
    "  userData:",
    "  assetBundleName:",
    "  assetBundleVariant:",
    ""
  ].join("\n"));
  console.log(`Wrote Android MCP permission library to ${androidRoot}`);
}

function removeAndroidPermissions(projectRoot, options = {}) {
  const packageRoot = resolvePumPackageRoot(projectRoot, options);
  removePathByAbsolutePath(projectRoot, path.join(packageRoot, "Plugins", "Android", "puerts-unity-mcp.androidlib"));
}

function removeLegacyProjectAndroidPluginArtifacts(projectRoot) {
  for (const abi of androidAbiCpuNames.keys()) {
    for (const library of androidNativeLibraries) {
      removePathInsideProject(projectRoot, path.join("Assets", "Plugins", "Android", "libs", abi, library.fileName));
    }

    removeEmptyDirectory(projectRoot, path.join("Assets", "Plugins", "Android", "libs", abi));
  }

  removeEmptyDirectory(projectRoot, path.join("Assets", "Plugins", "Android", "libs"));
  removePathInsideProject(projectRoot, path.join("Assets", "Plugins", "Android", "puerts-unity-mcp.androidlib"));
}

function removeLegacyProjectGeneratedPluginArtifacts(projectRoot) {
  removePathInsideProject(projectRoot, path.join("Assets", "Gen", "Plugins", "puerts_il2cpp"));
  removeEmptyDirectory(projectRoot, path.join("Assets", "Gen", "Plugins"));
  removeEmptyDirectory(projectRoot, path.join("Assets", "Gen"));
  removePathByAbsolutePath(projectRoot, path.join(projectRoot, "puerts-unity-mcp-extension", "Plugins", "puerts_il2cpp"));
  removeEmptyDirectoryByAbsolutePath(projectRoot, path.join(projectRoot, "puerts-unity-mcp-extension", "Plugins"));
  removePathByAbsolutePath(projectRoot, path.join(projectRoot, "puerts-unity-mcp-extension", "Generated"));
}

function defaultMobileConfig() {
  return {
    version: 4,
    runtimeAutoStart: true,
    runtimeBindAddress: "0.0.0.0",
    runtimePort: 18991,
    runtimeLogBufferSize: 500,
    lanDiscoveryEnabled: true,
    name: "",
    name_group: "default",
    allowJsEval: true,
    allowReflection: true,
    allowPrivateReflection: true,
    allowFileAccess: true,
    allowNetworkAccess: true,
    allowRuntimeCodeLoad: true,
    targetId: "",
    maxCommandsPerFrame: 4,
    runInBackground: true,
    enableFileCommandPump: false,
    enableDiskHeartbeat: false,
    enableDiscoveredEndpointCache: false,
    enableAotMissLog: false,
    screenshotWriteMode: "memory",
    heartbeatIntervalMs: 30000
  };
}

function addPackageTestable(manifestPath) {
  const packageName = "puerts-unity-mcp";
  const file = readFileLines(manifestPath);
  const lines = file.lines;
  for (let index = 0; index < lines.length; index++) {
    if (/^\s*"testables"\s*:\s*\[\s*\]\s*,?\s*$/.test(lines[index])) {
      const comma = lines[index].trimEnd().endsWith(",") ? "," : "";
      lines.splice(index, 1, '  "testables": [', `    "${packageName}"`, `  ]${comma}`);
      writeFileLines(manifestPath, lines, file.newLine);
      return;
    }

    if (/^\s*"testables"\s*:\s*\[/.test(lines[index])) {
      for (let end = index + 1; end < lines.length; end++) {
        if (lines[end].includes(`"${packageName}"`)) {
          return;
        }

        if (/^\s*\]/.test(lines[end])) {
          for (let previous = end - 1; previous > index; previous--) {
            if (lines[previous].trim()) {
              if (!/,\s*$/.test(lines[previous])) {
                lines[previous] += ",";
              }

              break;
            }
          }

          lines.splice(end, 0, `    "${packageName}"`);
          writeFileLines(manifestPath, lines, file.newLine);
          return;
        }
      }
    }
  }

  const rootEnd = findRootObjectEnd(lines);
  if (rootEnd < 0) {
    throw new Error(`Could not find root object end in ${manifestPath}`);
  }

  for (let index = rootEnd - 1; index >= 0; index--) {
    if (lines[index].trim()) {
      if (!/,\s*$/.test(lines[index])) {
        lines[index] += ",";
      }

      break;
    }
  }

  lines.splice(rootEnd, 0, '  "testables": [', `    "${packageName}"`, "  ]");
  writeFileLines(manifestPath, lines, file.newLine);
}

function readFileLines(filePath) {
  const text = fs.readFileSync(filePath, "utf8");
  const newLine = text.includes("\r\n") ? "\r\n" : "\n";
  const lines = text.split(/\r\n|\n|\r/);
  if (lines.length > 0 && lines[lines.length - 1] === "") {
    lines.pop();
  }

  return { lines, newLine };
}

function writeFileLines(filePath, lines, newLine) {
  fs.writeFileSync(filePath, `${lines.join(newLine)}${newLine}`, "utf8");
}

function findDependenciesStart(lines) {
  return lines.findIndex((line) => /^\s*"dependencies"\s*:\s*\{\s*$/.test(line));
}

function findObjectEnd(lines, startIndex) {
  for (let index = startIndex + 1; index < lines.length; index++) {
    if (/^\s*}\s*,?\s*$/.test(lines[index])) {
      return index;
    }
  }

  return -1;
}

function findRootObjectEnd(lines) {
  for (let index = lines.length - 1; index >= 0; index--) {
    if (/^\s*}\s*$/.test(lines[index])) {
      return index;
    }
  }

  return -1;
}

function removePackageDependencyLines(lines) {
  return lines.filter((line) => !packageKeys.some((key) => new RegExp(`^\\s*"${escapeRegExp(key)}"\\s*:`).test(line)));
}

function countDependencyEntries(lines, startIndex) {
  const endIndex = findObjectEnd(lines, startIndex);
  if (endIndex < 0) {
    throw new Error("Could not find the end of the dependencies object in manifest.json");
  }

  let count = 0;
  for (let index = startIndex + 1; index < endIndex; index++) {
    if (/^\s*"[^"]+"\s*:/.test(lines[index])) {
      count++;
    }
  }

  return count;
}

function repairDependencyTrailingComma(lines) {
  const start = findDependenciesStart(lines);
  if (start < 0) {
    return lines;
  }

  const end = findObjectEnd(lines, start);
  if (end < 0) {
    return lines;
  }

  for (let index = end - 1; index > start; index--) {
    if (!lines[index].trim()) {
      continue;
    }

    lines[index] = lines[index].replace(/,(\s*)$/, "$1");
    break;
  }

  return lines;
}

function removePathInsideProject(projectRoot, relativePath) {
  const target = path.resolve(projectRoot, relativePath);
  assertPathInside(projectRoot, target);
  if (fs.existsSync(target)) {
    fs.rmSync(target, { recursive: true, force: true });
    console.log(`Removed ${target}`);
  }

  const meta = `${target}.meta`;
  if (fs.existsSync(meta)) {
    fs.rmSync(meta, { force: true });
    console.log(`Removed ${meta}`);
  }
}

function removePathByAbsolutePath(projectRoot, targetPath) {
  const target = path.resolve(targetPath);
  assertPathInside(projectRoot, target);
  const relativePath = path.relative(projectRoot, target);
  removePathInsideProject(projectRoot, relativePath);
}

function removeEmptyDirectory(projectRoot, relativePath) {
  const target = path.resolve(projectRoot, relativePath);
  if (fs.existsSync(target) && fs.readdirSync(target).length === 0) {
    removePathInsideProject(projectRoot, relativePath);
  }
}

function removeEmptyDirectoryByAbsolutePath(projectRoot, targetPath) {
  const target = path.resolve(targetPath);
  assertPathInside(projectRoot, target);
  if (fs.existsSync(target) && fs.readdirSync(target).length === 0) {
    removePathByAbsolutePath(projectRoot, target);
  }
}

function removeDirectorySafe(target) {
  if (!fs.existsSync(target)) {
    return;
  }

  const resolved = path.resolve(target);
  const leaf = path.basename(resolved);
  if (!leaf || resolved.length < 10) {
    throw new Error(`Refusing to remove unsafe path: ${resolved}`);
  }

  fs.rmSync(resolved, { recursive: true, force: true });
}

function assertPathInside(root, target) {
  const resolvedRoot = path.resolve(root);
  const resolvedTarget = path.resolve(target);
  const relative = path.relative(resolvedRoot, resolvedTarget);
  if (relative.startsWith("..") || path.isAbsolute(relative)) {
    throw new Error(`Refusing to operate outside root. Root=${resolvedRoot} Target=${resolvedTarget}`);
  }
}

function copyDirectoryMirror(source, target, ignoredNames = new Set()) {
  if (!fs.existsSync(source)) {
    throw new Error(`Source directory not found: ${source}`);
  }

  removeDirectorySafe(target);
  ensureDirectory(target);
  for (const entry of fs.readdirSync(source, { withFileTypes: true })) {
    if (ignoredNames.has(entry.name)) {
      continue;
    }

    copyRecursive(path.join(source, entry.name), path.join(target, entry.name));
  }
}

function copyDirectoryOverlay(source, target) {
  if (!fs.existsSync(source)) {
    throw new Error(`Source directory not found: ${source}`);
  }

  ensureDirectory(target);
  for (const entry of fs.readdirSync(source, { withFileTypes: true })) {
    copyRecursive(path.join(source, entry.name), path.join(target, entry.name));
  }
}

function copyRecursive(source, target) {
  const stats = fs.statSync(source);
  if (stats.isDirectory()) {
    ensureDirectory(target);
    for (const entry of fs.readdirSync(source, { withFileTypes: true })) {
      copyRecursive(path.join(source, entry.name), path.join(target, entry.name));
    }
    return;
  }

  ensureDirectory(path.dirname(target));
  fs.copyFileSync(source, target);
}

function ensureDirectory(directory) {
  fs.mkdirSync(directory, { recursive: true });
}

function writeTextIfMissing(filePath, content) {
  if (fs.existsSync(filePath)) {
    return;
  }

  ensureDirectory(path.dirname(filePath));
  fs.writeFileSync(filePath, content, "utf8");
}

async function expandReleasePackage(packageName, version, upmsRoot, downloadsRoot) {
  const url = await getReleaseAssetUrl(packageName, version);
  const archive = path.join(downloadsRoot, path.basename(new URL(url).pathname));
  if (!fs.existsSync(archive)) {
    await downloadFile(url, archive);
  }

  const result = spawnSync("tar", ["-xzf", archive, "-C", upmsRoot], { stdio: "inherit" });
  if (result.status !== 0) {
    throw new Error(`tar failed while extracting ${archive}`);
  }
}

async function getReleaseAssetUrl(packageName, version) {
  const assetsUrl = `https://github.com/Tencent/puerts/releases/expanded_assets/Unity_v${version}`;
  const html = await readHttpsText(assetsUrl);
  const pattern = new RegExp(`href="(/Tencent/puerts/releases/download/[^"]*PuerTS_${packageName}_[^"]*\\.tar\\.gz)"`);
  const match = html.match(pattern);
  if (!match) {
    throw new Error(`PuerTS ${packageName} release archive not found for Unity_v${version}`);
  }

  return `https://github.com${match[1].replace(/&amp;/g, "&")}`;
}

function readHttpsText(url) {
  return new Promise((resolve, reject) => {
    https.get(url, { headers: { "user-agent": "puerts-unity-mcp" } }, (response) => {
      if (response.statusCode >= 300 && response.statusCode < 400 && response.headers.location) {
        readHttpsText(new URL(response.headers.location, url).toString()).then(resolve, reject);
        return;
      }

      if (response.statusCode !== 200) {
        reject(new Error(`HTTP ${response.statusCode} while reading ${url}`));
        return;
      }

      response.setEncoding("utf8");
      let body = "";
      response.on("data", (chunk) => {
        body += chunk;
      });
      response.on("end", () => resolve(body));
    }).on("error", reject);
  });
}

function downloadFile(url, target) {
  return new Promise((resolve, reject) => {
    ensureDirectory(path.dirname(target));
    const file = fs.createWriteStream(target);
    https.get(url, { headers: { "user-agent": "puerts-unity-mcp" } }, (response) => {
      if (response.statusCode >= 300 && response.statusCode < 400 && response.headers.location) {
        file.close();
        fs.rmSync(target, { force: true });
        downloadFile(new URL(response.headers.location, url).toString(), target).then(resolve, reject);
        return;
      }

      if (response.statusCode !== 200) {
        file.close();
        fs.rmSync(target, { force: true });
        reject(new Error(`HTTP ${response.statusCode} while downloading ${url}`));
        return;
      }

      response.pipe(file);
      file.on("finish", () => {
        file.close(resolve);
      });
    }).on("error", (error) => {
      file.close();
      fs.rmSync(target, { force: true });
      reject(error);
    });
  });
}

function removeBlockedAssemblyFilter(root) {
  const filterPath = path.join(root, "unity", "upms", "core", "Editor", "Src", "Generator", "InstructionsFilter.cs");
  if (!fs.existsSync(filterPath)) {
    return;
  }

  const blockedAssembly = `Unity.Plastic.${"New"}tonsoft.Json`;
  const filtered = fs.readFileSync(filterPath, "utf8")
    .split(/\r\n|\n|\r/)
    .filter((line) => !line.includes(blockedAssembly))
    .join(os.EOL);
  fs.writeFileSync(filterPath, filtered, "utf8");
}

function assertRequiredNativeFiles(root) {
  const required = [
    path.join("unity", "upms", "core", "Plugins", "x86_64", "PuertsCore.dll"),
    path.join("unity", "upms", "v8", "Plugins", "x86_64", "PapiV8.dll"),
    path.join("unity", "upms", "core", "Plugins", "Android", "libs", "arm64-v8a", "libPuertsCore.so"),
    path.join("unity", "upms", "v8", "Plugins", "Android", "libs", "arm64-v8a", "libPapiV8.so"),
    path.join("unity", "upms", "core", "Plugins", "iOS", "libPuertsCore.a"),
    path.join("unity", "upms", "v8", "Plugins", "iOS", "libPapiV8.a")
  ];

  for (const relative of required) {
    const target = path.join(root, relative);
    if (!fs.existsSync(target)) {
      throw new Error(`Required PuerTS native file is missing: ${target}`);
    }
  }
}

async function invokeMcp(baseUrl, body, timeoutSeconds = 30) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutSeconds * 1000);
  try {
    const response = await fetch(`${baseUrl}/mcp`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
      signal: controller.signal
    });
    const text = await response.text();
    if (!response.ok) {
      throw new Error(`HTTP ${response.status} from ${baseUrl}/mcp: ${text}`);
    }

    return JSON.parse(text);
  } finally {
    clearTimeout(timeout);
  }
}

async function getEndpointHealth(baseUrl, timeoutSeconds = 2) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutSeconds * 1000);
  try {
    const response = await fetch(`${baseUrl}/health`, { signal: controller.signal });
    if (!response.ok) {
      throw new Error(`HTTP ${response.status} from ${baseUrl}/health`);
    }

    return await response.json();
  } finally {
    clearTimeout(timeout);
  }
}

async function findEndpoint(projectRoot, unityProcess, timeoutSeconds, endpointKind = "editor") {
  const deadline = Date.now() + timeoutSeconds * 1000;
  const projectName = path.basename(projectRoot);
  while (Date.now() < deadline) {
    for (let port = 18990; port <= 18999; port++) {
      const baseUrl = `http://127.0.0.1:${port}`;
      try {
        const health = await getEndpointHealth(baseUrl, 2);
        const kindMatches = !endpointKind || health.endpointKind === endpointKind;
        if (kindMatches && (health.endpointName === projectName || path.resolve(health.projectRoot || "") === projectRoot)) {
          return { baseUrl, health };
        }
      } catch {
      }
    }

    if (unityProcess.exitCode !== null) {
      throw new Error(`Unity exited before MCP endpoint started. ExitCode=${unityProcess.exitCode}`);
    }

    await sleep(1000);
  }

  throw new Error(`MCP endpoint did not start within ${timeoutSeconds} seconds.`);
}

async function waitForPlayModeTarget(projectRoot, unityProcess, timeoutSeconds) {
  const deadline = Date.now() + timeoutSeconds * 1000;
  let lastError = "";
  let lastTargets = null;
  while (Date.now() < deadline) {
    try {
      const endpoint = await findEndpoint(projectRoot, unityProcess, 15, "editor");
      const response = await invokeMcp(endpoint.baseUrl, {
        jsonrpc: "2.0",
        id: "targets",
        method: "tools/call",
        params: { name: "runtime.targets.list", arguments: {} }
      }, 15);
      const targets = getMcpStructuredContent(response);
      lastTargets = targets;
      if ((targets?.targets || []).some((target) => target.source === "local-playmode")) {
        return { baseUrl: endpoint.baseUrl, targets };
      }
    } catch (error) {
      lastError = error.message;
    }

    if (unityProcess.exitCode !== null) {
      throw new Error(`Unity exited while waiting for Play Mode target. ExitCode=${unityProcess.exitCode}`);
    }

    await sleep(2000);
  }

  throw new Error(`Play Mode runtime target did not appear. Last error: ${lastError}. Last targets: ${JSON.stringify(lastTargets)}`);
}

async function callTool(baseUrl, id, name, args, timeoutSeconds = 30) {
  const response = await invokeMcp(baseUrl, {
    jsonrpc: "2.0",
    id,
    method: "tools/call",
    params: { name, arguments: args }
  }, timeoutSeconds);
  return getMcpStructuredContent(response);
}

async function runDomainReloadSmoke(projectRoot, unityProcess, endpoint, timeoutSeconds, results) {
  const marker = `dr_${Date.now()}`;
  const requestId = `domain_reload_${marker}`;
  const sourcePath = path.join(resolvePumPackageRoot(projectRoot), "Editor", "PuertsUnityMcpDomainReloadProbe.cs");
  ensureDirectory(path.dirname(sourcePath));
  fs.writeFileSync(sourcePath, `public static class PuertsUnityMcpDomainReloadProbe { public const string Marker = "${marker}"; public const int Value = 42; }\n`, "utf8");
  const compile = await callTool(endpoint.baseUrl, "domainReloadCompile", "editor.compile", {
    requestId,
    wait: false
  }, 30);
  const compileResultPath = compile.compileResultPath;
  const deadline = Date.now() + timeoutSeconds * 1000;
  while (Date.now() < deadline) {
    if (fs.existsSync(compileResultPath)) {
      const compileResult = JSON.parse(fs.readFileSync(compileResultPath, "utf8"));
      assertCondition(compileResult.success === true, `domain reload probe compile failed: ${JSON.stringify(compileResult)}`);
      const readyEndpoint = await findEndpoint(projectRoot, unityProcess, 45, "editor");
      const evalResult = await callTool(readyEndpoint.baseUrl, "domainReloadEval", "editor.js.eval", {
        code: "20 + 22",
        mode: "expression",
        chunkName: "test-domain-reload-post-eval"
      }, 60);
      assertCondition(evalResult.kind === "number" && Number(evalResult.value) === 42, "editor.js.eval failed after domain reload");
      cleanupDomainReloadProbe(sourcePath, readyEndpoint);
      results.push(pass("domain reload recovery", { requestId, sourcePath, compileResultPath, postReloadEval: evalResult }));
      return;
    }

    if (unityProcess.exitCode !== null) {
      throw new Error(`Unity exited during domain reload test. ExitCode=${unityProcess.exitCode}`);
    }

    await sleep(250);
  }

  throw new Error(`Domain reload recovery did not complete within ${timeoutSeconds} seconds.`);
}

async function cleanupDomainReloadProbe(sourcePath, endpoint) {
  try {
    fs.rmSync(sourcePath, { force: true });
    fs.rmSync(`${sourcePath}.meta`, { force: true });
  } catch {
  }

  try {
    await callTool(endpoint.baseUrl, "domainReloadCleanup", "editor.compile", {
      requestId: `domain_reload_cleanup_${Date.now()}`,
      wait: false
    }, 10);
  } catch {
  }
}

function getMcpStructuredContent(response) {
  if (!response?.result) {
    return null;
  }

  if (response.result.structuredContent) {
    return response.result.structuredContent;
  }

  if (response.result.structuredContentJson) {
    return JSON.parse(response.result.structuredContentJson);
  }

  return null;
}

function pass(name, detail) {
  return { name, passed: true, detail };
}

function assertCondition(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
