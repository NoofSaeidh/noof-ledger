// PreToolUse hook: refuses a `find` rooted at the whole disk or the home directory (see CLAUDE.md
// "Shell and search"). A `find / ...` has held 11.6M handles for hours on this machine and slowed
// every process spawn; the built-in Glob tool is instant for the same job.
//
// Best-effort shell parsing, not a real shell grammar. It splits a compound command into segments at
// unquoted `;`, `&`, `&&`, `||`, `|`, `` ` `` and `$(` (the last one even inside double quotes, since
// bash still runs command substitution there), tracks `cd`/`Set-Location` targets and simple
// `NAME=value` assignments across those segments, recurses into `sh -c '...'`, `cmd /c ...`,
// `powershell -Command ...` and `(...)` subshells, and resolves `..`/`.`/repeated slashes and a stray
// trailing `)` left by unbalanced `$(...)` splitting before comparing a path against a disallowed
// root. It matches only exact-case `find` (or `find.exe`) as the command word, so it does not fire on
// `Find-Module` or similar PowerShell cmdlets.
//
// Known, accepted limits (verified by an adversarial Codex review, not fixed — see the PR):
// - It cannot resolve a variable it never saw assigned in the same command (e.g. one exported by an
//   earlier shell session), and it does not follow symlinks — both require actually running a shell.
// - A single-quoted `'~'` or `'$HOME'` is a literal directory name in Bash, not the home directory;
//   this hook still refuses it. Over-refusing an oddly-named literal directory is cheaper than
//   under-refusing a real home-directory scan, so this is accepted rather than fixed.
// - `find / -maxdepth 1` / `find / -prune` are refused on purpose: the point is to never start at
//   root at all, since Glob is instant for the same job regardless of depth.

const LEADING_FIND_OPTION_RE = /^-(?:[HLP]|D|O\d*)$/;
const FIND_COMMAND_WORD_RE = /^(?:.*[\\/])?find(?:\.exe)?$/;
const CD_COMMAND_RE = /^(?:cd|chdir|sl|set-location)(?:\s+(.+))?$/i;
const ASSIGNMENT_RE = /^([A-Za-z_][A-Za-z0-9_]*)=(\S*)$/;
const NESTED_SHELL_NAMES = new Set(["sh", "bash", "zsh", "dash", "ksh"]);
const DISALLOWED_EXACT = new Set(["/", "~", "%userprofile%"]);

function normalizePath(raw) {
  if (!raw) return raw;
  let s = raw.replace(/\)+$/, "");
  s = s.replace(/\\/g, "/");
  const driveMatch = s.match(/^([A-Za-z]):(\/.*)?$/);
  let rootPrefix = null;
  let rest = s;
  if (driveMatch) {
    rootPrefix = driveMatch[1].toLowerCase() + ":";
    rest = driveMatch[2] || "";
  } else if (s.startsWith("/")) {
    rootPrefix = "";
    rest = s;
  }
  const segments = [];
  for (const seg of rest.split("/")) {
    if (seg === "" || seg === ".") continue;
    if (seg === "..") {
      segments.pop();
      continue;
    }
    segments.push(seg);
  }
  if (rootPrefix === null) return segments.join("/");
  if (rootPrefix === "") return "/" + segments.join("/");
  return segments.length ? rootPrefix + "/" + segments.join("/") : rootPrefix;
}

function isDiskOrHomeRoot(rawPath) {
  if (!rawPath) return false;
  const norm = normalizePath(rawPath);
  if (!norm) return false;
  const lower = norm.toLowerCase();
  if (DISALLOWED_EXACT.has(lower)) return true;
  if (/^[a-z]:$/.test(lower)) return true;
  if (/^\/[a-z]$/.test(lower)) return true;
  if (/^\/[a-z]\/users\/[^/]+$/.test(lower)) return true;
  if (/^[a-z]:\/users\/[^/]+$/.test(lower)) return true;
  if (/^\/home\/[^/]+$/.test(lower)) return true;
  if (lower === "/i") return true;
  if (/^\$\{?home(:[-=?~][^}]*)?\}?$/.test(lower)) return true;
  if (/^\$\{?userprofile(:[-=?~][^}]*)?\}?$/.test(lower)) return true;
  if (/^\$env:userprofile$/.test(lower)) return true;
  return false;
}

