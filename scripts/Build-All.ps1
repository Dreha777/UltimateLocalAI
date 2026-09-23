param(
    [switch]$CpuOnly,
    [switch]$Clean,
    [switch]$BuildBlackwell
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$PreferredLlamaTag = 'b11060'
$BuildWarnings = New-Object System.Collections.Generic.List[string]
$BuiltBackends = New-Object System.Collections.Generic.List[string]
$FailedOptionalBackends = New-Object System.Collections.Generic.List[string]

function Find-Exe([string]$Name, [string[]]$Candidates = @()) {
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($c in $Candidates) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}

function Fail([string]$Message) {
    Write-Host "`nОШИБКА: $Message" -ForegroundColor Red
    Write-Host "Подробности: README_RU.md" -ForegroundColor Yellow
    throw $Message
}

function Add-Warning([string]$Message) {
    $BuildWarnings.Add($Message)
    Write-Warning $Message
}

function Get-CMakeVersion([string]$CmakeExe) {
    try {
        $text = (& $CmakeExe --version | Select-Object -First 1)
        $m = [regex]::Match($text, '(\d+)\.(\d+)\.(\d+)')
        if ($m.Success) { return [version]$m.Value }
    } catch {}
    return [version]'0.0.0'
}

function Get-LlamaSourceInfo([string]$LlamaDir) {
    $cmakeFile = Join-Path $LlamaDir 'CMakeLists.txt'
    if (-not (Test-Path $cmakeFile)) { return 'не найден' }
    try {
        $text = Get-Content $cmakeFile -Raw
        $major = [regex]::Match($text, 'set\(LLAMA_VERSION_MAJOR\s+(\d+)\)').Groups[1].Value
        $minor = [regex]::Match($text, 'set\(LLAMA_VERSION_MINOR\s+(\d+)\)').Groups[1].Value
        $patch = [regex]::Match($text, 'set\(LLAMA_VERSION_PATCH\s+(\d+)\)').Groups[1].Value
        if ($major -ne '' -and $minor -ne '' -and $patch -ne '') { return "$major.$minor.$patch-dev/local" }
    } catch {}
    return 'локальные исходники'
}

Write-Host "==============================================" -ForegroundColor DarkGray
Write-Host " Ultimate Local AI Portable v1.2" -ForegroundColor Cyan
Write-Host " llama.cpp b11060-ready / One Click Build" -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor DarkGray

function Resolve-DotNet8Sdk {
    $candidates = New-Object System.Collections.Generic.List[string]

    # Сначала проверяем стандартный x64 dotnet. Это важно, если в PATH попал x86 host.
    if ($env:ProgramFiles) {
        $candidates.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))
    }
    if ($env:DOTNET_ROOT) {
        $candidates.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe'))
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe'))
    }

    $pathDotnet = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if ($pathDotnet) { $candidates.Add($pathDotnet.Source) }

    foreach ($candidate in ($candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique)) {
        try {
            $sdks = @(& $candidate --list-sdks 2>$null)
            $sdk8 = $sdks | Where-Object { $_ -match '^8\.' } | Select-Object -First 1
            if ($sdk8) {
                return [pscustomobject]@{
                    Exe = $candidate
                    Sdk = $sdk8
                    AllSdks = $sdks
                }
            }
        } catch {}
    }
    return $null
}

