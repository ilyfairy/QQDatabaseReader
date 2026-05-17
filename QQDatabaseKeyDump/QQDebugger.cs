using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace QQDatabaseKeyDump;

public class QQDebugger
{
    public static string? NewProcess(string qqntFilePath)
    {
        using var proc = Process.Start(qqntFilePath);
        proc.WaitForInputIdle();

        int pid = proc.Id;

        var debugger = new NativeDebugger(pid);

        return From(debugger);
    }

    public static string? From(NativeDebugger debugger)
    {
        debugger.Attach();
        if (!TryFindCodeOffset(debugger, out var codeAddress, out var codeOffset))
        {
            return null;
        }

        string? key = null;

        // 设置软件断点
        debugger.SetSoftwareBreakpoint((ulong)codeAddress, ctx =>
        {
            Span<byte> main = stackalloc byte[4];
            debugger.ReadBytes(ctx.Rdx, 4, ref main[0]);
            if (main.SequenceEqual("main"u8))
            {
                var r8 = debugger.ReadBytes(ctx.R8, 16);
                var tempKey = Encoding.ASCII.GetString(r8);
                if (IsBestKey(tempKey))
                {
                    key = tempKey;
                    debugger.Detach();
                }
            }
        });

        debugger.Run();

        return key;
    }

    public static bool IsBestKey(string key)
    {
        return key.All(v => char.IsAscii(v) && !char.IsControl(v)) && !key.All(v => char.IsAsciiHexDigitUpper(v));
    }

    public static unsafe bool TryFindCodeOffset(NativeDebugger debugger, out nint codeAddress, out nint codeOffset)
    {
        var module = debugger.FindModule("wrapper.node");
        if(module == null)
        {
            codeAddress = codeOffset = 0;
            return false;
        }

        var rdataSection = debugger.FindSection(module.Value, ".rdata")!;
        var textSection = debugger.FindSection(module.Value, ".text")!;

        nint keyStringOffset;
        {
            byte* rdataMemoryPtr = null;
            try
            {
                rdataMemoryPtr = (byte*)NativeMemory.Alloc(rdataSection.Value.VirtualSize);
                debugger.ReadBytes(rdataSection.Value.StartAddress, (int)rdataSection.Value.VirtualSize, ref rdataMemoryPtr[0]);
                var keyStringIndex = new Span<byte>(rdataMemoryPtr, (int)rdataSection.Value.VirtualSize).IndexOf("nt_sqlite3_key_v2: db=%p zDb=%s"u8);
                keyStringOffset = (nint)rdataSection.Value.StartAddress - (nint)textSection.Value.StartAddress + keyStringIndex;
            }
            finally
            {
                if(rdataMemoryPtr != null)
                    NativeMemory.Free(rdataMemoryPtr);
            }
        }

        byte* textMemoryPtr = null;
        try
        {
            textMemoryPtr = (byte*)NativeMemory.Alloc(textSection.Value.VirtualSize);
            debugger.ReadBytes(textSection.Value.StartAddress, (int)textSection.Value.VirtualSize, ref textMemoryPtr[0]);
            var textSpan = new Span<byte>(textMemoryPtr, (int)textSection.Value.VirtualSize);

            /**
             * 
             * 基地址: 0x00007FF9973E1000
             * 代码(.text):    0x00007FF999450A35  0x206FA35
             * 字符串(.rdata): 0x00007FF99A955D63  0x3574D63
             * 
             * 0x3574D63 - 0x206FA35 = 字符串地址 - 代码地址 - 代码大小 = 0x1505327 在内存中是27 53 50 01
             * 加上指令前缀 =  48 8D15 27 53 50 01
             * 
             * x64dbg: 00007FF999450A35  | 48:8D15 27535001    | lea rdx,qword ptr ds:[7FF99A955D63]
             * 
             */

            Span<byte> findValue = stackalloc byte[7] { 0x48, 0x8D, 0x15, 0, 0, 0, 0 }; // 48 8D 15 x x x x
            Span<byte> pinnedInstructionSpan = findValue[..3];
            Span<byte> offsetAddrSpan = findValue[3..];
            for (int i = 0; i < textSpan.Length - findValue.Length; i++)
            {
                var currentSpan = textSpan[i..(i + findValue.Length)];
                if (currentSpan[..3].SequenceEqual(pinnedInstructionSpan)) // 先找指令部分
                {
                    int value = (int)(keyStringOffset - i - findValue.Length);
                    Unsafe.As<byte, int>(ref offsetAddrSpan[0]) = value;
                    if (currentSpan[3..].SequenceEqual(offsetAddrSpan)) // 再找地址部分
                    {
                        codeOffset = (int)(textSection.Value.StartAddress - (ulong)module.Value.hModule + (ulong)i);
                        codeAddress = module.Value.hModule + codeOffset;
                        return true;
                    }
                }
            }

            codeAddress = codeOffset = 0;
            return false;
        }
        finally
        {
            if(textMemoryPtr != null)
                NativeMemory.Free(textMemoryPtr);
        }
    }
}
