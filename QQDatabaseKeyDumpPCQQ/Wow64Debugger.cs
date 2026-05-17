using System.Runtime.InteropServices;
using System.Text;

namespace QQDatabaseKeyDumpPCQQ;

/// <summary>
/// 用于调试 32 位（WOW64）目标进程的最小化 Win32 调试器。
/// 仅支持设置软件断点 + 命中后立即终止目标进程的工作模式。
/// </summary>
public sealed class Wow64Debugger : IDisposable
{
    private const uint TH32CS_SNAPMODULE = 0x00000008;
    private const uint TH32CS_SNAPMODULE32 = 0x00000010;

    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_TERMINATE = 0x0001;

    private const uint THREAD_GET_CONTEXT = 0x0008;
    private const uint THREAD_SET_CONTEXT = 0x0010;
    private const uint THREAD_QUERY_INFORMATION = 0x0040;
    private const uint THREAD_ALL_ACCESS = THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_QUERY_INFORMATION;

    private const uint EXCEPTION_BREAKPOINT = 0x80000003;
    private const uint EXCEPTION_SINGLE_STEP = 0x80000004;
    private const uint STATUS_WX86_BREAKPOINT = 0x4000001F;
    private const uint STATUS_WX86_SINGLE_STEP = 0x4000001E;
    private const uint DBG_CONTINUE = 0x00010002;
    private const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;

    private const uint EXCEPTION_DEBUG_EVENT = 1;
    private const uint CREATE_THREAD_DEBUG_EVENT = 2;
    private const uint CREATE_PROCESS_DEBUG_EVENT = 3;
    private const uint EXIT_THREAD_DEBUG_EVENT = 4;
    private const uint EXIT_PROCESS_DEBUG_EVENT = 5;
    private const uint LOAD_DLL_DEBUG_EVENT = 6;
    private const uint UNLOAD_DLL_DEBUG_EVENT = 7;
    private const uint OUTPUT_DEBUG_STRING_EVENT = 8;
    private const uint RIP_EVENT = 9;

    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    // WOW64 上下文相关
    private const uint WOW64_CONTEXT_i386 = 0x00010000;
    private const uint WOW64_CONTEXT_CONTROL = WOW64_CONTEXT_i386 | 0x00000001;
    private const uint WOW64_CONTEXT_INTEGER = WOW64_CONTEXT_i386 | 0x00000002;
    private const int WOW64_CONTEXT_SIZE = 716; // sizeof WOW64_CONTEXT
    private const int WOW64_OFFSET_EFLAGS = 192;
    private const int WOW64_OFFSET_EIP = 184;
    private const int WOW64_OFFSET_ESP = 196;
    private const int WOW64_OFFSET_EAX = 176;
    private const int WOW64_OFFSET_ECX = 172;
    private const int WOW64_OFFSET_EDX = 168;
    private const int WOW64_OFFSET_EBX = 164;
    private const int WOW64_OFFSET_EBP = 180;
    private const int WOW64_OFFSET_ESI = 160;
    private const int WOW64_OFFSET_EDI = 156;

    private readonly uint _processId;
    private readonly bool _attach;
    private IntPtr _hProcess;
    private bool _attached;
    private bool _shouldExit;

    private readonly Dictionary<ulong, Breakpoint> _breakpoints = new();
    // 单步状态：tid -> 该线程命中后需要重新写回 INT3 的断点地址
    private readonly Dictionary<uint, ulong> _singleStepThreads = new();

