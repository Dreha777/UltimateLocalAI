param(
    [string]$Destination = "$PSScriptRoot\..\src\UltimateLocalAI\Ocr\tessdata"
)
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$files = @{
    "rus.traineddata" = "https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main/rus.traineddata"
    "eng.traineddata" = "https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main/eng.traineddata"
    "osd.traineddata" = "https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main/osd.traineddata"
}

foreach ($entry in $files.GetEnumerator()) {
    $out = Join-Path $Destination $entry.Key
    if (Test-Path $out) {
        $size = (Get-Item $out).Length
        if ($size -gt 1MB) {
            Write-Host "OCR asset already present: $($entry.Key) ($size bytes)"
            continue
        }
        Remove-Item $out -Force
    }

    Write-Host "Downloading OCR asset: $($entry.Key)"
    curl.exe -L --fail --retry 3 --retry-delay 2 -o $out $entry.Value
    if ($LASTEXITCODE -ne 0) { throw "Failed to download $($entry.Key)" }

    $size = (Get-Item $out).Length
    if ($size -lt 1MB) { throw "Downloaded OCR asset is unexpectedly small: $($entry.Key) ($size bytes)" }
    Write-Host "OK: $($entry.Key) ($size bytes)"
}
