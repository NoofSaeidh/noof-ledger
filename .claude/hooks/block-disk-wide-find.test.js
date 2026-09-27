const { test } = require("node:test");
const assert = require("node:assert/strict");
const { spawnSync } = require("node:child_process");
const path = require("node:path");

const HOOK_PATH = path.join(__dirname, "block-disk-wide-find.js");

function run(command, toolName = "Bash") {
  const payload = JSON.stringify({ tool_name: toolName, tool_input: { command } });
  return spawnSync(process.execPath, [HOOK_PATH], { input: payload, encoding: "utf8" });
}

function runRaw(input) {
  return spawnSync(process.execPath, [HOOK_PATH], { input, encoding: "utf8" });
}

const REFUSE_CASES = [
  ["whole disk root", "find /"],
  ["git-bash /c mount", "find /c"],
  ["git-bash /c mount, trailing slash", "find /c/"],
  ["windows drive root, backslash", "find C:\\"],
  ["windows drive root, forward slash", "find C:/"],
  ["home, tilde", "find ~"],
  ["home, tilde with slash", "find ~/"],
  ["home, $HOME", "find $HOME"],
  ["home, $USERPROFILE", "find $USERPROFILE"],
  ["home, %USERPROFILE%", "find %USERPROFILE%"],
  ["home, /c/Users/<name> with no deeper path", "find /c/Users/noofs"],
  ["windows home, backslash form", "find C:\\Users\\noofs"],
  ["options before the path", "find -L /"],
  ["after &&", "cd /tmp && find / -iname foo"],
  ["after ;", "ls; find ~ -name x"],
  ["after ||", "echo done || find / -name x"],
  ["windows-style find /c piped in bash", 'tasklist | find /c "x"'],
  ["windows-style find /i piped in bash", 'something | find /i "x"'],
  ["inside command substitution", "echo $(find / -name x)"],
  ["a second, unbounded starting path", "find src / -name '*.dll'"],
  ["nested sh -c shell invocation", "sh -c 'find / -name x'"],
  ["command substitution inside double quotes", 'echo "$(find / -name x)"'],
  ["bare command substitution with no predicate", "echo $(find /)"],
  ["command builtin prefix", "command find / -name x"],
  ["absolute path to the find executable", "/usr/bin/find / -name x"],
  ["a leading env-var assignment", "LC_ALL=C find / -name x"],
  ["a quoted executable name", '"find" / -name x'],
  ["a parenthesised subshell", "(find / -name x)"],
  ["a background job delimiter (single &)", "echo ready & find / -name x"],
  ["root written as /.", "find /. -name x"],
  ["root written as repeated slashes", "find /// -name x"],
  ["root reached via a trailing ..", "find /tmp/.. -name x"],
  ["$HOME written with a trailing /.", 'find "$HOME/." -name x'],
  ["a git-bash mount for another drive letter", "find /d -name x"],
  ["a POSIX-style home directory for another user", "find /home/alice -name x"],
  ["a shell variable assigned to root earlier in the command", 'root=/; find "$root" -name x'],
  ["a parameter expansion with a default naming $HOME", 'find "${HOME:-/tmp}" -name x'],
  ["PowerShell's own $env:USERPROFILE syntax", "find $env:USERPROFILE -name x"],
  ["cd to root, then a bare find in the same command", "cd / && find . -name x"],
  ["cd to home (no args), then find with no path", "cd ~ && find -name x"],
  ["a backslash-escaped slash", "find \\/ -name x"],
  ["ANSI-C quoting of the root path", "find $'/' -name x"],
  ["a backslash-newline line continuation splitting the word find", "fi\\\nnd / -name x"],
];

for (const [label, command] of REFUSE_CASES) {
  test(`refuses: ${label} (${command})`, () => {
    const result = run(command);
    assert.equal(result.status, 2, `expected exit 2, got ${result.status}; stderr: ${result.stderr}`);
    assert.ok(result.stderr.length > 0, "expected a stderr hint");
  });
}

const ALLOW_CASES = [
  ["current directory", "find ."],
  ["bounded repo-relative path", "find src -name x"],
  ["quoted env-derived bounded path", 'find "$LOCALAPPDATA/NoofLedger/manual-backups" -name "*.dump"'],
  ["nuget cache subdirectory under home", "find ~/.nuget/packages/foo -name x"],
  ["bounded git-bash mount path", "find /c/repos/dev/noof-ledger/tests -name x"],
  ["findstr is not find", "findstr foo bar"],
  ["git log --find-renames is not find", "git log --find-renames"],
  ["PowerShell Find-Module is not find", "Find-Module something"],
  ["find as literal text inside a quoted grep pattern", 'grep "find /"'],
  ["a predicate argument that happens to look like a root, e.g. -name '~'", "find -name '~'"],
  ["cd to a bounded directory, then a bare find", "cd /tmp && find . -name x"],
  ["a Windows path with backslash separators, not escapes", "find C:\\repos\\dev -name x"],
];

for (const [label, command] of ALLOW_CASES) {
  test(`allows: ${label} (${command})`, () => {
    const result = run(command);
    assert.equal(result.status, 0, `expected exit 0, got ${result.status}; stderr: ${result.stderr}`);
  });
}

test("applies the same rule to PowerShell tool calls", () => {
  const result = run("find /", "PowerShell");
  assert.equal(result.status, 2);
});

test("ignores tool calls other than Bash/PowerShell", () => {
  const result = run("find /", "Read");
  assert.equal(result.status, 0);
});

test("ignores malformed JSON on stdin", () => {
  const result = runRaw("not json");
  assert.equal(result.status, 0);
});

test("ignores a payload with no command field", () => {
  const result = runRaw(JSON.stringify({ tool_name: "Bash", tool_input: {} }));
  assert.equal(result.status, 0);
});
