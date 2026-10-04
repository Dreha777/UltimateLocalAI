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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    public async Task<HardwareInfo> DetectAsync()
    {
        var info = new HardwareInfo
        {
            CpuName = GetCpuName(),
            LogicalProcessors = Environment.ProcessorCount,
            Sse42 = Sse42.IsSupported,
            Avx = Avx.IsSupported,
            Avx2 = Avx2.IsSupported
        };

        RefreshMemory(info);
        await DetectNvidiaAsync(info);
        DetectGenericGpu(info);
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

    public static void RefreshMemory(HardwareInfo info)
    {
        var stat = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref stat)) return;
        info.TotalRamMb = (long)(stat.ullTotalPhys / 1024 / 1024);
        info.AvailableRamMb = (long)(stat.ullAvailPhys / 1024 / 1024);
    }


    private static void DetectGenericGpu(HardwareInfo info)
    {
        // NVIDIA с рабочим nvidia-smi уже определена точнее, включая VRAM и compute capability.
        if (info.NvidiaDetected) return;

        try
        {
            var names = new List<string>();
            for (uint i = 0; i < 16; i++)
            {
                var device = new DISPLAY_DEVICE
                {
                    cb = Marshal.SizeOf<DISPLAY_DEVICE>(),
                    DeviceName = "",
                    DeviceString = "",
                    DeviceID = "",
                    DeviceKey = ""
                };
                if (!EnumDisplayDevices(null, i, ref device, 0)) break;

                var name = device.DeviceString?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) continue;
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            }

            string? selected = names.FirstOrDefault(x => x.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                                                         x.Contains("AMD", StringComparison.OrdinalIgnoreCase));
            selected ??= names.FirstOrDefault(x => x.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
                                                   x.Contains("Arc", StringComparison.OrdinalIgnoreCase));
            selected ??= names.FirstOrDefault(x => x.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
            selected ??= names.FirstOrDefault(x => x.Contains("Intel", StringComparison.OrdinalIgnoreCase));
            selected ??= names.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(selected)) return;

            info.GpuDetected = true;
            info.GpuName = selected;
            info.GpuVendor = selected.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
                : selected.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || selected.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD"
                : selected.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel"
                : "Other";
        }
        catch { }
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
                info.GpuDetected = true;
                info.GpuVendor = "NVIDIA";
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
