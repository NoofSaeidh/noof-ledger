#Requires -Version 7
# Exercises gh-bot.ps1's pure functions with a throwaway RSA key - no `gh`, no network, no real key.
# Plain PowerShell like pr-wait.tests.ps1. Run by `.\run.ps1 test fast` (and so by CI); exits 1 on any
# failed case.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'gh-bot.ps1')

$script:failures = [System.Collections.Generic.List[string]]::new()
$script:passed = 0

function Assert-Equal {
    param([string]$Case, $Expected, $Actual)
    if ($Expected -ceq $Actual) { $script:passed++ }
    else { $script:failures.Add("$Case - expected '$Expected', got '$Actual'") }
}

function ConvertFrom-Base64Url {
    param([string]$Text)
    $padded = $Text.Replace('-', '+').Replace('_', '/')
    $padded += '=' * ((4 - $padded.Length % 4) % 4)
    [Convert]::FromBase64String($padded)
}

Assert-Equal 'base64url drops padding' 'YQ' (ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes('a')))
Assert-Equal 'base64url replaces + and /' '-___' (ConvertTo-Base64Url ([byte[]](0xFB, 0xFF, 0xFF)))

Assert-Equal 'https remote' 'NoofSaeidh/noof-ledger' (Get-RepoFromRemoteUrl 'https://github.com/NoofSaeidh/noof-ledger.git')
Assert-Equal 'https remote without .git' 'NoofSaeidh/noof-ledger' (Get-RepoFromRemoteUrl 'https://github.com/NoofSaeidh/noof-ledger')
Assert-Equal 'ssh remote' 'NoofSaeidh/noof-ledger' (Get-RepoFromRemoteUrl 'git@github.com:NoofSaeidh/noof-ledger.git')

$rsa = [Security.Cryptography.RSA]::Create(2048)
$pem = $rsa.ExportRSAPrivateKeyPem()
$now = [DateTimeOffset]::FromUnixTimeSeconds(1790000000)
$jwt = New-AppJwt -PrivateKeyPem $pem -AppId 5147899 -Now $now
$header, $payload, $signature = $jwt.Split('.')

$headerJson = [Text.Encoding]::UTF8.GetString((ConvertFrom-Base64Url $header)) | ConvertFrom-Json
Assert-Equal 'header alg' 'RS256' $headerJson.alg
Assert-Equal 'header typ' 'JWT' $headerJson.typ

$claims = [Text.Encoding]::UTF8.GetString((ConvertFrom-Base64Url $payload)) | ConvertFrom-Json
Assert-Equal 'iss is the app id, as a number' 5147899 $claims.iss
Assert-Equal 'iat 60 s in the past, against clock drift' 1789999940 ([long]$claims.iat)
Assert-Equal 'exp under GitHub''s 10-minute ceiling' 1790000540 ([long]$claims.exp)

$signed = [Text.Encoding]::ASCII.GetBytes("$header.$payload")
$valid = $rsa.VerifyData($signed, (ConvertFrom-Base64Url $signature),
    [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
Assert-Equal 'signature verifies with the key' $true $valid

if ($script:failures.Count -gt 0) {
    $script:failures | ForEach-Object { Write-Host "FAIL: $_" }
    Write-Host "$($script:failures.Count) failed, $script:passed passed"
    exit 1
}
Write-Host "gh-bot.tests.ps1: $script:passed passed"
