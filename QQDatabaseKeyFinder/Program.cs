using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace QQNTDatabaseKeyFinder;

static partial class Program
{
    public static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        HashSet<string> qqFilePaths =
        [
            Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "Tencent", "QQNT", "QQ.exe"),
            @"C:\Program Files\Tencent\QQNT\QQ.exe",
            @"D:\Program Files\Tencent\QQNT\QQ.exe",
        ];
        string? qqntFilePath = null;
        foreach (var item in qqFilePaths)
        {
            if (File.Exists(item))
            {
                qqntFilePath = item;
                break;
            }
        }

        if (qqntFilePath == null)
        {
            throw new Exception("没有找到QQ.exe");
        }

        var keys = QQDebugger.NewProcess(qqntFilePath);
        if (keys.Count == 0)
        {
            Console.WriteLine("没有找到任何key");
            return;
        }

        Console.WriteLine("找到可用Keys: ");
        foreach (var key in keys)
        {
            Console.WriteLine(key);
        }
        Console.WriteLine();

        if (keys.Count > 1)
        {
            Console.WriteLine($"最佳Key: {keys.FirstOrDefault(v => v.Any(v => char.IsSymbol(v)))}");
        }
    }

}

public class QQDebugger
{
    public static IReadOnlyCollection<string> NewProcess(string qqntFilePath)
    {
        using var proc = Process.Start(qqntFilePath);
        proc.WaitForInputIdle();

        int pid = proc.Id;

        var debugger = new NativeDebugger(pid);

        return From(debugger);
    }

    public static IReadOnlyCollection<string> From(NativeDebugger debugger)
    {
        debugger.Attach();
        (var codeAddress, var codeOffset) = FindCodeOffset(debugger);

        HashSet<string> keys = new();

        // 设置软件断点
        debugger.SetSoftwareBreakpoint((ulong)codeAddress, ctx =>
        {
            Span<byte> main = stackalloc byte[4];
            debugger.ReadBytes(ctx.Rdx, 4, ref main[0]);
            if (main.SequenceEqual("main"u8))
            {
                var r8 = debugger.ReadBytes(ctx.R8, 16);
                var key = Encoding.ASCII.GetString(r8);
                if (key.All(static v => char.IsAscii(v) && !char.IsControl(v)) && key.Any(v => char.IsSymbol(v))) // 需要的密钥
                {
                    keys.Add(key);
                    debugger.Detach();
                }
                else if (key.All(static v => char.IsAscii(v) && !char.IsControl(v))) // 不清楚是什么密钥
                {
                    keys.Add(key);
                }
            }
        });

        debugger.Run();

        return keys;
    }

    public static (nint CodeAddress, int CodeOffset) FindCodeOffset(NativeDebugger debugger)
    {

        var module = debugger.FindModule("wrapper.node")!;
        var rdataSection = debugger.FindSection(module.Value, ".rdata")!;
        var textSection = debugger.FindSection(module.Value, ".text")!;


        var rdataMemory = debugger.ReadBytes(rdataSection.Value.StartAddress, (int)rdataSection.Value.VirtualSize);
        var textMemory = debugger.ReadBytes(textSection.Value.StartAddress, (int)textSection.Value.VirtualSize);


        var keyStringIndex = rdataMemory.AsSpan().IndexOf("nt_sqlite3_key_v2: db=%p zDb=%s"u8);
        var keyStringOffset = (nint)rdataSection.Value.StartAddress - (nint)textSection.Value.StartAddress + keyStringIndex;


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

        var textSpan = textMemory.AsSpan();
        Span<byte> findValue = stackalloc byte[7] { 0x48, 0x8D, 0x15, 0, 0, 0, 0 }; // 48 8D 15 x x x x
        Span<byte> offsetAddrSpan = findValue[3..];
        int codeOffset = 0;
        for (int i = 0; i < textSpan.Length - findValue.Length; i++)
        {
            int value = (int)(keyStringOffset - i - findValue.Length);
            Unsafe.As<byte, int>(ref offsetAddrSpan[0]) = value;
            var codeIndex = textSpan[i..(i + findValue.Length)].IndexOf(findValue);
            if (codeIndex != -1)
            {
                codeOffset = (int)(textSection.Value.StartAddress - (ulong)module.Value.hModule + (ulong)i);
            }
        }

        var codeAddress = module.Value.hModule + codeOffset;

        return (codeAddress, codeOffset);
    }
}