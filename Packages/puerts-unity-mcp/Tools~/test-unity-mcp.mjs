#!/usr/bin/env node
import { getArg, getBoolArg, getIntArg, parseArgs, runUnityMcpSmokeTest } from "./pum-cli-lib.mjs";

const args = parseArgs();
const unityProjectRoot = getArg(args, ["unity-project-root", "UnityProjectRoot"], "");
if (!unityProjectRoot || unityProjectRoot === true) {
  throw new Error("Missing required --unity-project-root");
}

await runUnityMcpSmokeTest({
  unityProjectRoot: String(unityProjectRoot),
  unityExe: String(getArg(args, ["unity-exe", "UnityExe"], "")),
  logFile: String(getArg(args, ["log-file", "LogFile"], "")),
  startupTimeoutSeconds: getIntArg(args, ["startup-timeout-seconds", "StartupTimeoutSeconds"], 300),
  playModeTimeoutSeconds: getIntArg(args, ["play-mode-timeout-seconds", "PlayModeTimeoutSeconds"], 360),
  domainReloadTimeoutSeconds: getIntArg(args, ["domain-reload-timeout-seconds", "DomainReloadTimeoutSeconds"], 360),
  skipPlayMode: getBoolArg(args, ["skip-play-mode", "SkipPlayMode"], false),
  includeDomainReload: getBoolArg(args, ["include-domain-reload", "IncludeDomainReload"], false),
  keepUnityAlive: getBoolArg(args, ["keep-unity-alive", "KeepUnityAlive"], false)
});
