#Requires -Version 7

<#
.SYNOPSIS
The one entry point for building, running, testing and operating noof-ledger.

.DESCRIPTION
run.ps1 replaces "sometimes dotnet run, sometimes the published exe, sometimes an ops/ script" with
one command every launch and operational path goes through, so `dotnet run` and the published exe
behave the same and nobody has to remember which ops/*.ps1 script does what. It does not replace any
ops/*.ps1 script - each one still exists and still works stand-alone - it is the thing that calls
them with the right arguments, plus the things ops/ never had a script for (starting the host itself,
checking status, tailing logs).

Commands (run `.\run.ps1 help <command>` for the detail on any one of them):

  start              - dotnet run in Production (the main way to run the app; -Dev for Development)
  publish            - build, test and publish to publish/ (ops/publish.ps1)
  start-published    - run the published Noof.Ledger.Host.exe from any current directory
  set-password       - create or reset a user's password (the host's `user set-password` verb)
  test               - run fast / db / e2e / all tests, with the shared PostgreSQL lock where needed
  update-test-template - apply the latest migration to noof_ledger_test_template
  clean-test-dbs     - drop leftover noof_test_*/noof_e2e_* databases (ops/clean-test-databases.ps1)
  restore-check      - verify a backup dump restores to the same balances (ops/restore-check.ps1)
  db-auth-reset      - one-time PostgreSQL auth setup (ops/reset-database-auth.ps1, needs admin)
  pg                 - start, stop or check the postgresql-x64-18 Windows service
  status             - PostgreSQL service state, whether the host answers /healthz, the newest log
  logs               - open, tail or follow the log directory
  backups            - open the backup directory
  inspect            - the solution-wide accessibility sweep (ops/inspect.ps1)
  help               - this table, or `.\run.ps1 help <command>` for one command's detail

An unrecognised command prints this table and exits 1.

.PARAMETER Command
The command to run. Defaults to `help` when omitted.

.PARAMETER Dev
For `start` only: use the Development launch profile instead of the Production default.

.PARAMETER Output
For `publish` only: the directory to publish into. Defaults to `publish` under the repo root.

.PARAMETER Path
For `start-published` only: the directory the published host lives in. Defaults to `publish` under
the repo root.

.PARAMETER Filter
For `test` only: a test class name, passed to `dotnet test --filter`.

.PARAMETER WhatIf
For `clean-test-dbs` only: list what would be dropped and drop nothing.

.PARAMETER Tail
For `logs` only: print the last N lines of the newest log file instead of opening the directory.

.PARAMETER Follow
For `logs` only: keep printing new log lines as they are written.

.EXAMPLE
.\run.ps1 start
Runs the host in Production, the same way the published exe would.

.EXAMPLE
.\run.ps1 start -Dev
Runs the host with the Development launch profile, for local debugging in an IDE-like setting.

.EXAMPLE
.\run.ps1 publish -Output C:\deploy\noof-ledger
Builds, runs the full test suite, and publishes to the given directory.

.EXAMPLE
.\run.ps1 start-published -Path C:\deploy\noof-ledger
Runs a previously published host from whatever directory you happen to be in.

.EXAMPLE
.\run.ps1 set-password noof
Prompts for a password (masked) and creates or resets the "noof" user.

.EXAMPLE
.\run.ps1 test fast
Runs every test project that needs no PostgreSQL and no browser.

.EXAMPLE
.\run.ps1 test db -Filter WalletBalancesViewTests
Runs one Persistence.Tests class under the shared suite lock.

.EXAMPLE
.\run.ps1 test all
Runs the whole solution suite (database and E2E included), once, under the shared suite lock.

.EXAMPLE
.\run.ps1 clean-test-dbs -WhatIf
Lists leftover noof_test_*/noof_e2e_* databases without dropping anything.

.EXAMPLE
.\run.ps1 restore-check -SourceDatabase noof_ledger_test_template
Forwards its arguments straight to ops/restore-check.ps1.

.EXAMPLE
.\run.ps1 pg status
Reports whether the postgresql-x64-18 Windows service is running.

.EXAMPLE
.\run.ps1 status
One-screen summary: PostgreSQL, /healthz, the newest log file.

.EXAMPLE
.\run.ps1 logs -Tail 50
Prints the last 50 lines of the newest log file.

