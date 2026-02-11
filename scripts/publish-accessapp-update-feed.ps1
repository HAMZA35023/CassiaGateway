param(
    [string]$ProjectFile = "AccessAPP\AccessAPP.csproj",
    [string]$Configuration = "Release",
    [string]$Runtime = "linux-arm",
    [bool]$SelfContained = $true,
    [switch]$SkipPublish,
    [switch]$SkipClientBuild,
    [string]$Version = "",
    [string]$WebRoot = "C:\Ampps\www\public\accessapp",
    [string]$BaseUrl = "http://prod.statistics.niko-test.nu/accessapp",
    [string]$Channel = "stable",
    [string]$ManifestFileName = "manifest.json",
    [ValidateSet("Optimal", "Fastest", "NoCompression", "SmallestSize")]
    [string]$CompressionLevel = "Optimal",
    [bool]$StripSymbols = $true,
    [string]$SevenZipPath = "",
    [switch]$AutoBuildAllChannels,
    [hashtable]$BranchChannelMap = @{
        "PROD-STABLE"  = "stable"
        "PROD-TEST"    = "test"
        "PROD-DEVELOP" = "develop"
    },
    [string]$ShaStoreDir = "",
    [string]$WorktreeRoot = "",
    [string]$VersionFile = "AccessAPP\Version.cs",
    [string]$NotifyUrlTemplate = "",
    [switch]$NotifyOnNoChanges
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir "..")).Path

function Write-Log {
    param([string]$Message)
    Write-Host ("[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message)
}

function Resolve-FullPath {
    param(
        [string]$Path,
        [string]$Base
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $Base $Path))
}

function Get-VersionFromSource {
    param(
        [string]$RepoRootPath,
        [string]$VersionFilePath,
        [string]$ProvidedVersion
    )

    $resolvedVersionFile = Resolve-FullPath -Path $VersionFilePath -Base $RepoRootPath
    if (-not (Test-Path $resolvedVersionFile)) {
        throw "Version source file missing: $resolvedVersionFile"
    }

    $versionText = Get-Content -Path $resolvedVersionFile -Raw
    $match = [regex]::Match($versionText, 'AppVersion\s*=\s*"([^"]+)"')
    if (-not $match.Success) {
        throw "Could not parse AppVersion from $resolvedVersionFile"
    }

    $versionFromSource = $match.Groups[1].Value
    if ([string]::IsNullOrWhiteSpace($ProvidedVersion)) {
        return $versionFromSource
    }

    if ($ProvidedVersion -ne $versionFromSource) {
        throw "Provided -Version '$ProvidedVersion' does not match '$resolvedVersionFile' ('$versionFromSource')."
    }

    return $ProvidedVersion
}