$dotnetInfo = Resolve-DotNet8Sdk
if (-not $dotnetInfo) {
    Write-Host "`nНайденные dotnet SDK:" -ForegroundColor Yellow
    $allDotnet = @()
    if ($env:ProgramFiles) { $allDotnet += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') }
    if (${env:ProgramFiles(x86)}) { $allDotnet += (Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe') }
    $pathDotnet = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if ($pathDotnet) { $allDotnet += $pathDotnet.Source }
    foreach ($d in ($allDotnet | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique)) {
        Write-Host ("  " + $d) -ForegroundColor DarkGray
        try { & $d --list-sdks } catch {}
    }
    Fail 'Не найден именно .NET 8 SDK x64. Runtime/ASP.NET Runtime недостаточно. Проверьте командой: "C:\Program Files\dotnet\dotnet.exe" --list-sdks'
}
$dotnet = $dotnetInfo.Exe
Write-Host (".NET 8 SDK: " + $dotnetInfo.Sdk) -ForegroundColor DarkGray
Write-Host ("dotnet.exe: " + $dotnet) -ForegroundColor DarkGray

$cmakeCandidates = @(
    "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
    "$env:ProgramFiles\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
    "$env:ProgramFiles\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
    "$env:ProgramFiles\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
)
# Предпочитаем CMake из Visual Studio, чтобы не подхватить случайный старый standalone CMake из PATH.
$cmake = $cmakeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cmake) { $cmake = Find-Exe 'cmake.exe' @() }
if (-not $cmake) { Fail 'Не найден CMake из Visual Studio. Добавьте workload Desktop development with C++ и C++ CMake tools for Windows.' }
$cmakeVersion = Get-CMakeVersion $cmake
if ($cmakeVersion -lt [version]'3.18.0') { Fail "CMake $cmakeVersion слишком старый для CUDA CMAKE_CUDA_ARCHITECTURES. Нужен CMake 3.18+." }
Write-Host ("CMake: " + $cmakeVersion + " -> " + $cmake) -ForegroundColor DarkGray

# .NET 8 SDK already resolved and validated above.

$llama = Join-Path $Root 'vendor\llama.cpp'
if (-not (Test-Path (Join-Path $llama 'CMakeLists.txt'))) {
    Write-Host "`nИсходники llama.cpp отсутствуют. Пробую получить рекомендуемый $PreferredLlamaTag..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force $llama | Out-Null
    Get-ChildItem $llama -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
    $git = Find-Exe 'git.exe' @("$env:ProgramFiles\Git\cmd\git.exe")
    $fetched = $false
    if ($git) {
        & $git clone --depth 1 --branch $PreferredLlamaTag https://github.com/ggml-org/llama.cpp.git $llama
        $fetched = ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $llama 'CMakeLists.txt')))
    }
    if (-not $fetched) {
        try {
            $zip = Join-Path $env:TEMP ("llama.cpp-$PreferredLlamaTag.zip")
            $tmp = Join-Path $env:TEMP ("llama.cpp-$PreferredLlamaTag-" + [guid]::NewGuid().ToString('N'))
            Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/ggml-org/llama.cpp/archive/refs/tags/$PreferredLlamaTag.zip" -OutFile $zip
            Expand-Archive -Path $zip -DestinationPath $tmp -Force
            $src = Get-ChildItem $tmp -Directory | Select-Object -First 1
            if ($src) {
                New-Item -ItemType Directory -Force $llama | Out-Null
                Copy-Item (Join-Path $src.FullName '*') $llama -Recurse -Force
            }
            Remove-Item $zip -Force -ErrorAction SilentlyContinue
            Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
            $fetched = Test-Path (Join-Path $llama 'CMakeLists.txt')
        } catch {
            Add-Warning ('Не удалось скачать llama.cpp автоматически: ' + $_.Exception.Message)
        }
    }
    if (-not $fetched) {
        Fail "Не удалось получить llama.cpp. Для офлайн-сборки положите исходники $PreferredLlamaTag в vendor\llama.cpp так, чтобы CMakeLists.txt лежал прямо в этой папке."
    }
}

Write-Host ("llama.cpp: " + (Get-LlamaSourceInfo $llama)) -ForegroundColor DarkGray
Write-Host ("Источник: " + $llama) -ForegroundColor DarkGray

