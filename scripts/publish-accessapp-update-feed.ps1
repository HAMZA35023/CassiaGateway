param(
    [string]$ProjectFile = ".\AccessAPP\AccessAPP.csproj",
    [string]$Configuration = "Release",
    [string]$Runtime = "linux-arm",
    [bool]$SelfContained = $true,
    [string]$PublishDir = "",
    [switch]$SkipPublish,
    [string]$Version = "",
    [string]$WebRoot = "C:\Ampps\www\public\accessapp",
    [string]$BaseUrl = "http://prod.statistics.niko-test.nu/accessapp",
    [string]$Channel = "stable"
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")

if (-not [System.IO.Path]::IsPathRooted($ProjectFile)) {
    $ProjectFile = Join-Path $repoRoot $ProjectFile
}
$ProjectFile = (Resolve-Path $ProjectFile).Path

if ([string]::IsNullOrWhiteSpace($Version)) {
    $versionFile = Join-Path $repoRoot "AccessAPP\Version.cs"
    if (-not (Test-Path $versionFile)) {
        throw "Version not provided and version file missing: $versionFile"
    }

    $versionText = Get-Content -Path $versionFile -Raw
    $m = [regex]::Match($versionText, 'AppVersion\s*=\s*"([^"]+)"')
    if (-not $m.Success) {
        throw "Could not parse AppVersion from $versionFile"
    }

    $Version = $m.Groups[1].Value
}

if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $repoRoot "temp_build\publish_accessapp_update"
}

if (-not $SkipPublish) {
    if (Test-Path $PublishDir) {
        Remove-Item -Path $PublishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

    $selfContainedArg = if ($SelfContained) { "--self-contained" } else { "--no-self-contained" }
    $publishArgs = @(
        "publish"
        "`"$ProjectFile`""
        "-c"
        $Configuration
        "-r"
        $Runtime
        $selfContainedArg
        "--output"
        "`"$PublishDir`""
    )

    Write-Host "Publishing AccessAPP..."
    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}
elseif (-not (Test-Path $PublishDir)) {
    throw "SkipPublish was set but PublishDir does not exist: $PublishDir"
}

if (-not (Test-Path $WebRoot)) {
    New-Item -ItemType Directory -Path $WebRoot -Force | Out-Null
}

$packScript = Join-Path $scriptDir "create-update-package.ps1"
if (-not (Test-Path $packScript)) {
    throw "Missing helper script: $packScript"
}

Write-Host "Creating zip + manifest in web root: $WebRoot"
& $packScript `
    -PublishDir $PublishDir `
    -Version $Version `
    -BaseUrl $BaseUrl `
    -OutputDir $WebRoot `
    -AppName "AccessAPP" `
    -RuntimeTag $Runtime `
    -Channel $Channel `
    -ManifestFileName "manifest.json"

if ($LASTEXITCODE -ne 0) {
    throw "create-update-package.ps1 failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "Update feed published successfully."
Write-Host "Version: $Version"
Write-Host "Manifest: $(Join-Path $WebRoot 'manifest.json')"
