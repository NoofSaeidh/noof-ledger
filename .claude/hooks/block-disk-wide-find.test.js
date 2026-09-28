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
  ["git-bash /c mount, after ;", "ls; find /c"],
  ["git-bash /c mount, trailing slash", "find /c/"],
  ["windows drive root, backslash, after &&", "cd C:\\repos && find C:\\"],
  ["windows drive root, forward slash", "find C:/"],
  ["home, tilde, after ||", "echo done || find ~"],
  ["home, tilde with trailing slash", "find ~/"],
  ["home, $HOME, after a pipe", "echo x | find $HOME"],
  ["home, $USERPROFILE", "find $USERPROFILE"],
  ["home, $env:USERPROFILE, after (", "(find $env:USERPROFILE)"],
  ["home, %USERPROFILE%, after $(", "echo $(find %USERPROFILE%)"],
  ["home, /c/Users/<name> with no deeper path", "find /c/Users/noofs"],
  ["options before the path", "find -L /"],
  ["windows-style find /c piped in bash", 'tasklist | find /c "x"'],
  ["windows-style find /i piped in bash", 'something | find /i "x"'],
  ["after a bare newline (heredoc/multi-line command)", "echo ok\nfind /"],
  ["after a CRLF line ending", "echo ok\r\nfind /"],
  ["root in double quotes", 'find "/" -name x'],
  ["root in single quotes", "find '/' -name x"],
  ["$HOME in double quotes", 'find "$HOME" -name x'],
  ["${HOME} braced", "find ${HOME} -name x"],
  ["tilde in double quotes", 'find "~"'],
  ["home, forward-slash windows path in quotes", 'find "C:/Users/noofs" -name x'],
  ["home, backslash windows path", "find C:\\Users\\noofs -name x"],
  ["home, backslash windows path with trailing backslash", "find C:\\Users\\noofs\\"],
  ["the Users directory itself", "find /c/Users -name x"],
  ["windows drive root, bare backslash", "find C:\\ -name x"],
  ["another git-bash drive mount", "find /d -name x"],
  ["another windows drive root, backslash", "find D:\\ -name x"],
  ["another windows drive root, forward slash", "find e:/"],
  ["-maxdepth N before the path", "find -maxdepth 2 / -name x"],
  ["-mindepth N before the path", "find -mindepth 1 ~"],
  ["-L then -maxdepth N before the path", "find -L -maxdepth 3 $HOME"],
  ["root as a second starting point", "find src / -name x"],
  ["$env:USERPROFILE with trailing backslash, in quotes", 'find "$env:USERPROFILE\\" -name x'],
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
  ["quoted backups path under LOCALAPPDATA", 'find "$LOCALAPPDATA/NoofLedger/backups" -name "*.dump"'],
  ["-maxdepth N before a bounded path", "find -maxdepth 2 src -name x"],
  ["bounded windows path with backslashes", "find C:\\repos\\dev -name x"],
  ["bounded path under a user's home", 'find "C:/Users/noofs/.nuget/packages/foo" -name x'],
  ["a root-like name only as the -name pattern", 'find src -name "/"'],
  ["a longer top-level mount path", "find /tmp -name x"],
  ["several bounded starting points", "find src tests -name x"],
  ["an escaped-paren expression after a bounded path", "find src \\( -name a -o -name / \\)"],
  ["a negated expression after a bounded path", "find src ! -path /"],
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
