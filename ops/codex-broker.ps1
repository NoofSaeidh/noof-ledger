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

Stopping asks the broker to shut down through the plugin's own code (ops\codex-broker-teardown.mjs),
ends whatever of its process chain outlived that - each process verified by its start time and killed
through a handle held since, so a reused process id is never hit - and then lets the plugin remove
the broker's files.

Arguments are parsed from $args by hand, not bound as named parameters: run.ps1 forwards its own
arguments as an array of strings, and array splatting binds "-Path" as a positional value rather
than as a parameter name.

.OUTPUTS
review: the companion's output and exit code. stop/sweep: one line per broker; exit 0.
#>

$ErrorActionPreference = 'Stop'

$Usage = 'Usage: codex-broker.ps1 review -Base <branch> -Focus <text> [-Cwd <dir>] | stop -Path <dir> | sweep [-Stop]'

function ConvertFrom-CodexBrokerArguments {
    param([string[]]$Arguments)
    $options = @{ Action = $null; Base = $null; Focus = $null; Cwd = (Get-Location).Path; Path = $null; Stop = $false }
    if (-not $Arguments) { return $options }
    $options.Action = $Arguments[0]
    for ($i = 1; $i -lt $Arguments.Count; $i++) {
        $name = $Arguments[$i]
        if ($name -eq '-Stop') { $options.Stop = $true; continue }
        if ($name -notin '-Base', '-Focus', '-Cwd', '-Path') { throw "Unknown argument '$name'. $Usage" }
        if ($i + 1 -ge $Arguments.Count) { throw "$name needs a value. $Usage" }
        $options[$name.Substring(1)] = $Arguments[++$i]
    }
    $options
}

