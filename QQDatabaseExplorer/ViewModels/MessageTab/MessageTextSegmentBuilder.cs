using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class MessageTextSegmentBuilder
{
    private static readonly Regex UrlRegex = new(
        @"(?<url>(?:https?://|www\.)[^\s<>()""']+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string CreateDisplayText(IReadOnlyList<AvaQQMessageSegment> segments)
    {
        if (segments.Count == 0)
            return string.Empty;

        var displayText = new StringBuilder();
        var isAtLineStart = true;

        for (var i = 0; i < segments.Count; i++)
        {
            var segmentText = segments[i].DisplayText;
            if (string.IsNullOrEmpty(segmentText))
                continue;

            if (segments[i].Type == AvaQQMessageSegmentType.Image)
            {
                if (!isAtLineStart)
                {
                    displayText.Append('\n');
                }

                displayText.Append(segmentText);
                isAtLineStart = false;

                if (HasDisplayTextAfter(segments, i))
                {
                    displayText.Append('\n');
                    isAtLineStart = true;
                }

                continue;
            }

            displayText.Append(segmentText);
            isAtLineStart = segmentText[^1] is '\n' or '\r';
        }

        return displayText.ToString();
    }

    public static bool HasDisplayContent(IReadOnlyList<AvaQQMessageSegment> segments)
    {
        return segments.Count > 0 && !string.IsNullOrWhiteSpace(CreateDisplayText(segments));
    }

    public static IReadOnlyList<AvaQQMessageSegment> CreateTextSegments(
        string text,
        AvaQQMessageSegmentTone tone = AvaQQMessageSegmentTone.Normal,
        bool isMention = false,
        string? mentionUid = null)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var segments = new List<AvaQQMessageSegment>();
        var index = 0;

        foreach (Match match in UrlRegex.Matches(text))
        {
            if (match.Index > index)
            {
                segments.Add(AvaQQMessageSegment.CreateText(text[index..match.Index], tone, isMention: isMention, mentionUid: mentionUid));
            }

            var urlText = match.Groups["url"].Value.TrimEnd('.', ',', ';', ':', '!', '?', '，', '。', '；', '：', '！', '？');
            var trailingText = match.Groups["url"].Value[urlText.Length..];
            segments.Add(AvaQQMessageSegment.CreateText(urlText, tone, NormalizeUrl(urlText), isMention, mentionUid));

            if (!string.IsNullOrEmpty(trailingText))
            {
                segments.Add(AvaQQMessageSegment.CreateText(trailingText, tone, isMention: isMention, mentionUid: mentionUid));
            }

            index = match.Index + match.Length;
        }

        if (index < text.Length)
        {
            segments.Add(AvaQQMessageSegment.CreateText(text[index..], tone, isMention: isMention, mentionUid: mentionUid));
        }

        return segments;
    }

    private static bool HasDisplayTextAfter(IReadOnlyList<AvaQQMessageSegment> segments, int index)
    {
        for (var i = index + 1; i < segments.Count; i++)
        {
            if (!string.IsNullOrEmpty(segments[i].DisplayText))
                return true;
        }

        return false;
    }

    private static string NormalizeUrl(string url)
    {
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"https://{url}";
    }
}
