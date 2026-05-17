using System.Text;

namespace QQDatabaseKeyDumpPCQQ;

public sealed class PCQQKeyDumper : IDisposable
{
    private readonly Action<string> _log;
    private readonly Action<string> _errorLog;
    private readonly Dictionary<(uint pid, uint tid), string> _lastOpen = new();
    private readonly HashSet<string> _printed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _infoPrinted = new(StringComparer.OrdinalIgnoreCase);
    private MultiProcDebugger? _debugger;
    private string? _qqExe;

    public PCQQKeyDumper(Action<string> log, Action<string>? errorLog = null)
    {
        _log = log;
        _errorLog = errorLog ?? log;
    }

    public int Run(PCQQKeyDumpOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PCQQKeyDumpOptions();

        if (!OperatingSystem.IsWindows())
        {
            _errorLog("仅支持 Windows");
            return 2;
        }

        _qqExe = ResolveQQExe(options.QQFilePath);
        if (_qqExe is null)
        {
            _errorLog("找不到 PCQQ。请填写 PCQQ.exe 路径，或把 PCQQ 安装到默认路径。");
            return 3;
        }

        using var cancellationRegistration = cancellationToken.Register(Stop);
        _lastOpen.Clear();
        _printed.Clear();
        _infoPrinted.Clear();

        var infoIndex = BuildInfoDbPayloadIndex();
        _log($"[Info.db] indexed InfoDb={infoIndex.InfoDbCount}, streams={infoIndex.StreamCount}, ES records={infoIndex.EsRecordCount}");

        cancellationToken.ThrowIfCancellationRequested();

        using var dbg = MultiProcDebugger.Spawn(_qqExe);
        _debugger = dbg;
        dbg.OnDllLoaded = (pid, name, baseAddr, size) =>
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            if (name.Equals("KernelUtil.dll", StringComparison.OrdinalIgnoreCase))
            {
                HookKernelUtilSqlite(dbg, pid, baseAddr, size);
            }
            else if (name.Equals("Common.dll", StringComparison.OrdinalIgnoreCase))
            {
                HookCommonInfoDbTea(dbg, infoIndex, pid, baseAddr);
            }
        };

