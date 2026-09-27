#Requires -Version 7
<#
.SYNOPSIS
Blocks until a PR's CI checks have finished (and, only when Copilot is actually in play, its review
has landed for the current head), or a timeout passes - one call an agent can await instead of
polling `gh` itself.

.DESCRIPTION
Polls the GitHub API internally every -PollSeconds (this script polls, not the caller). CI is the
gate: it waits until every CI check run for the PR's current head SHA has completed (no checks at all
counts as done), or -TimeoutMinutes passes.

Copilot is optional, not a default gate - the operator's Copilot quota runs out, so most PRs never
get a Copilot review at all. This only waits on Copilot when Copilot is actually pending on the PR:
it shows up in the PR's requested reviewers, or -Copilot is passed to force it regardless. If Copilot
is not pending, its state is reported (a review it already posted earlier, and any comment threads
still open) but never blocks the result. If Copilot is pending and then posts a comment saying it
could not complete the review (quota/limit wording), that is reported and treated the same as
"nothing to wait for" - it will not arrive.

If Copilot is pending, CI has resolved (or there is none), and the PR is a draft with no Copilot
review or quota message yet, this returns early rather than spinning out the full timeout: Copilot
does not reliably review a draft on its own (this repo's own history - PR #8 - shows it only reviewed
a draft after an explicit review request), so waiting a fixed budget for it here would usually just
be waiting for something that was never coming.

Prints a compact summary an agent can read without re-querying: head SHA, each check's
name/conclusion/duration, Copilot's state for that head, and the Copilot review comments that are
still unresolved and have not been replied to (via GraphQL reviewThreads, so a resolved or already-
answered comment is left out) - listed regardless of whether Copilot is currently pending, since they
can be left over from an earlier round.

.PARAMETER Number
The pull request number.

.PARAMETER TimeoutMinutes
How long to poll before giving up. Default 9, so one call fits inside a 600000 ms tool timeout.

.PARAMETER PollSeconds
The internal poll interval. Default 25.

.PARAMETER Copilot
Wait for a Copilot review even if Copilot does not show up in the PR's requested reviewers.

.OUTPUTS
Exit code 0: CI green (or no checks at all), and Copilot is either not pending, has reviewed the
             current head, or has reported it cannot (quota/limit).
Exit code 1: a check failed or was cancelled - prints a `gh run view --log-failed` hint.
Exit code 2: timed out (or gave up early on a draft with Copilot still pending) - says what's pending.
Exit code 3: usage error (bad PR number, `gh` not authenticated, etc).
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [int]$Number,

    [int]$TimeoutMinutes = 9,

    [int]$PollSeconds = 25,

    [switch]$Copilot
)

$ErrorActionPreference = 'Stop'

$CopilotReviewLogin = 'copilot-pull-request-reviewer[bot]'
# Deliberately narrower than matching "quota" or "rate limit" as bare words - a real review's own
# prose can legitimately discuss rate limiting or quota tracking as the PR's subject matter, and that
# is not Copilot reporting that IT could not review. Every alternative below pairs the inability with
# "review" or with a verb like reached/hit/exceeded/out of, so ordinary review content cannot match.
$CopilotQuotaPattern = '(?i)unable to (review|complete (this|the) review)|could not (complete|finish) (the )?review|cannot review this|has (reached|hit|exceeded) (its|the) (usage )?(limit|quota)|out of (reviews|quota)|no (reviews|quota) (remaining|left)'

function Get-RepoNameWithOwner {
    $raw = gh repo view --json owner,name 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gh repo view failed: $raw" }
    $repo = $raw | ConvertFrom-Json
    return "$($repo.owner.login)/$($repo.name)"
}

function Get-PrSnapshot {
    param([int]$Number)
    $raw = gh pr view $Number --json 'number,headRefName,headRefOid,isDraft,state,url' 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gh pr view $Number failed: $raw" }
    return $raw | ConvertFrom-Json
}

