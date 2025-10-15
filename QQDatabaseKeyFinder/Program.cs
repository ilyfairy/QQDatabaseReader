using System.Text;
using QQDatabaseKeyFinder;


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

Console.WriteLine("等待QQ登录...");

var key = QQDebugger.NewProcess(qqntFilePath);
if (key is null)
{
    Console.WriteLine("没有找到key");
    return;
}

Console.WriteLine($"Key: {key}");
