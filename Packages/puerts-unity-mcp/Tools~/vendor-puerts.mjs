#!/usr/bin/env node
import { getArg, getToolRoots, parseArgs, vendorPuerts } from "./pum-cli-lib.mjs";

const roots = getToolRoots(import.meta.url);
const args = parseArgs();
await vendorPuerts({
  repoRoot: roots.repoRoot,
  source: getArg(args, ["source", "Source"], ""),
  destination: getArg(args, ["destination", "Destination"], ""),
  version: String(getArg(args, ["version", "Version"], "3.0.2")),
  downloadDirectory: getArg(args, ["download-directory", "DownloadDirectory"], "")
});
