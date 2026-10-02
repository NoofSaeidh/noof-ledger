#Requires -Version 7
# Exercises codex-broker.ps1's pure functions against fixtures - no processes are listed or stopped.
# Plain PowerShell rather than Pester, like pr-wait.tests.ps1. Run by `.\run.ps1 test fast` (and so by
# CI); exits 1 on any failed case.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'codex-broker.ps1')

$script:failures = [System.Collections.Generic.List[string]]::new()
$script:passed = 0

function Assert-Equal {
    param([string]$Case, $Expected, $Actual)
    if ($Expected -ceq $Actual) { $script:passed++ }
    else { $script:failures.Add("$Case - expected '$Expected', got '$Actual'") }
}

$plugin = 'C:\Users\u\.claude\plugins\cache\openai-codex\codex\1.0.6\scripts'

# ConvertFrom-BrokerCommandLine: the exact shape spawnBrokerProcess starts, quoted or not.
$plain = ConvertFrom-BrokerCommandLine "`"C:\Program Files\nodejs\node.exe`" $plugin\app-server-broker.mjs serve --endpoint pipe:\\.\pipe\cxc-pBCpna-codex-app-server --cwd C:/repos/dev/noof-ledger --pid-file C:\Users\u\AppData\Local\Temp\cxc-pBCpna\broker.pid"
Assert-Equal 'endpoint' 'pipe:\\.\pipe\cxc-pBCpna-codex-app-server' $plain.Endpoint
Assert-Equal 'cwd' 'C:/repos/dev/noof-ledger' $plain.Cwd
Assert-Equal 'pid file' 'C:\Users\u\AppData\Local\Temp\cxc-pBCpna\broker.pid' $plain.PidFile
Assert-Equal 'plugin scripts directory, so teardown uses the broker''s own version' $plugin $plain.ScriptsDirectory

$quoted = ConvertFrom-BrokerCommandLine "node `"C:\Users\A B\codex\scripts\app-server-broker.mjs`" serve --endpoint pipe:\\.\pipe\x --cwd `"C:\work\my repo`" --pid-file `"C:\Temp Dir\cxc-1\broker.pid`""
Assert-Equal 'quoted cwd keeps its space' 'C:\work\my repo' $quoted.Cwd
Assert-Equal 'quoted pid file keeps its space' 'C:\Temp Dir\cxc-1\broker.pid' $quoted.PidFile
Assert-Equal 'quoted script path' 'C:\Users\A B\codex\scripts' $quoted.ScriptsDirectory

Assert-Equal 'a companion review is not a broker' $null (ConvertFrom-BrokerCommandLine "node $plugin\codex-companion.mjs adversarial-review --wait --cwd C:\x")
Assert-Equal 'no command line (a process we may not read) is not a broker' $null (ConvertFrom-BrokerCommandLine $null)

# ConvertFrom-WorktreeList: `git worktree list --porcelain`; the first entry is the main checkout.
$porcelain = @(
    'worktree C:/repos/dev/noof-ledger', 'HEAD abc', 'branch refs/heads/master', '',
    'worktree C:/repos/dev/noof-ledger/.claude/worktrees/decision-edit-boundary', 'HEAD def', 'branch refs/heads/x', '',
    'worktree C:/repos/dev/noof-ledger/.claude/worktrees/gone', 'HEAD 123', 'detached', 'prunable gitdir file points to non-existent location', ''
)
$worktrees = @(ConvertFrom-WorktreeList $porcelain)
Assert-Equal 'every worktree listed' 3 $worktrees.Count
Assert-Equal 'main checkout first' 'C:/repos/dev/noof-ledger' $worktrees[0]

# Get-BrokerVerdict: only a broker under .claude\worktrees\ whose worktree is gone is an orphan.
$main = 'C:/repos/dev/noof-ledger'
$registered = @($main, 'C:/repos/dev/noof-ledger/.claude/worktrees/decision-edit-boundary', 'C:/repos/dev/noof-ledger/.claude/worktrees/feature')
$existing = { param($Path) $Path -notlike '*missing*' }
function Get-Verdict { param([string]$Cwd) Get-BrokerVerdict -BrokerCwd $Cwd -Worktrees $registered -PathExists $existing }

Assert-Equal 'main checkout is kept' 'main checkout' (Get-Verdict 'C:/repos/dev/noof-ledger')
Assert-Equal 'main checkout, other slashes and case' 'main checkout' (Get-Verdict 'c:\Repos\Dev\noof-ledger\')
Assert-Equal 'a folder of the main checkout that is not a worktree is kept' 'main checkout' (Get-Verdict 'C:\repos\dev\noof-ledger\src')
Assert-Equal 'registered worktree is kept' 'registered worktree' (Get-Verdict 'C:\repos\dev\noof-ledger\.claude\worktrees\decision-edit-boundary')
Assert-Equal 'a subfolder of a registered worktree is kept' 'registered worktree' (Get-Verdict 'C:\repos\dev\noof-ledger\.claude\worktrees\feature\src')
Assert-Equal 'removed worktree is an orphan' 'orphan' (Get-Verdict 'C:/repos/dev/noof-ledger/.claude/worktrees/p7-2a')
Assert-Equal 'a prefix of a registered name is not that worktree' 'orphan' (Get-Verdict 'C:/repos/dev/noof-ledger/.claude/worktrees/feature-old')
$registered += 'C:/repos/dev/noof-ledger/.claude/worktrees/missing-dir'
Assert-Equal 'registered but its folder is gone is an orphan' 'orphan' (Get-Verdict 'C:/repos/dev/noof-ledger/.claude/worktrees/missing-dir')
Assert-Equal 'another repo is never touched' 'elsewhere' (Get-Verdict 'C:/repos/dev/other-repo')
Assert-Equal 'a sibling whose name starts like this repo is elsewhere' 'elsewhere' (Get-Verdict 'C:/repos/dev/noof-ledger-old/.claude/worktrees/x')

# Get-DescendantProcessIds: the broker's child chain, by parent id - and not a process that merely
# reuses a dead parent's id (it started before that parent, so it cannot be its child).
$t0 = [datetime]'2026-10-02T10:00:00'
$processes = @(
    [PSCustomObject]@{ ProcessId = 100; ParentProcessId = 1; CreationDate = $t0 }
    [PSCustomObject]@{ ProcessId = 101; ParentProcessId = 100; CreationDate = $t0.AddSeconds(1) }
    [PSCustomObject]@{ ProcessId = 102; ParentProcessId = 101; CreationDate = $t0.AddSeconds(2) }
    [PSCustomObject]@{ ProcessId = 103; ParentProcessId = 102; CreationDate = $t0.AddSeconds(3) }
    [PSCustomObject]@{ ProcessId = 200; ParentProcessId = 100; CreationDate = $t0.AddSeconds(-60) }
    [PSCustomObject]@{ ProcessId = 300; ParentProcessId = 1; CreationDate = $t0.AddSeconds(5) }
)
Assert-Equal 'whole chain, nothing else' '101,102,103' ((Get-DescendantProcessIds -RootId 100 -Processes $processes | Sort-Object) -join ',')
Assert-Equal 'a leaf has no descendants' '' ((Get-DescendantProcessIds -RootId 103 -Processes $processes) -join ',')

if ($script:failures.Count -gt 0) {
    $script:failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "codex-broker tests: $($script:passed) passed, $($script:failures.Count) failed." -ForegroundColor Red
    exit 1
}
Write-Host "codex-broker tests: $($script:passed) passed."
exit 0
