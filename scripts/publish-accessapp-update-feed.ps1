param(
    [string]$ProjectFile = "AccessAPP\AccessAPP.csproj",
    [string]$Configuration = "Release",
    [string]$Runtime = "linux-arm",
    [string[]]$Runtimes = @("linux-arm", "linux-x64"),
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
    [switch]$EnableSmsNotification,
    [string]$SmsUsername = "nikodk",
    [string]$SmsApiKey = "cadd2536-45bd-448b-8599-09f29e7d14db",
    [string]$SmsRecipient = "4528110897",
    [string]$SmsSender = "CS-Build",
    [switch]$SmsSummaryOnly,
    [switch]$NotifyOnNoChanges
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir "..")).Path
$scriptExitCode = 0

$repoRootBytes = [System.Text.Encoding]::UTF8.GetBytes($repoRoot.ToLowerInvariant())
$shaHashDataMethod = [System.Security.Cryptography.SHA256].GetMethod("HashData", [Type[]]@([byte[]]))
if ($shaHashDataMethod) {
    $repoRootHash = [System.Security.Cryptography.SHA256]::HashData($repoRootBytes)
}
else {
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $repoRootHash = $sha256.ComputeHash($repoRootBytes)
    }
    finally {
        $sha256.Dispose()
    }
}
$repoRootHashHex = ([System.BitConverter]::ToString($repoRootHash)).Replace("-", "")
$singleInstanceMutexName = "Global\CassiaGateway.PublishAccessApp.$repoRootHashHex"
$singleInstanceMutex = New-Object System.Threading.Mutex($false, $singleInstanceMutexName)

