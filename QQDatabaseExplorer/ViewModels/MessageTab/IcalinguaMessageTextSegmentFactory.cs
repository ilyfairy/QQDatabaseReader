using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.ViewModels.MessageTab;

internal static class IcalinguaMessageTextSegmentFactory
{
    private static readonly Regex AtRegex = new(
        @"<IcalinguaAt qq=(?<qq>\d+)>(?<text>.*?)</IcalinguaAt>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex QLottieRegex = new(
        @"^\[QLottie:\s*\d+,(?<faceId>\d+)(?:,\d+)?\]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FaceRegex = new(
        @"^\[Face:\s*(?<faceId>\d+)\]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ForwardRegex = new(
        @"^\[(?<kind>Forward|NestedForward):\s*(?<value>.+)\]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryAddSpecialTextSegment(List<AvaQQMessageSegment> segments, string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;

        if (QLottieRegex.Match(trimmed) is { Success: true } qlottieMatch &&
            int.TryParse(qlottieMatch.Groups["faceId"].Value, out var qlottieFaceId))
        {
            var faceSegment = AvaQQMessageSegment.CreateQQFace(qlottieFaceId);
            segments.Add(string.IsNullOrWhiteSpace(faceSegment.FaceAssetPath)
                ? AvaQQMessageSegment.CreateText("[动画表情]")
                : faceSegment);
            return true;
        }

        if (FaceRegex.Match(trimmed) is { Success: true } faceMatch &&
            int.TryParse(faceMatch.Groups["faceId"].Value, out var faceId))
        {
            var faceSegment = AvaQQMessageSegment.CreateQQFace(faceId);
            segments.Add(string.IsNullOrWhiteSpace(faceSegment.FaceAssetPath)
                ? AvaQQMessageSegment.CreateText(faceSegment.DisplayText)
                : faceSegment);
            return true;
        }

        if (ForwardRegex.Match(trimmed) is { Success: true } forwardMatch)
        {
            var kind = forwardMatch.Groups["kind"].Value;
            var value = forwardMatch.Groups["value"].Value.Trim();
            segments.Add(AvaQQMessageSegment.CreateForwardedMessage(new ForwardedMessageCard(
                "聊天记录",
                "查看转发消息",
                [],
                string.Equals(kind, "Forward", StringComparison.Ordinal) ? value : null,
                null,
                string.Equals(kind, "NestedForward", StringComparison.Ordinal) ? value : null,
                null,
                trimmed)));
            return true;
        }

        return false;
    }

    public static void AddTextSegments(List<AvaQQMessageSegment> segments, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var index = 0;
        foreach (Match match in AtRegex.Matches(text))
        {
            if (match.Index > index)
            {
                segments.AddRange(MessageTextSegmentBuilder.CreateTextSegments(text[index..match.Index]));
            }

            var qq = match.Groups["qq"].Value;
            var atText = DecodeAtText(match.Groups["text"].Value);
            if (string.IsNullOrEmpty(atText))
                atText = qq == "1" ? "@全体成员" : $"@{qq}";

            segments.AddRange(MessageTextSegmentBuilder.CreateTextSegments(
                atText,
                isMention: true,
                mentionUid: qq == "1" ? null : qq));
            index = match.Index + match.Length;
        }

        if (index < text.Length)
        {
            segments.AddRange(MessageTextSegmentBuilder.CreateTextSegments(text[index..]));
        }
    }

    private static string DecodeAtText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        try
        {
            return WebUtility.UrlDecode(value);
        }
        catch
        {
            return value;
        }
    }
}
