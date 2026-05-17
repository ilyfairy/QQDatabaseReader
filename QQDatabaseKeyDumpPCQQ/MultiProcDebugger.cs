using System.Runtime.InteropServices;
using System.Text;

namespace QQDatabaseKeyDumpPCQQ;

/// <summary>
/// 多进程 32 位 (WOW64) 调试器：用 CreateProcess(DEBUG_PROCESS) 启动目标，
/// 自动跟踪由其 fork 出来的所有子进程。可在每个子进程加载特定 DLL 后扫描签名 + 设置断点。
/// </summary>
public sealed class MultiProcDebugger : IDisposable
{
    // ========== 常量 ==========
    private const uint DEBUG_PROCESS = 0x00000001;
    private const uint DEBUG_ONLY_THIS_PROCESS = 0x00000002;

    private const uint EXCEPTION_BREAKPOINT  = 0x80000003;
    private const uint EXCEPTION_SINGLE_STEP = 0x80000004;
    private const uint STATUS_WX86_BREAKPOINT  = 0x4000001F;
    private const uint STATUS_WX86_SINGLE_STEP = 0x4000001E;
    private const uint DBG_CONTINUE              = 0x00010002;
    private const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;

    private const uint EXCEPTION_DEBUG_EVENT      = 1;
    private const uint CREATE_THREAD_DEBUG_EVENT  = 2;
    private const uint CREATE_PROCESS_DEBUG_EVENT = 3;
    private const uint EXIT_THREAD_DEBUG_EVENT    = 4;
    private const uint EXIT_PROCESS_DEBUG_EVENT   = 5;
    private const uint LOAD_DLL_DEBUG_EVENT       = 6;
    private const uint UNLOAD_DLL_DEBUG_EVENT     = 7;
    private const uint OUTPUT_DEBUG_STRING_EVENT  = 8;

    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    private const uint THREAD_ALL_ACCESS = 0x001F03FF;

    private const uint WOW64_CONTEXT_i386    = 0x00010000;
    private const uint WOW64_CONTEXT_CONTROL = WOW64_CONTEXT_i386 | 0x00000001;
    private const uint WOW64_CONTEXT_INTEGER = WOW64_CONTEXT_i386 | 0x00000002;
    private const int WOW64_CONTEXT_SIZE = 716;
    private const int WOW64_OFFSET_EFLAGS = 192;
    private const int WOW64_OFFSET_EIP    = 184;
    private const int WOW64_OFFSET_ESP    = 196;

    // ========== 进程上下文 ==========
    private sealed class ProcCtx
    {
        public uint Pid;
        public IntPtr hProcess;
        public Dictionary<ulong, Bp> Breakpoints = new();
        public Dictionary<uint, ulong> SingleStepThreads = new(); // tid -> bp addr to restore
        public Dictionary<ulong, string> LoadedModules = new();   // base -> (lower-cased) path
    }
    private sealed class Bp
    {
        public byte OriginalByte;
        public Action<uint, X86Regs> OnHit = (_, _) => { };
    }

    private readonly Dictionary<uint, ProcCtx> _procs = new();
    private bool _shouldExit;

    public bool VerboseEvents { get; set; } = false;

    /// <summary>每个子进程加载某个 DLL 后触发；可用此回调时调用 SetBreakpoint。</summary>
    public Action<uint, string, ulong, uint /*size*/> OnDllLoaded { get; set; } = (_, _, _, _) => { };

    public void Stop(bool terminateProcesses = true)
    {
        _shouldExit = true;
        if (!terminateProcesses)
            return;

        foreach (var pid in _procs.Keys.ToArray())
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
    }

