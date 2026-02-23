param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [string]$OutputDir = ".\update-artifacts",
    [string]$AppName = "AccessAPP",
    [string]$RuntimeTag = "linux-arm",
    [string]$Channel = "stable",
    [string]$ManifestFileName = "manifest.json",
    [ValidateSet("Optimal", "Fastest", "NoCompression", "SmallestSize")]
    [string]$CompressionLevel = "Optimal",
    [bool]$StripSymbols = $true,
    [string]$SevenZipPath = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $PublishDir)) {
    throw "PublishDir not found: $PublishDir"
}

$publishFull = (Resolve-Path $PublishDir).Path
$outputFull = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $outputFull -Force | Out-Null

$zipName = "$AppName-$Version-$RuntimeTag.zip"
$zipPath = Join-Path $outputFull $zipName

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("accessapp_pkg_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    Copy-Item -Path (Join-Path $publishFull "*") -Destination $tempRoot -Recurse -Force
    Set-Content -Path (Join-Path $tempRoot "version.txt") -Value $Version -NoNewline

    if ($StripSymbols) {
        Get-ChildItem -Path $tempRoot -Recurse -File -Include *.pdb, *.dbg -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path $zipPath) {
        Remove-Item -Path $zipPath -Force
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $enumNames = [System.Enum]::GetNames([System.IO.Compression.CompressionLevel])
    $resolvedCompression = [System.IO.Compression.CompressionLevel]::Optimal
    $use7Zip = $false

    if ($enumNames -contains $CompressionLevel) {
        $resolvedCompression = [System.IO.Compression.CompressionLevel]::$CompressionLevel
    }
    elseif ($CompressionLevel -eq "SmallestSize") {
        $candidate = $SevenZipPath
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            foreach ($name in @("7z", "7za", "7zz")) {
                $cmd = Get-Command $name -ErrorAction SilentlyContinue
                if ($cmd) {
                    $candidate = $cmd.Source
                    break
                }
            }
        }
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            $common7z = @(
                "C:\Program Files\7-Zip\7z.exe",
                "C:\Program Files (x86)\7-Zip\7z.exe"
            )
            foreach ($p in $common7z) {
                if (Test-Path $p) {
                    $candidate = $p
                    break
                }
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
            $use7Zip = $true
            $SevenZipPath = $candidate
            Write-Host "Using 7-Zip for SmallestSize compression: $SevenZipPath"
        }
        else {
            Write-Warning "CompressionLevel 'SmallestSize' not supported on this runtime and 7-Zip not found. Falling back to 'Optimal'."
        }
    }
    else {
        Write-Warning "CompressionLevel '$CompressionLevel' not supported on this runtime. Falling back to 'Optimal'."
    }

    if ($use7Zip) {
        Push-Location $tempRoot
        try {
            $zipTarget = [System.IO.Path]::GetFullPath($zipPath)
            # Use standard ZIP + Deflate for maximum compatibility with Linux/.NET unzip.
            & $SevenZipPath a -tzip -mm=Deflate -mx=9 -mfb=258 -mpass=15 $zipTarget ".\*" | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "7-Zip failed with exit code $LASTEXITCODE"
            }
        }
        finally {
            Pop-Location
        }
    }
    else {
        $zipStream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::CreateNew)
        try {
            $archive = [System.IO.Compression.ZipArchive]::new($zipStream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
            try {
                Get-ChildItem -Path $tempRoot -Recurse -File | ForEach-Object {
                    $entryName = $_.FullName.Substring($tempRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entryName, $resolvedCompression) | Out-Null
                }
            }
            finally {
                $archive.Dispose()
            }
        }
        finally {
            $zipStream.Dispose()
        }
    }

    $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = (Get-Item -Path $zipPath).Length
    $publishedAt = (Get-Date).ToUniversalTime().ToString("o")

    $trimmedBase = $BaseUrl.TrimEnd("/")
    $zipUrl = "$trimmedBase/$zipName"

    # IMPORTANT: Use plain hashtables (not [ordered]) to avoid duplicate-key
    # exceptions on some PowerShell/.NET combinations when keys differ only by case
    # (e.g. legacy manifests created with 'Version' vs 'version').
    $artifact = @{
            runtime = $RuntimeTag
            url = $zipUrl
            sha256 = $hash
            sizeBytes = $size
            publishedAtUtc = $publishedAt
        }

    $manifestPath = Join-Path $outputFull $ManifestFileName

    function ConvertTo-HashtableDeep {
        param([object]$Obj)
        if ($null -eq $Obj) { return $null }
        if ($Obj -is [System.Collections.IDictionary]) {
            $h = @{}
            foreach ($k in $Obj.Keys) { $h[$k] = ConvertTo-HashtableDeep $Obj[$k] }
            return $h
        }
        if ($Obj -is [System.Collections.IEnumerable] -and -not ($Obj -is [string])) {
            $list = @()
            foreach ($item in $Obj) { $list += ,(ConvertTo-HashtableDeep $item) }
            return $list
        }
        if ($Obj -is [pscustomobject]) {
            $h = @{}
            foreach ($p in $Obj.PSObject.Properties) { $h[$p.Name] = ConvertTo-HashtableDeep $p.Value }
            return $h
        }
        return $Obj
    }

    $manifest = $null
    if (Test-Path $manifestPath) {
        try {
            $existingObj = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
            $manifest = ConvertTo-HashtableDeep $existingObj
        }
        catch {
            Write-Warning "Existing manifest could not be parsed. It will be overwritten: $manifestPath"
            $manifest = $null
        }
    }

    if (-not $manifest) {
        $manifest = @{
            app = $AppName
            channel = $Channel
            generatedAtUtc = $publishedAt
            latest = @{
                version = $Version
                channel = $Channel
                # Legacy fields (for older updaters). We keep linux-arm here when available.
                url = $zipUrl
                sha256 = $hash
                sizeBytes = $size
                publishedAtUtc = $publishedAt
                builds = @{}
            }
            releases = @()
        }
    }

    # Ensure latest exists
    if (-not $manifest.Contains("latest") -or -not $manifest.latest) {
        $manifest.latest = @{
            version = $Version
            channel = $Channel
            builds = @{}
        }
    }

    # Normalize legacy manifest into builds (assume linux-arm when only legacy fields exist)
    if (-not $manifest.latest.Contains("builds") -or -not $manifest.latest.builds) {
        $manifest.latest.builds = @{}
    }
    if ($manifest.latest.Contains("url") -and -not $manifest.latest.builds.Contains("linux-arm")) {
        $manifest.latest.builds["linux-arm"] = @{
            runtime = "linux-arm"
            url = $manifest.latest.url
            sha256 = $manifest.latest.sha256
            sizeBytes = $manifest.latest.sizeBytes
            publishedAtUtc = $manifest.latest.publishedAtUtc
        }
    }

    $manifest.app = $AppName
    $manifest.channel = $Channel
    $manifest.generatedAtUtc = $publishedAt

    # Update latest
    $manifest.latest.version = $Version
    $manifest.latest.channel = $Channel
    $manifest.latest.publishedAtUtc = $publishedAt
    $manifest.latest.builds[$RuntimeTag] = $artifact

    # Keep legacy top-level latest fields pointing to linux-arm if available, otherwise current runtime.
    $legacyKey = "linux-arm"
    if (-not $manifest.latest.builds.Contains($legacyKey)) {
        $legacyKey = $RuntimeTag
    }
    $legacy = $manifest.latest.builds[$legacyKey]
    $manifest.latest.url = $legacy.url
    $manifest.latest.sha256 = $legacy.sha256
    $manifest.latest.sizeBytes = $legacy.sizeBytes

    # Update releases
    if (-not $manifest.Contains("releases") -or -not $manifest.releases) {
        $manifest.releases = @()
    }

    $release = $null
    foreach ($r in $manifest.releases) {
        if ($r.version -eq $Version) { $release = $r; break }
    }
    if (-not $release) {
        $release = @{
            version = $Version
            channel = $Channel
            publishedAtUtc = $publishedAt
            url = $zipUrl
            sha256 = $hash
            sizeBytes = $size
            builds = @{}
        }
        $manifest.releases += $release
    }

    if (-not $release.Contains("builds") -or -not $release.builds) { $release.builds = @{} }
    if ($release.Contains("url") -and -not $release.builds.Contains("linux-arm")) {
        $release.builds["linux-arm"] = @{
            runtime = "linux-arm"
            url = $release.url
            sha256 = $release.sha256
            sizeBytes = $release.sizeBytes
            publishedAtUtc = $release.publishedAtUtc
        }
    }
    $release.channel = $Channel
    $release.publishedAtUtc = $publishedAt
    $release.builds[$RuntimeTag] = $artifact

    # Keep legacy fields for the release as linux-arm if available.
    $legacyKey2 = "linux-arm"
    if (-not $release.builds.Contains($legacyKey2)) { $legacyKey2 = $RuntimeTag }
    $legacy2 = $release.builds[$legacyKey2]
    $release.url = $legacy2.url
    $release.sha256 = $legacy2.sha256
    $release.sizeBytes = $legacy2.sizeBytes

    # Sort releases newest first
    $manifest.releases = @(
        $manifest.releases | Sort-Object -Property @{Expression = { $_.version }; Descending = $true}, @{Expression = { $_.publishedAtUtc }; Descending = $true}
    )

    $manifest | ConvertTo-Json -Depth 20 | Set-Content -Path $manifestPath -Encoding UTF8

    Write-Host "Update package created:"
    Write-Host "  Zip      : $zipPath"
    Write-Host "  Manifest : $manifestPath"
    Write-Host "  Version  : $Version"
    Write-Host "  Compress : $CompressionLevel"
    Write-Host "  Symbols  : $(if ($StripSymbols) { 'stripped' } else { 'kept' })"
    Write-Host "  SHA256   : $hash"
    Write-Host "  URL      : $zipUrl"
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item -Path $tempRoot -Recurse -Force
    }
}
