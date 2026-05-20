using Google.Protobuf;

namespace QQDatabaseReader;

public sealed record QQMessageReaction(
    string FaceId,
    int Count,
    int LocalState,
    int Unknown);

public static class QQMessageReactionParser
{
    public static IReadOnlyList<QQMessageReaction> Parse(byte[]? data)
    {
        if (data is not { Length: > 0 })
            return [];

        var reactions = new List<QQMessageReaction>();
        if (TryParseTopLevel(data, reactions) && reactions.Count > 0)
            return MergeReactions(reactions);

        reactions.Clear();
        if (TryParseEntry(data, out var reaction))
            reactions.Add(reaction);

        return reactions.Count == 0 ? [] : MergeReactions(reactions);
    }

    private static bool TryParseTopLevel(byte[] data, List<QQMessageReaction> reactions)
    {
        var input = new CodedInputStream(data);
        try
        {
            while (!input.IsAtEnd)
            {
                var tag = input.ReadTag();
                if (tag == 0)
                    break;

                var key = WireFormat.GetTagFieldNumber(tag);
                if (key == 40062 &&
                    WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                {
                    if (TryParseEntry(input.ReadBytes().ToByteArray(), out var reaction))
                    {
                        reactions.Add(reaction);
                    }

                    continue;
                }

                input.SkipLastField();
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseEntry(byte[] data, out QQMessageReaction reaction)
    {
        reaction = new QQMessageReaction(string.Empty, 0, 0, 0);
        var input = new CodedInputStream(data);
        string? faceId = null;
        var count = 0;
        var localState = 0;
        var unknown = 0;

        try
        {
            while (!input.IsAtEnd)
            {
                var tag = input.ReadTag();
                if (tag == 0)
                    break;

                var key = WireFormat.GetTagFieldNumber(tag);
                var wireType = WireFormat.GetTagWireType(tag);
                switch (key)
                {
                    case 48301 when wireType == WireFormat.WireType.LengthDelimited:
                        faceId = input.ReadBytes().ToStringUtf8();
                        break;
                    case 48302 when wireType == WireFormat.WireType.Varint:
                        localState = input.ReadInt32();
                        break;
                    case 48303 when wireType == WireFormat.WireType.Varint:
                        count = input.ReadInt32();
                        break;
                    case 48304 when wireType == WireFormat.WireType.Varint:
                        unknown = input.ReadInt32();
                        break;
                    default:
                        input.SkipLastField();
                        break;
                }
            }
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(faceId))
            return false;

        reaction = new QQMessageReaction(faceId.Trim(), Math.Max(0, count), localState, unknown);
        return true;
    }

    private static IReadOnlyList<QQMessageReaction> MergeReactions(IEnumerable<QQMessageReaction> reactions)
    {
        return reactions
            .Where(reaction => !string.IsNullOrWhiteSpace(reaction.FaceId))
            .GroupBy(reaction => reaction.FaceId, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return first with { Count = group.Sum(reaction => reaction.Count) };
            })
            .Where(reaction => reaction.Count > 0)
            .ToArray();
    }
}
