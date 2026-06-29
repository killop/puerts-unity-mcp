#!/usr/bin/env node
import { getArg, getBoolArg, getToolRoots, installToUnityProject, parseArgs } from "./pum-cli-lib.mjs";

const roots = getToolRoots(import.meta.url);
const args = parseArgs();
const unityProjectRoot = getArg(args, ["unity-project-root", "UnityProjectRoot"], "");
if (!unityProjectRoot || unityProjectRoot === true) {
  throw new Error("Missing required --unity-project-root");
}

installToUnityProject({
  unityProjectRoot: String(unityProjectRoot),
  packageRoot: getArg(args, ["package-root", "PackageRoot"], ""),
  enablePackageTests: getBoolArg(args, ["enable-package-tests", "EnablePackageTests"], false),
  useProjectLocalPackage: getBoolArg(args, ["use-project-local-package", "UseProjectLocalPackage"], false),
  syncLocalPackage: getBoolArg(args, ["sync-local-package", "SyncLocalPackage"], false),
  localPackageDirectoryName: String(getArg(args, ["local-package-directory-name", "LocalPackageDirectoryName"], "puerts-unity-mcp")),
  toolsRoot: roots.toolsRoot
});
