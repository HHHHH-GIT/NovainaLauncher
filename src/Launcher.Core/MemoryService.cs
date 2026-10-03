using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Launcher.Core;

public sealed class MemoryService
{
    public static int GetAvailableMemoryMb()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (int)Math.Min(int.MaxValue, status.AvailablePhysical / 1048576);
    }
    public static int Recommend(int availableMb, int enabledMods)
    {
        if (availableMb < 256) return 0;
        var amount = (int)(availableMb * (0.50 + 0.01 * Math.Clamp(enabledMods, 0, 20)));
        return Math.Clamp(amount / 256 * 256, 256, availableMb);
    }
    public static int CountEnabledMods(string gameDirectory)
    {
        var directory = System.IO.Path.Combine(gameDirectory, "mods");
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Count(x => x.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) : 0;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