function Get-PrChecksRaw {
    param([int]$Number)
    # `gh pr checks --json` exits 8 (not 0) while checks are still pending - still with valid JSON on
    # stdout - so this parses the output first and only falls back to the exit code as an error signal
    # when that fails, rather than gating on "exit 0" and misreading a pending run as a failure.
    $raw = gh pr checks $Number --json 'name,state,bucket,startedAt,completedAt,workflow,link' 2>&1
    if ($raw -match 'no checks reported') { return , @() }
    try {
        $parsed = $raw | ConvertFrom-Json
    } catch {
        throw "gh pr checks $Number failed: $raw"
    }
    # A comma before the array forces PowerShell to hand the caller the array itself as one pipeline
    # object - without it, an empty or single-item array unwraps to $null or a bare scalar once it
    # leaves the function, and run.ps1's Set-StrictMode -Version Latest then throws on the very next
    # `.Count` a caller does against what it assumed was still an array.
    return , @($parsed)
}

# Pure - takes gh pr checks' own JSON shape, decides the one thing the caller needs: is CI still
# running, and if not, did anything fail. Kept separate from Get-PrChecksRaw so it can be exercised
# against fixture arrays without a network call.
function ConvertTo-ChecksSummary {
    param([array]$Checks)
    if (-not $Checks -or $Checks.Count -eq 0) {
        return [PSCustomObject]@{ Status = 'none'; Checks = @() }
    }
    if (@($Checks | Where-Object { $_.bucket -eq 'pending' }).Count -gt 0) {
        return [PSCustomObject]@{ Status = 'pending'; Checks = $Checks }
    }
    if (@($Checks | Where-Object { $_.bucket -in @('fail', 'cancel') }).Count -gt 0) {
        return [PSCustomObject]@{ Status = 'fail'; Checks = $Checks }
    }
    return [PSCustomObject]@{ Status = 'pass'; Checks = $Checks }
}

function Get-PrReviewsRaw {
    param([string]$RepoNameWithOwner, [int]$Number)
    $raw = gh api "repos/$RepoNameWithOwner/pulls/$Number/reviews" --paginate 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gh api pulls/$Number/reviews failed: $raw" }
    return , @($raw | ConvertFrom-Json)
}

function Get-IssueCommentsRaw {
    param([string]$RepoNameWithOwner, [int]$Number)
    $raw = gh api "repos/$RepoNameWithOwner/issues/$Number/comments" --paginate 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gh api issues/$Number/comments failed: $raw" }
    return , @($raw | ConvertFrom-Json)
}

function Get-RequestedReviewersRaw {
    param([string]$RepoNameWithOwner, [int]$Number)
    $raw = gh api "repos/$RepoNameWithOwner/pulls/$Number/requested_reviewers" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gh api pulls/$Number/requested_reviewers failed: $raw" }
    return $raw | ConvertFrom-Json
}

function Get-IssueTimelineRaw {
    param([string]$RepoNameWithOwner, [int]$Number)
    $raw = gh api "repos/$RepoNameWithOwner/issues/$Number/timeline" --paginate 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gh api issues/$Number/timeline failed: $raw" }
    return , @($raw | ConvertFrom-Json)
}

# Pure. A PR can be requested from Copilot, quota out, then requested again later once quota frees up
# - a quota message from the FIRST request must not silently satisfy the SECOND. This is the anchor:
# only a quota message at or after this moment belongs to the request that is currently outstanding.
function Get-LatestCopilotRequestTime {
    param([array]$TimelineEvents)
    $requests = @($TimelineEvents | Where-Object {
        $_.event -eq 'review_requested' -and $_.requested_reviewer -and $_.requested_reviewer.login -match 'copilot'
    })
    if ($requests.Count -eq 0) { return $null }
    return [DateTimeOffset]::Parse(($requests[-1]).created_at)
}

# Pure. Copilot's requested-reviewer login is cleared once it submits a review, so this is true only
# while a request is genuinely outstanding right now - exactly "would waiting for Copilot ever pay off".
function Test-CopilotRequested {
    param($RequestedReviewers)
    if (-not $RequestedReviewers -or -not $RequestedReviewers.users) { return $false }
    return @($RequestedReviewers.users | Where-Object { $_.login -match 'copilot' }).Count -gt 0
}