# Проверяем ключевые опции, чтобы при несовместимой будущей версии ошибка была понятной.
$llamaTop = Get-Content (Join-Path $llama 'CMakeLists.txt') -Raw
foreach ($required in @('LLAMA_BUILD_SERVER','LLAMA_BUILD_UI','LLAMA_OPENSSL')) {
    if ($llamaTop -notmatch $required) { Fail "Локальная версия llama.cpp не содержит ожидаемую CMake-опцию $required. Нужна совместимая версия (рекомендуется $PreferredLlamaTag)." }
}
$ggmlTopPath = Join-Path $llama 'ggml\CMakeLists.txt'
if (-not (Test-Path $ggmlTopPath)) { Fail 'В vendor\llama.cpp отсутствует ggml\CMakeLists.txt. Архив llama.cpp распакован неполностью.' }

$script:CudaPathResolved = $null
$script:CudaVersionMajor = 0
$script:CudaVersionMinor = 0

function Resolve-CudaToolkit {
    # Для Pascal приоритетно выбираем самый свежий установленный CUDA 12.x,
    # даже если в PATH случайно находится CUDA 13.
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($env:CUDA_PATH -and $env:CUDA_PATH -match '\\v12\.') {
        $candidates.Add((Join-Path $env:CUDA_PATH 'bin\nvcc.exe'))
    }
    $cudaRoot = Join-Path $env:ProgramFiles 'NVIDIA GPU Computing Toolkit\CUDA'
    if (Test-Path $cudaRoot) {
        Get-ChildItem $cudaRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'v12.*' } |
            Sort-Object { try { [version]($_.Name.TrimStart('v')) } catch { [version]'0.0' } } -Descending |
            ForEach-Object { $candidates.Add((Join-Path $_.FullName 'bin\nvcc.exe')) }
    }
    if ($env:CUDA_PATH -and $env:CUDA_PATH -notmatch '\\v12\.') {
        $candidates.Add((Join-Path $env:CUDA_PATH 'bin\nvcc.exe'))
    }
    if (Test-Path $cudaRoot) {
        Get-ChildItem $cudaRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notlike 'v12.*' } |
            Sort-Object Name -Descending |
            ForEach-Object { $candidates.Add((Join-Path $_.FullName 'bin\nvcc.exe')) }
    }
    $pathNvcc = Get-Command 'nvcc.exe' -ErrorAction SilentlyContinue
    if ($pathNvcc) { $candidates.Add($pathNvcc.Source) }

    $nvcc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique -First 1
    if (-not $nvcc) { return $null }
    $nvccText = (& $nvcc --version | Out-String)
    $m = [regex]::Match($nvccText, 'release\s+(\d+)\.(\d+)')
    if ($m.Success) {
        $script:CudaVersionMajor = [int]$m.Groups[1].Value
        $script:CudaVersionMinor = [int]$m.Groups[2].Value
    }
    $script:CudaPathResolved = Split-Path -Parent (Split-Path -Parent $nvcc)
    return $nvcc
}

function Copy-CudaRuntime([string]$Dest) {
    if (-not $script:CudaPathResolved) { return }
    $cudaBin = Join-Path $script:CudaPathResolved 'bin'
    if (-not (Test-Path $cudaBin)) { return }
    foreach ($pattern in @('cudart64_*.dll','cublas64_*.dll','cublasLt64_*.dll','nvJitLink_*.dll','nvrtc64_*.dll','nvrtc-builtins64_*.dll')) {
        Get-ChildItem $cudaBin -Filter $pattern -ErrorAction SilentlyContinue | Copy-Item -Destination $Dest -Force
    }
}