if (-not $singleInstanceMutex.WaitOne(0, $false)) {
    Write-Error "Another instance of publish-accessapp-update-feed.ps1 is already running for this repository."
    $singleInstanceMutex.Dispose()
    exit 2
}

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

    $channelOutputDir = Join-Path $WebRoot $TargetChannel
    $channelBaseUrl = ($BaseUrl.TrimEnd("/") + "/" + $TargetChannel)

    if (-not (Test-Path $channelOutputDir)) {
        New-Item -ItemType Directory -Path $channelOutputDir -Force | Out-Null
    }

    $packScript = Join-Path $RepoRootPath "scripts\create-update-package.ps1"
    if (-not (Test-Path $packScript)) {
        throw "Missing helper script: $packScript"
    }

    # Default: publish BOTH linux-arm and linux-x64 (packaged as linux-64).
    $effectiveRuntimes = @()
    if ($PSBoundParameters.ContainsKey("Runtimes") -and $Runtimes -and $Runtimes.Count -gt 0) {
        $effectiveRuntimes = $Runtimes
    }
    elseif ($PSBoundParameters.ContainsKey("Runtime") -and -not [string]::IsNullOrWhiteSpace($Runtime) -and $Runtime -ne "linux-arm") {
        # Backwards compatibility: if someone explicitly set -Runtime, publish only that runtime.
        $effectiveRuntimes = @($Runtime)
    }
    else {
        $effectiveRuntimes = @("linux-arm", "linux-x64")
    }

    $runtimeTagMap = @{
        "linux-arm" = "linux-arm"
        "linux-x64" = "linux-64"
        "linux-64"  = "linux-64"
    }

    foreach ($rid in $effectiveRuntimes) {
        $runtimeTag = $runtimeTagMap[$rid]
        if (-not $runtimeTag) { $runtimeTag = $rid }

        $publishDir = Join-Path $RepoRootPath (Join-Path "temp_build\publish_accessapp_update" (Join-Path $TargetChannel $runtimeTag))

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
                $rid
                $selfContainedArg
                "--output"
                $publishDir
            )
            if ($SkipClientBuild) {
                $publishArgs += "-p:SkipClientAppBuild=true"
            }

            Write-Log "Publishing AccessAPP for channel '$TargetChannel' runtime '$rid'..."
            & dotnet @publishArgs
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet publish failed with exit code $LASTEXITCODE"
            }
        }
        elseif (-not (Test-Path $publishDir)) {
            throw "SkipPublish was set but PublishDir does not exist: $publishDir"
        }

        Write-Log "Creating zip + manifest for channel '$TargetChannel' runtime '$runtimeTag' in: $channelOutputDir"
        & $packScript `
            -PublishDir $publishDir `
            -Version $effectiveVersion `
            -BaseUrl $channelBaseUrl `
            -OutputDir $channelOutputDir `
            -AppName "AccessAPP" `
            -RuntimeTag $runtimeTag `
            -Channel $TargetChannel `
            -ManifestFileName $ManifestFileName `
            -CompressionLevel $CompressionLevel `
            -StripSymbols:$StripSymbols `
            -SevenZipPath $SevenZipPath

        if ($LASTEXITCODE -ne 0) {
            throw "create-update-package.ps1 failed with exit code $LASTEXITCODE"
        }
    }

    Write-Log "Published channel '$TargetChannel' version '$effectiveVersion' (runtimes: $($effectiveRuntimes -join ', '))."
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

function Send-SmsNotification {
    param (
        [string]$Message
    )

    if (-not $EnableSmsNotification) {
        return
    }

    if ([string]::IsNullOrWhiteSpace($SmsUsername) -or
        [string]::IsNullOrWhiteSpace($SmsApiKey) -or
        [string]::IsNullOrWhiteSpace($SmsRecipient)) {

        Write-Warning "SMS notification is enabled, but SmsUsername/SmsApiKey/SmsRecipient is missing. Skipping SMS."
        return
    }

    $smsMsg = $Message
    if ($smsMsg.Length -gt 160) {
        $smsMsg = $smsMsg.Substring(0, 160)
    }

    $params = @{
        username  = $SmsUsername
        apikey    = $SmsApiKey
        flash     = "0"
        recipient = $SmsRecipient
        message   = $smsMsg
        from      = $SmsSender
    }

    $pairs = @()
    foreach ($key in $params.Keys) {
        $pairs += (
            [Uri]::EscapeDataString($key) + "=" +
            [Uri]::EscapeDataString([string]$params[$key])
        )
    }

    $qs = $pairs -join "&"
    $url = "https://www.cpsms.dk/sms/?$qs"

    try {
        Invoke-RestMethod -Uri $url -Method Get | Out-Null
        Write-Log "SMS notification sent."
    }
    catch {
        Write-Warning "Failed to send SMS notification: $_"
    }
}


function Remove-Worktree {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return
    }

    & git -c core.longpaths=true worktree remove --force -- $Path | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "git worktree remove failed for '$Path'. Trying filesystem cleanup."
    }

    if (-not (Test-Path $Path)) {
        return
    }

    # Always prune metadata first so git forgets already-broken worktree records.
    & git worktree prune | Out-Null

    try {
        Remove-Item -LiteralPath ("\\?\{0}" -f $Path) -Recurse -Force -ErrorAction Stop
        return
    }
    catch {}

    try {
        Remove-Item -Path $Path -Recurse -Force -ErrorAction Stop
        return
    }
    catch {}

    # Final fallback for Windows long-path trees.
    & cmd /c "rmdir /s /q ""\\?\$Path""" | Out-Null
    if (Test-Path $Path) {
        Write-Warning "Failed to remove worktree '$Path'."
    }
}

function Invoke-StartupWorktreeCleanup {
    param([string]$RootPath)

    if (-not (Test-Path $RootPath)) {
        return
    }

    Write-Log "Cleaning stale worktrees in '$RootPath'..."
    Get-ChildItem -Path $RootPath -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Worktree -Path $_.FullName
    }
}

function Get-WorktreeRootsToClean {
    param(
        [string]$PrimaryRoot,
        [string]$RepoRootPath
    )

    $roots = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($PrimaryRoot)) {
        $roots.Add($PrimaryRoot) | Out-Null
    }

    # Legacy location used by previous versions of this script.
    $legacyRoot = Join-Path $RepoRootPath "temp_build\branch-worktrees"
    if ($legacyRoot -ne $PrimaryRoot) {
        $roots.Add($legacyRoot) | Out-Null
    }

    return $roots
}

try {
    if (-not $AutoBuildAllChannels) {
        $resultVersion = Invoke-PublishAndPackage -RepoRootPath $repoRoot -TargetChannel $Channel
        Write-Host ""
        Write-Log "Update feed published successfully."
        Write-Log "Version: $resultVersion"
        Write-Log "Manifest: $(Join-Path (Join-Path $WebRoot $Channel) $ManifestFileName)"
        Send-SmsNotification -Message ("Build {0} {1} published" -f $Channel, $resultVersion)
        Send-CompletionNotification -Message ("Build {0} {1} published" -f $Channel, $resultVersion)
        $scriptExitCode = 0
    }
    else {
        if ([string]::IsNullOrWhiteSpace($ShaStoreDir)) {
            $ShaStoreDir = Join-Path $repoRoot "temp_build\branch-shas"
        }
        if ([string]::IsNullOrWhiteSpace($WorktreeRoot)) {
            $drive = if ([string]::IsNullOrWhiteSpace($env:SystemDrive)) { "C:" } else { $env:SystemDrive }
            $WorktreeRoot = Join-Path $drive "cgwt"
        }

        if ($BranchChannelMap.Count -eq 0) {
            throw "BranchChannelMap is empty. Remove -BranchChannelMap to use defaults, or pass mappings such as @{ 'PROD-STABLE'='stable'; 'PROD-TEST'='test'; 'PROD-DEVELOP'='develop' }."
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
            foreach ($root in (Get-WorktreeRootsToClean -PrimaryRoot $WorktreeRoot -RepoRootPath $repoRoot)) {
                Invoke-StartupWorktreeCleanup -RootPath $root
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
                    Remove-Worktree -Path $worktreePath
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
                    if (-not $SmsSummaryOnly) {
                        Send-SmsNotification -Message ("Build {0} {1} published" -f $branch, $builtVersion)
                    }
                }
                catch {
                    $failed.Add("$branch->$targetChannel") | Out-Null
                    Write-Warning "Build failed for '$branch' -> '$targetChannel': $_"
                }
                finally {
                    if (Test-Path $worktreePath) {
                        Remove-Worktree -Path $worktreePath
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
                Send-SmsNotification -Message $summary
            }

            if ($failed.Count -gt 0) {
                $scriptExitCode = 1
            }
        }
        finally {
            Pop-Location
        }
    }
}
finally {
    if ($singleInstanceMutex) {
        try {
            $singleInstanceMutex.ReleaseMutex()
        }
        catch {}
        $singleInstanceMutex.Dispose()
    }
}

exit $scriptExitCode