    public Wow64Debugger(int processId, bool attach)
    {
        _processId = (uint)processId;
        _attach = attach;
        _hProcess = OpenProcess(
            PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION |
            PROCESS_QUERY_INFORMATION | PROCESS_TERMINATE,
            false, _processId);
        if (_hProcess == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                $"OpenProcess(pid={processId}) failed");

        if (_attach)
        {
            if (!DebugActiveProcess(_processId))
            {
                int err = Marshal.GetLastWin32Error();
                CloseHandle(_hProcess);
                _hProcess = IntPtr.Zero;
                throw new System.ComponentModel.Win32Exception(err,
                    $"DebugActiveProcess(pid={processId}) failed");
            }
            DebugSetProcessKillOnExit(true);
            _attached = true;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_attached)
            {
                DebugActiveProcessStop(_processId);
                _attached = false;
            }
        }
        catch { }
        if (_hProcess != IntPtr.Zero)
        {
            CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }
    }

    public void SetBreakpoint(ulong address, Func<X86Regs, bool> onHit)
    {
        if (_breakpoints.ContainsKey(address))
            return;
        byte[] orig = ReadBytes(address, 1);
        _breakpoints[address] = new Breakpoint
        {
            OriginalByte = orig[0],
            OnHit = onHit,
        };
        WriteByteWithProtect(address, 0xCC);
        FlushInstructionCache(_hProcess, (IntPtr)(long)address, (UIntPtr)1);
    }

    public bool VerboseEvents { get; set; } = false;

    public void RunUntilCapture()
    {
        DEBUG_EVENT dbgEvent = default;
        while (!_shouldExit)
        {
            if (!WaitForDebugEvent(ref dbgEvent, 0xFFFFFFFF))
                break;

            uint continueStatus = DBG_CONTINUE;
            switch (dbgEvent.dwDebugEventCode)
            {
                case CREATE_PROCESS_DEBUG_EVENT:
                    if (VerboseEvents) Console.WriteLine($"[dbg] CREATE_PROCESS pid={dbgEvent.dwProcessId} tid={dbgEvent.dwThreadId} base=0x{dbgEvent.u.CreateProcessInfo.lpBaseOfImage.ToInt64():X}");
                    if (dbgEvent.u.CreateProcessInfo.hFile != IntPtr.Zero)
                        CloseHandle(dbgEvent.u.CreateProcessInfo.hFile);
                    break;
                case LOAD_DLL_DEBUG_EVENT:
                    if (VerboseEvents) Console.WriteLine($"[dbg] LOAD_DLL base=0x{dbgEvent.u.LoadDll.lpBaseOfDll.ToInt64():X}");
                    if (dbgEvent.u.LoadDll.hFile != IntPtr.Zero)
                        CloseHandle(dbgEvent.u.LoadDll.hFile);
                    break;
                case EXCEPTION_DEBUG_EVENT:
                    if (VerboseEvents)
                    {
                        var er = dbgEvent.u.Exception.ExceptionRecord;
                        Console.WriteLine($"[dbg] EXCEPTION code=0x{er.ExceptionCode:X8} addr=0x{er.ExceptionAddress.ToInt64():X} firstChance={dbgEvent.u.Exception.dwFirstChance} tid={dbgEvent.dwThreadId}");
                    }
                    continueStatus = HandleException(ref dbgEvent);
                    break;
                case EXIT_PROCESS_DEBUG_EVENT:
                    Console.WriteLine($"[dbg] EXIT_PROCESS exitCode=0x{dbgEvent.u.ExitProcess.dwExitCode:X8}");
                    ContinueDebugEvent(dbgEvent.dwProcessId, dbgEvent.dwThreadId, continueStatus);
                    return;
            }

            ContinueDebugEvent(dbgEvent.dwProcessId, dbgEvent.dwThreadId, continueStatus);
        }
    }

    private uint HandleException(ref DEBUG_EVENT dbgEvent)
    {
        var exRec = dbgEvent.u.Exception.ExceptionRecord;
        bool isBreakpoint = exRec.ExceptionCode == EXCEPTION_BREAKPOINT
                         || exRec.ExceptionCode == STATUS_WX86_BREAKPOINT;
        bool isSingleStep = exRec.ExceptionCode == EXCEPTION_SINGLE_STEP
                         || exRec.ExceptionCode == STATUS_WX86_SINGLE_STEP;

        if (isSingleStep)
        {
            // 单步完成后，重新放回 INT3 让断点继续生效
            if (_singleStepThreads.TryGetValue(dbgEvent.dwThreadId, out ulong bpAddr))
            {
                _singleStepThreads.Remove(dbgEvent.dwThreadId);
                if (!_shouldExit && _breakpoints.ContainsKey(bpAddr))
                {
                    WriteByteWithProtect(bpAddr, 0xCC);
                    FlushInstructionCache(_hProcess, (IntPtr)(long)bpAddr, (UIntPtr)1);
                }
                return DBG_CONTINUE;
            }
            return DBG_EXCEPTION_NOT_HANDLED;
        }

        if (!isBreakpoint)
            return DBG_EXCEPTION_NOT_HANDLED;

        ulong ip = (ulong)exRec.ExceptionAddress.ToInt64() & 0xFFFFFFFFUL;
        if (!_breakpoints.TryGetValue(ip, out var bp))
        {
            // 不是我们的断点（可能是系统初始 BP），忽略并继续
            if (VerboseEvents) Console.WriteLine($"[dbg]   -> not our BP (ip=0x{ip:X}), continuing");
            return DBG_CONTINUE;
        }

        if (VerboseEvents) Console.WriteLine($"[dbg]   -> hit our BP @ 0x{ip:X}");

        // 还原原始字节，让 CPU 这次能正常执行原指令
        WriteByteWithProtect(ip, bp.OriginalByte);
        FlushInstructionCache(_hProcess, (IntPtr)(long)ip, (UIntPtr)1);

        // 读取 WOW64 寄存器
        IntPtr hThread = OpenThread(THREAD_ALL_ACCESS, false, dbgEvent.dwThreadId);
        if (hThread == IntPtr.Zero)
            return DBG_CONTINUE;

        bool capture;
        try
        {
            byte[] ctxBuf = AllocCtxBuffer();
            SetCtxFlags(ctxBuf, WOW64_CONTEXT_CONTROL | WOW64_CONTEXT_INTEGER);
            if (!Wow64GetThreadContext(hThread, ctxBuf))
                return DBG_CONTINUE;

            X86Regs regs = ParseRegs(ctxBuf);
            regs.Tid = dbgEvent.dwThreadId;

            // 调用回调
            try
            {
                capture = bp.OnHit(regs);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"breakpoint callback exception: {ex}");
                capture = true;
            }

            if (!capture)
            {
                // 持续模式：把 EIP 回退到 INT3 处（在 WOW64 中 ExceptionAddress 已经是 INT3 地址，
                // 但 EIP 一般是 INT3+1），并设置 TF 让 CPU 执行完原指令再产生 single-step 异常
                uint eip = BitConverter.ToUInt32(ctxBuf, WOW64_OFFSET_EIP);
                uint eflags = BitConverter.ToUInt32(ctxBuf, WOW64_OFFSET_EFLAGS);
                if (eip != (uint)ip)
                    BitConverter.GetBytes((uint)ip).CopyTo(ctxBuf, WOW64_OFFSET_EIP);
                BitConverter.GetBytes(eflags | 0x100u).CopyTo(ctxBuf, WOW64_OFFSET_EFLAGS);
                Wow64SetThreadContext(hThread, ctxBuf);
                _singleStepThreads[dbgEvent.dwThreadId] = ip;
            }
        }
        finally
        {
            CloseHandle(hThread);
        }

        if (capture)
        {
            // 命中即终止
            TerminateProcess(_hProcess, 0);
            _shouldExit = true;
        }

        return DBG_CONTINUE;
    }

    public uint ReadUInt32(ulong address)
    {
        byte[] buf = ReadBytes(address, 4);
        return BitConverter.ToUInt32(buf, 0);
    }

    public byte[] ReadBytes(ulong address, int size)
    {
        byte[] buf = new byte[size];
        if (!ReadProcessMemory(_hProcess, (IntPtr)(long)address, buf, size, out IntPtr read) ||
            read.ToInt64() != size)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                $"ReadProcessMemory(0x{address:X}, {size}) failed (read={read.ToInt64()})");
        }
        return buf;
    }

    /// <summary>
    /// 在远程进程一段连续地址范围内搜索字节模式，返回首次匹配的虚拟地址，找不到返回 0。
    /// </summary>
    public ulong ScanMemory(ulong baseAddress, uint size, ReadOnlySpan<byte> pattern)
    {
        // 分块读取，避免一次性 alloc 太大；并支持跨块匹配。
        const int CHUNK = 1 << 20; // 1 MiB
        int patLen = pattern.Length;
        if (patLen == 0 || size < patLen) return 0;

        byte[] tail = new byte[patLen - 1];
        int tailLen = 0;
        ulong offset = 0;
        byte[] chunk = new byte[CHUNK];

        while (offset < size)
        {
            int toRead = (int)Math.Min((ulong)CHUNK, size - offset);
            if (!ReadProcessMemory(_hProcess, (IntPtr)(long)(baseAddress + offset),
                    chunk, toRead, out IntPtr read))
            {
                offset += (ulong)toRead;
                tailLen = 0;
                continue;
            }
            int got = (int)read.ToInt64();
            if (got <= 0)
            {
                offset += (ulong)toRead;
                tailLen = 0;
                continue;
            }

            // 拼上一段尾部 + 当前块的开头来检查跨块匹配
            if (tailLen > 0)
            {
                byte[] join = new byte[tailLen + Math.Min(patLen - 1, got)];
                Buffer.BlockCopy(tail, 0, join, 0, tailLen);
                Buffer.BlockCopy(chunk, 0, join, tailLen, join.Length - tailLen);
                int idx = IndexOf(join, pattern);
                if (idx >= 0)
                    return baseAddress + offset - (ulong)tailLen + (ulong)idx;
            }

            int hit = IndexOf(chunk.AsSpan(0, got), pattern);
            if (hit >= 0)
                return baseAddress + offset + (ulong)hit;

            // 保存末尾 patLen-1 字节供下次拼接
            int keep = Math.Min(patLen - 1, got);
            Buffer.BlockCopy(chunk, got - keep, tail, 0, keep);
            tailLen = keep;

            offset += (ulong)got;
        }
        return 0;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        return haystack.IndexOf(needle);
    }

    private void WriteByteWithProtect(ulong address, byte value)
    {
        if (!VirtualProtectEx(_hProcess, (IntPtr)(long)address, (IntPtr)1, PAGE_EXECUTE_READWRITE, out uint old))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                $"VirtualProtectEx(0x{address:X}) failed");
        try
        {
            byte[] one = new byte[] { value };
            if (!WriteProcessMemory(_hProcess, (IntPtr)(long)address, one, 1, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                    $"WriteProcessMemory(0x{address:X}) failed");
        }
        finally
        {
            VirtualProtectEx(_hProcess, (IntPtr)(long)address, (IntPtr)1, old, out _);
        }
    }

    private static byte[] AllocCtxBuffer()
    {
        // 16 字节对齐
        byte[] buf = new byte[WOW64_CONTEXT_SIZE + 16];
        return buf;
    }

    private static void SetCtxFlags(byte[] buf, uint flags)
    {
        // ContextFlags 在结构体偏移 0
        buf[0] = (byte)(flags & 0xFF);
        buf[1] = (byte)((flags >> 8) & 0xFF);
        buf[2] = (byte)((flags >> 16) & 0xFF);
        buf[3] = (byte)((flags >> 24) & 0xFF);
    }

    private static X86Regs ParseRegs(byte[] buf)
    {
        return new X86Regs
        {
            Eax = BitConverter.ToUInt32(buf, WOW64_OFFSET_EAX),
            Ecx = BitConverter.ToUInt32(buf, WOW64_OFFSET_ECX),
            Edx = BitConverter.ToUInt32(buf, WOW64_OFFSET_EDX),
            Ebx = BitConverter.ToUInt32(buf, WOW64_OFFSET_EBX),
            Esp = BitConverter.ToUInt32(buf, WOW64_OFFSET_ESP),
            Ebp = BitConverter.ToUInt32(buf, WOW64_OFFSET_EBP),
            Esi = BitConverter.ToUInt32(buf, WOW64_OFFSET_ESI),
            Edi = BitConverter.ToUInt32(buf, WOW64_OFFSET_EDI),
            Eip = BitConverter.ToUInt32(buf, WOW64_OFFSET_EIP),
            EFlags = BitConverter.ToUInt32(buf, WOW64_OFFSET_EFLAGS),
        };
    }

    public static bool TryGetModule(int pid, string moduleName, out ulong baseAddr, out uint size)
    {
        baseAddr = 0; size = 0;
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)pid);
        if (snap == IntPtr.Zero || snap == (IntPtr)(-1))
            return false;
        try
        {
            MODULEENTRY32 me = default;
            me.dwSize = (uint)Marshal.SizeOf<MODULEENTRY32>();
            if (!Module32FirstW(snap, ref me))
                return false;
            do
            {
                if (string.Equals(me.szModule, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    baseAddr = (ulong)me.modBaseAddr.ToInt64() & 0xFFFFFFFFUL;
                    size = me.modBaseSize;
                    return true;
                }
            }
            while (Module32NextW(snap, ref me));
            return false;
        }
        finally
        {
            CloseHandle(snap);
        }
    }

    private struct Breakpoint
    {
        public byte OriginalByte;
        public Func<X86Regs, bool> OnHit;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODULEENTRY32
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPTION_RECORD
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public IntPtr ExceptionRecord;
        public IntPtr ExceptionAddress;
        public uint NumberParameters;
        public UIntPtr ExceptionInformation0;
        public UIntPtr ExceptionInformation1;
        public UIntPtr ExceptionInformation2;
        public UIntPtr ExceptionInformation3;
        public UIntPtr ExceptionInformation4;
        public UIntPtr ExceptionInformation5;
        public UIntPtr ExceptionInformation6;
        public UIntPtr ExceptionInformation7;
        public UIntPtr ExceptionInformation8;
        public UIntPtr ExceptionInformation9;
        public UIntPtr ExceptionInformation10;
        public UIntPtr ExceptionInformation11;
        public UIntPtr ExceptionInformation12;
        public UIntPtr ExceptionInformation13;
        public UIntPtr ExceptionInformation14;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPTION_DEBUG_INFO
    {
        public EXCEPTION_RECORD ExceptionRecord;
        public uint dwFirstChance;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREATE_PROCESS_DEBUG_INFO
    {
        public IntPtr hFile;
        public IntPtr hProcess;
        public IntPtr hThread;
        public IntPtr lpBaseOfImage;
        public uint dwDebugInfoFileOffset;
        public uint nDebugInfoSize;
        public IntPtr lpThreadLocalBase;
        public IntPtr lpStartAddress;
        public IntPtr lpImageName;
        public ushort fUnicode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LOAD_DLL_DEBUG_INFO
    {
        public IntPtr hFile;
        public IntPtr lpBaseOfDll;
        public uint dwDebugInfoFileOffset;
        public uint nDebugInfoSize;
        public IntPtr lpImageName;
        public ushort fUnicode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXIT_PROCESS_DEBUG_INFO
    {
        public uint dwExitCode;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct DEBUG_EVENT_UNION
    {
        [FieldOffset(0)] public EXCEPTION_DEBUG_INFO Exception;
        [FieldOffset(0)] public CREATE_PROCESS_DEBUG_INFO CreateProcessInfo;
        [FieldOffset(0)] public LOAD_DLL_DEBUG_INFO LoadDll;
        [FieldOffset(0)] public EXIT_PROCESS_DEBUG_INFO ExitProcess;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEBUG_EVENT
    {
        public uint dwDebugEventCode;
        public uint dwProcessId;
        public uint dwThreadId;
        public DEBUG_EVENT_UNION u;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugActiveProcess(uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugActiveProcessStop(uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugSetProcessKillOnExit(bool KillOnExit);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WaitForDebugEvent(ref DEBUG_EVENT lpDebugEvent, uint dwMilliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ContinueDebugEvent(uint dwProcessId, uint dwThreadId, uint dwContinueStatus);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32 lpme);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

    // WOW64 上下文
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Wow64GetThreadContext(IntPtr hThread, [In, Out] byte[] lpContext);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Wow64SetThreadContext(IntPtr hThread, [In] byte[] lpContext);
}

public struct X86Regs
{
    public uint Tid;
    public uint Eax;
    public uint Ecx;
    public uint Edx;
    public uint Ebx;
    public uint Esp;
    public uint Ebp;
    public uint Esi;
    public uint Edi;
    public uint Eip;
    public uint EFlags;
}
