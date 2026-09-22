#requires -Version 7
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

Push-Location $root
try {
    $reportDir = Join-Path 'artifacts' 'inspect'
    $report = Join-Path $reportDir 'report.xml'
    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null

    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }

    # --no-build requires NoofLedger.slnx to already be built (dotnet build NoofLedger.slnx).
    # -s points at a settings file this script owns. It is NOT NoofLedger.sln.DotSettings, because
    # Rider reads that name too, and raising these inspections to ERROR there painted every test
    # project red over findings requirement 1 exempts. The severity floor is the gate's business.
    dotnet jb inspectcode NoofLedger.slnx -o="$report" -f=Xml -e=WARNING -s="ops/inspect.DotSettings" --caches-home="$reportDir/caches" --no-build

    if (-not (Test-Path $report)) {
        throw "inspectcode produced no report at $report"
    }

    # InspectCode exits 0 whether or not it found anything - verified on this solution - so the
    # report is the gate. An <Issue> carries no Severity of its own; severity lives on the
    # <IssueType> it names through TypeId, so the two have to be joined.
    [xml]$xml = Get-Content $report
    $severityOf = @{}
    foreach ($type in $xml.Report.IssueTypes.IssueType) { $severityOf[$type.Id] = $type.Severity }

    $all = @($xml.SelectNodes('//Issue') | Where-Object { $severityOf[$_.TypeId] -eq 'ERROR' })

    # Requirement 1 of Phase 1C exempts tests: "Исключение - тесты. Для них можно делать internal
    # или private protected." The sweep still inspects them - their count is reported below, and it
    # is worth a look now and then - but gating on them would mean gating on 67 ClassCanBeSealed
    # findings against xUnit fixtures, and a gate that can never pass guards nothing.
    $errors = @($all | Where-Object { $_.File -like 'src\*' })
    $inTests = $all.Count - $errors.Count

    foreach ($issue in $errors) {
        Write-Host "$($issue.File):$($issue.Line) $($issue.TypeId) $($issue.Message)"
    }

    if ($inTests -gt 0) {
        Write-Host "$inTests further finding(s) under tests\, not gated. See $report."
    }

    if ($errors.Count -gt 0) {
        throw "$($errors.Count) ERROR-severity inspection finding(s) under src\. See $report."
    }

    Write-Host "No ERROR-severity findings under src\."
}
finally { Pop-Location }
