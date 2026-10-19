using System.Runtime.InteropServices;

namespace Venix.Trainer;

/// <summary>Win32 imports used by the trainer engine.</summary>
internal static partial class Native
{
    public const uint PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint THREAD_SUSPEND_RESUME = 0x0002;
    public const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, MEM_FREE = 0x10000, MEM_PRIVATE = 0x20000;
    public const uint PAGE_NOACCESS = 0x01, PAGE_GUARD = 0x100, PAGE_EXECUTE_READWRITE = 0x40;
    public const uint TH32CS_SNAPMODULE = 0x08, TH32CS_SNAPMODULE32 = 0x10;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadProcessMemory(nint h, nint address, [Out] byte[] buffer, nint size, out nint read);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteProcessMemory(nint h, nint address, [In] byte[] buffer, nint size, out nint written);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint VirtualAllocEx(nint h, nint address, nint size, uint type, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualFreeEx(nint h, nint address, nint size, uint type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualProtectEx(nint h, nint address, nint size, uint protect, out uint old);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushInstructionCache(nint h, nint address, nint size);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint VirtualQueryEx(nint h, nint address, out MEMORY_BASIC_INFORMATION info, nint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int tid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint SuspendThread(nint h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(nint h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWow64Process(nint h, [MarshalAs(UnmanagedType.Bool)] out bool wow64);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateToolhelp32Snapshot(uint flags, int pid);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MODULEENTRY32W
    {
        public int dwSize;
        public int th32ModuleID;
        public int th32ProcessID;
        public int GlblcntUsage;
        public int ProccntUsage;
        public nint modBaseAddr;
        public int modBaseSize;
        public nint hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Module32FirstW(nint snapshot, ref MODULEENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Module32NextW(nint snapshot, ref MODULEENTRY32W entry);
}
