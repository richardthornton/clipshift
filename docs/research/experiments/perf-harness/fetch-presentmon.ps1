<#
.SYNOPSIS
  Fetch the pinned Intel PresentMon CLI build into tools\ and verify its hash.

.DESCRIPTION
  The measurement instrument for issue #13's budget. Pinned by version AND hash
  so a later run cannot silently measure with a different build -- a changed
  PresentMon is a changed instrument, and the noise floor established in #18
  would no longer apply to it.

  The binary is gitignored; this script is what is committed.
#>
[CmdletBinding()]
param(
    [string]$Version = '2.5.1',
    [string]$ExpectedSha256 = '9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191',
    [string]$ToolsDir = (Join-Path $PSScriptRoot '..\..\..\..\tools')
)

$ErrorActionPreference = 'Stop'

$ToolsDir = [System.IO.Path]::GetFullPath($ToolsDir)
if (-not (Test-Path $ToolsDir)) { New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null }

$asset  = "PresentMon-$Version-x64.exe"
$target = Join-Path $ToolsDir $asset

if (Test-Path $target) {
    $have = (Get-FileHash $target -Algorithm SHA256).Hash
    if ($have -eq $ExpectedSha256) {
        Write-Host "PresentMon $Version already present and verified." -ForegroundColor Green
        return
    }
    Write-Warning "Existing $asset has hash $have, expected $ExpectedSha256. Re-downloading."
}

$url = "https://github.com/GameTechDev/PresentMon/releases/download/v$Version/$asset"
Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing

$got = (Get-FileHash $target -Algorithm SHA256).Hash
if ($got -ne $ExpectedSha256) {
    Remove-Item $target -Force
    throw "Hash mismatch for $asset. Expected $ExpectedSha256, got $got. Refusing to keep an unverified instrument."
}

Write-Host "PresentMon $Version fetched and verified: $target" -ForegroundColor Green
