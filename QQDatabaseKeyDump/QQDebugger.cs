using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace QQDatabaseKeyDump;

public class QQDebugger
{
    /// <param name="killAfterKeyFound">拿到密钥后结束这个 QQ (连同它启动的子进程), 没拿到时不结束</param>
    public static string? NewProcess(string qqntFilePath, bool killAfterKeyFound = false)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = qqntFilePath,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start QQ.");
        proc.WaitForInputIdle();

        string? key;
        using (var debugger = new NativeDebugger(proc.Id))
        {
            key = From(debugger);
        }

        if (key is not null && killAfterKeyFound)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception) when (proc.HasExited)
            {
                // Already closed by the user
            }
        }

        return key;
    }

    public static string? From(NativeDebugger debugger)
    {
        try
        {
            debugger.Attach();
            if (!TryFindCodeOffset(debugger, out var codeAddress, out _))
                return null;

            string? key = null;

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
                        debugger.RequestStop();
                    }
                }
            });

            debugger.Run();

            return key;
        }
        finally
        {
            debugger.Detach();
        }
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

            // 在 .text 中定位引用密钥格式字符串的 LEA 指令。
            Span<byte> findValue = stackalloc byte[7] { 0x48, 0x8D, 0x15, 0, 0, 0, 0 };
            Span<byte> pinnedInstructionSpan = findValue[..3];
            Span<byte> offsetAddrSpan = findValue[3..];
            for (int i = 0; i < textSpan.Length - findValue.Length; i++)
            {
                var currentSpan = textSpan[i..(i + findValue.Length)];
                if (currentSpan[..3].SequenceEqual(pinnedInstructionSpan))
                {
                    int value = (int)(keyStringOffset - i - findValue.Length);
                    Unsafe.As<byte, int>(ref offsetAddrSpan[0]) = value;
                    if (currentSpan[3..].SequenceEqual(offsetAddrSpan))
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
