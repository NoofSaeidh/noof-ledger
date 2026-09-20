#Requires -RunAsAdministrator
<#
    One-time setup. Resets the PostgreSQL superuser password, creates the two
    databases Phase 0 needs, and stores the credential OUTSIDE the repository.

    Reversible: pg_hba.conf is backed up before any edit and restored at the end.
    If this script fails partway, restore the backup it names and restart the service.
#>
param(
    [string]$PgRoot      = 'C:\Program Files\PostgreSQL\18',
    [string]$ServiceName = 'postgresql-x64-18'
)

$ErrorActionPreference = 'Stop'

$dataDir = Join-Path $PgRoot 'data'
$hba     = Join-Path $dataDir 'pg_hba.conf'
$psql    = Join-Path $PgRoot 'bin\psql.exe'
$backup  = Join-Path $dataDir ("pg_hba.conf.backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))

foreach ($p in @($hba, $psql)) {
    if (-not (Test-Path $p)) { throw "Not found: $p. Check -PgRoot." }
}

Write-Host "Backing up pg_hba.conf -> $backup"
Copy-Item $hba $backup

function Set-LoopbackAuth([string]$method) {
    (Get-Content $hba) |
        ForEach-Object {
            if ($_ -match '^\s*host\s+all\s+all\s+(127\.0\.0\.1/32|::1/128)\s+\S+\s*$') {
                $_ -replace '(\S+)\s*$', $method
            } else { $_ }
        } |
        Set-Content $hba -Encoding ASCII
}

try {
    Write-Host 'Switching loopback auth to trust...'
    Set-LoopbackAuth 'trust'
    Restart-Service $ServiceName
    Start-Sleep -Seconds 3

    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $password = ([Convert]::ToBase64String($bytes)) -replace '[+/=]', 'x'

    Write-Host 'Resetting postgres password and creating databases...'
    $env:PGPASSWORD = ''
    & $psql -U postgres -h 127.0.0.1 -v ON_ERROR_STOP=1 -c "ALTER USER postgres WITH PASSWORD '$password';"
    if ($LASTEXITCODE -ne 0) { throw 'Failed to set the postgres password.' }

    foreach ($db in @('noof_ledger', 'noof_ledger_test_template')) {
        $exists = & $psql -U postgres -h 127.0.0.1 -tAc "SELECT 1 FROM pg_database WHERE datname='$db';"
        if ($exists -ne '1') {
            & $psql -U postgres -h 127.0.0.1 -v ON_ERROR_STOP=1 -c "CREATE DATABASE $db;"
            if ($LASTEXITCODE -ne 0) { throw "Failed to create database $db." }
            Write-Host "  created $db"
        } else {
            Write-Host "  $db already exists"
        }

        & $psql -U postgres -h 127.0.0.1 -d $db -v ON_ERROR_STOP=1 -c 'CREATE EXTENSION IF NOT EXISTS pg_trgm; CREATE EXTENSION IF NOT EXISTS unaccent;'
        if ($LASTEXITCODE -ne 0) { throw "Failed to create extensions in $db." }
    }
}
finally {
    Write-Host 'Restoring password authentication...'
    Set-LoopbackAuth 'scram-sha-256'
    Restart-Service $ServiceName
    Start-Sleep -Seconds 3
    $env:PGPASSWORD = ''
}

$connection = "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=$password;Include Error Detail=true"

$dataRoot = Join-Path $env:LOCALAPPDATA 'NoofLedger'
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
$credentialFile = Join-Path $dataRoot 'db.connection'
# UTF8 without a BOM. PowerShell 5.1's -Encoding UTF8 always writes one, and Npgsql
# rejects a connection string whose first character is a BOM when the file is piped
# straight into a command line (File.ReadAllText strips it, so the app never notices).
[System.IO.File]::WriteAllText($credentialFile, $connection, (New-Object System.Text.UTF8Encoding $false))

[Environment]::SetEnvironmentVariable('NOOF_TEST_PG', $connection, 'User')

Write-Host ''
Write-Host 'Done.'
Write-Host "  Credential file : $credentialFile"
Write-Host '  User env var    : NOOF_TEST_PG'
Write-Host "  pg_hba backup   : $backup"
Write-Host ''
Write-Host 'Verifying...'
$env:PGPASSWORD = $password
& $psql -U postgres -h 127.0.0.1 -tAc "SELECT 'OK ' || current_setting('server_version') || ' databases=' || (SELECT count(*) FROM pg_database WHERE datname LIKE 'noof%');"
$env:PGPASSWORD = ''
