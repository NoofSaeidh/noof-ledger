// Stops one Codex plugin broker the way the plugin's own SessionEnd hook does, using the plugin's
// functions from the broker's own version. Called by ops/codex-broker.ps1:
//   node codex-broker-teardown.mjs <plugin scripts dir> <endpoint> <broker pid> <pid file> <cwd>
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { pathToFileURL } from "node:url";

const [scriptsDirectory, endpoint, brokerPid, pidFile, cwd] = process.argv.slice(2);
const pluginLib = (name) => import(pathToFileURL(path.join(scriptsDirectory, "lib", name)).href);
const { clearBrokerSession, loadBrokerSession, sendBrokerShutdown, teardownBrokerSession } =
  await pluginLib("broker-lifecycle.mjs");
const { terminateProcessTree } = await pluginLib("process.mjs");

// A broker that accepts the connection but never answers would otherwise hang this forever.
const shutdownTimeout = new Promise((resolve) => setTimeout(resolve, 5000).unref());
await Promise.race([sendBrokerShutdown(endpoint), shutdownTimeout]);

const sessionDir = path.dirname(pidFile);
teardownBrokerSession({
  endpoint,
  pidFile,
  logFile: path.join(sessionDir, "broker.log"),
  sessionDir,
  pid: Number(brokerPid),
  killProcess: terminateProcessTree
});

// The plugin keeps broker.json under CLAUDE_PLUGIN_DATA when the companion ran from a Claude Code
// Bash shell, and under %TEMP% when it did not (a PowerShell shell has no such variable). Clear it
// wherever it names this broker - never a newer broker's.
const pluginDataDirectories = new Set([
  process.env.CLAUDE_PLUGIN_DATA,
  path.join(os.homedir(), ".claude", "plugins", "data", "codex-openai-codex"),
  undefined
]);
for (const dataDirectory of pluginDataDirectories) {
  if (dataDirectory === undefined) {
    delete process.env.CLAUDE_PLUGIN_DATA;
  } else {
    process.env.CLAUDE_PLUGIN_DATA = dataDirectory;
  }
  if (loadBrokerSession(cwd)?.endpoint === endpoint) {
    clearBrokerSession(cwd);
  }
}