    public static MultiProcDebugger Spawn(string exePath, string? args = null)
    {
        var dbg = new MultiProcDebugger();
        var si = new STARTUPINFO();
        si.cb = (uint)Marshal.SizeOf<STARTUPINFO>();
        var pi = new PROCESS_INFORMATION();
        string cmd = args is null ? $"\"{exePath}\"" : $"\"{exePath}\" {args}";
        if (!CreateProcessW(null!, cmd, IntPtr.Zero, IntPtr.Zero, false,
                DEBUG_PROCESS, IntPtr.Zero, Path.GetDirectoryName(exePath)!, ref si, out pi))
        {
            int err = Marshal.GetLastWin32Error();
            throw new System.ComponentModel.Win32Exception(err, $"CreateProcess({exePath}) failed");
        }
        // 主进程的 hProcess 会通过 CREATE_PROCESS_DEBUG_EVENT 上报，关闭这里的 handle 防止泄漏
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        return dbg;
    }

    public void Dispose()
    {
        Stop();
        foreach (var p in _procs.Values)
            if (p.hProcess != IntPtr.Zero) CloseHandle(p.hProcess);
        _procs.Clear();
    }

    public void SetBreakpoint(uint pid, ulong address, Action<uint, X86Regs> onHit)
    {
        if (!_procs.TryGetValue(pid, out var pc)) throw new InvalidOperationException($"pid {pid} unknown");
        if (pc.Breakpoints.ContainsKey(address)) return;
        byte[] orig = ReadBytes(pid, address, 1);
        pc.Breakpoints[address] = new Bp { OriginalByte = orig[0], OnHit = onHit };
        WriteByteWithProtect(pc.hProcess, address, 0xCC);
        FlushInstructionCache(pc.hProcess, (IntPtr)(long)address, (UIntPtr)1);
    }

