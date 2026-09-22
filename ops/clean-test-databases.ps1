#requires -Version 7
<#
.SYNOPSIS
Drops throwaway test databases left behind by an interrupted or failed test run.

.DESCRIPTION
The test suites create one database per test - noof_test_* from Noof.Ledger.Persistence.Tests and
noof_e2e_* from Noof.Ledger.E2E.Tests - and drop them when the run finishes. A run killed partway
through, or a drop that times out on a Postgres checkpoint, leaves them behind. They are harmless
but unbounded: 166 had accumulated before this script existed.

The operator's real ledger (noof_ledger) and the template the tests clone
(noof_ledger_test_template) are refused by name, not merely excluded by the pattern. A pattern is a
filter; a refusal is a guarantee, and this script's whole job is dropping databases.

.PARAMETER WhatIf
Lists what would be dropped and drops nothing.
#>
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'

$Protected = @('noof_ledger', 'noof_ledger_test_template', 'postgres', 'template0', 'template1')

function Get-AdminConnectionString {
    if ($env:NOOF_TEST_PG) { return $env:NOOF_TEST_PG }

    $credentialFile = Join-Path $env:LOCALAPPDATA 'NoofLedger\db.connection'
    if (Test-Path $credentialFile) {
        $fromFile = (Get-Content $credentialFile -Raw).Trim()
        if ($fromFile) { return $fromFile }
    }

    throw "No PostgreSQL connection string. Set NOOF_TEST_PG or run ops/reset-database-auth.ps1 to create $credentialFile."
}

$parts = @{}
foreach ($pair in (Get-AdminConnectionString).Split(';')) {
    if ($pair -match '^\s*([^=]+)=(.*)$') { $parts[$Matches[1].Trim()] = $Matches[2].Trim() }
}

$psql = Join-Path $env:ProgramFiles 'PostgreSQL\18\bin\psql.exe'
if (-not (Test-Path $psql)) { throw "psql not found at $psql." }

$env:PGPASSWORD = $parts['Password']
# DROP DATABASE cannot run inside a transaction block, and psql wraps multiple statements in one -c
# into exactly that - so the timeout cannot be a SET alongside the DROP. It goes on the connection
# instead. DROP waits on a Postgres checkpoint, which outlasts the default patience under load.
$env:PGOPTIONS = '-c statement_timeout=120000'
$connection = @('-h', $parts['Host'], '-p', $parts['Port'], '-U', $parts['Username'], '-d', 'postgres')

# LIKE with an escaped underscore: an unescaped _ is a single-character wildcard, so 'noof_test_%'
# would also match a database called noofXtestYsomething. Unlikely, but this script drops things.
$query = @"
SELECT datname FROM pg_database
WHERE datname LIKE 'noof\_test\_%' OR datname LIKE 'noof\_e2e\_%'
ORDER BY datname
"@

$found = @(& $psql @connection -t -A -c $query | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($LASTEXITCODE -ne 0) { throw 'Could not list databases.' }

$targets = @($found | Where-Object { $Protected -notcontains $_ })
$refused = @($found | Where-Object { $Protected -contains $_ })

foreach ($name in $refused) { Write-Warning "Refusing to drop protected database '$name'." }

if ($targets.Count -eq 0) {
    Write-Host 'No leftover test databases.'
    return
}

Write-Host "$($targets.Count) leftover test database(s)."

$dropped = 0
$failed = @()
foreach ($name in $targets) {
    if (-not $PSCmdlet.ShouldProcess($name, 'DROP DATABASE')) { continue }

    # WITH (FORCE) terminates sessions still attached - a leaked database usually has one.
    & $psql @connection -q -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS ""$name"" WITH (FORCE);" 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { $dropped++ } else { $failed += $name }
}

Write-Host "Dropped $dropped."
if ($failed.Count -gt 0) {
    throw "Could not drop $($failed.Count): $($failed -join ', '). Something is probably still connected."
}
