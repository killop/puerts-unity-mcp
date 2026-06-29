#!/usr/bin/env node
import { getArg, getBoolArg, getToolRoots, parseArgs, removePumFromBuild } from "./pum-cli-lib.mjs";

const roots = getToolRoots(import.meta.url);
const args = parseArgs();
const unityProjectRoot = getArg(args, ["unity-project-root", "UnityProjectRoot"], "");
if (!unityProjectRoot || unityProjectRoot === true) {
  throw new Error("Missing required --unity-project-root");
}

removePumFromBuild(String(unityProjectRoot), {
  removeState: getBoolArg(args, ["remove-state", "RemoveState"], false),
  toolsRoot: roots.toolsRoot
});