function splitCommands(command) {
  const segments = [];
  let cur = "";
  let quote = null;
  let i = 0;
  while (i < command.length) {
    const ch = command[i];
    if (quote === "'") {
      cur += ch;
      if (ch === "'") quote = null;
      i++;
      continue;
    }
    if (quote === '"') {
      if (command.startsWith("$(", i)) {
        segments.push(cur);
        cur = "";
        quote = null;
        i += 2;
        continue;
      }
      cur += ch;
      if (ch === '"') quote = null;
      i++;
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
      cur += ch;
      i++;
      continue;
    }
    if (command.startsWith("&&", i) || command.startsWith("||", i)) {
      segments.push(cur);
      cur = "";
      i += 2;
      continue;
    }
    if (ch === ";" || ch === "|" || ch === "&" || ch === "\n" || ch === "`") {
      segments.push(cur);
      cur = "";
      i += 1;
      continue;
    }
    if (command.startsWith("$(", i)) {
      segments.push(cur);
      cur = "";
      i += 2;
      continue;
    }
    cur += ch;
    i++;
  }
  segments.push(cur);
  return segments;
}

function tokenize(segment) {
  const tokens = [];
  let cur = "";
  let quote = null;
  for (let i = 0; i < segment.length; i++) {
    const ch = segment[i];
    if (quote) {
      if (ch === quote) quote = null;
      else cur += ch;
      continue;
    }
    if (ch === "\\" && segment[i + 1] === "/") {
      cur += "/";
      i++;
      continue;
    }
    if (ch === "$" && segment[i + 1] === "'") {
      quote = "'";
      i++;
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
      continue;
    }
    if (/\s/.test(ch)) {
      if (cur.length) {
        tokens.push(cur);
        cur = "";
      }
      continue;
    }
    cur += ch;
  }
  if (cur.length) tokens.push(cur);
  return tokens;
}

function resolveToken(token, vars) {
  const m = token.match(/^\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?$/);
  if (m && Object.prototype.hasOwnProperty.call(vars, m[1])) return vars[m[1]];
  return token;
}

function stripLeadingNoise(tokens) {
  let idx = 0;
  while (idx < tokens.length && ASSIGNMENT_RE.test(tokens[idx])) idx++;
  while (idx < tokens.length && (tokens[idx] === "command" || tokens[idx] === "exec")) idx++;
  return tokens.slice(idx);
}

function extractNestedShellCommand(tokens) {
  if (tokens.length < 3) return null;
  const exe = tokens[0].toLowerCase().replace(/\.exe$/, "");
  if (NESTED_SHELL_NAMES.has(exe) && tokens[1] === "-c") return tokens[2];
  if (exe === "cmd" && tokens[1] === "/c") return tokens.slice(2).join(" ");
  if ((exe === "powershell" || exe === "pwsh") && /^-command$/i.test(tokens[1])) return tokens[2];
  return null;
}

function collectPathTokens(tokens, startIdx) {
  let idx = startIdx;
  while (idx < tokens.length && LEADING_FIND_OPTION_RE.test(tokens[idx])) idx++;
  const paths = [];
  while (idx < tokens.length && !tokens[idx].startsWith("-")) {
    paths.push(tokens[idx]);
    idx++;
  }
  return paths;
}

function evaluateSegment(segment, state) {
  const trimmed = segment.trim();
  if (trimmed === "") return false;

  if (trimmed.startsWith("(")) {
    const inner = trimmed.slice(1).replace(/\)\s*$/, "");
    return commandHasDiskWideFind(inner, state);
  }

  const cdMatch = trimmed.match(CD_COMMAND_RE);
  if (cdMatch) {
    const argToken = cdMatch[1] ? tokenize(cdMatch[1])[0] : undefined;
    const target = argToken === undefined ? "~" : resolveToken(argToken, state.vars);
    state.dangerousCwd = isDiskOrHomeRoot(target);
    return false;
  }

  const assignMatch = trimmed.match(ASSIGNMENT_RE);
  if (assignMatch) {
    state.vars[assignMatch[1]] = assignMatch[2];
    return false;
  }

  const wholeTokens = tokenize(trimmed);
  if (wholeTokens.length === 0) return false;

  const nested = extractNestedShellCommand(wholeTokens);
  if (nested !== null) return commandHasDiskWideFind(nested, state);

  const strippedTokens = stripLeadingNoise(wholeTokens);
  if (strippedTokens.length === 0 || !FIND_COMMAND_WORD_RE.test(strippedTokens[0])) return false;

  const pathTokens = collectPathTokens(strippedTokens, 1);
  let sawBoundedPath = false;
  for (const token of pathTokens) {
    const resolved = resolveToken(token, state.vars);
    if (isDiskOrHomeRoot(resolved)) return true;
    if (normalizePath(resolved) !== "") sawBoundedPath = true;
  }
  return !sawBoundedPath && state.dangerousCwd;
}

function commandHasDiskWideFind(command, parentState) {
  const state = parentState
    ? { dangerousCwd: parentState.dangerousCwd, vars: parentState.vars }
    : { dangerousCwd: false, vars: {} };
  for (const segment of splitCommands(command)) {
    if (evaluateSegment(segment, state)) return true;
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

  const spliced = command.replace(/\\\r?\n/g, "");
  if (!commandHasDiskWideFind(spliced)) return 0;

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
