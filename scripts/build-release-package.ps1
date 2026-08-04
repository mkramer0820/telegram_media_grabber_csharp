<#
.SYNOPSIS
  Builds a clean, shareable release .zip -- safe to upload to a GitHub
  Release or hand to someone else. Never contains your real .env or
  config/channels.yaml.

.DESCRIPTION
  This is a different job from scripts/publish-release.ps1, which deploys
  a build into YOUR OWN local folder and deliberately preserves your real
  .env/config/channels.yaml/session/state there. This script instead
  produces a distributable package for OTHERS (or a GitHub Release
  upload) that must never contain your personal secrets or config at all.

  The publish output itself never contains your real .env or
  config/channels.yaml in the first place -- those only ever get copied
  into a deployment as a separate, explicit step (see
  publish-release.ps1), never bundled by the .csproj. This script relies
  on that AND double-checks it before zipping: it fails loudly if a real
  .env or config/channels.yaml is found anywhere in the publish output,
  rather than silently packaging it.

  The zip contains: the self-contained .exe, .env.example,
  config/channels.example.yaml, README.md, CONFIG.md, LICENSE.

.PARAMETER Rid
  .NET runtime identifier to publish for. Default: win-x64.

.PARAMETER Configuration
  Build configuration. Default: Release.

.PARAMETER Version
  Label used in the output zip's filename (e.g. "v1.0"). Default: a
  UTC timestamp, so repeated runs never collide.

.EXAMPLE
  ./scripts/build-release-package.ps1 -Version "v1.0"
  # -> dist/TelegramMediaGrabber-v1.0-win-x64.zip
#>
param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release",
    [string]$Version = (Get-Date -AsUTC -Format "yyyyMMdd-HHmmss")
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$cliProject = Join-Path $repoRoot "src\TelegramMediaGrabber.Cli"
$publishDir = Join-Path $cliProject "bin\$Configuration\net9.0\$Rid\publish"
$binDir = Join-Path $cliProject "bin\$Configuration\net9.0\$Rid"
$objDir = Join-Path $cliProject "obj\$Configuration\net9.0\$Rid"

# Same stale-cache precaution as publish-release.ps1: always start clean
# so the package can't silently ship without its reference docs.
foreach ($dir in @($binDir, $objDir)) {
    if (Test-Path $dir) {
        Remove-Item -Path $dir -Recurse -Force
    }
}

Write-Host "Publishing $Configuration/$Rid (clean build)..." -ForegroundColor Cyan
dotnet publish $cliProject -c $Configuration -r $Rid -p:SelfContained=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $publishDir)) {
    throw "Expected publish output not found at '$publishDir'."
}

# --- Safety check: refuse to package real secrets/config, even by accident ---
$forbiddenNames = @(".env", "channels.yaml")
$foundForbidden = Get-ChildItem -Path $publishDir -Recurse -File |
    Where-Object { $forbiddenNames -contains $_.Name }
if ($foundForbidden.Count -gt 0) {
    $names = ($foundForbidden | ForEach-Object { $_.FullName }) -join ", "
    throw "Refusing to package: found real secret/config file(s) in the publish output: $names. " `
        + "This should never happen from a plain 'dotnet publish' -- check for local contamination " `
        + "(e.g. a previous manual copy into the publish folder) before re-running."
}
Write-Host "Safety check passed: no real .env or channels.yaml in publish output." -ForegroundColor Green

$expected = @("TelegramMediaGrabber.Cli.exe", ".env.example", "README.md", "CONFIG.md", "config\channels.example.yaml")
$missing = $expected | Where-Object { -not (Test-Path (Join-Path $publishDir $_)) }
if ($missing.Count -gt 0) {
    throw "Publish output is missing expected file(s): $($missing -join ', ')."
}

$distDir = Join-Path $repoRoot "dist"
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$zipName = "TelegramMediaGrabber-$Version-$Rid.zip"
$zipPath = Join-Path $distDir $zipName
if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}

# .pdb files are debug symbols only -- leave them out of the shareable package.
$stagingDir = Join-Path $distDir "_staging"
if (Test-Path $stagingDir) {
    Remove-Item -Path $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
Copy-Item -Path (Join-Path $publishDir "*") -Destination $stagingDir -Recurse -Exclude "*.pdb"

Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $zipPath
Remove-Item -Path $stagingDir -Recurse -Force

Write-Host ""
Write-Host "Done. Package: $zipPath" -ForegroundColor Green
Write-Host "Contains: .exe, .env.example, config/channels.example.yaml, README.md, CONFIG.md, LICENSE" -ForegroundColor DarkGray
