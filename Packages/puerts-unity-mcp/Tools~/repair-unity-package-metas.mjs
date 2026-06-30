#!/usr/bin/env node
import { getArg, parseArgs, repairUnityPackageImportMetas } from "./pum-cli-lib.mjs";

const args = parseArgs();
const unityProjectRoot = getArg(args, ["unity-project-root", "UnityProjectRoot"], "");
if (!unityProjectRoot || unityProjectRoot === true) {
  throw new Error("Missing required --unity-project-root");
}

repairUnityPackageImportMetas(String(unityProjectRoot), {
  localPackageDirectoryName: String(getArg(args, ["local-package-directory-name", "LocalPackageDirectoryName"], "puerts-unity-mcp"))
});
