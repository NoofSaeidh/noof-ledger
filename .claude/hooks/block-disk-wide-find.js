// PreToolUse hook: refuses an accidental `find` rooted at the whole disk or the home directory (see
// CLAUDE.md "Shell and search"). A `find / ...` has held 11.6M handles for hours on this machine and
// slowed every process spawn; the built-in Glob tool is instant for the same job.
//
// Threat model: an agent typing the wrong path by accident, not someone deliberately evading this
// hook. It splits the raw command text with a couple of regexes, not a real shell parser: it strips
// one pair of quotes and treats `\` as `/`, but does not track `cd`, expand shell variables, look
// inside nested shells (`sh -c`, subshells) or honour escaping. It only sees `find` at the start of a
// command, so `time find /`, `sudo find /`, `xargs find` and the like are deliberately not handled.

const FIND_START_RE = /(?:^|[;&|(\n\r])\s*find(?=\s)/g;
const TOKEN_RE = /[ \t]*("[^"\n]*"|'[^'\n]*'|[^\s;&|()<>]+)/y;
const OPTIONS_TAKING_A_VALUE = new Set(["-maxdepth", "-mindepth", "-D"]);
const DRIVE_ROOT_RE = /^(\/[a-z]|[a-z]:)$/;
const USERS_DIR_RE = /^(\/[a-z]|[a-z]:)\/users(\/[^/]+)?$/;
const HOME_PATHS = new Set([
  "~",
  "$home",
  "${home}",
  "$userprofile",
  "${userprofile}",
  "$env:userprofile",
  "${env:userprofile}",
  "%userprofile%",
]);

function unquote(token) {
  const first = token[0];
  if (token.length >= 2 && (first === '"' || first === "'") && token.endsWith(first)) {
    return token.slice(1, -1);
  }
  return token;
}

function normalizePath(token) {
  let path = unquote(token).toLowerCase().replace(/\\/g, "/");
  if (path.length > 1) path = path.replace(/\/+$/, "");
  return path;
}

function isDiskWide(token) {
  const path = normalizePath(token);
  return path === "/" || DRIVE_ROOT_RE.test(path) || USERS_DIR_RE.test(path) || HOME_PATHS.has(path);
}

function startingPoints(command, from) {
  const points = [];
  let skippingLeadingOptions = true;
  TOKEN_RE.lastIndex = from;
  let match;
  while ((match = TOKEN_RE.exec(command)) !== null) {
    const token = match[1];
    const isOption = token.startsWith("-");
    if (skippingLeadingOptions && isOption) {
      if (OPTIONS_TAKING_A_VALUE.has(token)) TOKEN_RE.exec(command);
      continue;
    }
    skippingLeadingOptions = false;
    // `\(` tokenizes as a lone `\`, since `(` ends a token.
    if (isOption || token === "!" || token === "\\") break;
    points.push(token);
  }
  return points;
}

function commandHasDiskWideFind(command) {
  FIND_START_RE.lastIndex = 0;
  let match;
  while ((match = FIND_START_RE.exec(command)) !== null) {
    if (startingPoints(command, FIND_START_RE.lastIndex).some(isDiskWide)) return true;
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
