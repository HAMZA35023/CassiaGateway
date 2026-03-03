$ErrorActionPreference = "Stop"

$src = "c:\Users\PLO\OneDrive\Sensor_Settings.xlsx"
$dst = "temp\Sensor_Settings.xlsx"
Copy-Item -Path $src -Destination $dst -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $dst))

try {
    function Get-EntryText([string]$name) {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq $name }
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Close()
        }
    }

    $sharedXml = [xml](Get-EntryText "xl/sharedStrings.xml")
    $nsShared = New-Object System.Xml.XmlNamespaceManager($sharedXml.NameTable)
    $nsShared.AddNamespace("x", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
    $shared = New-Object System.Collections.Generic.List[string]
    foreach ($si in $sharedXml.SelectNodes("//x:sst/x:si", $nsShared)) {
        $parts = $si.SelectNodes(".//x:t", $nsShared) | ForEach-Object { $_.InnerText }
        [void]$shared.Add(($parts -join ""))
    }

    function Get-ColNum([string]$cellRef) {
        $letters = ($cellRef -replace "[^A-Z]", "")
        $n = 0
        foreach ($c in $letters.ToCharArray()) {
            $n = $n * 26 + ([int][char]$c - [int][char]'A' + 1)
        }
        return $n
    }

    function Get-CellVal($cell) {
        if ([string]$cell.t -eq "s") {
            $idx = [int]$cell.v
            if ($idx -ge 0 -and $idx -lt $shared.Count) {
                return $shared[$idx]
            }
        }

        if ($cell.v) {
            return [string]$cell.v
        }

        return ""
    }

    $sheetXml = [xml](Get-EntryText "xl/worksheets/sheet3.xml")
    $ns = New-Object System.Xml.XmlNamespaceManager($sheetXml.NameTable)
    $ns.AddNamespace("x", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($row in $sheetXml.SelectNodes("//x:worksheet/x:sheetData/x:row", $ns)) {
        $map = @{}
        foreach ($cell in $row.SelectNodes("./x:c", $ns)) {
            $map[(Get-ColNum ([string]$cell.r))] = Get-CellVal $cell
        }

        $vals = for ($i = 1; $i -le 8; $i++) {
            if ($map.ContainsKey($i)) { $map[$i] } else { "" }
        }

        $line = ("{0,4} | " -f [int]$row.r) + ($vals -join " | ")
        [void]$lines.Add($line)
    }

    Set-Content -Path "temp\sheet3_dali102_dump_full.txt" -Value $lines -Encoding UTF8
}
finally {
    $zip.Dispose()
}

Write-Output "Done"
