using System.Collections.Concurrent;
using QQDatabaseReader.Archives;

namespace QQDatabaseReader.Media;

/// <summary>
/// 媒体目录的文件列表缓存. 定位一张图片要试好几个候选文件名, 还要在缩略图目录里按 &lt;图片 hash&gt;_&lt;规格&gt; 找,
/// 批量定位时逐个查文件状态 / 每次列目录都很慢. 先查这里的列表, 文件不在列表里就不用再查.
/// 普通目录按修改时间判断是否过期 (最多 2 秒查一次), 归档内的目录不会变
/// </summary>
internal static class MediaDirectoryListing
{
    private const int MaxDirectories = 2_000;
    private const long RecheckMilliseconds = 2_000;

    private static readonly StringComparer NameComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly ConcurrentDictionary<string, Listing> Cache = new(NameComparer);

    /// <summary>目录里有没有这个文件 (Windows 上不区分大小写). 目录不存在时返回 false</summary>
    public static bool Contains(string directory, string fileName) => Get(directory).Names.Contains(fileName);

    /// <summary>目录里文件名以 "<paramref name="stem"/>_" 开头 (不区分大小写) 的文件, 返回完整路径</summary>
    public static IReadOnlyList<string> FilesWithStem(string directory, string stem) =>
        Get(directory).ByStem.TryGetValue(stem.ToLowerInvariant(), out var files) ? files : [];

    private static Listing Get(string directory)
    {
        var now = Environment.TickCount64;
        if (Cache.TryGetValue(directory, out var cached) && now - cached.CheckedAt < RecheckMilliseconds)
            return cached;

        var stamp = Stamp(directory);
        if (cached is not null && cached.Stamp == stamp)
        {
            cached.CheckedAt = now;
            return cached;
        }

        string[] files;
        try
        {
            files = ArchiveFile.EnumerateFiles(directory).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            files = [];
        }
        var listing = new Listing(
            stamp,
            files.Select(Path.GetFileName).OfType<string>().ToHashSet(NameComparer),
            files.Select(path => (Path: path, Name: Path.GetFileName(path)))
                .Where(x => x.Name.Contains('_'))
                .GroupBy(x => x.Name[..x.Name.IndexOf('_')].ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.Select(x => x.Path).ToArray()))
        {
            CheckedAt = now
        };
        if (Cache.Count >= MaxDirectories)
            Cache.Clear();
        Cache[directory] = listing;
        return listing;
    }

    // 不存在的目录用 MinValue, 之后被创建时修改时间不同, 会重新列
    private static DateTime Stamp(string directory) =>
        Directory.Exists(directory) ? Directory.GetLastWriteTimeUtc(directory) : DateTime.MinValue;

    private sealed record Listing(DateTime Stamp, HashSet<string> Names, Dictionary<string, string[]> ByStem)
    {
        public long CheckedAt { get; set; }
    }
}
