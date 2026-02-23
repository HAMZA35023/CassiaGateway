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

function New-BuildInfo {
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [Parameter(Mandatory=$true)][string]$Sha256,
        [Parameter(Mandatory=$true)][long]$SizeBytes,
        [Parameter(Mandatory=$true)][string]$PublishedAtUtc
    )
    return @{
        url = $Url
        sha256 = $Sha256
        sizeBytes = $SizeBytes
        publishedAtUtc = $PublishedAtUtc
    }
}

function Ensure-Hashtable([object]$obj) {
    if ($null -eq $obj) { return @{} }
    if ($obj -is [hashtable]) { return $obj }
    # Convert PSCustomObject / OrderedDictionary -> hashtable
    $ht = @{}
    foreach ($p in $obj.PSObject.Properties) { $ht[$p.Name] = $p.Value }
    return $ht
}

if (-not (Test-Path $PublishDir)) {
    throw "PublishDir not found: $PublishDir"
}

$publishFull = (Resolve-Path $PublishDir).Path
$outputFull = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $outputFull -Force | Out-Null

# Keep ARM naming identical for backwards compatibility
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
                if ($cmd) { $candidate = $cmd.Source; break }
            }
        }
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            $common7z = @(
                "C:\Program Files\7-Zip\7z.exe",
                "C:\Program Files (x86)\7-Zip\7z.exe"
            )
            foreach ($p in $common7z) { if (Test-Path $p) { $candidate = $p; break } }
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
            & $SevenZipPath a -tzip -mm=Deflate -mx=9 -mfb=258 -mpass=15 $zipTarget ".\*" | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "7-Zip failed with exit code $LASTEXITCODE" }
        }
        finally { Pop-Location }
    }
    else {
        $zipStream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::CreateNew)
        try {
            $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
            try {
                $files = Get-ChildItem -Path $tempRoot -Recurse -File
                foreach ($file in $files) {
                    $rel = $file.FullName.Substring($tempRoot.Length).TrimStart("\","/")
                    $entry = $archive.CreateEntry($rel, $resolvedCompression)
                    $inStream = [System.IO.File]::OpenRead($file.FullName)
                    try {
                        $outStream = $entry.Open()
                        try { $inStream.CopyTo($outStream) }
                        finally { $outStream.Dispose() }
                    }
                    finally { $inStream.Dispose() }
                }
            }
            finally { $archive.Dispose() }
        }
        finally { $zipStream.Dispose() }
    }

    $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = (Get-Item -Path $zipPath).Length
    $publishedAt = (Get-Date).ToUniversalTime().ToString("o")

    $trimmedBase = $BaseUrl.TrimEnd("/")
    $zipUrl = "$trimmedBase/$zipName"

    # Load existing manifest (so we can merge multi-arch builds)
    $manifestPath = Join-Path $outputFull $ManifestFileName
    $manifest = $null
    if (Test-Path $manifestPath) {
        try { $manifest = Get-Content -Raw -Path $manifestPath | ConvertFrom-Json -ErrorAction Stop }
        catch { $manifest = $null }
    }

    $m = @{}
    if ($null -ne $manifest) { $m = Ensure-Hashtable $manifest }
    if (-not $m.ContainsKey("app")) { $m["app"] = $AppName }
    $m["channel"] = $Channel
    $m["generatedAtUtc"] = $publishedAt

    # latest
    $latest = Ensure-Hashtable $m["latest"]
    $latest["version"] = $Version
    $latest["channel"] = $Channel
    $latest["publishedAtUtc"] = $publishedAt

    # Backwards compatibility fields (ARM only)
    if ($RuntimeTag -eq "linux-arm") {
        $latest["url"] = $zipUrl
        $latest["sha256"] = $hash
        $latest["sizeBytes"] = [long]$size
    }
    elseif (-not $latest.ContainsKey("url")) {
        # Do not overwrite legacy url/sha256/sizeBytes from ARM publish
    }

    # builds
    $builds = Ensure-Hashtable $latest["builds"]
    $builds[$RuntimeTag] = (New-BuildInfo -Url $zipUrl -Sha256 $hash -SizeBytes ([long]$size) -PublishedAtUtc $publishedAt)
    $latest["builds"] = $builds
    $m["latest"] = $latest

    # releases
    $releases = @()
    if ($m.ContainsKey("releases") -and $null -ne $m["releases"]) {
        $releases = @($m["releases"])
    }

    $found = $false
    for ($i=0; $i -lt $releases.Count; $i++) {
        $r = $releases[$i]
        $rh = Ensure-Hashtable $r
        if ($rh["version"] -eq $Version) {
            $rh["channel"] = $Channel
            if (-not $rh.ContainsKey("publishedAtUtc")) { $rh["publishedAtUtc"] = $publishedAt }

            if ($RuntimeTag -eq "linux-arm") {
                $rh["url"] = $zipUrl
                $rh["sha256"] = $hash
                $rh["sizeBytes"] = [long]$size
            }

            $rb = Ensure-Hashtable $rh["builds"]
            $rb[$RuntimeTag] = (New-BuildInfo -Url $zipUrl -Sha256 $hash -SizeBytes ([long]$size) -PublishedAtUtc $publishedAt)
            $rh["builds"] = $rb

            $releases[$i] = $rh
            $found = $true
            break
        }
    }

    if (-not $found) {
        $rh = @{
            version = $Version
            channel = $Channel
            publishedAtUtc = $publishedAt
            builds = @{
                $RuntimeTag = (New-BuildInfo -Url $zipUrl -Sha256 $hash -SizeBytes ([long]$size) -PublishedAtUtc $publishedAt)
            }
        }
        if ($RuntimeTag -eq "linux-arm") {
            $rh["url"] = $zipUrl
            $rh["sha256"] = $hash
            $rh["sizeBytes"] = [long]$size
        }
        $releases = @($rh) + @($releases)
    }

    $m["releases"] = $releases

    $m | ConvertTo-Json -Depth 20 | Set-Content -Path $manifestPath -Encoding UTF8

    Write-Host "Update package created:"
    Write-Host "  Zip      : $zipPath"
    Write-Host "  Manifest : $manifestPath"
    Write-Host "  Version  : $Version"
    Write-Host "  Runtime  : $RuntimeTag"
    Write-Host "  Compress : $CompressionLevel"
    Write-Host "  Symbols  : $(if ($StripSymbols) { 'stripped' } else { 'kept' })"
    Write-Host "  SHA256   : $hash"
    Write-Host "  URL      : $zipUrl"
}
finally {
    if (Test-Path $tempRoot) { Remove-Item -Path $tempRoot -Recurse -Force }
}
