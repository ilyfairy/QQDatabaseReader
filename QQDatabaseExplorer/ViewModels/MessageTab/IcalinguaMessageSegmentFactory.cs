using System;
using System.Collections.Generic;
using QQDatabaseExplorer.Models;
using QQDatabaseReader;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal sealed class IcalinguaMessageSegmentFactory
{
    private readonly IcalinguaMessageFileSegmentFactory _fileSegmentFactory;

    public IcalinguaMessageSegmentFactory(IcalinguaMessageFileSegmentFactory fileSegmentFactory)
    {
        _fileSegmentFactory = fileSegmentFactory;
    }

    public List<AvaQQMessageSegment> Create(
        IcalinguaMessagePayload? payload,
        AvaQQGroup conversation,
        bool forSystemHint = false)
    {
        if (payload is null)
            return [];

        var segments = new List<AvaQQMessageSegment>();
        if (payload.Deleted && !payload.Reveal)
        {
            segments.Add(AvaQQMessageSegment.CreateText(
                FirstNonEmpty(IcalinguaMessageReader.CreateRecallPreviewText(payload.RecallInfo), "[已撤回]"),
                AvaQQMessageSegmentTone.Warning));
            return segments;
        }

        if (payload.Hide && !payload.Reveal)
        {
            segments.Add(AvaQQMessageSegment.CreateText("[已隐藏]", AvaQQMessageSegmentTone.Warning));
            return segments;
        }

        if (!payload.System &&
            MiniAppCardParser.TryParse(payload.Code, out var miniApp) &&
            miniApp is not null)
        {
            segments.Add(AvaQQMessageSegment.CreateMiniApp(miniApp));
            return segments;
        }

        var text = IcalinguaMessageReader.CreateDisplayMessageText(payload.Content, payload.MiraiJson, payload.Code);
        AddContentSegments(segments, text, payload.Files, conversation, forSystemHint);

        if (segments.Count == 0 && !string.IsNullOrWhiteSpace(payload.PreviewText))
            IcalinguaMessageTextSegmentFactory.AddTextSegments(segments, payload.PreviewText);

        return segments;
    }

    private void AddContentSegments(
        List<AvaQQMessageSegment> segments,
        string text,
        IReadOnlyList<IcalinguaMessageFile> files,
        AvaQQGroup conversation,
        bool forSystemHint)
    {
        if (string.IsNullOrEmpty(text))
        {
            foreach (var file in files)
                segments.Add(_fileSegmentFactory.Create(file, conversation, forSystemHint));
            return;
        }

        if (files.Count > 0 && TrySplitMediaPlaceholder(text, out var placeholderSuffix))
        {
            foreach (var file in files)
                segments.Add(_fileSegmentFactory.Create(file, conversation, forSystemHint));

            if (!string.IsNullOrWhiteSpace(placeholderSuffix))
                IcalinguaMessageTextSegmentFactory.AddTextSegments(segments, placeholderSuffix);
            return;
        }

        var parts = text.Split("<ica:img>", StringSplitOptions.None);
        if (parts.Length == 1)
        {
            if (!IcalinguaMessageTextSegmentFactory.TryAddSpecialTextSegment(segments, text))
                IcalinguaMessageTextSegmentFactory.AddTextSegments(segments, text);
            foreach (var file in files)
                segments.Add(_fileSegmentFactory.Create(file, conversation, forSystemHint));
            return;
        }

        var fileIndex = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            IcalinguaMessageTextSegmentFactory.AddTextSegments(segments, parts[i]);
            if (i >= parts.Length - 1 || fileIndex >= files.Count)
                continue;

            segments.Add(_fileSegmentFactory.Create(files[fileIndex], conversation, forSystemHint));
            fileIndex++;
        }

        for (; fileIndex < files.Count; fileIndex++)
            segments.Add(_fileSegmentFactory.Create(files[fileIndex], conversation, forSystemHint));
    }

    private static bool TrySplitMediaPlaceholder(string text, out string suffix)
    {
        suffix = string.Empty;
        var trimmed = text.Trim();
        foreach (var placeholder in new[] { "[Image]", "[Sticker]" })
        {
            if (string.Equals(trimmed, placeholder, StringComparison.OrdinalIgnoreCase))
                return true;

            if (trimmed.StartsWith(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                suffix = trimmed[placeholder.Length..].Trim();
                return true;
            }
        }

        return false;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }
}
