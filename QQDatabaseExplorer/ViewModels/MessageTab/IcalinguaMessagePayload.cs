using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed record IcalinguaMessagePayload(
    string RawId,
    string Content,
    string? FileJson,
    string? FilesJson,
    bool Deleted,
    bool Hide,
    bool Reveal,
    bool Flash,
    bool System,
    string? ReplyMessageJson,
    string? MiraiJson,
    string? Title,
    string? RecallInfo,
    string? Code,
    string? Role,
    string? AnonymousId,
    string? AnonymousFlag,
    string? BubbleId,
    string? SubId,
    string? HeadImage,
    string PreviewText,
    IReadOnlyList<IcalinguaMessageFile> Files,
    IcalinguaReplyPreview? Reply)
{
    public static byte[] ToContentBytes(IcalinguaMessageRecord message)
    {
        var payload = new SerializableIcalinguaPayload(
            message.RawId,
            message.Content,
            message.FileJson,
            message.FilesJson,
            message.Deleted,
            message.Hide,
            message.Reveal,
            message.Flash,
            message.System,
            message.ReplyMessageJson,
            message.MiraiJson,
            message.Title,
            message.RecallInfo,
            message.Code,
            message.Role,
            message.AnonymousId,
            message.AnonymousFlag,
            message.BubbleId,
            message.SubId,
            message.HeadImage,
            message.PreviewText);
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    public static IcalinguaMessagePayload? FromContent(byte[]? content)
    {
        if (content is not { Length: > 0 })
            return null;
        if (content[0] != (byte)'{')
            return null;

        try
        {
            var payload = JsonSerializer.Deserialize<SerializableIcalinguaPayload>(content);
            if (payload is null)
                return null;

            return new IcalinguaMessagePayload(
                payload.RawId ?? string.Empty,
                payload.Content ?? string.Empty,
                payload.FileJson,
                payload.FilesJson,
                payload.Deleted,
                payload.Hide,
                payload.Reveal,
                payload.Flash,
                payload.System,
                payload.ReplyMessageJson,
                payload.MiraiJson,
                payload.Title,
                payload.RecallInfo,
                payload.Code,
                payload.Role,
                payload.AnonymousId,
                payload.AnonymousFlag,
                payload.BubbleId,
                payload.SubId,
                payload.HeadImage,
                FirstNonEmpty(payload.PreviewText, IcalinguaMessageReader.CreatePreviewText(
                    payload.Content,
                    payload.FileJson,
                    payload.FilesJson,
                    payload.Deleted,
                    payload.Hide,
                    payload.Reveal,
                    payload.System,
                    payload.RecallInfo,
                    payload.MiraiJson,
                    payload.Code)),
                IcalinguaMessageReader.ParseFiles(payload.FileJson, payload.FilesJson, payload.Code, payload.Content),
                IcalinguaMessageReader.ParseReplyPreview(payload.ReplyMessageJson));
        }
        catch
        {
            return null;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private sealed record SerializableIcalinguaPayload(
        string? RawId,
        string? Content,
        string? FileJson,
        string? FilesJson,
        bool Deleted,
        bool Hide,
        bool Reveal,
        bool Flash,
        bool System,
        string? ReplyMessageJson,
        string? MiraiJson,
        string? Title,
        string? RecallInfo,
        string? Code,
        string? Role,
        string? AnonymousId,
        string? AnonymousFlag,
        string? BubbleId,
        string? SubId,
        string? HeadImage,
        string? PreviewText);
}
