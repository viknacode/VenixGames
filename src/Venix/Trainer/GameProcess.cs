using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Venix.Trainer;

public record GameModule(string Name, nint Base, int Size);

/// <summary>
/// An attached game process: memory read/write/alloc/protect, module map and bitness.
/// Generalized from CrimsonTrainer's GameProcess for any 32- or 64-bit game.
/// </summary>
public sealed class GameProcess : IDisposable
{
    const uint Access = Native.PROCESS_VM_OPERATION | Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_QUERY_INFORMATION;

    public Process Process { get; }
    public nint Handle { get; }
    public bool Is64Bit { get; }
    public int PointerSize => Is64Bit ? 8 : 4;
    public int Bitness => Is64Bit ? 64 : 32;
    public string Name => Process.ProcessName;
    public IReadOnlyList<GameModule> Modules => _modules;
    public GameModule MainModule => _modules.FirstOrDefault(m => m.Name.Equals(Name + ".exe", StringComparison.OrdinalIgnoreCase)) ?? _modules[0];

    List<GameModule> _modules = new();

    GameProcess(Process process, nint handle, bool is64)
    {
        Process = process;
        Handle = handle;
        Is64Bit = is64;
        RefreshModules();
    }

    public static GameProcess Attach(Process process)
    {
        var h = Native.OpenProcess(Access, false, process.Id);
        if (h == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Não foi possível abrir {process.ProcessName} (PID {process.Id}). Tente executar o Venix como administrador.");
        Native.IsWow64Process(h, out var wow64);
        var gp = new GameProcess(process, h, Environment.Is64BitOperatingSystem && !wow64);
        if (gp._modules.Count == 0) { gp.Dispose(); throw new InvalidOperationException("Não foi possível listar os módulos do processo (ele ainda está iniciando?)."); }
        return gp;
    }

    public bool HasExited
    {
        get { try { return Process.HasExited; } catch { return true; } }
    }

    public void RefreshModules()
    {
        var list = new List<GameModule>();
        var snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPMODULE | Native.TH32CS_SNAPMODULE32, Process.Id);
        if (snap == -1 || snap == 0) return;
        try
        {
            var e = new Native.MODULEENTRY32W { dwSize = Marshal.SizeOf<Native.MODULEENTRY32W>() };
            if (Native.Module32FirstW(snap, ref e))
                do list.Add(new GameModule(e.szModule, e.modBaseAddr, e.modBaseSize));
                while (Native.Module32NextW(snap, ref e));
        }
        finally { Native.CloseHandle(snap); }
        // the game executable first, so "$process" and unqualified scans hit it
        _modules = list.OrderByDescending(m => m.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public GameModule? FindModule(string name)
    {
        name = name.Trim().Trim('"');
        var m = _modules.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (m == null) { RefreshModules(); m = _modules.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }
        return m;
    }

    public string Describe(nint address)
    {
        foreach (var m in _modules)
            if (address >= m.Base && address < m.Base + m.Size) return $"{m.Name}+{(long)(address - m.Base):X}";
        return ((long)address).ToString("X");
    }

    /* ---------------------------------------------------------- read / write */

    public bool TryRead(nint address, byte[] buffer, int size) =>
        Native.ReadProcessMemory(Handle, address, buffer, size, out var read) && read == size;

    public byte[]? TryRead(nint address, int size)
    {
        var b = new byte[size];
        return TryRead(address, b, size) ? b : null;
    }

    public byte[] Read(nint address, int size) =>
        TryRead(address, size) ?? throw new InvalidOperationException($"Não foi possível ler {size} bytes em {Describe(address)}.");

    public bool TryReadPointer(nint address, out nint value)
    {
        var b = TryRead(address, PointerSize);
        value = b == null ? 0 : Is64Bit ? (nint)BitConverter.ToInt64(b) : (nint)BitConverter.ToUInt32(b);
        return b != null;
    }

    /// <summary>Reads a range page by page, zero-filling what cannot be read (AOB scans).</summary>
    public void ReadLenient(nint address, byte[] buffer, int size)
    {
        if (TryRead(address, buffer, size)) return;
        var page = new byte[0x1000];
        for (int off = 0; off < size; off += 0x1000)
        {
            int n = Math.Min(0x1000, size - off);
            if (TryRead(address + off, page, n)) Array.Copy(page, 0, buffer, off, n);
            else Array.Clear(buffer, off, n);
        }
    }

    /// <summary>Writes bytes, lifting page protection so code pages can be patched.</summary>
    public void Write(nint address, byte[] data)
    {
        if (data.Length == 0) return;
        var protectedOk = Native.VirtualProtectEx(Handle, address, data.Length, Native.PAGE_EXECUTE_READWRITE, out uint old);
        try
        {
            if (!Native.WriteProcessMemory(Handle, address, data, data.Length, out var written) || written != data.Length)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Falha ao escrever {data.Length} bytes em {Describe(address)}.");
        }
        finally
        {
            if (protectedOk) Native.VirtualProtectEx(Handle, address, data.Length, old, out _);
            Native.FlushInstructionCache(Handle, address, data.Length);
        }
    }

    public bool TryWrite(nint address, byte[] data)
    {
        try { Write(address, data); return true; } catch { return false; }
    }

    /* ---------------------------------------------------------- memory management */

    public void FullAccess(nint address, int size) =>
        Native.VirtualProtectEx(Handle, address, size, Native.PAGE_EXECUTE_READWRITE, out _);

    /// <summary>
    /// CE alloc(name,size,near): in 64-bit games code caves must sit within ±2 GB of the hook
    /// so a 5-byte "jmp rel32" reaches them. Walks free regions around <paramref name="near"/>.
    /// </summary>
    public nint Allocate(int size, nint near = 0)
    {
        size = Math.Max(size, 0x1000);
        if (Is64Bit && near != 0)
        {
            const long Range = 0x7FFF0000;
            const long Granularity = 0x10000;
            long lo = Math.Max(0x10000, (long)near - Range), hi = (long)near + Range;
            int mbiSize = Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>();
            // search downwards first (CE does the same), then upwards
            foreach (var downwards in new[] { true, false })
            {
                long addr = downwards ? ((long)near & ~(Granularity - 1)) - Granularity : (((long)near + Granularity) & ~(Granularity - 1));
                for (int tries = 0; tries < 4000 && addr > lo && addr < hi; tries++)
                {
                    if (Native.VirtualQueryEx(Handle, (nint)addr, out var mbi, mbiSize) == 0) break;
                    if (mbi.State == Native.MEM_FREE && (long)mbi.RegionSize >= size)
                    {
                        var start = ((long)mbi.BaseAddress + Granularity - 1) & ~(Granularity - 1);
                        var candidate = downwards ? (((long)mbi.BaseAddress + (long)mbi.RegionSize - size) & ~(Granularity - 1)) : start;
                        if (candidate >= (long)mbi.BaseAddress && candidate + size <= (long)mbi.BaseAddress + (long)mbi.RegionSize)
                        {
                            var p = Native.VirtualAllocEx(Handle, (nint)candidate, size, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_EXECUTE_READWRITE);
                            if (p != 0) return p;
                        }
                    }
                    addr = downwards ? (long)mbi.BaseAddress - Granularity : (long)mbi.BaseAddress + (long)mbi.RegionSize;
                }
            }
        }
        var any = Native.VirtualAllocEx(Handle, 0, size, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_EXECUTE_READWRITE);
        if (any == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Falha ao alocar {size} bytes no jogo.");
        return any;
    }

    public void Free(nint address)
    {
        if (address != 0) Native.VirtualFreeEx(Handle, address, 0, Native.MEM_RELEASE);
    }

    public List<(nint Start, long Size)> ReadableRegions(long maxRegion = 1L << 30)
    {
        var regions = new List<(nint, long)>();
        nint address = 0;
        int mbiSize = Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>();
        long limit = Is64Bit ? 0x7FFFFFFEFFFF : 0xFFFFFFFF;
        while ((long)address < limit && Native.VirtualQueryEx(Handle, address, out var mbi, mbiSize) != 0)
        {
            bool ok = mbi.State == Native.MEM_COMMIT && (mbi.Protect & Native.PAGE_GUARD) == 0 && mbi.Protect != Native.PAGE_NOACCESS && mbi.Protect != 0
                      && (long)mbi.RegionSize <= maxRegion;
            if (ok) regions.Add((mbi.BaseAddress, (long)mbi.RegionSize));
            var next = mbi.BaseAddress + mbi.RegionSize;
            if (next <= address) break;
            address = next;
        }
        return regions;
    }

    /// <summary>Suspends every game thread while a multi-byte code patch is written.</summary>
    public IDisposable SuspendThreads()
    {
        var handles = new List<nint>();
        if (Process.Id == Environment.ProcessId) return new Resumer(handles); // never freeze ourselves
        try
        {
            Process.Refresh();
            foreach (ProcessThread t in Process.Threads)
            {
                var h = Native.OpenThread(Native.THREAD_SUSPEND_RESUME, false, t.Id);
                if (h == 0) continue;
                if (Native.SuspendThread(h) == uint.MaxValue) { Native.CloseHandle(h); continue; }
                handles.Add(h);
            }
        }
        catch { /* best effort */ }
        return new Resumer(handles);
    }

    sealed class Resumer(List<nint> handles) : IDisposable
    {
        public void Dispose()
        {
            foreach (var h in handles) { Native.ResumeThread(h); Native.CloseHandle(h); }
            handles.Clear();
        }
    }

    public void Dispose()
    {
        if (Handle != 0) Native.CloseHandle(Handle);
    }
}