        try
        {
            dbg.Run();
            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }
        finally
        {
            _debugger = null;
        }
    }

    public void Stop()
    {
        _debugger?.Stop();
    }

    private InfoDbPayloadIndex BuildInfoDbPayloadIndex()
    {
        try
        {
            return InfoDbPayloadIndex.BuildDefault(_errorLog);
        }
        catch (Exception ex)
        {
            _errorLog($"[Info.db] index failed: {ex.Message}");
            return new InfoDbPayloadIndex();
        }
    }

    private void HookKernelUtilSqlite(MultiProcDebugger dbg, uint pid, ulong baseAddr, uint size)
    {
        if (size == 0) size = 0x200000;
        ulong addrKey = dbg.ScanMemory(pid, baseAddr, size, PCQQKeyDumpPatterns.SqliteKey);
        ulong addrOpen = dbg.ScanMemory(pid, baseAddr, size, PCQQKeyDumpPatterns.SqliteOpen);
        if (addrKey != 0 || addrOpen != 0)
            _log($"[KernelUtil] pid={pid} sqlite3_key=0x{addrKey:X} sqlite3_open=0x{addrOpen:X}");

        if (addrKey != 0)
        {
            dbg.SetBreakpoint(pid, addrKey, (p, ctx) =>
            {
                try
                {
                    uint pKey = dbg.ReadUInt32(p, ctx.Esp + 8);
                    int nKey = (int)dbg.ReadUInt32(p, ctx.Esp + 12);
                    if (nKey <= 0 || nKey > 4096 || pKey == 0) return;
                    byte[] key = dbg.ReadBytes(p, pKey, nKey);
                    string file = _lastOpen.TryGetValue((p, ctx.Tid), out var f) ? f : "<unknown>";
                    if (file.StartsWith("<", StringComparison.Ordinal)) return;

                    string keyHex = Convert.ToHexString(key);
                    string type = key.All(b => b >= 0x20 && b < 0x7F) ? "ASCII" : "HEX";
                    string value = type == "ASCII" ? Encoding.ASCII.GetString(key) : keyHex;
                    string dedupe = $"{file}|{keyHex}";
                    if (!_printed.Add(dedupe)) return;

                    bool isMsg = IsDatabase(file, "Msg3.0.db");
                    string mark = isMsg ? "  <<< Msg3.0.db !!" : "";
                    _log($"[SQLite] pid={p} {type}  {value}  ->  {file}{mark}");
                }
                catch
                {
                }
            });
        }

        if (addrOpen != 0)
        {
            dbg.SetBreakpoint(pid, addrOpen, (p, ctx) =>
            {
                try
                {
                    uint pFile = dbg.ReadUInt32(p, ctx.Esp + 4);
                    string file = ReadCString(dbg, p, pFile, 1024);
                    _lastOpen[(p, ctx.Tid)] = file;
                }
                catch
                {
                }
            });
        }
    }

    private void HookCommonInfoDbTea(
        MultiProcDebugger dbg,
        InfoDbPayloadIndex infoIndex,
        uint pid,
        ulong baseAddr)
    {
        string? commonPath = new[]
        {
            Path.Combine(Path.GetDirectoryName(_qqExe!)!, "Common.dll"),
            @"C:\Program Files (x86)\Tencent\QQ\Bin\Common.dll",
            @"C:\Program Files\Tencent\QQ\Bin\Common.dll",
            @"D:\Program Files (x86)\Tencent\QQ\Bin\Common.dll",
            @"D:\Program Files\Tencent\QQ\Bin\Common.dll",
        }.FirstOrDefault(File.Exists);
        if (commonPath is null)
        {
            _log("[Common] Common.dll on disk not found; skip Info.db hook");
            return;
        }

        foreach (var exp in new[]
        {
            (Label: "InfoDB/TEA decrypt", Name: PCQQKeyDumpPatterns.CommonDecryptExport),
            (Label: "InfoDB/TEA decrypt2", Name: PCQQKeyDumpPatterns.CommonDecrypt2Export),
        })
        {
            uint? rva = PeExportResolver.GetExportRva(commonPath, exp.Name);
            if (!rva.HasValue)
            {
                _log($"[Common] export not found: {exp.Name}");
                continue;
            }

            ulong entry = baseAddr + rva.Value;
            _log($"[Common] pid={pid} hook {exp.Label} @0x{entry:X}");
            dbg.SetBreakpoint(pid, entry, (p, ctx) =>
            {
                try
                {
                    uint pCipher = dbg.ReadUInt32(p, ctx.Esp + 4);
                    int cipherLen = (int)dbg.ReadUInt32(p, ctx.Esp + 8);
                    uint pKey = dbg.ReadUInt32(p, ctx.Esp + 12);
                    if (pCipher == 0 || pKey == 0 || cipherLen <= 0 || cipherLen > 32 * 1024 * 1024)
                        return;

                    byte[] head = dbg.ReadBytes(p, pCipher, Math.Min(64, cipherLen));
                    var matches = infoIndex.Match(cipherLen, head);
                    if (matches.Count == 0)
                        return;

                    byte[] key = dbg.ReadBytes(p, pKey, 16);
                    string keyHex = Convert.ToHexString(key);
                    foreach (var m in matches)
                    {
                        string dedupe = $"{m.InfoDbPath}|{keyHex}";
                        if (!_infoPrinted.Add(dedupe))
                            continue;

                        _log($"[Info.db] pid={p} KEY={keyHex} -> {m.InfoDbPath} (first match: {m.StreamName}, tag=0x{m.Tag:X8}, len={m.Length})");
                    }
                }
                catch
                {
                }
            });
        }
    }

    private static string? ResolveQQExe(string? requestedPath)
    {
        string[] candidatePaths =
        {
            requestedPath ?? string.Empty,
            Environment.GetEnvironmentVariable("QQ_DATABASE_READER_PCQQ_EXE") ?? string.Empty,
            @"C:\Program Files (x86)\Tencent\QQ\Bin\QQ.exe",
            @"C:\Program Files\Tencent\QQ\Bin\QQ.exe",
            @"D:\Program Files (x86)\Tencent\QQ\Bin\QQ.exe",
            @"D:\Program Files\Tencent\QQ\Bin\QQ.exe",
        };

        return candidatePaths.FirstOrDefault(File.Exists);
    }

    private static string ReadCString(MultiProcDebugger dbg, uint pid, uint addr, int max)
    {
        if (addr == 0) return "<null>";
        try
        {
            byte[] buf = dbg.ReadBytes(pid, addr, max);
            int len = Array.IndexOf(buf, (byte)0);
            if (len < 0) len = buf.Length;
            return Encoding.UTF8.GetString(buf, 0, len);
        }
        catch
        {
            return $"<read fail @0x{addr:X}>";
        }
    }

    private static bool IsDatabase(string path, string databaseName) =>
        Path.GetFileName(path).Equals(databaseName, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        Stop();
    }
}

public sealed record PCQQKeyDumpOptions
{
    public string? QQFilePath { get; init; }
}

internal static class PCQQKeyDumpPatterns
{
    public static readonly byte[] SqliteKey =
    [
        0x55, 0x8B, 0xEC, 0x56, 0x6B, 0x75, 0x10, 0x11, 0x83, 0x7D,
        0x10, 0x10, 0x74, 0x0D, 0x68, 0x17, 0x02, 0x00, 0x00, 0xE8,
    ];

    public static readonly byte[] SqliteOpen =
    [
        0x55, 0x8B, 0xEC, 0x6A, 0x00, 0x6A, 0x06, 0xFF, 0x75, 0x0C,
        0xFF, 0x75, 0x08, 0xE8,
    ];

    public const string CommonDecryptExport = "?oi_symmetry_decrypt@@YAHPBEH0PAEPAH@Z";
    public const string CommonDecrypt2Export = "?oi_symmetry_decrypt2@@YAHPBEH0PAEPAH@Z";
}
