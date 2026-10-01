#Requires -Version 7
<#
.SYNOPSIS
Runs one `gh` command as the noof-ledger-bot GitHub App instead of the operator.

.DESCRIPTION
Every argument goes to `gh` unchanged: `.\ops\gh-bot.ps1 pr create --draft --title ...`.

Mints a GitHub App installation token for this repository only - it expires after an hour - and
hands it to that one `gh` process through GH_TOKEN, restored afterwards. The token is never an
argument, never printed and never written to a file.

Reads, from %LOCALAPPDATA%\NoofLedger\github-app\ (setup and key rotation: ops/RUNBOOK.md):
  app-id.txt    - the app's numeric App ID, not a secret
  key.dpapi     - the app's private key, DPAPI-encrypted for the current Windows user

.OUTPUTS
Whatever `gh` prints, and its exit code.
#>

$ErrorActionPreference = 'Stop'

$AppDirectory = Join-Path $env:LOCALAPPDATA 'NoofLedger\github-app'
$GitHubApiHeaders = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }

function ConvertTo-Base64Url {
    param([byte[]]$Bytes)
    [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Get-RepoFromRemoteUrl {
    param([string]$Url)
    if ($Url -notmatch 'github\.com[:/](?<repo>[^/]+/[^/]+?)(\.git)?$') { throw "Not a GitHub remote: $Url" }
    $Matches.repo
}

function New-AppJwt {
    param([string]$PrivateKeyPem, [long]$AppId, [DateTimeOffset]$Now)
    $header = @{ alg = 'RS256'; typ = 'JWT' } | ConvertTo-Json -Compress
    $claims = @{
        iat = $Now.AddSeconds(-60).ToUnixTimeSeconds()
        exp = $Now.AddSeconds(540).ToUnixTimeSeconds()
        iss = $AppId
    } | ConvertTo-Json -Compress
    $unsigned = "$(ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($header))).$(ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($claims)))"

    $rsa = [Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem($PrivateKeyPem)
        $signature = $rsa.SignData([Text.Encoding]::ASCII.GetBytes($unsigned),
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    }
    finally { $rsa.Dispose() }
    "$unsigned.$(ConvertTo-Base64Url $signature)"
}

function Get-InstallationToken {
    param([string]$Repo)
    $appId = [long](Get-Content (Join-Path $AppDirectory 'app-id.txt') -Raw).Trim()
    $pem = (Get-Content (Join-Path $AppDirectory 'key.dpapi') -Raw).Trim() | ConvertTo-SecureString | ConvertFrom-SecureString -AsPlainText
    $jwt = New-AppJwt -PrivateKeyPem $pem -AppId $appId -Now ([DateTimeOffset]::UtcNow)
    $appHeaders = $GitHubApiHeaders + @{ Authorization = "Bearer $jwt" }

    $installation = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/installation" -Headers $appHeaders
    $body = @{ repositories = @($Repo.Split('/')[1]) } | ConvertTo-Json -Compress
    $response = Invoke-RestMethod -Method Post -Uri "https://api.github.com/app/installations/$($installation.id)/access_tokens" `
        -Headers $appHeaders -Body $body -ContentType 'application/json'
    $response.token
}

if ($MyInvocation.InvocationName -ne '.') {
    $repo = Get-RepoFromRemoteUrl (git -C $PSScriptRoot remote get-url origin)
    $token = Get-InstallationToken -Repo $repo
    $previousToken = $env:GH_TOKEN
    try {
        $env:GH_TOKEN = $token
        & gh @args
        $ghExitCode = $LASTEXITCODE
    }
    finally { $env:GH_TOKEN = $previousToken }
    exit $ghExitCode
}