.EXAMPLE
.\run.ps1 logs -Follow
Keeps printing new log lines as the running host writes them.

.EXAMPLE
.\run.ps1 help test
Prints the detail for the `test` command alone.
#>
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$ServiceName = 'postgresql-x64-18'

# No formal parameters are declared above (deliberately: -WhatIf, -Filter, -Output and friends are
# meant per-command, not shared, and a shared [CmdletBinding()] would swallow -WhatIf as the common
# ShouldProcess parameter before this script ever saw it). Everything is parsed from $args by hand.
$CommandName = if ($args.Count -ge 1) { $args[0] } else { 'help' }
$Rest = @()
if ($args.Count -ge 2) { $Rest = @($args[1..($args.Count - 1)]) }

function Get-ArgValue {
    param([string[]]$Values, [string]$Name, $Default = $null)
    for ($i = 0; $i -lt $Values.Count; $i++) {
        if ($Values[$i] -eq $Name -and $i + 1 -lt $Values.Count) { return $Values[$i + 1] }
    }
    return $Default
}

function Test-ArgSwitch {
    param([string[]]$Values, [string]$Name)
    return $Values -contains $Name
}

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-Elevated-Or-Explain {
    param([string]$Because)
    if (Test-Elevated) { return $true }
    Write-Host "'$Because' needs an elevated (Run as Administrator) PowerShell. Re-run this command from one." -ForegroundColor Yellow
    return $false
}

# Reads the same two keys LoggingSetup.cs and Program.cs's "Urls" own - never re-hardcodes their
# defaults, so a future change to either only needs to change one file plus this one, not three
# (that drift is exactly what LogDirectoryDefaultTests guards inside the solution itself).
function Get-AppSettings {
    $path = Join-Path $Root 'src\Noof.Ledger.Host\appsettings.json'
    return Get-Content $path -Raw | ConvertFrom-Json
}

function Get-HostUrl {
    $settings = Get-AppSettings
    if ($settings.Urls) { return $settings.Urls }
    return 'http://127.0.0.1:5263'
}

function Get-LogDirectory {
    $settings = Get-AppSettings
    $configured = $settings.Logging.File.Directory
    if (-not $configured) { $configured = '%LOCALAPPDATA%\NoofLedger\logs' }
    return [Environment]::ExpandEnvironmentVariables($configured)
}

