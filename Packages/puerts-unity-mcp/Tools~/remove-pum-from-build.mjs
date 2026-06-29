#!/usr/bin/env node
import { getBoolArg, getToolRoots, parseArgs, removePumFromBuild, resolveUnityProjectRoot } from "./pum-cli-lib.mjs";

const roots = getToolRoots(import.meta.url);
const args = parseArgs();
const projectRoot = resolveUnityProjectRoot(args, roots.toolsRoot);
removePumFromBuild(projectRoot, {
  removeState: getBoolArg(args, ["remove-state", "RemoveState"], false)
});
