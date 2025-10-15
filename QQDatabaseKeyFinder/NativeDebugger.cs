using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace QQDatabaseKeyFinder;

public class NativeDebugger
{
    private const uint TH32CS_SNAPMODULE = 0x00000008;
    private const uint TH32CS_SNAPMODULE32 = 0x00000010;
    private const uint TH32CS_SNAPTHREAD = 0x00000004;

    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    private const uint THREAD_GET_CONTEXT = 0x0008;
    private const uint THREAD_SET_CONTEXT = 0x0010;
    private const uint THREAD_QUERY_INFORMATION = 0x0040;
    private const uint THREAD_ALL_ACCESS = THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_QUERY_INFORMATION;

    private const uint CONTEXT_AMD64 = 0x00100000;
    private const uint CONTEXT_CONTROL = CONTEXT_AMD64 | 0x00000001;
    private const uint CONTEXT_INTEGER = CONTEXT_AMD64 | 0x00000002;

    private const uint EXCEPTION_SINGLE_STEP = 0x80000004;
    private const uint EXCEPTION_BREAKPOINT = 0x80000003;
    private const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;

    private const uint EXCEPTION_DEBUG_EVENT = 1;
    private const uint CREATE_PROCESS_DEBUG_EVENT = 3;
    private const uint EXIT_PROCESS_DEBUG_EVENT = 5;
    private const uint LOAD_DLL_DEBUG_EVENT = 6;

    private readonly uint _processId;
    private IntPtr _hProcess = IntPtr.Zero;
    private bool _attached = false;
    private bool _shouldExit = false;
    private Dictionary<ulong, BreakpointCallback> _softwareBreakpoints = new();
    private Dictionary<uint, ulong> _singleStepThreads = new();

    public NativeDebugger(int processId)
    {
        _processId = (uint)processId;
    }

    public void Attach()
    {
        if (_attached)
            return;
        if (!DebugActiveProcess(_processId))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        DebugSetProcessKillOnExit(false);

        _hProcess = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION, false, _processId);
        if (_hProcess == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        _attached = true;
    }

    public void Detach()
    {
        _shouldExit = true;

        foreach (var (address, _) in _softwareBreakpoints.ToList())
            RestoreSoftwareBreakpoint(address);
        
        _softwareBreakpoints.Clear();

        if (_attached)
        {
            DebugActiveProcessStop(_processId);
            _attached = false;
        }
        if (_hProcess != IntPtr.Zero)
        {
            CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }
    }

    private void RestoreSoftwareBreakpoint(ulong address)
    {
        if (!_softwareBreakpoints.TryGetValue(address, out var callback))
            return;

        const uint PAGE_EXECUTE_READWRITE = 0x40;
        if (!VirtualProtectEx(_hProcess, new IntPtr((long)address), new IntPtr(1), PAGE_EXECUTE_READWRITE, out uint oldProtect))
            return;

        WriteByte(address, callback.OriginalByte);
        VirtualProtectEx(_hProcess, new IntPtr((long)address), new IntPtr(1), oldProtect, out _);
        FlushInstructionCacheRemote(address, 1);
    }

    public MODULEENTRY32? FindModule(string moduleName)
    {
        return GetModuleEntry((int)_processId, moduleName);
    }

    public SectionInfo? FindSection(MODULEENTRY32 module, string sectionName)
    {
        var sections = ReadSections(module.modBaseAddr);
        foreach (var sec in sections)
        {
            if (sec.Name.Equals(sectionName, StringComparison.OrdinalIgnoreCase))
                return sec;
        }
        return null;
    }

    public void SetSoftwareBreakpoint(ulong address, Action<CONTEXT> callback)
    {
        if (_softwareBreakpoints.ContainsKey(address))
            return;

        byte[] original = new byte[1];
        if (!ReadProcessMemory(_hProcess, new IntPtr((long)address), original, 1, out _))
            return;

        _softwareBreakpoints[address] = new BreakpointCallback()
        {
            OriginalByte = original[0],
            Callback = callback,
        };
        
        SetSoftwareBreakpointInternal(address);
    }

    private bool SetSoftwareBreakpointInternal(ulong address)
    {
        const uint PAGE_EXECUTE_READWRITE = 0x40;
        if (!VirtualProtectEx(_hProcess, new IntPtr((long)address), new IntPtr(1), PAGE_EXECUTE_READWRITE, out uint oldProtect))
            return false;

        byte[] int3 = new byte[] { 0xCC };
        bool writeSuccess = WriteBytes(_hProcess, address, int3);

        VirtualProtectEx(_hProcess, new IntPtr((long)address), new IntPtr(1), oldProtect, out _);

        if (!writeSuccess)
            return false;

        return FlushInstructionCacheRemote(address, 1);
    }