function Invoke-PublishAndPackage {
    param(
        [string]$RepoRootPath,
        [string]$TargetChannel
    )

    $resolvedProject = Resolve-FullPath -Path $ProjectFile -Base $RepoRootPath
    if (-not (Test-Path $resolvedProject)) {
        throw "Project file not found: $resolvedProject"
    }

    $effectiveVersion = Get-VersionFromSource -RepoRootPath $RepoRootPath -VersionFilePath $VersionFile -ProvidedVersion $Version
    $publishDir = Join-Path $RepoRootPath (Join-Path "temp_build\publish_accessapp_update" $TargetChannel)
    $channelOutputDir = Join-Path $WebRoot $TargetChannel
    $channelBaseUrl = ($BaseUrl.TrimEnd("/") + "/" + $TargetChannel)

    if (-not $SkipPublish) {
        if (Test-Path $publishDir) {
            Remove-Item -Path $publishDir -Recurse -Force
        }
        New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

        $selfContainedArg = if ($SelfContained) { "--self-contained" } else { "--no-self-contained" }
        $publishArgs = @(
            "publish"
            $resolvedProject
            "-c"
            $Configuration
            "-r"
            $Runtime
            $selfContainedArg
            "--output"
            $publishDir
        )
        if ($SkipClientBuild) {
            $publishArgs += "-p:SkipClientAppBuild=true"
        }

        Write-Log "Publishing AccessAPP for channel '$TargetChannel'..."
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE"
        }
    }
    elseif (-not (Test-Path $publishDir)) {
        throw "SkipPublish was set but PublishDir does not exist: $publishDir"
    }

    if (-not (Test-Path $channelOutputDir)) {
        New-Item -ItemType Directory -Path $channelOutputDir -Force | Out-Null
    }

    $packScript = Join-Path $RepoRootPath "scripts\create-update-package.ps1"
    if (-not (Test-Path $packScript)) {
        throw "Missing helper script: $packScript"
    }

    Write-Log "Creating zip + manifest for channel '$TargetChannel' in: $channelOutputDir"
    & $packScript `
        -PublishDir $publishDir `
        -Version $effectiveVersion `
        -BaseUrl $channelBaseUrl `
        -OutputDir $channelOutputDir `
        -AppName "AccessAPP" `
        -RuntimeTag $Runtime `
        -Channel $TargetChannel `
        -ManifestFileName $ManifestFileName `
        -CompressionLevel $CompressionLevel `
        -StripSymbols:$StripSymbols `
        -SevenZipPath $SevenZipPath

    if ($LASTEXITCODE -ne 0) {
        throw "create-update-package.ps1 failed with exit code $LASTEXITCODE"
    }

    Write-Log "Published channel '$TargetChannel' version '$effectiveVersion'."
    return $effectiveVersion
}

function Send-CompletionNotification {
    param([string]$Message)

    if ([string]::IsNullOrWhiteSpace($NotifyUrlTemplate)) {
        return
    }

    $encoded = [Uri]::EscapeDataString($Message)
    $url = $NotifyUrlTemplate.Replace("{message}", $encoded)
    try {
        Invoke-RestMethod -Uri $url -Method Get | Out-Null
        Write-Log "Completion notification sent."
    }
    catch {
        Write-Warning "Failed to send completion notification: $_"
    }
}

if (-not $AutoBuildAllChannels) {
    $resultVersion = Invoke-PublishAndPackage -RepoRootPath $repoRoot -TargetChannel $Channel
    Write-Host ""
    Write-Log "Update feed published successfully."
    Write-Log "Version: $resultVersion"
    Write-Log "Manifest: $(Join-Path (Join-Path $WebRoot $Channel) $ManifestFileName)"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ShaStoreDir)) {
    $ShaStoreDir = Join-Path $repoRoot "temp_build\branch-shas"
}
if ([string]::IsNullOrWhiteSpace($WorktreeRoot)) {
    $WorktreeRoot = Join-Path $repoRoot "temp_build\branch-worktrees"
}

New-Item -ItemType Directory -Path $ShaStoreDir -Force | Out-Null
New-Item -ItemType Directory -Path $WorktreeRoot -Force | Out-Null

Push-Location $repoRoot
try {
    Write-Log "Fetching latest remote refs..."
    & git fetch origin --prune
    if ($LASTEXITCODE -ne 0) {
        throw "git fetch origin failed with exit code $LASTEXITCODE"
    }

    & git worktree prune
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "git worktree prune returned exit code $LASTEXITCODE"
    }

    $built = New-Object System.Collections.Generic.List[string]
    $failed = New-Object System.Collections.Generic.List[string]
    $skipped = New-Object System.Collections.Generic.List[string]

    foreach ($entry in $BranchChannelMap.GetEnumerator() | Sort-Object Name) {
        $branch = [string]$entry.Key
        $targetChannel = [string]$entry.Value
        $remoteRef = "origin/$branch"
        $shaFile = Join-Path $ShaStoreDir ($branch + ".sha")

        $oldSha = ""
        if (Test-Path $shaFile) {
            try {
                $oldSha = (Get-Content -Path $shaFile -Raw).Trim()
            }
            catch {
                $oldSha = ""
            }
        }

        $newSha = ""
        try {
            $newSha = (& git rev-parse $remoteRef).Trim()
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($newSha)) {
                throw "Unable to resolve $remoteRef"
            }
        }
        catch {
            Write-Warning "Remote branch '$remoteRef' not found; skipping."
            $failed.Add("$branch (missing remote)") | Out-Null
            continue
        }

        if ($newSha -eq $oldSha) {
            Write-Log "No new commit on '$branch' for channel '$targetChannel'; skipping."
            $skipped.Add("$branch->$targetChannel") | Out-Null
            continue
        }

        Write-Log "New commit detected on '$branch' ($newSha). Building channel '$targetChannel'..."
        $safeBranchName = ($branch -replace '[^A-Za-z0-9._-]', '_')
        $worktreePath = Join-Path $WorktreeRoot $safeBranchName

        if (Test-Path $worktreePath) {
            & git worktree remove --force $worktreePath | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Could not remove stale worktree '$worktreePath'."
            }
            if (Test-Path $worktreePath) {
                Remove-Item -Path $worktreePath -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        try {
            & git worktree add --force --detach $worktreePath $remoteRef
            if ($LASTEXITCODE -ne 0) {
                throw "git worktree add failed for '$branch'"
            }

            $builtVersion = Invoke-PublishAndPackage -RepoRootPath $worktreePath -TargetChannel $targetChannel
            $newSha | Set-Content -Path $shaFile -Encoding ascii -NoNewline
            $built.Add("$branch->$targetChannel ($builtVersion)") | Out-Null
            Write-Log "Build completed for '$branch' -> '$targetChannel'."
        }
        catch {
            $failed.Add("$branch->$targetChannel") | Out-Null
            Write-Warning "Build failed for '$branch' -> '$targetChannel': $_"
        }
        finally {
            if (Test-Path $worktreePath) {
                & git worktree remove --force $worktreePath | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "Failed to remove worktree '$worktreePath'."
                }
            }
        }
    }

    $summary = @(
        "Cassia AccessAPP channel build completed.",
        "Built: $($built.Count)",
        "Skipped: $($skipped.Count)",
        "Failed: $($failed.Count)"
    ) -join " "

    Write-Log $summary
    if ($built.Count -gt 0) {
        Write-Log ("Built branches: " + ($built -join ", "))
    }
    if ($failed.Count -gt 0) {
        Write-Warning ("Failed branches: " + ($failed -join ", "))
    }

    if (($built.Count -gt 0) -or ($failed.Count -gt 0) -or $NotifyOnNoChanges) {
        Send-CompletionNotification -Message $summary
    }

    if ($failed.Count -gt 0) {
        exit 1
    }
}
finally {
    Pop-Location
}