function Build-Llama([string]$Name, [string[]]$Args, [bool]$Cuda, [bool]$Required) {
    $build = Join-Path $Root ("build\llama-" + $Name)
    $dest = Join-Path $Root ("backends\" + $Name)
    try {
        if ($Clean -and (Test-Path $build)) { Remove-Item $build -Recurse -Force }
        New-Item -ItemType Directory -Force $build | Out-Null
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
        New-Item -ItemType Directory -Force $dest | Out-Null

        Write-Host "`n[BACKEND] $Name" -ForegroundColor Green
        $common = @(
            '-G','Visual Studio 17 2022','-A','x64',
            '-DLLAMA_BUILD_TESTS=OFF','-DLLAMA_BUILD_EXAMPLES=OFF','-DLLAMA_BUILD_TOOLS=ON','-DLLAMA_BUILD_SERVER=ON',
            '-DLLAMA_BUILD_UI=OFF','-DLLAMA_USE_PREBUILT_UI=OFF','-DLLAMA_OPENSSL=OFF',
            '-DGGML_NATIVE=OFF','-DGGML_AVX512=OFF'
        )
        & $cmake -S $llama -B $build @common @Args
        if ($LASTEXITCODE -ne 0) { throw "CMake configure завершился с кодом $LASTEXITCODE" }
        & $cmake --build $build --config Release --target llama-server --parallel
        if ($LASTEXITCODE -ne 0) { throw "Сборка llama-server завершилась с кодом $LASTEXITCODE" }

        $src = @((Join-Path $build 'bin\Release'), (Join-Path $build 'bin')) |
            Where-Object { Test-Path (Join-Path $_ 'llama-server.exe') } | Select-Object -First 1
        if (-not $src) { throw 'llama-server.exe не найден после сборки' }

        Copy-Item (Join-Path $src '*') $dest -Recurse -Force
        if ($Cuda) { Copy-CudaRuntime $dest }

        $BuiltBackends.Add($Name)
        Write-Host "[OK] $Name" -ForegroundColor Green
    } catch {
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue }
        $msg = "${Name}: $($_.Exception.Message)"
        if ($Required) { Fail $msg }
        $FailedOptionalBackends.Add($Name)
        Add-Warning ("Необязательный backend не собран: " + $msg + '. Остальные backend и GUI будут собраны.')
    }
}

# Обязательные CPU backend'ы.
# Xeon E3-1270 / Sandy Bridge: AVX, без AVX2/BMI2.
Build-Llama 'cpu-avx'  @('-DGGML_SSE42=ON','-DGGML_AVX=ON','-DGGML_AVX2=OFF','-DGGML_BMI2=OFF','-DGGML_CUDA=OFF') $false $true
# Core i7-6700 / Skylake и более новые: AVX2/BMI2.
Build-Llama 'cpu-avx2' @('-DGGML_SSE42=ON','-DGGML_AVX=ON','-DGGML_AVX2=ON','-DGGML_BMI2=ON','-DGGML_CUDA=OFF') $false $true

