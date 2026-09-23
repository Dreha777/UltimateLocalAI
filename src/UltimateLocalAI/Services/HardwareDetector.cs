using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32;
using UltimateLocalAI.Models;

namespace UltimateLocalAI.Services;

public sealed class HardwareDetector
{
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public async Task<HardwareInfo> DetectAsync()
    {
        var info = new HardwareInfo
        {
            CpuName = GetCpuName(),
            LogicalProcessors = Environment.ProcessorCount,
            Sse42 = Sse42.IsSupported,
            Avx = Avx.IsSupported,
            Avx2 = Avx2.IsSupported,
            TotalRamMb = GetRamMb()
        };

        await DetectNvidiaAsync(info);
        return info;
    }

    private static string GetCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Не определён";
        }
        catch { return "Не определён"; }
    }

    private static long GetRamMb()
    {
        var stat = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref stat) ? (long)(stat.ullTotalPhys / 1024 / 1024) : 0;
    }

    private static async Task DetectNvidiaAsync(HardwareInfo info)
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
                    Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p is null) continue;
                var output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) continue;

                var first = output.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (first is null) continue;
                var parts = first.Split(',').Select(x => x.Trim()).ToArray();
                info.GpuName = parts.ElementAtOrDefault(0) ?? "NVIDIA GPU";
                int.TryParse(parts.ElementAtOrDefault(1), out var vram);
                info.GpuVramMb = vram;
                info.NvidiaDetected = true;

                try
                {
                    var ccPsi = new ProcessStartInfo
                    {
                        FileName = exe, Arguments = "--query-gpu=compute_cap --format=csv,noheader,nounits",
                        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
                    };
                    using var ccProcess = Process.Start(ccPsi);
                    if (ccProcess is not null)
                    {
                        var cc = await ccProcess.StandardOutput.ReadToEndAsync();
                        await ccProcess.WaitForExitAsync();
                        if (ccProcess.ExitCode == 0) info.ComputeCapability = cc.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
                    }
                }
                catch { }
                return;
            }
            catch { }
        }
    }
}