function Get-NewestLogFile {
    $directory = Get-LogDirectory
    if (-not (Test-Path $directory)) { return $null }
    return Get-ChildItem $directory -Filter 'noof-ledger-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

# Every filtered database/E2E test run, every test-template migration and every private-template
# `dotnet ef database update` shares this one server across worktrees - the operator's own rule
# (docs/superpowers/sdd/.../global-constraints.md and CLAUDE.md's testing section). A lock older
# than 30 minutes is treated as orphaned (a killed process left it) and taken.
function Enter-SuiteLock {
    $lockPath = Join-Path $env:TEMP 'noof-suite.lock'
    for ($attempt = 1; $attempt -le 120; $attempt++) {
        try {
            if ((Test-Path $lockPath) -and ((Get-Item $lockPath).LastWriteTime -lt (Get-Date).AddMinutes(-30))) {
                Write-Warning "Removing orphaned suite lock at $lockPath (older than 30 minutes)."
                Remove-Item $lockPath -Recurse -Force -ErrorAction SilentlyContinue
            }
            New-Item -ItemType Directory -Path $lockPath -ErrorAction Stop | Out-Null
            return $lockPath
        } catch {
            if ($attempt -eq 1) { Write-Host 'Waiting for the shared PostgreSQL/browser suite lock...' }
            Start-Sleep -Seconds 30
        }
    }
    throw "Could not acquire the suite lock at $lockPath after 60 minutes."
}

function Exit-SuiteLock {
    param([string]$LockPath)
    if ($LockPath -and (Test-Path $LockPath)) { Remove-Item $LockPath -Recurse -Force -ErrorAction SilentlyContinue }
}

function Invoke-Checked {
    param([scriptblock]$Action)
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE." }
}

# Ordered so the table and Get-Help both read top-to-bottom as "the main way, then the rest".
$Commands = [ordered]@{
    'start' = @{
        Summary = 'Run the host (the main way): Production via dotnet run, matching the published exe'
        Detail  = @'
start [-Dev]

The main way to run the app during development: `dotnet run --project src\Noof.Ledger.Host -c Release
--no-launch-profile`, which runs in Production - the same environment, the same content-root
resolution and the same appsettings "Urls" the published exe uses, so the two behave the same.

-Dev runs with the Development launch profile instead (ASPNETCORE_ENVIRONMENT=Development), for
local debugging in an IDE-like setting. Same URL either way.

Prerequisites: PostgreSQL reachable (or the host just shows the "Waiting for the database..."
banner until it is); a .NET 10 SDK.
'@
    }
    'publish' = @{
        Summary = 'Build, test and publish to a directory (refuses to publish if a test failed)'
        Detail  = @'
publish [-Output <dir>]

Runs ops\publish.ps1: builds the solution in Release, runs the full test suite, and refuses to
publish if a single test failed or if no test actually ran. Publishes to -Output (default: .\publish
under the repo root).

Prerequisites: PostgreSQL reachable and the test template up to date - the suite this runs includes
the database and E2E projects.
'@
    }
    'start-published' = @{
        Summary = 'Run the published Noof.Ledger.Host.exe, from any current directory'
        Detail  = @'
start-published [-Path <dir>]

Runs Noof.Ledger.Host.exe from -Path (default: .\publish under the repo root). Program.cs resolves
its content root from its own install directory (AppContext.BaseDirectory), not from the shell's
current directory, so this now works no matter where you run it from.

Prerequisites: the directory must already hold a publish (run `.\run.ps1 publish` first).
'@
    }
    'set-password' = @{
        Summary = 'Create or reset a user''s password (the host''s `user set-password` verb)'
        Detail  = @'
set-password <username>

Runs `dotnet run --project src\Noof.Ledger.Host -- user set-password <username>`. Prompts for a
password on the console (masked while you type) and writes its hash to the database - this is the
only way a user is ever created; there is no registration page.

Prerequisites: PostgreSQL reachable.
'@
    }
    'test' = @{
        Summary = 'Run tests: fast (no db/browser), db, e2e, or all (db/e2e/all take the suite lock)'
        Detail  = @'
test [fast|db|e2e|all] [-Filter <class>]

fast (the default) runs every project that needs neither PostgreSQL nor a browser: Domain, Ai,
Architecture, Telegram and Host.Tests.

db runs Noof.Ledger.Persistence.Tests, e2e runs Noof.Ledger.E2E.Tests, all runs the whole solution
suite (`dotnet test --solution`). Each of these three acquires the shared suite lock
($env:TEMP\noof-suite.lock - PostgreSQL and Playwright browsers are shared across worktrees) before
running and always releases it afterwards, even on failure.

-Filter passes a test class name straight to `dotnet test --filter`. Per the project's own testing
rule, db and e2e should almost always be run filtered to the class you are actually changing - `all`
is the one unfiltered run, done once at the end of a phase.

Prerequisites: for db/e2e/all, PostgreSQL reachable and (for e2e/all) a Chromium install for
Playwright.
'@
    }
    'update-test-template' = @{
        Summary = 'Apply the latest EF Core migration to noof_ledger_test_template'
        Detail  = @'
update-test-template

Runs the command in ops\RUNBOOK.md's "After adding a migration" section: resolves the admin
connection string, rewrites its Database to noof_ledger_test_template, refuses to proceed if that
rewrite did not actually happen, and runs `dotnet ef database update`. The template connection
string (it carries the postgres password) is handed to `dotnet ef` through the
NOOF_LEDGER_EF_CONNECTION environment variable, never as a --connection argument, so the password
never appears on that process's command line. Never touches noof_ledger. Takes the shared suite
lock, since the template is shared across worktrees. Run this right after any `dotnet ef migrations
add`.

Prerequisites: PostgreSQL reachable; NOOF_TEST_PG set or %LOCALAPPDATA%\NoofLedger\db.connection
present (ops\reset-database-auth.ps1 creates it).
'@
    }
    'clean-test-dbs' = @{
        Summary = 'Drop leftover noof_test_*/noof_e2e_* databases from interrupted test runs'
        Detail  = @'
clean-test-dbs [-WhatIf]

Runs ops\clean-test-databases.ps1. -WhatIf lists what would be dropped without dropping anything.
Refuses noof_ledger and noof_ledger_test_template by exact name, never merely by pattern.

Prerequisites: PostgreSQL reachable.
'@
    }
    'restore-check' = @{
        Summary = 'Prove a backup dump restores to the same balances (ops\restore-check.ps1)'
        Detail  = @'
restore-check [args passthrough]

Forwards every argument after `restore-check` straight to ops\restore-check.ps1 (for example
-DumpPath, -SourceDatabase, -PgRoot) - see that script's own comment header for the full parameter
list. Restores a dump into a throwaway scratch database, compares wallet_balances and every ledger
table's row count against -SourceDatabase (default noof_ledger_test_template), and drops the scratch
database either way. Never lets the scratch target be a real or template database.

Prerequisites: PostgreSQL reachable; a dump under %LOCALAPPDATA%\NoofLedger\backups, or -DumpPath.
'@
    }
    'db-auth-reset' = @{
        Summary = 'One-time PostgreSQL auth setup (elevated; ops\reset-database-auth.ps1)'
        Detail  = @'
db-auth-reset

Runs ops\reset-database-auth.ps1: resets the postgres superuser password, creates noof_ledger and
noof_ledger_test_template if missing, and writes the connection string to
%LOCALAPPDATA%\NoofLedger\db.connection. Idempotent - safe to re-run.

Prerequisites: an elevated (Run as Administrator) PowerShell. run.ps1 checks for this itself and
explains rather than failing with a raw #Requires error.
'@
    }
    'pg' = @{
        Summary = 'Start, stop or check the postgresql-x64-18 Windows service'
        Detail  = @'
pg start|stop|status

Controls the postgresql-x64-18 Windows service directly. start and stop need an elevated shell;
status works from any shell.

Prerequisites: start/stop need an elevated (Run as Administrator) PowerShell.
'@
    }
    'status' = @{
        Summary = 'One-screen summary: PostgreSQL service, /healthz, the newest log file'
        Detail  = @'
status

Prints three things: the postgresql-x64-18 service state, whether the host answers /healthz at the
configured URL (a few seconds' timeout - "not running" is a normal answer, not an error), and the
newest file under the configured log directory with its last-write time.

Prerequisites: none - every check degrades to "not reachable" / "not found" rather than throwing.
'@
    }
    'logs' = @{
        Summary = 'Open, tail or follow the log directory'
        Detail  = @'
logs [-Tail <n>] [-Follow]

With no switches, opens the configured log directory (Logging:File:Directory in appsettings.json,
environment variables expanded; default %LOCALAPPDATA%\NoofLedger\logs) in Explorer. -Tail <n>
prints the last n lines of the newest noof-ledger-*.log file. -Follow keeps printing new lines as
the running host writes them (Ctrl+C to stop).

Prerequisites: none, beyond the host having logged at least once for -Tail/-Follow to show anything.
'@
    }
    'backups' = @{
        Summary = 'Open the backup directory (%LOCALAPPDATA%\NoofLedger\backups)'
        Detail  = @'
backups

Opens %LOCALAPPDATA%\NoofLedger\backups in Explorer - where BackupWorker writes its daily
`pg_dump -Fc` dumps. See ops\RUNBOOK.md for restoring one by hand or with `restore-check`.

Prerequisites: none (the folder may not exist yet if the host has never backed up).
'@
    }
    'inspect' = @{
        Summary = 'Solution-wide accessibility sweep (ops\inspect.ps1)'
        Detail  = @'
inspect

Runs ops\inspect.ps1 (dotnet jb inspectcode against the whole solution) and fails on any
ERROR-severity finding under src\. Run `dotnet build NoofLedger.slnx` first - this inspects whatever
was last built, not what is currently on disk.

Prerequisites: JetBrains.ReSharper.GlobalTools restored (dotnet tool restore); a prior build.
'@
    }
}

function Write-CommandTable {
    Write-Host ''
    Write-Host 'Usage: .\run.ps1 <command> [options]        .\run.ps1 help <command> for detail'
    Write-Host ''
    $width = ($Commands.Keys | Measure-Object -Property Length -Maximum).Maximum
    foreach ($name in $Commands.Keys) {
        Write-Host ('  ' + $name.PadRight($width) + '  ' + $Commands[$name].Summary)
    }
    Write-Host ''
}

function Write-CommandDetail {
    param([string]$Name)
    if (-not $Commands.Contains($Name)) {
        Write-Host "Unknown command '$Name'."
        Write-CommandTable
        exit 1
    }
    Write-Host ''
    Write-Host $Commands[$Name].Detail.Trim()
    Write-Host ''
}

switch ($CommandName) {
    'help' {
        if ($Rest.Count -ge 1) { Write-CommandDetail -Name $Rest[0] }
        else { Write-CommandTable }
    }

    'start' {
        $dev = Test-ArgSwitch $Rest '-Dev'
        Write-Host "Listening on $(Get-HostUrl)"
        if ($dev) {
            Invoke-Checked { dotnet run --project (Join-Path $Root 'src\Noof.Ledger.Host') --launch-profile 'Noof.Ledger.Host' }
        } else {
            Invoke-Checked { dotnet run --project (Join-Path $Root 'src\Noof.Ledger.Host') -c Release --no-launch-profile }
        }
    }

    'publish' {
        $output = Get-ArgValue $Rest '-Output' (Join-Path $Root 'publish')
        Invoke-Checked { & (Join-Path $Root 'ops\publish.ps1') -Output $output }
    }

    'start-published' {
        $path = Get-ArgValue $Rest '-Path' (Join-Path $Root 'publish')
        $exe = Join-Path $path 'Noof.Ledger.Host.exe'
        if (-not (Test-Path $exe)) { throw "Not found: $exe. Run '.\run.ps1 publish' first, or pass -Path." }
        Push-Location $path
        try { Invoke-Checked { & $exe } }
        finally { Pop-Location }
    }

    'set-password' {
        if ($Rest.Count -lt 1) { throw "Usage: .\run.ps1 set-password <username>" }
        $username = $Rest[0]
        Invoke-Checked {
            dotnet run --project (Join-Path $Root 'src\Noof.Ledger.Host') -- user set-password $username
        }
    }

    'test' {
        $suite = if ($Rest.Count -ge 1 -and -not $Rest[0].StartsWith('-')) { $Rest[0] } else { 'fast' }
        $filter = Get-ArgValue $Rest '-Filter'

        $fastProjects = @(
            'tests\Noof.Ledger.Domain.Tests\Noof.Ledger.Domain.Tests.csproj',
            'tests\Noof.Ledger.Ai.Tests\Noof.Ledger.Ai.Tests.csproj',
            'tests\Noof.Ledger.Architecture.Tests\Noof.Ledger.Architecture.Tests.csproj',
            'tests\Noof.Ledger.Telegram.Tests\Noof.Ledger.Telegram.Tests.csproj',
            'tests\Noof.Ledger.Host.Tests\Noof.Ledger.Host.Tests.csproj'
        )

        switch ($suite) {
            'fast' {
                foreach ($project in $fastProjects) {
                    $full = Join-Path $Root $project
                    if ($filter) { Invoke-Checked { dotnet test --project $full --filter $filter } }
                    else { Invoke-Checked { dotnet test --project $full } }
                }
            }
            'db' {
                $lock = Enter-SuiteLock
                try {
                    $full = Join-Path $Root 'tests\Noof.Ledger.Persistence.Tests\Noof.Ledger.Persistence.Tests.csproj'
                    if ($filter) { Invoke-Checked { dotnet test --project $full --filter $filter } }
                    else { Invoke-Checked { dotnet test --project $full } }
                } finally { Exit-SuiteLock $lock }
            }
            'e2e' {
                $lock = Enter-SuiteLock
                try {
                    $full = Join-Path $Root 'tests\Noof.Ledger.E2E.Tests\Noof.Ledger.E2E.Tests.csproj'
                    if ($filter) { Invoke-Checked { dotnet test --project $full --filter $filter } }
                    else { Invoke-Checked { dotnet test --project $full } }
                } finally { Exit-SuiteLock $lock }
            }
            'all' {
                $lock = Enter-SuiteLock
                try { Invoke-Checked { dotnet test --solution (Join-Path $Root 'NoofLedger.slnx') } }
                finally { Exit-SuiteLock $lock }
            }
            default { throw "Unknown test suite '$suite'. Use fast, db, e2e or all." }
        }
    }

    'update-test-template' {
        $lock = Enter-SuiteLock
        try {
            $admin = if ($env:NOOF_TEST_PG) { $env:NOOF_TEST_PG } else { (Get-Content "$env:LOCALAPPDATA\NoofLedger\db.connection").Trim() }
            $template = $admin -replace 'Database=postgres', 'Database=noof_ledger_test_template'
            if ($template -notmatch 'Database=noof_ledger_test_template') {
                throw 'Refusing: the connection string does not name the test template.'
            }
            $persistence = Join-Path $Root 'src\Noof.Ledger.Persistence'
            # The template connection string (it carries the postgres password) goes to the `dotnet ef`
            # child process through this environment variable, which DesignTimeDbContextFactory reads -
            # never through --connection, which would put the password on that process's command line.
            $env:NOOF_LEDGER_EF_CONNECTION = $template
            try {
                Invoke-Checked {
                    dotnet ef database update --project $persistence --startup-project $persistence
                }
            } finally {
                Remove-Item Env:\NOOF_LEDGER_EF_CONNECTION -ErrorAction SilentlyContinue
            }
        } finally { Exit-SuiteLock $lock }
    }

    'clean-test-dbs' {
        $whatIf = Test-ArgSwitch $Rest '-WhatIf'
        if ($whatIf) { Invoke-Checked { & (Join-Path $Root 'ops\clean-test-databases.ps1') -WhatIf } }
        else { Invoke-Checked { & (Join-Path $Root 'ops\clean-test-databases.ps1') } }
    }

    'restore-check' {
        Invoke-Checked { & (Join-Path $Root 'ops\restore-check.ps1') @Rest }
    }

    'db-auth-reset' {
        if (Get-Elevated-Or-Explain -Because 'db-auth-reset') {
            Invoke-Checked { & (Join-Path $Root 'ops\reset-database-auth.ps1') }
        } else { exit 1 }
    }

    'pg' {
        $action = if ($Rest.Count -ge 1) { $Rest[0] } else { 'status' }
        switch ($action) {
            'start' {
                if (Get-Elevated-Or-Explain -Because 'pg start') { Start-Service $ServiceName } else { exit 1 }
            }
            'stop' {
                if (Get-Elevated-Or-Explain -Because 'pg stop') { Stop-Service $ServiceName } else { exit 1 }
            }
            'status' {
                $service = Get-Service $ServiceName -ErrorAction SilentlyContinue
                if ($service) { Write-Host "$ServiceName is $($service.Status)." }
                else { Write-Host "$ServiceName is not installed on this machine." }
            }
            default { throw "Unknown pg action '$action'. Use start, stop or status." }
        }
    }

    'status' {
        $service = Get-Service $ServiceName -ErrorAction SilentlyContinue
        if ($service) { Write-Host "PostgreSQL ($ServiceName): $($service.Status)" }
        else { Write-Host "PostgreSQL ($ServiceName): not installed" }

        $url = Get-HostUrl
        try {
            $response = Invoke-WebRequest -Uri "$url/healthz" -TimeoutSec 3 -UseBasicParsing
            Write-Host "Host ($url/healthz): $($response.StatusCode)"
        } catch {
            Write-Host "Host ($url/healthz): not reachable"
        }

        $newest = Get-NewestLogFile
        if ($newest) { Write-Host "Newest log: $($newest.FullName) ($($newest.LastWriteTime))" }
        else { Write-Host "Newest log: none found under $(Get-LogDirectory)" }
    }

    'logs' {
        $directory = Get-LogDirectory
        $tail = Get-ArgValue $Rest '-Tail'
        $follow = Test-ArgSwitch $Rest '-Follow'

        if (-not $tail -and -not $follow) {
            if (-not (Test-Path $directory)) { throw "Log directory does not exist yet: $directory" }
            Invoke-Item $directory
            return
        }

        $newest = Get-NewestLogFile
        if (-not $newest) { throw "No log file found under $directory" }

        if ($follow) { Get-Content $newest.FullName -Wait }
        elseif ($tail) { Get-Content $newest.FullName -Tail ([int]$tail) }
    }

    'backups' {
        $directory = Join-Path $env:LOCALAPPDATA 'NoofLedger\backups'
        if (-not (Test-Path $directory)) { throw "Backup directory does not exist yet: $directory" }
        Invoke-Item $directory
    }

    'inspect' {
        Invoke-Checked { & (Join-Path $Root 'ops\inspect.ps1') }
    }

    default {
        Write-Host "Unknown command '$CommandName'."
        Write-CommandTable
        exit 1
    }
}
