// The Codex plugin's own broker teardown, from the broker's own plugin version, minus every kill:
// ops/codex-broker.ps1 ends the processes itself, only those it verified, between the two steps.
//   node codex-broker-teardown.mjs shutdown <plugin scripts dir> <endpoint> <pid file> <cwd>
//   node codex-broker-teardown.mjs cleanup  <plugin scripts dir> <endpoint> <pid file> <cwd>
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { pathToFileURL } from "node:url";

const [step, scriptsDirectory, endpoint, pidFile, cwd] = process.argv.slice(2);
const { clearBrokerSession, loadBrokerSession, sendBrokerShutdown, teardownBrokerSession } = await import(
  pathToFileURL(path.join(scriptsDirectory, "lib", "broker-lifecycle.mjs")).href
);

if (step === "shutdown") {
  // A broker that accepts the connection but never answers would otherwise hang this forever.
  const shutdownTimeout = new Promise((resolve) => setTimeout(resolve, 5000).unref());
  await Promise.race([sendBrokerShutdown(endpoint), shutdownTimeout]);
} else if (step === "cleanup") {
  const sessionDir = path.dirname(pidFile);
  teardownBrokerSession({ endpoint, pidFile, logFile: path.join(sessionDir, "broker.log"), sessionDir });

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
} else {
  throw new Error(`Unknown step "${step}" - expected shutdown or cleanup.`);
}
