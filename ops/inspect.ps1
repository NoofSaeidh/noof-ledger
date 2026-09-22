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
    dotnet jb inspectcode NoofLedger.slnx -o="$report" -f=Xml -e=WARNING --caches-home="$reportDir/caches" --no-build

    if (-not (Test-Path $report)) {
        throw "inspectcode produced no report at $report"
    }

    # InspectCode exits 0 whether or not it found anything - verified on this solution - so the
    # report is the gate. An <Issue> carries no Severity of its own; severity lives on the
    # <IssueType> it names through TypeId, so the two have to be joined.
    [xml]$xml = Get-Content $report
    $severityOf = @{}
    foreach ($type in $xml.Report.IssueTypes.IssueType) { $severityOf[$type.Id] = $type.Severity }

    $errors = @($xml.SelectNodes('//Issue') | Where-Object { $severityOf[$_.TypeId] -eq 'ERROR' })

    foreach ($issue in $errors) {
        Write-Host "$($issue.File):$($issue.Line) $($issue.TypeId) $($issue.Message)"
    }

    if ($errors.Count -gt 0) {
        throw "$($errors.Count) ERROR-severity inspection finding(s). See $report."
    }

    Write-Host "No ERROR-severity findings."
}
finally { Pop-Location }
