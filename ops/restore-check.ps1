#Requires -Version 7
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$DumpPath,
    [string]$BackupDirectory = "$env:LOCALAPPDATA\NoofLedger\backups",
    [string]$SourceDatabase = "noof_ledger_test_template",
    [string]$PgRoot = "C:\Program Files\PostgreSQL\18"
)

$ErrorActionPreference = 'Stop'

# This is the guard that matters (B4/B5): the SCRATCH target this script creates and drops must
# never be able to land on a real database, no matter what -SourceDatabase names for comparison
# (SourceDatabase legitimately IS noof_ledger for the operator's real run in B5 - this script only
# ever reads it). A pattern is a filter; a refusal is a guarantee - same idiom as
# ops/clean-test-databases.ps1's $Protected array.
$Protected = @('noof_ledger', 'noof_ledger_test_template', 'postgres', 'template0', 'template1')
$scratch = "noof_restore_check_$([guid]::NewGuid().ToString('N'))"
if ($scratch -in $Protected) { throw "Refusing: generated scratch name collided with a protected database name - re-run." }

$connectionFile = $env:NOOF_TEST_PG
if (-not $connectionFile) { $connectionFile = (Get-Content "$env:LOCALAPPDATA\NoofLedger\db.connection" -Raw).Trim() }
$c = $connectionFile
$p = @{}; foreach ($x in $c.Split(';')) { if ($x -match '^\s*([^=]+)=(.*)$') { $p[$Matches[1].Trim()] = $Matches[2].Trim() } }
$env:PGPASSWORD = $p['Password']
$psql = Join-Path $PgRoot 'bin\psql.exe'
$pgRestore = Join-Path $PgRoot 'bin\pg_restore.exe'

if (-not $DumpPath) {
    # M-3 (Phase 4 final review): a hand-named file such as "noof_ledger-manual.dump" sorts as
    # newest forever under a loose "noof_ledger-*.dump" glob ('m' > '2', ordinally), so this
    # would check it instead of a real dump. Match only the automated file-name shape.
    $DumpPath = Get-ChildItem $BackupDirectory -Filter 'noof_ledger-????????-??????.dump' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^noof_ledger-\d{8}-\d{6}\.dump$' } |
        Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $DumpPath) { throw "No dump found in $BackupDirectory and none given via -DumpPath." }
}
if (-not (Test-Path $DumpPath)) { throw "Dump not found: $DumpPath" }

function Invoke-Psql([string]$Database, [string]$Sql) {
    $rows = & $psql -h $p['Host'] -p $p['Port'] -U $p['Username'] -d $Database -A -t -F',' -c $Sql
    if ($LASTEXITCODE -ne 0) { throw "psql against '$Database' failed with exit code $LASTEXITCODE" }
    return @($rows)
}

function Get-Comparable([string]$Database) {
    $tables = Invoke-Psql $Database "SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE' ORDER BY table_name"
    # I-1 (Phase 4 final review): backup_runs is written by BackupWorker AFTER the dump completes,
    # so a dump the worker made always has one fewer backup_runs row than the live database it was
    # taken from - by construction, not because anything is actually wrong. Excluded from the
    # gating comparison; its counts are still printed, informationally, below.
    $ledgerTables = $tables | Where-Object { $_ -ne 'backup_runs' }
    $counts = foreach ($table in $ledgerTables) { Invoke-Psql $Database "SELECT '$table', count(*) FROM `"$table`"" }
    [PSCustomObject]@{
        Balances        = Invoke-Psql $Database "SELECT wallet_id, currency, balance, checked_on FROM wallet_balances ORDER BY wallet_id, currency"
        Counts          = $counts
        BackupRunsCount = (Invoke-Psql $Database "SELECT count(*) FROM backup_runs")[0]
    }
}

try {
    Write-Host "Restoring '$DumpPath' into scratch database '$scratch'..."
    if ($PSCmdlet.ShouldProcess($scratch, 'CREATE DATABASE')) {
        & $psql -h $p['Host'] -p $p['Port'] -U $p['Username'] -d postgres -c "CREATE DATABASE `"$scratch`"" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "CREATE DATABASE failed with exit code $LASTEXITCODE" }
    }

    try {
        if ($PSCmdlet.ShouldProcess($DumpPath, "pg_restore into $scratch")) {
            & $pgRestore -h $p['Host'] -p $p['Port'] -U $p['Username'] -d $scratch --no-owner --no-privileges $DumpPath
            if ($LASTEXITCODE -ne 0) { throw "pg_restore failed with exit code $LASTEXITCODE" }
        }

        $source = Get-Comparable $SourceDatabase
        $restored = Get-Comparable $scratch

        Write-Host "backup_runs rows (informational, not compared - I-1): '$SourceDatabase' has $($source.BackupRunsCount), the restored dump has $($restored.BackupRunsCount)."

        # Compare-Object throws "Cannot bind argument to parameter 'ReferenceObject' because it is
        # null" when either side is empty (e.g. an empty wallet_balances in a freshly migrated
        # template) - wrapping both sides in @() keeps it an empty array instead of $null.
        $balanceDiff = Compare-Object @($source.Balances) @($restored.Balances)
        $countDiff = Compare-Object @($source.Counts) @($restored.Counts)

        if ($balanceDiff -or $countDiff) {
            Write-Host "MISMATCH between '$SourceDatabase' and the restored dump:"
            if ($balanceDiff) { Write-Host "wallet_balances differs:"; $balanceDiff | Format-Table | Out-String | Write-Host }
            if ($countDiff) { Write-Host "row counts differ:"; $countDiff | Format-Table | Out-String | Write-Host }
            $script:failed = $true
        }
        else {
            Write-Host "restore-check OK: '$DumpPath' matches '$SourceDatabase' (wallet_balances and every ledger table's row count; backup_runs is informational only)."
            $script:failed = $false
        }
    }
    finally {
        if ($PSCmdlet.ShouldProcess($scratch, 'DROP DATABASE')) {
            & $psql -h $p['Host'] -p $p['Port'] -U $p['Username'] -d postgres -c "DROP DATABASE IF EXISTS `"$scratch`" WITH (FORCE)" | Out-Null
        }
    }
}
finally {
    # M-2 (Phase 4 final review): otherwise the ledger password stays in this shell's environment
    # after the script exits, visible to every child process launched afterwards from it.
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}

if ($failed) { exit 1 }
exit 0