if (-not $CpuOnly) {
    $nvcc = Resolve-CudaToolkit
    if ($nvcc) {
        Write-Host "`nCUDA Toolkit $script:CudaVersionMajor.$script:CudaVersionMinor найден: $script:CudaPathResolved" -ForegroundColor Cyan

        # Pascal (GTX 10xx) поддерживаем только Toolkit 12.x: compute capability 6.1.
        if ($script:CudaVersionMajor -eq 12) {
            Build-Llama 'cuda-pascal-avx' @(
                '-DGGML_SSE42=ON','-DGGML_AVX=ON','-DGGML_AVX2=OFF','-DGGML_BMI2=OFF',
                '-DGGML_CUDA=ON','-DCMAKE_CUDA_ARCHITECTURES=61'
            ) $true $false
        } elseif ($script:CudaVersionMajor -ge 13) {
            Add-Warning 'CUDA Toolkit 13.x не используется для GTX 1050/Pascal. Установите CUDA Toolkit 12.x рядом с драйвером NVIDIA; драйвер откатывать не требуется.'
        } else {
            Add-Warning 'Для Pascal backend рекомендуется CUDA Toolkit 12.x.'
        }

        # RTX 20/30/40 + Hopper. Этот backend намеренно НЕ включает Blackwell,
        # чтобы ошибка/особенность sm_120 не могла сорвать Pascal-сборку.
        if ($script:CudaVersionMajor -ge 11) {
            $modernArch = if ($script:CudaVersionMajor -gt 11 -or $script:CudaVersionMinor -ge 8) { '75;86;89;90' } else { '75;86' }
            Build-Llama 'cuda-modern-avx2' @(
                '-DGGML_SSE42=ON','-DGGML_AVX=ON','-DGGML_AVX2=ON','-DGGML_BMI2=ON',
                '-DGGML_CUDA=ON',"-DCMAKE_CUDA_ARCHITECTURES=$modernArch"
            ) $true $false
        }

        # Blackwell вынесен в отдельный НЕОБЯЗАТЕЛЬНЫЙ backend и не строится обычным BUILD.cmd.
        if ($BuildBlackwell) {
            if ($script:CudaVersionMajor -gt 12 -or ($script:CudaVersionMajor -eq 12 -and $script:CudaVersionMinor -ge 8)) {
                Build-Llama 'cuda-blackwell-avx2' @(
                    '-DGGML_SSE42=ON','-DGGML_AVX=ON','-DGGML_AVX2=ON','-DGGML_BMI2=ON',
                    '-DGGML_CUDA=ON','-DCMAKE_CUDA_ARCHITECTURES=120'
                ) $true $false
            } else {
                Add-Warning 'Blackwell backend требует CUDA Toolkit 12.8+.'
            }
        }
    } else {
        Write-Host "`nCUDA Toolkit не найден. CPU portable-версия всё равно будет собрана." -ForegroundColor Yellow
        Write-Host "Для GTX 1050 установите CUDA Toolkit 12.x (например 12.8) и повторите BUILD.cmd." -ForegroundColor Yellow
    }
}

Write-Host "`n[APP] Публикация self-contained GUI..." -ForegroundColor Green
$project = Join-Path $Root 'src\UltimateLocalAI\UltimateLocalAI.csproj'
$dist = Join-Path $Root 'dist\UltimateLocalAI-Portable'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

& $dotnet restore $project --configfile (Join-Path $Root 'NuGet.config') --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { Fail 'dotnet restore. Проверьте наличие .NET 8 SDK/Windows Desktop targeting/runtime packs.' }
& $dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=false -p:PublishReadyToRun=false -o $dist
if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish' }

Copy-Item (Join-Path $Root 'backends') (Join-Path $dist 'backends') -Recurse -Force
New-Item -ItemType Directory -Force (Join-Path $dist 'Data') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $dist 'Logs') | Out-Null
Copy-Item (Join-Path $Root 'FIRST_START_RU.txt') $dist -Force
Copy-Item (Join-Path $Root 'VERSION.txt') $dist -Force

$exe = Join-Path $dist 'UltimateLocalAI.exe'
if (-not (Test-Path $exe)) { Fail 'Portable EXE не появился в dist.' }
if (-not (Test-Path (Join-Path $dist 'backends\cpu-avx\llama-server.exe'))) { Fail 'В portable-папке нет CPU AVX backend.' }
if (-not (Test-Path (Join-Path $dist 'backends\cpu-avx2\llama-server.exe'))) { Fail 'В portable-папке нет CPU AVX2 backend.' }

Write-Host "`n==============================================" -ForegroundColor DarkGray
Write-Host " ГОТОВО" -ForegroundColor Green
Write-Host " $dist" -ForegroundColor White
Write-Host " Запуск: UltimateLocalAI.exe" -ForegroundColor Cyan
Write-Host "" 
Write-Host (" Собраны backend: " + ($BuiltBackends -join ', ')) -ForegroundColor White
if ($FailedOptionalBackends.Count -gt 0) {
    Write-Host (" Не собраны необязательные: " + ($FailedOptionalBackends -join ', ')) -ForegroundColor Yellow
}
if ($BuildWarnings.Count -gt 0) {
    Write-Host " Есть предупреждения — см. строки WARNING выше." -ForegroundColor Yellow
}
Write-Host "==============================================" -ForegroundColor DarkGray
