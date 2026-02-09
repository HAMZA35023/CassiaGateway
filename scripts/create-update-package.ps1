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
    [bool]$StripSymbols = $true
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

    $resolvedCompression = [System.IO.Compression.CompressionLevel]::Optimal
    if ([System.Enum]::GetNames([System.IO.Compression.CompressionLevel]) -contains $CompressionLevel) {
        $resolvedCompression = [System.IO.Compression.CompressionLevel]::$CompressionLevel
    }
    else {
        Write-Warning "CompressionLevel '$CompressionLevel' not supported on this runtime. Falling back to 'Optimal'."
    }

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

    $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = (Get-Item -Path $zipPath).Length
    $publishedAt = (Get-Date).ToUniversalTime().ToString("o")

    $trimmedBase = $BaseUrl.TrimEnd("/")
    $zipUrl = "$trimmedBase/$zipName"

    $manifest = [ordered]@{
        app = $AppName
        channel = $Channel
        generatedAtUtc = $publishedAt
        latest = [ordered]@{
            version = $Version
            channel = $Channel
            url = $zipUrl
            sha256 = $hash
            sizeBytes = $size
            publishedAtUtc = $publishedAt
        }
        releases = @(
            [ordered]@{
                version = $Version
                channel = $Channel
                url = $zipUrl
                sha256 = $hash
                sizeBytes = $size
                publishedAtUtc = $publishedAt
            }
        )
    }

    $manifestPath = Join-Path $outputFull $ManifestFileName
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
