#!/usr/bin/env node
import { getArg, getBoolArg, getToolRoots, parseArgs, syncLocalPackage } from "./pum-cli-lib.mjs";

const roots = getToolRoots(import.meta.url);
const args = parseArgs();
const unityProjectRoot = getArg(args, ["unity-project-root", "UnityProjectRoot"], "");
if (!unityProjectRoot || unityProjectRoot === true) {
  throw new Error("Missing required --unity-project-root");
}

syncLocalPackage({
  unityProjectRoot: String(unityProjectRoot),
  direction: String(getArg(args, ["direction", "Direction"], "push")),
  localPackageDirectoryName: String(getArg(args, ["local-package-directory-name", "LocalPackageDirectoryName"], "puerts-unity-mcp")),
  skipManifestUpdate: getBoolArg(args, ["skip-manifest-update", "SkipManifestUpdate"], false),
  enablePackageTests: getBoolArg(args, ["enable-package-tests", "EnablePackageTests"], false),
  toolsRoot: roots.toolsRoot
});