# Pure. A PR can carry several Copilot reviews, one per push it reviewed - only the one against the
# CURRENT head answers "has Copilot reviewed what's actually here now."
function Find-CopilotReviewForHead {
    param([array]$Reviews, [string]$HeadSha)
    $forHead = @($Reviews | Where-Object { $_.user.login -eq $CopilotReviewLogin -and $_.commit_id -eq $HeadSha })
    if ($forHead.Count -eq 0) { return $null }
    return $forHead | Sort-Object submitted_at -Descending | Select-Object -First 1
}

# Pure. Best-effort: no confirmed wording sample was available for a real Copilot quota/limit message
# at the time this was written, so this matches on the vocabulary such a message is likely to use
# rather than one exact string. Checks both PR reviews and plain issue comments, since either could
# carry it.
function Find-CopilotQuotaMessage {
    param([array]$Reviews, [array]$IssueComments, $SinceUtc)
    $fromReviews = @($Reviews | Where-Object {
        $_.user.login -match 'copilot' -and $_.body -match $CopilotQuotaPattern -and
        (-not $SinceUtc -or [DateTimeOffset]::Parse($_.submitted_at) -ge $SinceUtc)
    })
    $fromComments = @($IssueComments | Where-Object {
        $_.user.login -match 'copilot' -and $_.body -match $CopilotQuotaPattern -and
        (-not $SinceUtc -or [DateTimeOffset]::Parse($_.created_at) -ge $SinceUtc)
    })
    # Both come back from `gh api` already in chronological order - the last element of whichever set
    # is non-empty is its most recent match. Reviews and comments carry different timestamp field
    # names (submitted_at vs. created_at), and touching a field an object lacks throws under
    # Set-StrictMode -Version Latest, so this picks the most recent within each set separately rather
    # than sorting the two kinds together.
    $hit = if ($fromReviews.Count -gt 0) { $fromReviews[-1] } elseif ($fromComments.Count -gt 0) { $fromComments[-1] } else { $null }
    if (-not $hit) { return $null }
    $body = $hit.body -replace '\s+', ' '
    if ($body.Length -gt 200) { $body = $body.Substring(0, 200) + '...' }
    return $body
}

function Get-ReviewThreadsRaw {
    param([string]$Owner, [string]$Repo, [int]$Number)
    $query = @'
query($owner: String!, $repo: String!, $number: Int!, $after: String) {
  repository(owner: $owner, name: $repo) {
    pullRequest(number: $number) {
      reviewThreads(first: 50, after: $after) {
        pageInfo { hasNextPage endCursor }
        nodes {
          isResolved
          comments(first: 20) {
            nodes { databaseId author { login } body path line originalLine }
          }
        }
      }
    }
  }
}
'@
    $nodes = @()
    $after = $null
    do {
        $args = @('api', 'graphql', '-f', "query=$query", '-f', "owner=$Owner", '-f', "repo=$Repo", '-F', "number=$Number")
        if ($after) { $args += @('-f', "after=$after") }
        $raw = gh @args 2>&1
        if ($LASTEXITCODE -ne 0) { throw "gh api graphql (reviewThreads) failed: $raw" }
        $page = ($raw | ConvertFrom-Json).data.repository.pullRequest.reviewThreads
        $nodes += $page.nodes
        $after = if ($page.pageInfo.hasNextPage) { $page.pageInfo.endCursor } else { $null }
    } while ($after)
    return , @($nodes)
}

# Pure. A thread counts as needing attention only when it is (a) unresolved and (b) nobody has
# replied yet - a single comment from the bot, and nothing after it.
function ConvertTo-UnrepliedUnresolvedComments {
    param([array]$Threads)
    $result = @()
    foreach ($thread in $Threads) {
        if ($thread.isResolved) { continue }
        $comments = @($thread.comments.nodes)
        if ($comments.Count -ne 1) { continue }
        $comment = $comments[0]
        if ($comment.author.login -notmatch 'copilot') { continue }
        $line = if ($comment.line) { $comment.line } else { $comment.originalLine }
        $body = $comment.body -replace '\s+', ' '
        if ($body.Length -gt 200) { $body = $body.Substring(0, 200) + '...' }
        $result += [PSCustomObject]@{
            Id   = $comment.databaseId
            Path = $comment.path
            Line = $line
            Body = $body
        }
    }
    return , @($result)
}