    public void Run()
    {
        DEBUG_EVENT debugEvent = new DEBUG_EVENT();

        while (!_shouldExit)
        {
            if (!WaitForDebugEvent(ref debugEvent, -1))
                break;

            uint continueStatus = 0x00010002; // DBG_CONTINUE

            switch (debugEvent.dwDebugEventCode)
            {
                case CREATE_PROCESS_DEBUG_EVENT:
                    if (debugEvent.u.CreateProcessInfo.hFile != IntPtr.Zero)
                        CloseHandle(debugEvent.u.CreateProcessInfo.hFile);
                    break;

                case LOAD_DLL_DEBUG_EVENT:
                    if (debugEvent.u.LoadDll.hFile != IntPtr.Zero)
                        CloseHandle(debugEvent.u.LoadDll.hFile);
                    break;

                case EXCEPTION_DEBUG_EVENT:
                    continueStatus = HandleException(ref debugEvent);
                    break;

                case EXIT_PROCESS_DEBUG_EVENT:
                    ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, continueStatus);
                    return;
            }

            ContinueDebugEvent(debugEvent.dwProcessId, debugEvent.dwThreadId, continueStatus);

            if (_shouldExit)
                break;
        }
    }

    private uint HandleException(ref DEBUG_EVENT debugEvent)
    {
        var exRecord = debugEvent.u.Exception.ExceptionRecord;

        switch (exRecord.ExceptionCode)
        {
            case EXCEPTION_SINGLE_STEP:
                if (_singleStepThreads.TryGetValue(debugEvent.dwThreadId, out ulong bpAddr))
                {
                    _singleStepThreads.Remove(debugEvent.dwThreadId);

                    if (!_shouldExit)
                        SetSoftwareBreakpointInternal(bpAddr);

                    return 0x00010002;
                }
                return 0x00010002;

            case EXCEPTION_BREAKPOINT:
                {
                    ulong ip = (ulong)exRecord.ExceptionAddress.ToInt64();

                    if (_softwareBreakpoints.TryGetValue(ip, out var bpContext))
                    {
                        const uint PAGE_EXECUTE_READWRITE = 0x40;
                        VirtualProtectEx(_hProcess, new IntPtr((long)ip), new IntPtr(1), PAGE_EXECUTE_READWRITE, out uint oldProtect);

                        WriteByte(ip, bpContext.OriginalByte);

                        VirtualProtectEx(_hProcess, new IntPtr((long)ip), new IntPtr(1), oldProtect, out _);
                        FlushInstructionCacheRemote(ip, 1);

                        IntPtr hThread = OpenThread(THREAD_ALL_ACCESS, false, debugEvent.dwThreadId);
                        if (hThread != IntPtr.Zero)
                        {
                            try
                            {
                                CONTEXT ctx = new CONTEXT();
                                ctx.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER;
                                if (GetThreadContext(hThread, ref ctx))
                                {
                                    ctx.Rip = ip;
                                    ctx.EFlags |= 0x100;
                                    SetThreadContext(hThread, ref ctx);

                                    _singleStepThreads[debugEvent.dwThreadId] = ip;

                                    // 执行回调
                                    bpContext.Callback?.Invoke(ctx);
                                }
                            }
                            finally { CloseHandle(hThread); }
                        }
                    }
                    return 0x00010002;
                }

            default:
                return DBG_EXCEPTION_NOT_HANDLED;
        }
    }

    private List<SectionInfo> ReadSections(IntPtr moduleBase)
    {
        var sections = new List<SectionInfo>();
        ulong baseAddr = (ulong)moduleBase;

        uint e_lfanew = ReadUInt32(baseAddr + 0x3C);
        ulong ntHeader = baseAddr + e_lfanew;
        uint signature = ReadUInt32(ntHeader + 0);
        if (signature != 0x00004550)
            return sections;

        ushort numberOfSections = ReadUInt16(ntHeader + 4 + 2);
        ushort sizeOfOptionalHeader = ReadUInt16(ntHeader + 4 + 16);

        ulong sectionHeaders = ntHeader + 4 + 20 + sizeOfOptionalHeader;
        for (int i = 0; i < numberOfSections; i++)
        {
            ulong sh = sectionHeaders + (ulong)i * 40UL;
            string name = ReadAsciiFixed(sh + 0, 8).TrimEnd('\0');
            uint virtualSize = ReadUInt32(sh + 8);
            uint virtualAddress = ReadUInt32(sh + 12);

            sections.Add(new SectionInfo
            {
                Name = name,
                VirtualSize = virtualSize,
                VirtualAddress = virtualAddress,
                StartAddress = baseAddr + virtualAddress
            });
        }

        return sections;
    }

    private string ReadAsciiFixed(ulong address, int count)
    {
        Span<byte> buffer = stackalloc byte[count];
        ReadBytes(address, count, ref buffer[0]);
        return Encoding.ASCII.GetString(buffer);
    }

    private ushort ReadUInt16(ulong address)
    {
        ushort value = 0;
        ReadBytes(address, 2, ref Unsafe.As<ushort, byte>(ref value));
        return value;
    }

    private uint ReadUInt32(ulong address)
    {
        uint value = 0;
        ReadBytes(address, 4, ref Unsafe.As<uint, byte>(ref value));
        return value;
    }

    public unsafe bool ReadBytes(ulong address, int size, ref byte b)
    {
        fixed (void* ptr = &b)
        {
            if (ReadProcessMemory(_hProcess, new IntPtr((long)address), (IntPtr)ptr, size, out IntPtr read) && read.ToInt64() == size)
                return true;
        }
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public byte[] ReadBytes(ulong address, int size)
    {
        byte[] buffer = new byte[size];
        if (!ReadProcessMemory(_hProcess, new IntPtr((long)address), buffer, buffer.Length, out IntPtr read) || read.ToInt64() != size)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return buffer;
    }

    ~NativeDebugger()
    {
        Detach();
    }

    private struct BreakpointCallback
    {
        public byte OriginalByte;
        public Action<CONTEXT> Callback;
    }

    private static MODULEENTRY32? GetModuleEntry(int pid, string moduleName)
    {
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)pid);
        if (snap == IntPtr.Zero || snap == (IntPtr)(-1))
            return null;
        try
        {
            MODULEENTRY32 me = new MODULEENTRY32();
            me.dwSize = (uint)Marshal.SizeOf<MODULEENTRY32>();
            if (!Module32FirstW(snap, ref me))
                return null;
            do
            {
                if (me.szModule.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                    return me;
            }
            while (Module32NextW(snap, ref me));
        }
        finally
        {
            CloseHandle(snap);
        }
        return null;
    }

    private unsafe bool WriteByte(ulong address, byte data)
    {
        return WriteProcessMemory(_hProcess, new IntPtr((long)address), new IntPtr(&data), 1, out _);
    }

    private static bool WriteBytes(IntPtr hProcess, ulong address, ReadOnlySpan<byte> data)
    {
        unsafe
        {
            fixed (byte* p = data)
            {
                return WriteProcessMemory(hProcess, new IntPtr((long)address), new IntPtr(p), data.Length, out _);
            }
        }
    }

    private bool FlushInstructionCacheRemote(ulong address, int size)
    {
        return FlushInstructionCache(_hProcess, new IntPtr((long)address), (UIntPtr)size);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MODULEENTRY32
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
    public struct M128A
    {
        public ulong Low;
        public long High;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct XMM_SAVE_AREA64
    {
        public ushort ControlWord;
        public ushort StatusWord;
        public byte TagWord;
        public byte Reserved1;
        public ushort ErrorOpcode;
        public uint ErrorOffset;
        public ushort ErrorSelector;
        public ushort Reserved2;
        public uint DataOffset;
        public ushort DataSelector;
        public ushort Reserved3;
        public uint MxCsr;
        public uint MxCsr_Mask;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public M128A[] FloatRegisters;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public M128A[] XmmRegisters;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 96)] public byte[] Reserved4;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct CONTEXT
    {
        public ulong P1Home;
        public ulong P2Home;
        public ulong P3Home;
        public ulong P4Home;
        public ulong P5Home;
        public ulong P6Home;

        public uint ContextFlags;
        public uint MxCsr;

        public ushort SegCs;
        public ushort SegDs;
        public ushort SegEs;
        public ushort SegFs;
        public ushort SegGs;
        public ushort SegSs;
        public uint EFlags;

        public ulong Dr0;
        public ulong Dr1;
        public ulong Dr2;
        public ulong Dr3;
        public ulong Dr6;
        public ulong Dr7;

        public ulong Rax;
        public ulong Rcx;
        public ulong Rdx;
        public ulong Rbx;
        public ulong Rsp;
        public ulong Rbp;
        public ulong Rsi;
        public ulong Rdi;
        public ulong R8;
        public ulong R9;
        public ulong R10;
        public ulong R11;
        public ulong R12;
        public ulong R13;
        public ulong R14;
        public ulong R15;

        public ulong Rip;

        public XMM_SAVE_AREA64 DUMMYUNIONNAME;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 26)] public M128A[] VectorRegister;
        public ulong VectorControl;

        public ulong DebugControl;
        public ulong LastBranchToRip;
        public ulong LastBranchFromRip;
        public ulong LastExceptionToRip;
        public ulong LastExceptionFromRip;
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

    public struct SectionInfo
    {
        public string Name;
        public uint VirtualAddress;
        public uint VirtualSize;
        public ulong StartAddress;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugActiveProcess(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugActiveProcessStop(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WaitForDebugEvent(ref DEBUG_EVENT lpDebugEvent, int dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ContinueDebugEvent(uint dwProcessId, uint dwThreadId, uint dwContinueStatus);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugSetProcessKillOnExit(bool KillOnExit);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadContext(IntPtr hThread, ref CONTEXT lpContext);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadContext(IntPtr hThread, ref CONTEXT lpContext);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);
}
