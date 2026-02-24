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

    $manifestPath = Join-Path $outputFull $ManifestFileName

    # Carry over builds from an existing manifest only if it covers the same version.
    # For a new version (or missing/corrupt manifest) we start fresh.
    # We never modify parsed objects in-place – each build entry is reconstructed from
    # scalar values to avoid PowerShell hashtable adapter quirks.
    $builds = [ordered]@{}
    if (Test-Path $manifestPath) {
        try {
            $existing = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
            if ($existing.latest.version -eq $Version) {
                foreach ($prop in $existing.latest.builds.PSObject.Properties) {
                    $builds[$prop.Name] = [ordered]@{
                        runtime        = [string]$prop.Value.runtime
                        url            = [string]$prop.Value.url
                        sha256         = [string]$prop.Value.sha256
                        sizeBytes      = [long]$prop.Value.sizeBytes
                        publishedAtUtc = [string]$prop.Value.publishedAtUtc
                    }
                }
            }
        }
        catch { }
    }

    # Add / overwrite this runtime's entry.
    $builds[$RuntimeTag] = [ordered]@{
        runtime        = $RuntimeTag
        url            = $zipUrl
        sha256         = $hash
        sizeBytes      = $size
        publishedAtUtc = $publishedAt
    }

    # Legacy top-level fields point to linux-arm when present, otherwise the current runtime.
    $legacyKey = if ($builds.Contains("linux-arm")) { "linux-arm" } else { $RuntimeTag }

    $manifest = [ordered]@{
        channel        = $Channel
        app            = $AppName
        generatedAtUtc = $publishedAt
        latest         = [ordered]@{
            publishedAtUtc = $publishedAt
            sizeBytes      = $builds[$legacyKey].sizeBytes
            url            = $builds[$legacyKey].url
            sha256         = $builds[$legacyKey].sha256
            builds         = $builds
            channel        = $Channel
            version        = $Version
        }
    }

    $manifest | ConvertTo-Json -Depth 10 | Set-Content -Path $manifestPath -Encoding UTF8

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
