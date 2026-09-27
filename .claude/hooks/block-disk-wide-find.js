// PreToolUse hook: refuses an accidental `find` rooted at the whole disk or the home directory (see
// CLAUDE.md "Shell and search"). A `find / ...` has held 11.6M handles for hours on this machine and
// slowed every process spawn; the built-in Glob tool is instant for the same job.
//
// Threat model: an agent typing the wrong path by accident, not someone deliberately evading this
// hook. It works over the raw command text with a couple of regexes, not a real shell parser — it
// does not track `cd`, shell variables, nested shells (`sh -c`, subshells), or escaping. See the PR
// for the fuller parser this replaced and why it was cut back down.

const FIND_INVOCATION_RE = /(?:^|[;&|(\n\r])\s*find\s+(?:-[A-Za-z]\S*\s+)*(\S+)/g;
const HOME_USERS_RE = /^\/c\/users\/[^/]+$/;
const DISALLOWED_PATHS = new Set([
  "/",
  "/c",
  "~",
  "c:",
  "$home",
  "$userprofile",
  "$env:userprofile",
  "%userprofile%",
  "/i",
]);

function normalizePath(token) {
  let path = token.toLowerCase();
  if (path.endsWith(")")) path = path.slice(0, -1);
  if (path.length > 1) path = path.replace(/[\\/]+$/, "");
  return path;
}

function commandHasDiskWideFind(command) {
  FIND_INVOCATION_RE.lastIndex = 0;
  let match;
  while ((match = FIND_INVOCATION_RE.exec(command)) !== null) {
    const path = normalizePath(match[1]);
    if (DISALLOWED_PATHS.has(path) || HOME_USERS_RE.test(path)) return true;
  }
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

  if (!commandHasDiskWideFind(command)) return 0;

  process.stderr.write(
    "find rooted at the whole disk or home directory is refused — it has hung this machine for " +
      "hours before. Use the Glob tool for files, ~/.nuget/packages/<id>/<version>/lib/<tfm>/ for " +
      "package DLLs/XML docs, ilspycmd for decompiled sources, or a bounded path."
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
