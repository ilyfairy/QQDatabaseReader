using System;
using System.Linq;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class QqNtSystemHintDisplayFactory
{
    private readonly Func<uint, string?, string?, string?[], string> _resolveParticipantName;
    private readonly Func<uint, string?, string> _resolveUin;

    public QqNtSystemHintDisplayFactory(
        Func<uint, string?, string?, string?[], string> resolveParticipantName,
        Func<uint, string?, string> resolveUin)
    {
        _resolveParticipantName = resolveParticipantName;
        _resolveUin = resolveUin;
    }

    public SystemHintDisplay? Create(
        QQMessageContent? content,
        uint groupId,
        uint senderId,
        string? senderUid,
        string? senderName)
    {
        if (content is null)
            return null;

        var hint = content.Segments
            .Select(segment => segment.SystemHint)
            .FirstOrDefault(systemHint =>
                systemHint is not null &&
                (systemHint.Participants.Count > 0 || !string.IsNullOrWhiteSpace(systemHint.SourceName)));
        if (hint is null)
            return null;

        var sourceParticipant = hint.Participants.FirstOrDefault();
        var sourceNtUid = CleanText(sourceParticipant?.Uid);
        var sourceUin = string.Empty;
        var sourceIsUser = hint.SourceIsUser && hint.Participants.Count > 0;
        if (hint.Participants.Count > 0)
        {
            sourceUin = CleanText(hint.GetProperty("uin_str1"));
            if (string.IsNullOrWhiteSpace(sourceUin))
            {
                sourceUin = _resolveUin(groupId, sourceNtUid);
            }
        }

        var sourceMatchesMessageSender = ParticipantMatchesMessageSender(
            sourceNtUid,
            sourceUin,
            senderUid,
            senderId);
        var sourceName = _resolveParticipantName(
            groupId,
            sourceNtUid,
            sourceUin,
            [
                hint.SourceName,
                sourceParticipant?.Nickname,
                sourceMatchesMessageSender ? senderName : null,
            ]);

        var targetParticipant = hint.Participants.Count >= 2 ? hint.Participants[1] : null;
        var targetIsUser = hint.Participants.Count >= 2;
        var targetNtUid = CleanText(targetParticipant?.Uid);
        var targetUin = CleanText(hint.GetProperty("uin_str2"));
        if (string.IsNullOrWhiteSpace(targetUin) && hint.Participants.Count >= 2)
        {
            targetUin = _resolveUin(groupId, targetNtUid);
        }

        var targetName = _resolveParticipantName(
            groupId,
            targetNtUid,
            targetUin,
            [
                targetParticipant?.Nickname,
                hint.TargetName,
            ]);

        var action = CleanText(hint.Action);
        var suffix = hint.Suffix ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(action))
            return null;

        if (!hint.IsSingleActor && string.IsNullOrWhiteSpace(targetName) && hint.FaceId <= 0)
            return null;

        var face = hint.FaceId > 0 ? QQFaceCatalog.Get(hint.FaceId) : null;
        var faceDisplayText = hint.FaceId > 0
            ? face is { Name.Length: > 0 }
                ? $"[{face.Name}]"
                : $"[QQ表情:{hint.FaceId}]"
            : string.Empty;
        var displayText = $"{sourceName}{action}{targetName}{faceDisplayText}{suffix}";
        return new SystemHintDisplay(
            sourceName,
            sourceUin,
            sourceIsUser,
            targetName,
            targetUin,
            targetIsUser,
            action,
            suffix,
            hint.ActionImageUrl,
            hint.TargetMessageSeq,
            hint.FaceId,
            face?.AssetPath,
            displayText);
    }

    public static string CleanText(string? value)
    {
        // QQNT 的系统提示里会混入不可见字符，直接参与匹配会导致昵称和 QQ 号解析失败。
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace('\u00A0', ' ')
                .Replace("\u200B", string.Empty, StringComparison.Ordinal)
                .Replace("\u200C", string.Empty, StringComparison.Ordinal)
                .Replace("\u200D", string.Empty, StringComparison.Ordinal)
                .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
                .Trim();
    }

    public static uint ParseUin(string? value)
    {
        return uint.TryParse(CleanText(value), out var uin) ? uin : 0;
    }

    private static bool ParticipantMatchesMessageSender(
        string? participantNtUid,
        string? participantUin,
        string? senderUid,
        uint senderId)
    {
        var cleanParticipantNtUid = CleanText(participantNtUid);
        if (!string.IsNullOrWhiteSpace(cleanParticipantNtUid) &&
            string.Equals(cleanParticipantNtUid, CleanText(senderUid), StringComparison.Ordinal))
        {
            return true;
        }

        return senderId != 0 && ParseUin(participantUin) == senderId;
    }
}
