param()
$ErrorActionPreference='Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Llama = Join-Path $Root 'vendor\llama.cpp'
$Tag='b11060'
if (-not (Test-Path (Join-Path $Llama 'CMakeLists.txt'))) {
    Write-Host "Получаю llama.cpp $Tag..." -ForegroundColor Cyan
    if (Test-Path $Llama) { Remove-Item $Llama -Recurse -Force }
    New-Item -ItemType Directory -Force $Llama | Out-Null
    $git=Get-Command git.exe -ErrorAction SilentlyContinue
    if ($git) {
        Remove-Item $Llama -Recurse -Force
        & $git.Source clone --depth 1 --branch $Tag https://github.com/ggml-org/llama.cpp.git $Llama
        if ($LASTEXITCODE -ne 0) { throw 'git clone llama.cpp завершился ошибкой' }
    } else {
        $zip=Join-Path $env:TEMP "llama-$Tag.zip"
        $tmp=Join-Path $env:TEMP ("llama-$Tag-"+[guid]::NewGuid().ToString('N'))
        Invoke-WebRequest -UseBasicParsing "https://github.com/ggml-org/llama.cpp/archive/refs/tags/$Tag.zip" -OutFile $zip
        Expand-Archive $zip $tmp -Force
        $src=Get-ChildItem $tmp -Directory | Select-Object -First 1
        if (-not $src) { throw 'Не найдена распакованная папка llama.cpp' }
        Copy-Item (Join-Path $src.FullName '*') $Llama -Recurse -Force
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
if (-not (Test-Path (Join-Path $Llama 'CMakeLists.txt'))) { throw 'Не удалось подготовить llama.cpp' }
$out=Join-Path (Split-Path $Root -Parent) 'UltimateLocalAI_OFFLINE_READY_v1.2.zip'
if (Test-Path $out) { Remove-Item $out -Force }
Compress-Archive -Path (Join-Path $Root '*') -DestinationPath $out -CompressionLevel Optimal
Write-Host ("Готово: $out") -ForegroundColor Green
