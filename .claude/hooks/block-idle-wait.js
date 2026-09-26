// PreToolUse hook: refuses idle-polling Bash/PowerShell calls (see CLAUDE.md §4 "Waiting on tests").
const WAITING_WORDS = /^(waiting|wait|still|polling|poll)/i;
const STRIP_PREFIX_RE = /^(cd|Set-Location)\s+(?:"[^"]*"|'[^']*'|\S+)\s*(?:&&|;)\s*/i;
const SLEEP_RE = /^(?:sleep\s+\d+(?:\.\d+)?|Start-Sleep\s+(?:-Seconds\s+)?\d+(?:\.\d+)?)\s*(?:(?:&&|;)\s*(?:echo|Write-Output|Write-Host)\s+.+)?$/i;

function isPureEchoWaiting(cmd) {
  const match = cmd.match(/^(?:echo|Write-Output|Write-Host)\s+(.*)$/i);
  if (!match) return false;
  const rest = match[1];
  if (/&&|;|\|/.test(rest)) return false;
  const quoted = rest.match(/^"([^"]*)"$/) || rest.match(/^'([^']*)'$/);
  const content = quoted ? quoted[1] : rest;
  return WAITING_WORDS.test(content.trim());
}

function isIdleWait(cmd) {
  const trimmed = cmd.trim();
  if (trimmed === "true" || trimmed === ":") return true;
  if (SLEEP_RE.test(trimmed)) return true;
  if (isPureEchoWaiting(trimmed)) return true;
  return false;
}

function main(input) {
  let payload;
  try {
    payload = JSON.parse(input);
  } catch {
    return 0;
  }

  const toolName = payload.tool_name;
  if (toolName !== "Bash" && toolName !== "PowerShell") return 0;

  const command = payload.tool_input && payload.tool_input.command;
  if (typeof command !== "string") return 0;

  const stripped = command.replace(STRIP_PREFIX_RE, "").trim();
  if (!isIdleWait(stripped)) return 0;

  process.stderr.write(
    'Idle polling re-reads the whole context on every call. Wait once, blocking: run tests in the foreground with timeout ≈ p90, and if it times out, `until grep -qE "Test run summary|error CS|Build FAILED" <output file>; do sleep 5; done` with timeout ≈ p99 (table: CLAUDE.md §4 Waiting on tests).'
  );
  return 2;
}

let input = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => {
  input += chunk;
});
process.stdin.on("end", () => {
  process.exitCode = main(input);
});
