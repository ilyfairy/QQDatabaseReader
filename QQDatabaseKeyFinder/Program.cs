using System.Text;
using Microsoft.Win32;
using QQDatabaseKeyFinder;


Console.OutputEncoding = Encoding.UTF8;

HashSet<string> qqFilePaths = new();
string? qqFilePath = null;

if (OperatingSystem.IsWindows())
{
    try
    {
        if (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Tencent\QQNT") is { } qqNTRegistry)
        {
            if (qqNTRegistry.GetValue("Install") is string qqInstallDirectory)
            {
                qqFilePaths.Add(Path.Combine(qqInstallDirectory, "QQ.exe"));
            }
        }
    }
    catch { }

    qqFilePaths.Add(Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "Tencent", "QQNT", "QQ.exe"));
    qqFilePaths.Add(@"C:\Program Files\Tencent\QQNT\QQ.exe");
    qqFilePaths.Add(@"D:\Program Files\Tencent\QQNT\QQ.exe");
}

foreach (var item in qqFilePaths)
{
    if (File.Exists(item))
    {
        qqFilePath = item;
        break;
    }
}

if (qqFilePath == null)
{
    throw new Exception("没有找到QQ.exe");
}

Console.WriteLine("等待QQ登录...");

var key = QQDebugger.NewProcess(qqFilePath);
if (key is null)
{
    Console.WriteLine("没有找到key");
    return;
}

Console.WriteLine($"Key: {key}");