    public byte[] ReadBytes(uint pid, ulong addr, int size)
    {
        if (!_procs.TryGetValue(pid, out var pc))
            throw new InvalidOperationException($"pid {pid} unknown");
        byte[] buf = new byte[size];
        if (!ReadProcessMemory(pc.hProcess, (IntPtr)(long)addr, buf, size, out IntPtr r))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"RPM(pid={pid}, 0x{addr:X}, {size})");
        if (r.ToInt64() != size) Array.Resize(ref buf, (int)r.ToInt64());
        return buf;
    }

    public uint ReadUInt32(uint pid, ulong addr) => BitConverter.ToUInt32(ReadBytes(pid, addr, 4), 0);

    public ulong ScanMemory(uint pid, ulong baseAddr, uint size, ReadOnlySpan<byte> pattern)
    {
        if (!_procs.TryGetValue(pid, out var pc)) return 0;
        const int CHUNK = 1 << 20;
        int patLen = pattern.Length;
        if (patLen == 0 || size < patLen) return 0;
        byte[] tail = new byte[patLen - 1];
        int tailLen = 0;
        ulong off = 0;
        byte[] chunk = new byte[CHUNK];
        while (off < size)
        {
            int toRead = (int)Math.Min((ulong)CHUNK, size - off);
            if (!ReadProcessMemory(pc.hProcess, (IntPtr)(long)(baseAddr + off), chunk, toRead, out IntPtr r))
            { off += (ulong)toRead; tailLen = 0; continue; }
            int got = (int)r.ToInt64();
            if (got <= 0) { off += (ulong)toRead; tailLen = 0; continue; }
            if (tailLen > 0)
            {
                byte[] join = new byte[tailLen + Math.Min(patLen - 1, got)];
                Buffer.BlockCopy(tail, 0, join, 0, tailLen);
                Buffer.BlockCopy(chunk, 0, join, tailLen, join.Length - tailLen);
                int idx = join.AsSpan().IndexOf(pattern);
                if (idx >= 0) return baseAddr + off - (ulong)tailLen + (ulong)idx;
            }
            int hit = chunk.AsSpan(0, got).IndexOf(pattern);
            if (hit >= 0) return baseAddr + off + (ulong)hit;
            int keep = Math.Min(patLen - 1, got);
            Buffer.BlockCopy(chunk, got - keep, tail, 0, keep);
            tailLen = keep;
            off += (ulong)got;
        }
        return 0;
    }

    public void Run()
    {
        DEBUG_EVENT ev = default;
        var hasDebuggedProcess = false;
        while (!_shouldExit)
        {
            if (!WaitForDebugEvent(ref ev, 100))
            {
                if (hasDebuggedProcess && _procs.Count == 0)
                    break;

                continue;
            }

            uint cont = DBG_CONTINUE;
            switch (ev.dwDebugEventCode)
            {
                case CREATE_PROCESS_DEBUG_EVENT:
                    hasDebuggedProcess = true;
                    HandleCreateProcess(ref ev);
                    break;
                case LOAD_DLL_DEBUG_EVENT:
                    HandleLoadDll(ref ev);
                    break;
                case UNLOAD_DLL_DEBUG_EVENT:
                    if (_procs.TryGetValue(ev.dwProcessId, out var pcU))
                        pcU.LoadedModules.Remove((ulong)ev.u.UnloadDll.lpBaseOfDll.ToInt64() & 0xFFFFFFFFUL);
                    break;
                case EXCEPTION_DEBUG_EVENT:
                    cont = HandleException(ref ev);
                    break;
                case EXIT_PROCESS_DEBUG_EVENT:
                    if (VerboseEvents) Console.WriteLine($"[dbg] EXIT pid={ev.dwProcessId} code=0x{ev.u.ExitProcess.dwExitCode:X}");
                    if (_procs.TryGetValue(ev.dwProcessId, out var pcE))
                    {
                        if (pcE.hProcess != IntPtr.Zero) CloseHandle(pcE.hProcess);
                        _procs.Remove(ev.dwProcessId);
                    }
                    break;
            }
            ContinueDebugEvent(ev.dwProcessId, ev.dwThreadId, cont);

            if (hasDebuggedProcess && _procs.Count == 0)
                break;
        }
    }

    private void HandleCreateProcess(ref DEBUG_EVENT ev)
    {
        var ci = ev.u.CreateProcessInfo;
        var pc = new ProcCtx { Pid = ev.dwProcessId, hProcess = ci.hProcess };
        _procs[ev.dwProcessId] = pc;
        if (VerboseEvents) Console.WriteLine($"[dbg] CREATE_PROCESS pid={ev.dwProcessId} hProcess=0x{ci.hProcess.ToInt64():X} base=0x{ci.lpBaseOfImage.ToInt64():X}");
        if (ci.hFile != IntPtr.Zero) CloseHandle(ci.hFile);
    }

    private void HandleLoadDll(ref DEBUG_EVENT ev)
    {
        var ld = ev.u.LoadDll;
        if (!_procs.TryGetValue(ev.dwProcessId, out var pc)) { if (ld.hFile != IntPtr.Zero) CloseHandle(ld.hFile); return; }
        ulong baseAddr = (ulong)ld.lpBaseOfDll.ToInt64() & 0xFFFFFFFFUL;
        string path = ld.hFile != IntPtr.Zero ? GetFinalPath(ld.hFile) : "";
        if (ld.hFile != IntPtr.Zero) CloseHandle(ld.hFile);
        string dllName = string.IsNullOrEmpty(path) ? $"<unknown@0x{baseAddr:X}>" : Path.GetFileName(path);
        pc.LoadedModules[baseAddr] = path;
        if (VerboseEvents) Console.WriteLine($"[dbg] LOAD_DLL pid={ev.dwProcessId} {dllName} base=0x{baseAddr:X}");
        // 计算 DLL size：读 PE header
        uint size = 0;
        try
        {
            byte[] hdr = ReadBytes(ev.dwProcessId, baseAddr, 0x400);
            int peOff = BitConverter.ToInt32(hdr, 0x3C);
            if (peOff > 0 && peOff < 0x300)
            {
                uint imgSize = BitConverter.ToUInt32(hdr, peOff + 24 + 56);
                size = imgSize;
            }
        }
        catch { }
        try { OnDllLoaded(ev.dwProcessId, dllName, baseAddr, size); }
        catch (Exception ex) { Console.Error.WriteLine($"OnDllLoaded callback ex: {ex}"); }
    }

    private uint HandleException(ref DEBUG_EVENT ev)
    {
        var er = ev.u.Exception.ExceptionRecord;
        bool isBp = er.ExceptionCode == EXCEPTION_BREAKPOINT  || er.ExceptionCode == STATUS_WX86_BREAKPOINT;
        bool isSs = er.ExceptionCode == EXCEPTION_SINGLE_STEP || er.ExceptionCode == STATUS_WX86_SINGLE_STEP;
        if (!_procs.TryGetValue(ev.dwProcessId, out var pc)) return DBG_EXCEPTION_NOT_HANDLED;

        if (isSs)
        {
            if (pc.SingleStepThreads.TryGetValue(ev.dwThreadId, out ulong bpAddr))
            {
                pc.SingleStepThreads.Remove(ev.dwThreadId);
                if (pc.Breakpoints.ContainsKey(bpAddr))
                {
                    WriteByteWithProtect(pc.hProcess, bpAddr, 0xCC);
                    FlushInstructionCache(pc.hProcess, (IntPtr)(long)bpAddr, (UIntPtr)1);
                }
                return DBG_CONTINUE;
            }
            return DBG_EXCEPTION_NOT_HANDLED;
        }
        if (!isBp) return DBG_EXCEPTION_NOT_HANDLED;

        ulong ip = (ulong)er.ExceptionAddress.ToInt64() & 0xFFFFFFFFUL;
        if (!pc.Breakpoints.TryGetValue(ip, out var bp))
            return DBG_CONTINUE; // 不是我们的（系统初始 BP）

        // 还原原指令
        WriteByteWithProtect(pc.hProcess, ip, bp.OriginalByte);
        FlushInstructionCache(pc.hProcess, (IntPtr)(long)ip, (UIntPtr)1);

        IntPtr hT = OpenThread(THREAD_ALL_ACCESS, false, ev.dwThreadId);
        if (hT == IntPtr.Zero) return DBG_CONTINUE;
        try
        {
            byte[] ctxBuf = AllocCtxBuffer();
            SetCtxFlags(ctxBuf, WOW64_CONTEXT_CONTROL | WOW64_CONTEXT_INTEGER);
            if (!Wow64GetThreadContext(hT, ctxBuf)) return DBG_CONTINUE;
            X86Regs regs = ParseRegs(ctxBuf);
            regs.Tid = ev.dwThreadId;
            try { bp.OnHit(ev.dwProcessId, regs); }
            catch (Exception ex) { Console.Error.WriteLine($"BP callback ex: {ex}"); }

            // 持续模式：把 EIP 回退到 INT3 处，设 TF 单步执行原指令后再恢复 INT3
            uint eip = BitConverter.ToUInt32(ctxBuf, WOW64_OFFSET_EIP);
            uint eflags = BitConverter.ToUInt32(ctxBuf, WOW64_OFFSET_EFLAGS);
            if (eip != (uint)ip) BitConverter.GetBytes((uint)ip).CopyTo(ctxBuf, WOW64_OFFSET_EIP);
            BitConverter.GetBytes(eflags | 0x100u).CopyTo(ctxBuf, WOW64_OFFSET_EFLAGS);
            Wow64SetThreadContext(hT, ctxBuf);
            pc.SingleStepThreads[ev.dwThreadId] = ip;
        }
        finally { CloseHandle(hT); }
        return DBG_CONTINUE;
    }

    // ===== helpers =====
    private static byte[] AllocCtxBuffer()
    {
        // 16 字节对齐
        return new byte[((WOW64_CONTEXT_SIZE + 15) / 16) * 16];
    }

    private static void SetCtxFlags(byte[] buf, uint flags)
    {
        buf[0] = (byte)(flags & 0xFF);
        buf[1] = (byte)((flags >> 8) & 0xFF);
        buf[2] = (byte)((flags >> 16) & 0xFF);
        buf[3] = (byte)((flags >> 24) & 0xFF);
    }

    private static X86Regs ParseRegs(byte[] buf) => new()
    {
        Eax = BitConverter.ToUInt32(buf, 176),
        Ecx = BitConverter.ToUInt32(buf, 172),
        Edx = BitConverter.ToUInt32(buf, 168),
        Ebx = BitConverter.ToUInt32(buf, 164),
        Esp = BitConverter.ToUInt32(buf, WOW64_OFFSET_ESP),
        Ebp = BitConverter.ToUInt32(buf, 180),
        Esi = BitConverter.ToUInt32(buf, 160),
        Edi = BitConverter.ToUInt32(buf, 156),
        Eip = BitConverter.ToUInt32(buf, WOW64_OFFSET_EIP),
        EFlags = BitConverter.ToUInt32(buf, WOW64_OFFSET_EFLAGS),
    };

    private static void WriteByteWithProtect(IntPtr hProc, ulong address, byte value)
    {
        VirtualProtectEx(hProc, (IntPtr)(long)address, (IntPtr)1, PAGE_EXECUTE_READWRITE, out uint old);
        WriteProcessMemory(hProc, (IntPtr)(long)address, new byte[] { value }, 1, out _);
        VirtualProtectEx(hProc, (IntPtr)(long)address, (IntPtr)1, old, out _);
    }

    private static string GetFinalPath(IntPtr hFile)
    {
        var sb = new StringBuilder(520);
        uint len = GetFinalPathNameByHandleW(hFile, sb, (uint)sb.Capacity, 0);
        if (len == 0 || len > sb.Capacity) return "";
        string p = sb.ToString();
        // 去掉 \\?\ 前缀
        if (p.StartsWith(@"\\?\")) p = p.Substring(4);
        return p;
    }

    // ===== Win32 =====
    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPTION_RECORD
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public IntPtr ExceptionRecord;
        public IntPtr ExceptionAddress;
        public uint NumberParameters;
        public UIntPtr I0, I1, I2, I3, I4, I5, I6, I7, I8, I9, I10, I11, I12, I13, I14;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPTION_DEBUG_INFO { public EXCEPTION_RECORD ExceptionRecord; public uint dwFirstChance; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CREATE_PROCESS_DEBUG_INFO
    {
        public IntPtr hFile, hProcess, hThread, lpBaseOfImage;
        public uint dwDebugInfoFileOffset, nDebugInfoSize;
        public IntPtr lpThreadLocalBase, lpStartAddress, lpImageName;
        public ushort fUnicode;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct LOAD_DLL_DEBUG_INFO
    {
        public IntPtr hFile, lpBaseOfDll;
        public uint dwDebugInfoFileOffset, nDebugInfoSize;
        public IntPtr lpImageName;
        public ushort fUnicode;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct UNLOAD_DLL_DEBUG_INFO { public IntPtr lpBaseOfDll; }
    [StructLayout(LayoutKind.Sequential)]
    private struct EXIT_PROCESS_DEBUG_INFO { public uint dwExitCode; }
    [StructLayout(LayoutKind.Explicit)]
    private struct DEBUG_EVENT_UNION
    {
        [FieldOffset(0)] public EXCEPTION_DEBUG_INFO Exception;
        [FieldOffset(0)] public CREATE_PROCESS_DEBUG_INFO CreateProcessInfo;
        [FieldOffset(0)] public LOAD_DLL_DEBUG_INFO LoadDll;
        [FieldOffset(0)] public UNLOAD_DLL_DEBUG_INFO UnloadDll;
        [FieldOffset(0)] public EXIT_PROCESS_DEBUG_INFO ExitProcess;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DEBUG_EVENT
    {
        public uint dwDebugEventCode, dwProcessId, dwThreadId;
        public DEBUG_EVENT_UNION u;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WaitForDebugEvent(ref DEBUG_EVENT ev, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ContinueDebugEvent(uint pid, uint tid, uint status);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint da, bool inh, uint tid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProc, IntPtr addr, [Out] byte[] buf, int sz, out IntPtr r);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProc, IntPtr addr, byte[] buf, int sz, out IntPtr w);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr hProc, IntPtr addr, IntPtr sz, uint flNew, out uint flOld);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr hProc, IntPtr addr, UIntPtr sz);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Wow64GetThreadContext(IntPtr hT, [In, Out] byte[] ctx);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Wow64SetThreadContext(IntPtr hT, [In] byte[] ctx);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugSetProcessKillOnExit(bool kill);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);
}
