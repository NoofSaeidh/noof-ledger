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