# Pure. The one decision the whole loop exists to make. Copilot only gates the result while it is
# actually pending ($CopilotPending) - and even then, a quota/limit message satisfies it exactly like
# a real review would, because neither will ever turn into more waiting paying off.
function Get-PrWaitOutcome {
    param(
        [string]$ChecksStatus,
        [bool]$CopilotPending,
        [bool]$CopilotFound,
        [bool]$CopilotQuotaHit,
        [bool]$IsDraft,
        [bool]$TimedOut
    )
    if ($ChecksStatus -eq 'fail') { return 'fail' }
    $copilotSatisfied = (-not $CopilotPending) -or $CopilotFound -or $CopilotQuotaHit
    if ($ChecksStatus -in @('none', 'pass') -and $copilotSatisfied) { return 'done' }
    if ($ChecksStatus -eq 'pending') { return $(if ($TimedOut) { 'timeout' } else { 'keep-polling' }) }
    if ($IsDraft) { return 'draft-no-review' }
    return $(if ($TimedOut) { 'timeout' } else { 'keep-polling' })
}

function Format-Duration {
    param($StartedAt, $CompletedAt)
    # `gh pr checks --json` reports a still-running check's completedAt as "0001-01-01T00:00:00" (Go's
    # zero time.Time, marshaled with no 'Z'/offset) rather than omitting it or using JSON null - not
    # falsy, so the guard above misses it, and parsing it as LOCAL time in a positive-UTC-offset zone
    # underflows past year 1 and throws. Reject the sentinel by prefix, and fall back to '-' on any
    # other unparseable value rather than letting the whole command die on a cosmetic duration.
    if (-not $StartedAt -or -not $CompletedAt) { return '-' }
    if ($StartedAt -like '0001-01-01*' -or $CompletedAt -like '0001-01-01*') { return '-' }
    try {
        $span = [DateTimeOffset]::Parse($CompletedAt) - [DateTimeOffset]::Parse($StartedAt)
        return "$([int]$span.TotalSeconds)s"
    } catch {
        return '-'
    }
}

