#Requires -Version 7
# Exercises pr-wait.ps1's pure decision functions against fixtures - no `gh`, no network. Plain
# PowerShell rather than Pester, which is not installed alongside pwsh 7 here. Run by
# `.\run.ps1 test fast` (and so by CI); exits 1 on any failed case.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'pr-wait.ps1') 1

$script:failures = [System.Collections.Generic.List[string]]::new()
$script:passed = 0

function Assert-Equal {
    param([string]$Case, $Expected, $Actual)
    if ($Expected -ceq $Actual) { $script:passed++ }
    else { $script:failures.Add("$Case - expected '$Expected', got '$Actual'") }
}

function New-CheckRun {
    param([string]$Name, [string]$Status, [string]$Conclusion)
    [PSCustomObject]@{
        name         = $Name
        status       = $Status
        conclusion   = if ($Conclusion) { $Conclusion } else { $null }
        started_at   = '2026-09-28T10:00:00Z'
        completed_at = if ($Status -eq 'completed') { '2026-09-28T10:02:11Z' } else { $null }
        html_url     = 'https://github.com/o/r/actions/runs/123/job/456'
    }
}

$required = 'build-and-fast-tests'

# ConvertTo-ChecksSummary: the required check gates, and its absence is "not registered yet", not "no CI".
Assert-Equal 'no check runs, a workflow exists -> pending' 'pending' (ConvertTo-ChecksSummary -CheckRuns @() -RequiredCheck $required).Status
Assert-Equal 'no check runs, no workflow -> none' 'none' (ConvertTo-ChecksSummary -CheckRuns @() -RequiredCheck $null).Status
Assert-Equal 'only an unrelated check, required one missing -> pending' 'pending' `
    (ConvertTo-ChecksSummary -CheckRuns @(New-CheckRun 'other' 'completed' 'success') -RequiredCheck $required).Status
Assert-Equal 'required check queued -> pending' 'pending' `
    (ConvertTo-ChecksSummary -CheckRuns @(New-CheckRun $required 'queued' $null) -RequiredCheck $required).Status
Assert-Equal 'required check in progress -> pending' 'pending' `
    (ConvertTo-ChecksSummary -CheckRuns @(New-CheckRun $required 'in_progress' $null) -RequiredCheck $required).Status
Assert-Equal 'required check succeeded -> pass' 'pass' `
    (ConvertTo-ChecksSummary -CheckRuns @(New-CheckRun $required 'completed' 'success') -RequiredCheck $required).Status
Assert-Equal 'required check failed -> fail' 'fail' `
    (ConvertTo-ChecksSummary -CheckRuns @(New-CheckRun $required 'completed' 'failure') -RequiredCheck $required).Status
Assert-Equal 'required check cancelled -> fail' 'fail' `
    (ConvertTo-ChecksSummary -CheckRuns @(New-CheckRun $required 'completed' 'cancelled') -RequiredCheck $required).Status
Assert-Equal 'required check timed out -> fail' 'fail' `
    (ConvertTo-ChecksSummary -CheckRuns @(New-CheckRun $required 'completed' 'timed_out') -RequiredCheck $required).Status
Assert-Equal 'required passed, another still running -> pending' 'pending' `
    (ConvertTo-ChecksSummary -CheckRuns @((New-CheckRun $required 'completed' 'success'), (New-CheckRun 'other' 'in_progress' $null)) -RequiredCheck $required).Status
Assert-Equal 'required passed, another skipped -> pass' 'pass' `
    (ConvertTo-ChecksSummary -CheckRuns @((New-CheckRun $required 'completed' 'success'), (New-CheckRun 'other' 'completed' 'skipped')) -RequiredCheck $required).Status
$summary = ConvertTo-ChecksSummary -CheckRuns @(New-CheckRun $required 'completed' 'failure') -RequiredCheck $required
Assert-Equal 'summary keeps the check name for display' $required $summary.Checks[0].name
Assert-Equal 'summary shows the conclusion as the state' 'failure' $summary.Checks[0].state
Assert-Equal 'summary keeps the run link for the failed-run hint' 'https://github.com/o/r/actions/runs/123/job/456' $summary.Checks[0].link
Assert-Equal 'summary duration comes from the check run timestamps' '131s' (Format-Duration $summary.Checks[0].startedAt $summary.Checks[0].completedAt)
$running = ConvertTo-ChecksSummary -CheckRuns @(New-CheckRun $required 'in_progress' $null) -RequiredCheck $required
Assert-Equal 'a running check shows its status as the state' 'in_progress' $running.Checks[0].state

# Get-RequiredCheckName: the escape from requiring a check exists only when there is no workflow file at all.
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "pr-wait-tests-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    Assert-Equal 'no workflow directory -> no required check' $null (Get-RequiredCheckName -WorkflowDirectory (Join-Path $scratch 'missing'))
    Assert-Equal 'empty workflow directory -> no required check' $null (Get-RequiredCheckName -WorkflowDirectory $scratch)
    Set-Content -Path (Join-Path $scratch 'ci.yml') -Value 'name: CI'
    Assert-Equal 'a workflow file -> build-and-fast-tests required' $required (Get-RequiredCheckName -WorkflowDirectory $scratch)
} finally {
    Remove-Item -Recurse -Force $scratch
}
Assert-Equal 'this repo has a workflow -> build-and-fast-tests required' $required `
    (Get-RequiredCheckName -WorkflowDirectory (Join-Path $PSScriptRoot '..\.github\workflows'))

