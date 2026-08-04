<#
.SYNOPSIS
  Builds a self-contained single-file release and deploys it to a target
  folder WITHOUT ever touching that folder's live data.

.DESCRIPTION
  `dotnet publish` always produces a fresh output folder -- if you just
  copy that wholesale over an existing deployment, you silently overwrite
  data/downloader.session, data/state.db, .env, and config/channels.yaml
  every time, forcing a fresh Telegram login and losing all download/
  upload history. This script copies only the app binary and reference
  docs; it never overwrites anything that represents live state or a
  user's own config.

  What ALWAYS gets updated in -DeployDir:
    - TelegramMediaGrabber.Cli.exe (and its .pdb, if present)
    - .env.example, config/channels.example.yaml, README.md, CONFIG.md
      (reference material -- safe to refresh every release)

  What is SEEDED only if missing (never overwritten if already there):
    - .env
    - config/channels.yaml

  What is NEVER touched, ever, even if present in the publish output:
    - data/  (session file, state.db)
    - logs/
    - downloads/
    - uploads/

.PARAMETER DeployDir
  Where to deploy the published build. Created if it doesn't exist.

.PARAMETER Rid
  .NET runtime identifier to publish for. Default: win-x64.

.PARAMETER Configuration
  Build configuration. Default: Release.

.EXAMPLE
  ./scripts/publish-release.ps1 -DeployDir "D:\wherever\you\keep\this"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$DeployDir,

    [string]$Rid = "win-x64",

    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$cliProject = Join-Path $repoRoot "src\TelegramMediaGrabber.Cli"
$publishDir = Join-Path $cliProject "bin\$Configuration\net9.0\$Rid\publish"

# A stale incremental-build cache has been observed to silently drop the
# bundled .env.example/config/channels.example.yaml/README.md/CONFIG.md
# from the publish output (no error -- the exe still builds fine, it's
# just missing the reference files). Always start from a clean bin/obj
# for this configuration+RID so a deploy can never silently ship an
# incomplete publish folder.
$binDir = Join-Path $cliProject "bin\$Configuration\net9.0\$Rid"
$objDir = Join-Path $cliProject "obj\$Configuration\net9.0\$Rid"
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

# Defense in depth against the same silent-drop failure mode the clean
# build above works around: fail loudly rather than deploy an incomplete
# folder if any expected reference file is still missing.
$expectedInPublish = @(
    "TelegramMediaGrabber.Cli.exe",
    ".env.example",
    "README.md",
    "CONFIG.md",
    "LICENSE",
    "config\channels.example.yaml"
)
$missing = $expectedInPublish | Where-Object { -not (Test-Path (Join-Path $publishDir $_)) }
if ($missing.Count -gt 0) {
    throw "Publish output at '$publishDir' is missing expected file(s): $($missing -join ', '). Not deploying an incomplete build."
}

if (-not (Test-Path $DeployDir)) {
    Write-Host "Creating deploy directory: $DeployDir" -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $DeployDir -Force | Out-Null
}

# --- Always refresh: the app binary + reference docs (never live data) ---
$alwaysUpdate = @(
    "TelegramMediaGrabber.Cli.exe",
    "TelegramMediaGrabber.Cli.pdb",
    ".env.example",
    "README.md",
    "CONFIG.md",
    "LICENSE"
)
foreach ($name in $alwaysUpdate) {
    $source = Join-Path $publishDir $name
    if (Test-Path $source) {
        Copy-Item -Path $source -Destination $DeployDir -Force
        Write-Host "  updated: $name"
    }
}

# config/ subfolder: channels.example.yaml always refreshes; channels.yaml
# (the user's real config) is only seeded if it doesn't exist yet.
$deployConfigDir = Join-Path $DeployDir "config"
New-Item -ItemType Directory -Path $deployConfigDir -Force | Out-Null

$exampleConfigSource = Join-Path $publishDir "config\channels.example.yaml"
if (Test-Path $exampleConfigSource) {
    Copy-Item -Path $exampleConfigSource -Destination $deployConfigDir -Force
    Write-Host "  updated: config\channels.example.yaml"
}

$realConfigDest = Join-Path $deployConfigDir "channels.yaml"
if (-not (Test-Path $realConfigDest)) {
    $exampleAsSeed = Join-Path $publishDir "config\channels.example.yaml"
    if (Test-Path $exampleAsSeed) {
        Copy-Item -Path $exampleAsSeed -Destination $realConfigDest
        Write-Host "  seeded:  config\channels.yaml (copy from example -- edit it!)" -ForegroundColor Yellow
    }
} else {
    Write-Host "  kept:    config\channels.yaml (already exists, not touched)" -ForegroundColor DarkGray
}

# .env: only seeded if missing, never overwritten.
$envDest = Join-Path $DeployDir ".env"
if (-not (Test-Path $envDest)) {
    $envExampleSource = Join-Path $publishDir ".env.example"
    if (Test-Path $envExampleSource) {
        Copy-Item -Path $envExampleSource -Destination $envDest
        Write-Host "  seeded:  .env (copy from .env.example -- fill in your credentials!)" -ForegroundColor Yellow
    }
} else {
    Write-Host "  kept:    .env (already exists, not touched)" -ForegroundColor DarkGray
}

# data/, logs/, downloads/, uploads/ are deliberately never referenced
# here at all -- nothing in this script ever creates, copies into, or
# deletes those paths, so whatever is already in DeployDir survives
# untouched no matter how many times this runs.
foreach ($preserved in @("data", "logs", "downloads", "uploads")) {
    $path = Join-Path $DeployDir $preserved
    if (Test-Path $path) {
        Write-Host "  kept:    $preserved\ (untouched)" -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "Done. Deployed to: $DeployDir" -ForegroundColor Green
