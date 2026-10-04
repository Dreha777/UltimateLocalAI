using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class ResourceSnapshot
{
    public double CpuPercent { get; init; } = -1;
    public long RamUsedMb { get; init; }
    public long RamTotalMb { get; init; }
    public long RamAvailableMb { get; init; }
    public double RamPercent => RamTotalMb > 0 ? RamUsedMb * 100.0 / RamTotalMb : 0;
    public double GpuPercent { get; init; } = -1;
    public int VramUsedMb { get; init; }
    public int VramTotalMb { get; init; }
    public int GpuTemperatureC { get; init; } = -1;
}

public sealed class ResourceMonitorService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low;
        public uint High;
        public ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private bool _hasCpuSample;

    public async Task<ResourceSnapshot> ReadAsync(HardwareInfo hardware, CancellationToken ct = default)
    {
        var cpu = ReadCpuPercent();
        var (usedMb, totalMb, availableMb) = ReadMemory();

        double gpuPercent = -1;
        var vramUsed = 0;
        var vramTotal = hardware.GpuVramMb;
        var temperature = -1;

        if (hardware.NvidiaDetected)
        {
            var gpu = await ReadNvidiaAsync(ct).ConfigureAwait(false);
            if (gpu is not null)
            {
                gpuPercent = gpu.Value.usage;
                vramUsed = gpu.Value.vramUsed;
                if (gpu.Value.vramTotal > 0) vramTotal = gpu.Value.vramTotal;
                temperature = gpu.Value.temperature;
            }
        }

        return new ResourceSnapshot
        {
            CpuPercent = cpu,
            RamUsedMb = usedMb,
            RamTotalMb = totalMb,
            RamAvailableMb = availableMb,
            GpuPercent = gpuPercent,
            VramUsedMb = vramUsed,
            VramTotalMb = vramTotal,
            GpuTemperatureC = temperature
        };
    }

    private double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return -1;

        var idleNow = idle.Value;
        var kernelNow = kernel.Value;
        var userNow = user.Value;

        if (!_hasCpuSample)
        {
            _lastIdle = idleNow;
            _lastKernel = kernelNow;
            _lastUser = userNow;
            _hasCpuSample = true;
            return -1;
        }

        var idleDelta = idleNow - _lastIdle;
        var kernelDelta = kernelNow - _lastKernel;
        var userDelta = userNow - _lastUser;
        _lastIdle = idleNow;
        _lastKernel = kernelNow;
        _lastUser = userNow;

        var total = kernelDelta + userDelta;
        if (total == 0) return 0;
        var busy = total > idleDelta ? total - idleDelta : 0;
        return Math.Clamp(busy * 100.0 / total, 0, 100);
    }

    private static (long usedMb, long totalMb, long availableMb) ReadMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0, 0);
        var total = (long)(status.ullTotalPhys / 1024 / 1024);
        var available = (long)(status.ullAvailPhys / 1024 / 1024);
        return (Math.Max(0, total - available), total, available);
    }

    private static async Task<(double usage, int vramUsed, int vramTotal, int temperature)?> ReadNvidiaAsync(CancellationToken ct)
    {
        var candidates = new[]
        {
            "nvidia-smi.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        };

        foreach (var exe in candidates)
        {
            try
            {
                if (exe.Contains(Path.DirectorySeparatorChar) && !File.Exists(exe)) continue;
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                if (process is null) continue;
                var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) continue;

                var first = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (first is null) continue;
                var parts = first.Split(',').Select(x => x.Trim()).ToArray();

                double.TryParse(parts.ElementAtOrDefault(0), NumberStyles.Float, CultureInfo.InvariantCulture, out var usage);
                int.TryParse(parts.ElementAtOrDefault(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var used);
                int.TryParse(parts.ElementAtOrDefault(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total);
                int.TryParse(parts.ElementAtOrDefault(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var temp);
                return (Math.Clamp(usage, 0, 100), used, total, temp);
            }
            catch { }
        }

        return null;
    }
}
