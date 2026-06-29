#!/usr/bin/env node
import { addPumToBuild, getArg, getBoolArg, getToolRoots, parseArgs, resolveUnityProjectRoot } from "./pum-cli-lib.mjs";

const roots = getToolRoots(import.meta.url);
const args = parseArgs();
const projectRoot = resolveUnityProjectRoot(args, roots.toolsRoot);
addPumToBuild(projectRoot, {
  localPackageDirectoryName: String(getArg(args, ["local-package-directory-name", "LocalPackageDirectoryName"], "puerts-unity-mcp")),
  packageRoot: getArg(args, ["package-root", "PackageRoot"], ""),
  skipAndroidPermissions: getBoolArg(args, ["skip-android-permissions", "SkipAndroidPermissions"], false)
});
