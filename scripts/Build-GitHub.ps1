param(
    [string]$StableSource = "$PSScriptRoot\..\vendor\llama-stable",
    [string]$LatestSource = "$PSScriptRoot\..\vendor\llama-latest",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Out = Join-Path $Root 'dist\UltimateLocalAI-Portable'
$BackendsOut = Join-Path $Out 'backends'
$BuildRoot = Join-Path $Root 'build-ci'

function Run([string]$Exe, [string[]]$Arguments) {
    Write-Host "> $Exe $($Arguments -join ' ')" -ForegroundColor DarkGray

    & $Exe @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "$Exe exited with code $LASTEXITCODE"
    }
}

function Copy-CudaRuntime([string]$Dest) {
    if (-not $env:CUDA_PATH) { return }
    $bin = Join-Path $env:CUDA_PATH 'bin'
    foreach ($pattern in @('cudart64_*.dll','cublas64_*.dll','cublasLt64_*.dll','nvJitLink_*.dll','nvrtc64_*.dll','nvrtc-builtins64_*.dll')) {
        Get-ChildItem $bin -Filter $pattern -ErrorAction SilentlyContinue | Copy-Item -Destination $Dest -Force
    }
}

function Build-Backend(
    [string]$Source,
    [string]$Channel,
    [string]$Name,
    [string[]]$ExtraArgs,
    [bool]$Cuda,
    [bool]$Required
) {
    try {
        if (-not (Test-Path (Join-Path $Source 'CMakeLists.txt'))) { throw "llama.cpp source missing: $Source" }
        $build = Join-Path $BuildRoot "$Channel-$Name"
        $dest = Join-Path $BackendsOut "$Channel\$Name"
        Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $build,$dest | Out-Null

        $common = @(
            '-G','Visual Studio 17 2022','-A','x64',
            '-DLLAMA_BUILD_TESTS=OFF','-DLLAMA_BUILD_EXAMPLES=OFF','-DLLAMA_BUILD_TOOLS=ON','-DLLAMA_BUILD_SERVER=ON',
            '-DLLAMA_BUILD_UI=OFF','-DLLAMA_USE_PREBUILT_UI=OFF','-DLLAMA_OPENSSL=OFF',
            '-DGGML_NATIVE=OFF','-DGGML_AVX512=OFF'
        )
        Write-Host "\n[$Channel] $Name" -ForegroundColor Cyan
        Run 'cmake' (@('-S',$Source,'-B',$build) + $common + $ExtraArgs)
        Run 'cmake' @('--build',$build,'--config',$Configuration,'--target','llama-server','--parallel')

        $bin = @((Join-Path $build "bin\$Configuration"),(Join-Path $build 'bin')) |
            Where-Object { Test-Path (Join-Path $_ 'llama-server.exe') } | Select-Object -First 1
        if (-not $bin) { throw 'llama-server.exe not found after build' }
        Copy-Item (Join-Path $bin '*') $dest -Recurse -Force
        if ($Cuda) { Copy-CudaRuntime $dest }
        Set-Content -Path (Join-Path $dest 'RUNTIME_INFO.txt') -Encoding UTF8 -Value @(
            "channel=$Channel",
            "backend=$Name",
            "source=$Source",
            "built_utc=$([DateTime]::UtcNow.ToString('O'))"
        )
        Write-Host "[OK] $Channel/$Name" -ForegroundColor Green
    } catch {
        if ($Required) { throw }
        Write-Warning "Optional backend failed: $Channel/$Name :: $($_.Exception.Message)"
    }
}

Remove-Item $Out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Out,$BackendsOut,$BuildRoot | Out-Null

# GUI: completely self-contained .NET 8 Windows x64
$proj = Join-Path $Root 'src\UltimateLocalAI\UltimateLocalAI.csproj'
Run 'dotnet' @('publish',$proj,'-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=false','-p:DebugType=None','-o',$Out)

$cpuAvx = @('-DGGML_CUDA=OFF','-DGGML_AVX=ON','-DGGML_AVX2=OFF','-DGGML_FMA=OFF','-DGGML_F16C=OFF','-DGGML_BMI2=OFF')
$cpuAvx2 = @('-DGGML_CUDA=OFF','-DGGML_AVX=ON','-DGGML_AVX2=ON','-DGGML_FMA=ON','-DGGML_F16C=ON','-DGGML_BMI2=ON')
$cudaPascal = @('-DGGML_CUDA=ON','-DCMAKE_CUDA_ARCHITECTURES=61','-DGGML_AVX=ON','-DGGML_AVX2=OFF','-DGGML_FMA=OFF','-DGGML_F16C=OFF','-DGGML_BMI2=OFF')
$cudaModern = @('-DGGML_CUDA=ON','-DCMAKE_CUDA_ARCHITECTURES=75;86;89','-DGGML_AVX=ON','-DGGML_AVX2=ON','-DGGML_FMA=ON','-DGGML_F16C=ON','-DGGML_BMI2=ON')
$cudaBlackwell = @('-DGGML_CUDA=ON','-DCMAKE_CUDA_ARCHITECTURES=120','-DGGML_AVX=ON','-DGGML_AVX2=ON','-DGGML_FMA=ON','-DGGML_F16C=ON','-DGGML_BMI2=ON')

# Stable b11060: target user's Xeon / i7 plus Pascal GPU.
Build-Backend $StableSource 'stable' 'cpu-avx' $cpuAvx $false $true
Build-Backend $StableSource 'stable' 'cpu-avx2' $cpuAvx2 $false $true
Build-Backend $StableSource 'stable' 'cuda-pascal-avx' $cudaPascal $true $true

# Latest runtime: maximizes compatibility with newly released GGUF architectures.
Build-Backend $LatestSource 'latest' 'cpu-avx' $cpuAvx $false $true
Build-Backend $LatestSource 'latest' 'cpu-avx2' $cpuAvx2 $false $true
Build-Backend $LatestSource 'latest' 'cuda-pascal-avx' $cudaPascal $true $true
Build-Backend $LatestSource 'latest' 'cuda-modern-avx2' $cudaModern $true $false
Build-Backend $LatestSource 'latest' 'cuda-blackwell-avx2' $cudaBlackwell $true $false

# Portable writable directories and docs
New-Item -ItemType Directory -Force -Path (Join-Path $Out 'Data'),(Join-Path $Out 'Logs') | Out-Null
Copy-Item (Join-Path $Root 'FIRST_START_RU.txt') $Out -Force
Copy-Item (Join-Path $Root 'THIRD_PARTY_NOTICES.md') $Out -Force
@"
Ultimate Local AI GitHub Build
Stable runtime: llama.cpp b11060
Latest runtime: llama.cpp master at workflow build time
CUDA Toolkit: $env:CUDA_PATH
Build UTC: $([DateTime]::UtcNow.ToString('O'))
"@ | Set-Content -Encoding UTF8 (Join-Path $Out 'BUILD_INFO.txt')

Write-Host "\nPortable package ready: $Out" -ForegroundColor Green