# Get-PrWaitOutcome: a push during the wait must never be reported as green (or red) for the old head.
function Get-Outcome {
    param([string]$Checks = 'pass', [bool]$CopilotPending = $false, [bool]$CopilotFound = $false,
        [bool]$QuotaHit = $false, [bool]$IsDraft = $false, [bool]$TimedOut = $false, [bool]$HeadMoved = $false,
        [bool]$Conflicting = $false)
    Get-PrWaitOutcome -ChecksStatus $Checks -CopilotPending $CopilotPending -CopilotFound $CopilotFound `
        -CopilotQuotaHit $QuotaHit -IsDraft $IsDraft -TimedOut $TimedOut -HeadMoved $HeadMoved -Conflicting $Conflicting
}
# GitHub runs no pull_request workflow for a PR that conflicts with its base, so CI would never arrive.
Assert-Equal 'CI pending on a conflicting PR -> conflict' 'conflict' (Get-Outcome -Checks 'pending' -Conflicting $true)
Assert-Equal 'CI passed on a PR that conflicts since -> done' 'done' (Get-Outcome -Conflicting $true)
Assert-Equal 'head moved on a conflicting PR -> keep polling' 'keep-polling' (Get-Outcome -Checks 'pending' -Conflicting $true -HeadMoved $true)
Assert-Equal 'CI passed, head unchanged -> done' 'done' (Get-Outcome)
Assert-Equal 'CI passed, but the head moved -> keep polling' 'keep-polling' (Get-Outcome -HeadMoved $true)
Assert-Equal 'CI failed, but the head moved -> keep polling' 'keep-polling' (Get-Outcome -Checks 'fail' -HeadMoved $true)
Assert-Equal 'head moved at the deadline -> timeout' 'timeout' (Get-Outcome -HeadMoved $true -TimedOut $true)
Assert-Equal 'CI failed -> fail' 'fail' (Get-Outcome -Checks 'fail')
Assert-Equal 'CI pending -> keep polling' 'keep-polling' (Get-Outcome -Checks 'pending')
Assert-Equal 'CI pending at the deadline -> timeout' 'timeout' (Get-Outcome -Checks 'pending' -TimedOut $true)
Assert-Equal 'no workflow, no checks -> done' 'done' (Get-Outcome -Checks 'none')
Assert-Equal 'Copilot pending on a draft, CI passed -> draft-no-review' 'draft-no-review' (Get-Outcome -CopilotPending $true -IsDraft $true)
Assert-Equal 'Copilot pending on a ready PR -> keep polling' 'keep-polling' (Get-Outcome -CopilotPending $true)
Assert-Equal 'Copilot reviewed -> done' 'done' (Get-Outcome -CopilotPending $true -CopilotFound $true)
Assert-Equal 'Copilot out of quota -> done' 'done' (Get-Outcome -CopilotPending $true -QuotaHit $true)

# Get-OutcomeExitCode: a draft Copilot will not review is not a timeout - re-running would not help.
Assert-Equal 'done -> 0' 0 (Get-OutcomeExitCode 'done')
Assert-Equal 'fail -> 1' 1 (Get-OutcomeExitCode 'fail')
Assert-Equal 'timeout -> 2' 2 (Get-OutcomeExitCode 'timeout')
Assert-Equal 'draft-no-review -> 4' 4 (Get-OutcomeExitCode 'draft-no-review')
Assert-Equal 'conflict -> 5' 5 (Get-OutcomeExitCode 'conflict')

# Get-PollDelaySeconds: never sleep past the deadline, so the last poll lands on it rather than a
# full interval after it.
Assert-Equal 'plenty of time left -> the full interval' 25 (Get-PollDelaySeconds -Remaining ([timespan]::FromMinutes(3)) -PollSeconds 25)
Assert-Equal 'less than an interval left -> only what remains' 7 (Get-PollDelaySeconds -Remaining ([timespan]::FromSeconds(6.2)) -PollSeconds 25)
Assert-Equal 'deadline already passed -> no sleep' 0 (Get-PollDelaySeconds -Remaining ([timespan]::FromSeconds(-4)) -PollSeconds 25)

if ($script:failures.Count -gt 0) {
    $script:failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "pr-wait tests: $($script:passed) passed, $($script:failures.Count) failed." -ForegroundColor Red
    exit 1
}
Write-Host "pr-wait tests: $($script:passed) passed."
exit 0
