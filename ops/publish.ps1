$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

Push-Location $root
try {
    dotnet build NoofFinance.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    dotnet test --solution NoofFinance.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; refusing to publish.' }

    if (Test-Path publish) { Remove-Item publish -Recurse -Force }

    dotnet publish src/Noof.Host/Noof.Host.csproj -c Release -o publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    Write-Host "Published to $root\publish"
}
finally { Pop-Location }