function Write-Summary {
    param($Pr, $ChecksSummary, $CopilotReview, $CopilotPending, $CopilotQuotaMessage, $UnrepliedComments)
    $head = $Pr.headRefOid.Substring(0, 7)
    $draftTag = if ($Pr.isDraft) { ' [DRAFT]' } else { '' }
    Write-Host "PR #$($Pr.number) ($($Pr.headRefName)) head $head$draftTag"
    Write-Host ''

    switch ($ChecksSummary.Status) {
        'none' { Write-Host 'CI: none' }
        default {
            Write-Host "CI (head $head):"
            foreach ($check in $ChecksSummary.Checks) {
                $duration = Format-Duration $check.startedAt $check.completedAt
                $name = if ($check.workflow) { "$($check.workflow) / $($check.name)" } else { $check.name }
                Write-Host ("  {0,-45} {1,-10} {2}" -f $name, $check.state, $duration)
            }
        }
    }
    Write-Host ''

    if ($CopilotReview) {
        Write-Host "Copilot review: posted for head $head ($($CopilotReview.state))"
    } elseif ($CopilotQuotaMessage) {
        Write-Host "Copilot: reported it could not review - not waiting on it. `"$CopilotQuotaMessage`""
    } elseif ($CopilotPending) {
        Write-Host "Copilot review: requested, not yet posted for head $head"
        if ($Pr.isDraft) {
            Write-Host '  note: PR is draft - Copilot''s reviewer does not reliably review a draft on its own' -ForegroundColor Yellow
            Write-Host '  (PR #8 in this repo only got reviewed as a draft after an explicit review request).' -ForegroundColor Yellow
            Write-Host '  Mark it ready, or request Copilot explicitly, rather than waiting out the full timeout.' -ForegroundColor Yellow
        }
    } else {
        Write-Host 'Copilot: not requested - not waiting on it'
    }
    Write-Host ''

    if ($UnrepliedComments.Count -gt 0) {
        Write-Host "Unresolved Copilot comments with no reply yet ($($UnrepliedComments.Count)):"
        foreach ($comment in $UnrepliedComments) {
            Write-Host "  $($comment.Id)  $($comment.Path):$($comment.Line)  $($comment.Body)"
        }
    } else {
        Write-Host 'Unresolved Copilot comments with no reply yet: none'
    }
    Write-Host ''
}

function Write-FailedRunHint {
    param($ChecksSummary)
    $failed = @($ChecksSummary.Checks | Where-Object { $_.bucket -in @('fail', 'cancel') })
    foreach ($check in $failed) {
        if ($check.link -match '/actions/runs/(\d+)') {
            Write-Host "  gh run view $($Matches[1]) --log-failed   ($($check.name))"
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $repoNameWithOwner = Get-RepoNameWithOwner
        $owner, $repo = $repoNameWithOwner.Split('/', 2)
        $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
        $outcome = 'keep-polling'
        $pr = $null
        $checksSummary = $null
        $copilotReview = $null
        $copilotPending = $false
        $copilotQuotaMessage = $null

        while ($true) {
            $pr = Get-PrSnapshot -Number $Number
            $checksSummary = ConvertTo-ChecksSummary (Get-PrChecksRaw -Number $Number)
            $reviews = Get-PrReviewsRaw -RepoNameWithOwner $repoNameWithOwner -Number $Number
            $copilotReview = Find-CopilotReviewForHead -Reviews $reviews -HeadSha $pr.headRefOid
            $requestedReviewers = Get-RequestedReviewersRaw -RepoNameWithOwner $repoNameWithOwner -Number $Number
            $copilotPending = $Copilot -or (Test-CopilotRequested -RequestedReviewers $requestedReviewers)
            $copilotQuotaMessage = $null
            if ($copilotPending -and -not $copilotReview) {
                $issueComments = Get-IssueCommentsRaw -RepoNameWithOwner $repoNameWithOwner -Number $Number
                $requestTime = Get-LatestCopilotRequestTime -TimelineEvents (Get-IssueTimelineRaw -RepoNameWithOwner $repoNameWithOwner -Number $Number)
                $copilotQuotaMessage = Find-CopilotQuotaMessage -Reviews $reviews -IssueComments $issueComments -SinceUtc $requestTime
            }

            $timedOut = (Get-Date) -ge $deadline
            $outcome = Get-PrWaitOutcome -ChecksStatus $checksSummary.Status -CopilotPending $copilotPending `
                -CopilotFound ([bool]$copilotReview) -CopilotQuotaHit ([bool]$copilotQuotaMessage) `
                -IsDraft $pr.isDraft -TimedOut $timedOut
            if ($outcome -ne 'keep-polling') { break }
            Start-Sleep -Seconds $PollSeconds
        }

        $threads = Get-ReviewThreadsRaw -Owner $owner -Repo $repo -Number $Number
        $unreplied = ConvertTo-UnrepliedUnresolvedComments -Threads $threads

        Write-Summary -Pr $pr -ChecksSummary $checksSummary -CopilotReview $copilotReview `
            -CopilotPending $copilotPending -CopilotQuotaMessage $copilotQuotaMessage -UnrepliedComments $unreplied

        switch ($outcome) {
            'done' { Write-Host 'Result: ready (CI green or none; Copilot not pending, reviewed, or reported it cannot).'; exit 0 }
            'fail' {
                Write-Host 'Result: a CI check failed.' -ForegroundColor Red
                Write-FailedRunHint -ChecksSummary $checksSummary
                exit 1
            }
            'draft-no-review' {
                Write-Host 'Result: pending - CI is done, but Copilot (pending on this PR) has not reviewed this draft''s current head.' -ForegroundColor Yellow
                exit 2
            }
            'timeout' {
                $pending = @()
                if ($checksSummary.Status -eq 'pending') { $pending += 'CI still running' }
                if ($copilotPending -and -not $copilotReview -and -not $copilotQuotaMessage) { $pending += 'Copilot review not posted for this head' }
                Write-Host "Result: timed out after $TimeoutMinutes min. Still pending: $($pending -join '; ')." -ForegroundColor Yellow
                exit 2
            }
            default { throw "Unreachable outcome '$outcome'." }
        }
    } catch {
        Write-Host $_.Exception.Message -ForegroundColor Red
        exit 3
    }
}
