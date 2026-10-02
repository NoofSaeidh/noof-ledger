#Requires -Version 7
<#
.SYNOPSIS
Runs a Codex adversarial review and stops the Codex plugin brokers it leaves behind.

.DESCRIPTION
The Codex plugin's companion script starts one detached broker per distinct --cwd
(app-server-broker.mjs, with a codex app-server process chain under it), all running in that
directory. The plugin's SessionEnd hook stops only the broker of the session's own directory, so a
review run with --cwd <worktree> leaves a broker that holds the worktree open until reboot, and
removing the worktree then fails with "being used by another process".

  review -Base <branch> -Focus <text> [-Cwd <dir>]
      Runs `codex-companion.mjs adversarial-review --wait --cwd <dir> --base <branch> <focus>` and
      then always stops that directory's broker, even when the review fails. -Cwd defaults to the
      current directory. The main checkout's broker is left alone: it belongs to the Claude Code
      session started there, whose SessionEnd hook stops it.
  stop -Path <dir>
      Stops the broker of one directory - run it before removing a worktree. Refuses the main
      checkout.
  sweep [-Stop]
      Lists every broker with a verdict. An orphan is one whose directory is under this repository's
      .claude\worktrees\ but is not a registered worktree (git worktree list), or no longer exists.
      -Stop stops the orphans. The main checkout and registered worktrees are never touched - other
      sessions run their reviews there - and neither is anything outside this repository.

Stopping uses the plugin's own teardown (ops\codex-broker-teardown.mjs), then ends any process of the
broker's chain that outlived it.

