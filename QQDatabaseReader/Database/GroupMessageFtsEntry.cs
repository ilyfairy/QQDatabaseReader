using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace QQDatabaseReader.Database;

[Table("group_msg_fts")]
public class GroupMessageFtsEntry
{
    [Key]
    [Column("40001")]
    public long MessageId { get; set; }

    [Column("40003")]
    public long MessageSeq { get; set; }

    [Column("40010")]
    public ChatType ChatType { get; set; }

    [Column("40020")]
    public string? SenderUid { get; set; }

    [Column("40021")]
    public string? PeerUid { get; set; }

    [Column("40027")]
    public uint GroupId { get; set; }

    [Column("40050")]
    public int MessageTime { get; set; }

    [Column("41700")]
    public string? SearchMetadata { get; set; }

    [Column("41701")]
    public string? Text1 { get; set; }

    [Column("41702")]
    public string? Text2 { get; set; }

    [Column("41703")]
    public string? Text3 { get; set; }

    [Column("41704")]
    public string? Text4 { get; set; }

    [Column("41705")]
    public string? Text5 { get; set; }

    [Column("41706")]
    public string? Text6 { get; set; }

    [Column("41707")]
    public string? Text7 { get; set; }

    public string CreatePreviewText()
    {
        return GroupMessageFtsText.CombinePreview(Text1, Text2, Text3, Text4, Text5, Text6, Text7);
    }
}

public class GroupMessageFtsSearchRow
{
    public long RowId { get; set; }
    public long MessageId { get; set; }
    public long MessageSeq { get; set; }
    public ChatType ChatType { get; set; }
    public string? SenderUid { get; set; }
    public string? PeerUid { get; set; }
    public uint GroupId { get; set; }
    public int MessageTime { get; set; }
    public string? Text1 { get; set; }
    public string? Text2 { get; set; }
    public string? Text3 { get; set; }
    public string? Text4 { get; set; }
    public string? Text5 { get; set; }
    public string? Text6 { get; set; }
    public string? Text7 { get; set; }
}

internal static class GroupMessageFtsText
{
    public static IReadOnlyList<string?> GetPreviewFields(params string?[] values)
    {
        if (values.Length < 7)
            return values;

        // QQNT stores numeric search helper fields in 41703/41704. Including them
        // makes almost every preview end with values like "2 1" or "9 33".
        return [values[0], values[1], values[4], values[5], values[6]];
    }

    public static string CombinePreview(params string?[] values)
    {
        return Combine(GetPreviewFields(values).ToArray());
    }

    public static string Combine(params string?[] values)
    {
        var parts = values
            .Select(Normalize)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return parts.Length == 0
            ? string.Empty
            : string.Join(" ", parts);
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
    }
}