function ConvertTo-ComparablePath {
    param([string]$Value)
    [IO.Path]::GetFullPath($Value).Replace('/', '\').TrimEnd('\').ToLowerInvariant()
}

function Test-PathAtOrUnder {
    param([string]$Candidate, [string]$Parent)
    $Candidate -eq $Parent -or $Candidate.StartsWith("$Parent\")
}

function Get-OptionValue {
    param([string]$CommandLine, [string]$Option)
    if ($CommandLine -notmatch "\s$Option\s+(?:`"(?<value>[^`"]+)`"|(?<value>[^`"\s]+))") { return $null }
    $Matches.value
}

function ConvertFrom-BrokerCommandLine {
    param([string]$CommandLine)
    $quotedScript = '"(?<script>(?:[^"]*[\\/])?app-server-broker\.mjs)"'
    $bareScript = '(?<script>(?:[^"\s]*[\\/])?app-server-broker\.mjs)'
    if ($CommandLine -notmatch "\s(?:$quotedScript|$bareScript)\s+serve\s") { return $null }
    $broker = [PSCustomObject]@{
        ScriptsDirectory = Split-Path $Matches.script -Parent
        Endpoint         = Get-OptionValue $CommandLine '--endpoint'
        Cwd              = Get-OptionValue $CommandLine '--cwd'
        PidFile          = Get-OptionValue $CommandLine '--pid-file'
    }
    if (-not ($broker.ScriptsDirectory -and $broker.Endpoint -and $broker.Cwd -and $broker.PidFile)) { return $null }
    $broker
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
    Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, CreationDate, Name, CommandLine
}

function Get-CodexBrokers {
    param([object[]]$Processes)
    foreach ($process in $Processes | Where-Object Name -eq 'node.exe') {
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

function Get-VerifiedProcess {
    param([object]$Snapshot)
    $process = Get-Process -Id $Snapshot.ProcessId -ErrorAction SilentlyContinue
    if (-not $process) { return $null }
    try {
        # Reading Handle opens and keeps a handle to this very process, so a later Kill() can only
        # reach it - never a process that takes over its id once it has exited.
        $null = $process.Handle
        if ([math]::Abs(($process.StartTime - $Snapshot.CreationDate).TotalSeconds) -lt 1) { return $process }
    }
    catch { }
    $null
}

function Invoke-BrokerTeardown {
    param([object]$Broker, [string]$Step)
    node --no-deprecation (Join-Path $PSScriptRoot 'codex-broker-teardown.mjs') $Step $Broker.ScriptsDirectory $Broker.Endpoint $Broker.PidFile $Broker.Cwd
    if ($LASTEXITCODE -ne 0) { Write-Host "The plugin's broker $Step failed for broker $($Broker.ProcessId)." -ForegroundColor Yellow }
}

function Stop-CodexBroker {
    param([object]$Broker, [object[]]$Processes)
    $byId = @{}
    foreach ($process in $Processes) { $byId[[int]$process.ProcessId] = $process }
    $chain = @(@($Broker.ProcessId) + @(Get-DescendantProcessIds -RootId $Broker.ProcessId -Processes $Processes) |
        ForEach-Object { Get-VerifiedProcess $byId[$_] } | Where-Object { $_ })

    Invoke-BrokerTeardown $Broker 'shutdown'
    foreach ($process in $chain) {
        try {
            if (-not $process.HasExited) { $process.Kill() }
            $null = $process.WaitForExit(5000)
        }
        catch { }
    }
    Invoke-BrokerTeardown $Broker 'cleanup'
    Write-Host "Stopped broker $($Broker.ProcessId) ($($Broker.Cwd))."
}

function Stop-EachCodexBroker {
    param([object[]]$Brokers, [object[]]$Processes)
    foreach ($broker in $Brokers) {
        try { Stop-CodexBroker $broker $Processes }
        catch { Write-Host "Could not stop broker $($broker.ProcessId) ($($broker.Cwd)): $($_.Exception.Message)" -ForegroundColor Yellow }
    }
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
    Stop-EachCodexBroker $brokers $processes
}

function Invoke-WithBrokerCleanup {
    param([scriptblock]$Review, [string]$Directory)
    try { & $Review }
    finally {
        # A cleanup failure must not replace the review's own result.
        try { Stop-CodexBrokerOf $Directory }
        catch { Write-Host "Could not stop the Codex broker of ${Directory}: $($_.Exception.Message). Run .\run.ps1 codex sweep." -ForegroundColor Yellow }
    }
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
    Invoke-WithBrokerCleanup -Directory $Directory -Review {
        node $companion adversarial-review --wait --cwd $Directory --base $BaseBranch $FocusText | Out-Host
        $LASTEXITCODE
    }
}

function Invoke-CodexBrokerSweep {
    param([switch]$StopOrphans)
    $worktrees = Get-RegisteredWorktrees
    $processes = @(Get-ProcessSnapshot)
    $brokers = @(Get-CodexBrokers $processes)
    if ($brokers.Count -eq 0) { Write-Host 'No Codex brokers are running.'; return }
    $orphans = foreach ($broker in $brokers) {
        $verdict = Get-BrokerVerdict -BrokerCwd $broker.Cwd -Worktrees $worktrees -PathExists { param($p) Test-Path $p }
        Write-Host ('{0,7}  {1,-19}  {2}' -f $broker.ProcessId, $verdict, $broker.Cwd)
        if ($verdict -eq 'orphan') { $broker }
    }
    if (-not $orphans) { return }
    if ($StopOrphans) { Stop-EachCodexBroker @($orphans) $processes }
    else { Write-Host 'Run with -Stop to stop the orphans.' }
}

if ($MyInvocation.InvocationName -ne '.') {
    $options = ConvertFrom-CodexBrokerArguments $args
    switch ($options.Action) {
        'review' { exit (Invoke-CodexReview -BaseBranch $options.Base -FocusText $options.Focus -Directory $options.Cwd) }
        'stop' {
            if (-not $options.Path) { throw 'Usage: stop -Path <dir>' }
            Stop-CodexBrokerOf $options.Path
        }
        'sweep' { Invoke-CodexBrokerSweep -StopOrphans:$options.Stop }
        default { throw $Usage }
    }
}