.OUTPUTS
review: the companion's output and exit code. stop/sweep: one line per broker; exit 0.
#>
param(
    [Parameter(Position = 0)][string]$Action,
    [string]$Base,
    [string]$Focus,
    [string]$Cwd = (Get-Location).Path,
    [string]$Path,
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'

function ConvertTo-ComparablePath {
    param([string]$Value)
    [IO.Path]::GetFullPath($Value).Replace('/', '\').TrimEnd('\').ToLowerInvariant()
}

function Test-PathAtOrUnder {
    param([string]$Candidate, [string]$Parent)
    $Candidate -eq $Parent -or $Candidate.StartsWith("$Parent\")
}

function Get-QuotedOrBareValue {
    param([string]$CommandLine, [string]$Before, [string]$ValuePattern = '[^"\s]+', [string]$After = '')
    if ($CommandLine -notmatch "$Before(?:`"(?<value>[^`"]+)`"|(?<value>$ValuePattern))$After") { return $null }
    $Matches.value
}

function ConvertFrom-BrokerCommandLine {
    param([string]$CommandLine)
    $scriptPath = Get-QuotedOrBareValue $CommandLine '(?<=\s)' '[^"\s]*app-server-broker\.mjs' '\s+serve\b'
    if (-not $scriptPath) { return $null }
    [PSCustomObject]@{
        ScriptsDirectory = Split-Path $scriptPath -Parent
        Endpoint         = Get-QuotedOrBareValue $CommandLine '--endpoint\s+'
        Cwd              = Get-QuotedOrBareValue $CommandLine '--cwd\s+'
        PidFile          = Get-QuotedOrBareValue $CommandLine '--pid-file\s+'
    }
}

function ConvertFrom-WorktreeList {
    param([string[]]$Porcelain)
    $Porcelain | Where-Object { $_ -like 'worktree *' } | ForEach-Object { $_.Substring('worktree '.Length) }
}

function Get-BrokerVerdict {
    param([string]$BrokerCwd, [string[]]$Worktrees, [scriptblock]$PathExists)
    $cwd = ConvertTo-ComparablePath $BrokerCwd
    $main = ConvertTo-ComparablePath $Worktrees[0]
    if (Test-PathAtOrUnder $cwd "$main\.claude\worktrees") {
        $owner = $Worktrees | Select-Object -Skip 1 | Where-Object { Test-PathAtOrUnder $cwd (ConvertTo-ComparablePath $_) }
        if ($owner -and (& $PathExists $BrokerCwd)) { return 'registered worktree' }
        return 'orphan'
    }
    if (Test-PathAtOrUnder $cwd $main) { return 'main checkout' }
    'elsewhere'
}

function Get-DescendantProcessIds {
    param([int]$RootId, [object[]]$Processes)
    $byId = @{}
    foreach ($process in $Processes) { $byId[[int]$process.ProcessId] = $process }
    $pending = [System.Collections.Generic.Queue[int]]::new([int[]]@($RootId))
    while ($pending.Count -gt 0) {
        $parent = $byId[$pending.Dequeue()]
        if ($null -eq $parent) { continue }
        # Windows reuses process ids, and an orphan keeps its dead parent's id as ParentProcessId:
        # only a process started after the parent can be its child.
        $children = $Processes | Where-Object { $_.ParentProcessId -eq $parent.ProcessId -and $_.CreationDate -ge $parent.CreationDate }
        foreach ($child in $children) {
            [int]$child.ProcessId
            $pending.Enqueue([int]$child.ProcessId)
        }
    }
}

function Get-ProcessSnapshot {
    Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, CreationDate, CommandLine
}

function Get-CodexBrokers {
    param([object[]]$Processes)
    foreach ($process in $Processes) {
        $broker = ConvertFrom-BrokerCommandLine $process.CommandLine
        if ($broker) { $broker | Add-Member -NotePropertyName ProcessId -NotePropertyValue ([int]$process.ProcessId) -PassThru }
    }
}

function Get-RegisteredWorktrees {
    $porcelain = git -C $PSScriptRoot worktree list --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'git worktree list failed.' }
    @(ConvertFrom-WorktreeList $porcelain)
}

function Get-WorkspaceRoot {
    param([string]$Directory)
    # The plugin keys a broker by the directory's git top level, not the directory itself.
    if (Test-Path $Directory -PathType Container) {
        $topLevel = git -C $Directory rev-parse --show-toplevel 2>$null
        if ($LASTEXITCODE -eq 0 -and $topLevel) { return $topLevel }
    }
    $Directory
}

function Stop-CodexBroker {
    param([object]$Broker, [object[]]$Processes)
    $chain = @(Get-DescendantProcessIds -RootId $Broker.ProcessId -Processes $Processes)
    node --no-deprecation (Join-Path $PSScriptRoot 'codex-broker-teardown.mjs') $Broker.ScriptsDirectory $Broker.Endpoint $Broker.ProcessId $Broker.PidFile $Broker.Cwd
    if ($LASTEXITCODE -ne 0) { Write-Host "The plugin's teardown failed for broker $($Broker.ProcessId); ending its processes directly." -ForegroundColor Yellow }

    $byId = @{}
    foreach ($process in $Processes) { $byId[[int]$process.ProcessId] = $process }
    foreach ($id in @($Broker.ProcessId) + $chain) {
        $survivor = Get-Process -Id $id -ErrorAction SilentlyContinue
        # Same id and same start time, so never a new process that took a dead one's id.
        if ($survivor -and [math]::Abs(($survivor.StartTime - $byId[$id].CreationDate).TotalSeconds) -lt 1) {
            Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host "Stopped broker $($Broker.ProcessId) ($($Broker.Cwd))."
}

function Stop-CodexBrokerOf {
    param([string]$Directory)
    $root = ConvertTo-ComparablePath (Get-WorkspaceRoot $Directory)
    if ($root -eq (ConvertTo-ComparablePath (Get-RegisteredWorktrees)[0])) {
        Write-Host 'Leaving the main checkout''s Codex broker alone: its Claude Code session stops it.'
        return
    }
    $processes = @(Get-ProcessSnapshot)
    $brokers = @(Get-CodexBrokers $processes | Where-Object { (ConvertTo-ComparablePath $_.Cwd) -eq $root })
    if ($brokers.Count -eq 0) { Write-Host "No Codex broker runs in $Directory." }
    foreach ($broker in $brokers) { Stop-CodexBroker $broker $processes }
}

function Get-CodexCompanionPath {
    $registry = Join-Path $HOME '.claude\plugins\installed_plugins.json'
    $installPath = if (Test-Path $registry) {
        $entry = (Get-Content $registry -Raw | ConvertFrom-Json).plugins.'codex@openai-codex'
        if ($entry) { @($entry)[0].installPath }
    }
    if (-not $installPath) { throw "The Codex plugin (codex@openai-codex) is not installed - $registry names no install path." }
    Join-Path $installPath 'scripts\codex-companion.mjs'
}

function Invoke-CodexReview {
    param([string]$BaseBranch, [string]$FocusText, [string]$Directory)
    if (-not $BaseBranch -or -not $FocusText) { throw 'Usage: review -Base <branch> -Focus <text> [-Cwd <dir>]' }
    $companion = Get-CodexCompanionPath
    try {
        node $companion adversarial-review --wait --cwd $Directory --base $BaseBranch $FocusText | Out-Host
        $reviewExitCode = $LASTEXITCODE
    }
    finally { Stop-CodexBrokerOf $Directory }
    $reviewExitCode
}

function Invoke-CodexBrokerSweep {
    param([switch]$StopOrphans)
    $worktrees = Get-RegisteredWorktrees
    $processes = @(Get-ProcessSnapshot)
    $brokers = @(Get-CodexBrokers $processes)
    if ($brokers.Count -eq 0) { Write-Host 'No Codex brokers are running.'; return }
    $orphans = 0
    foreach ($broker in $brokers) {
        $verdict = Get-BrokerVerdict -BrokerCwd $broker.Cwd -Worktrees $worktrees -PathExists { param($p) Test-Path $p }
        Write-Host ('{0,7}  {1,-19}  {2}' -f $broker.ProcessId, $verdict, $broker.Cwd)
        if ($verdict -ne 'orphan') { continue }
        $orphans++
        if ($StopOrphans) { Stop-CodexBroker $broker $processes }
    }
    if ($orphans -gt 0 -and -not $StopOrphans) { Write-Host 'Run with -Stop to stop the orphans.' }
}

if ($MyInvocation.InvocationName -ne '.') {
    switch ($Action) {
        'review' { exit (Invoke-CodexReview -BaseBranch $Base -FocusText $Focus -Directory $Cwd) }
        'stop' {
            if (-not $Path) { throw 'Usage: stop -Path <dir>' }
            Stop-CodexBrokerOf $Path
        }
        'sweep' { Invoke-CodexBrokerSweep -StopOrphans:$Stop }
        default { throw 'Usage: codex-broker.ps1 review -Base <branch> -Focus <text> [-Cwd <dir>] | stop -Path <dir> | sweep [-Stop]' }
    }
}
