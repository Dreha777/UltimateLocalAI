$ErrorActionPreference='SilentlyContinue'
Write-Host '=== Ultimate Local AI v1.2: проверка ПК ===' -ForegroundColor Cyan
Write-Host ('Windows: ' + [Environment]::OSVersion.VersionString)
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
if ($cpu) {
  Write-Host ('CPU: ' + $cpu.Name.Trim())
  Write-Host ('Ядер/потоков: ' + $cpu.NumberOfCores + '/' + $cpu.NumberOfLogicalProcessors)
}
$ram = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
if ($ram) { Write-Host ('RAM: {0:N1} GB' -f ($ram/1GB)) }

$nvsmi = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
if (-not $nvsmi) {
  $candidate = Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVSMI\nvidia-smi.exe'
  if (Test-Path $candidate) { $nvsmi = Get-Item $candidate }
}
if ($nvsmi) {
  $exe = if ($nvsmi.Source) { $nvsmi.Source } else { $nvsmi.FullName }
  $line = & $exe --query-gpu=name,driver_version,memory.total,compute_cap --format=csv,noheader 2>$null | Select-Object -First 1
  Write-Host ('NVIDIA: ' + $line) -ForegroundColor Green
  $drv = (& $exe --query-gpu=driver_version --format=csv,noheader 2>$null | Select-Object -First 1).Trim()
  try {
    $v=[version]$drv
    if ($v -lt [version]'527.41') {
      Write-Warning 'Драйвер NVIDIA ниже минимального уровня ранних CUDA 12.x. Обновите драйвер.'
    } else {
      Write-Host 'Драйвер достаточно новый для CUDA 12.x runtime.' -ForegroundColor Green
    }
  } catch {}
} else {
  Write-Host 'NVIDIA driver/nvidia-smi не найден. Будет доступен CPU backend.' -ForegroundColor Yellow
}

$nvcc = Get-Command nvcc.exe -ErrorAction SilentlyContinue
if ($nvcc) {
  $nvccText = (& $nvcc.Source --version | Out-String)
  $m=[regex]::Match($nvccText,'release\s+(\d+)\.(\d+)')
  if ($m.Success) {
    $maj=[int]$m.Groups[1].Value; $min=[int]$m.Groups[2].Value
    Write-Host ("CUDA Toolkit: $maj.$min") -ForegroundColor Cyan
    if ($maj -eq 12) { Write-Host 'Pascal/GTX 1050: Toolkit подходит.' -ForegroundColor Green }
    elseif ($maj -ge 13) { Write-Warning 'Для GTX 1050/Pascal нужен CUDA Toolkit 12.x. Драйвер NVIDIA при этом можно оставить свежим.' }
  }
} else {
  Write-Host 'CUDA Toolkit/nvcc не найден. Это не мешает CPU-сборке; для GTX 1050 установите CUDA Toolkit 12.x.' -ForegroundColor Yellow
}

$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($dotnet) { Write-Host ('dotnet: ' + (& $dotnet.Source --version | Select-Object -First 1)) }
else { Write-Host '.NET SDK не найден (нужен только на ПК сборки).' -ForegroundColor Yellow }

Write-Host ''
Write-Host 'Для ЗАПУСКА готовой portable-версии Visual Studio, .NET SDK и CUDA Toolkit не нужны.' -ForegroundColor Cyan
Write-Host 'Для GPU-запуска нужен только совместимый системный NVIDIA Driver.' -ForegroundColor Cyan
