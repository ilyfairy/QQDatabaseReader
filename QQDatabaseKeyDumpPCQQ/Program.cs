using System.Text;
using QQDatabaseKeyDumpPCQQ;

Console.OutputEncoding = Encoding.UTF8;

using var dumper = new PCQQKeyDumper(Console.WriteLine, Console.Error.WriteLine);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
    dumper.Stop();
};

try
{
    return dumper.Run(cancellationToken: cancellation.Token);
}
catch (OperationCanceledException)
{
    return 0;
}
