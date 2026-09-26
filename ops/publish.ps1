# -Output defaults to publish/ under the repo root, kept as a bare relative path (not
# Join-Path $root 'publish') so a caller that leaves it at the default sees exactly the path this
# script always published to.
param(
    [string]$Output = 'publish'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

Push-Location $root
try {
    dotnet build NoofLedger.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    $testOutput = dotnet test --solution NoofLedger.slnx -c Release 2>&1 | Out-String
    Write-Host $testOutput
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; refusing to publish.' }
    if ($testOutput -notmatch '(?m)^\s*succeeded:\s*([1-9]\d*)') { throw 'No tests ran; refusing to publish.' }

    if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

    dotnet publish src/Noof.Ledger.Host/Noof.Ledger.Host.csproj -c Release -o $Output
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    Write-Host "Published to $Output"
}
finally { Pop-Location }
