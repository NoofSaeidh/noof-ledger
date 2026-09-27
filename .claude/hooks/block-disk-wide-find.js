// PreToolUse hook: refuses a `find` rooted at the whole disk or the home directory (see CLAUDE.md
// "Shell and search"). A `find / ...` has held 11.6M handles for hours on this machine and slowed
// every process spawn; the built-in Glob tool is instant for the same job.
//
// Best-effort shell parsing, not a real shell grammar: it walks quote state to split a compound
// command into segments at unquoted `;`, `&&`, `||`, `|`, `` ` ``  and `$(`, then checks whether each
// segment invokes lowercase `find` against a disallowed root. Known limits: it does not see inside a
// nested shell invocation (`sh -c 'find /'`), and a predicate written before the path in already-
// malformed find usage (`find -iname foo /`) can be misread. It matches only exact-case `find` so it
// does not fire on `Find-Module` or similar PowerShell cmdlets.

const OPTION_TOKEN_RE = /^-[A-Za-z]+\d*$/;

const DISALLOWED_EXACT = new Set(["/", "/c", "~", "$home", "${home}", "$userprofile", "${userprofile}", "%userprofile%"]);

function splitCommands(command) {
  const segments = [];
  let cur = "";
  let quote = null;
  let i = 0;
  while (i < command.length) {
    const ch = command[i];
    if (quote) {
      cur += ch;
      if (ch === quote) quote = null;
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
    if (ch === ";" || ch === "|" || ch === "\n" || ch === "`") {
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

function isDiskOrHomeRoot(rawPath) {
  if (!rawPath) return false;
  let norm = rawPath.replace(/\\/g, "/");
  if (norm.length > 1) norm = norm.replace(/\/+$/, "");
  const lower = norm.toLowerCase();
  if (DISALLOWED_EXACT.has(lower)) return true;
  if (/^[a-z]:$/.test(lower)) return true;
  if (/^\/c\/users\/[^/]+$/.test(lower)) return true;
  if (/^[a-z]:\/users\/[^/]+$/.test(lower)) return true;
  if (lower === "/i") return true;
  return false;
}

function findsDiskWideRoot(segment) {
  const trimmed = segment.trimStart();
  if (!/^find(\s|$)/.test(trimmed)) return false;
  const tokens = tokenize(trimmed.slice(4));
  let idx = 0;
  while (idx < tokens.length && OPTION_TOKEN_RE.test(tokens[idx])) idx++;
  return isDiskOrHomeRoot(tokens[idx]);
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

  if (!splitCommands(command).some(findsDiskWideRoot)) return 0;

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
