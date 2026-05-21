using System;
using System.Buffers;

namespace QQDatabaseExplorer.Controls;

internal static class EmojiTextRunHelper
{
    private static readonly SearchValues<char> EmojiCandidateChars =
        SearchValues.Create("\uFE0F\u20E3\u200D\uD83C\uD83D\uD83E");

    public static bool MayContainEmojiFontText(string text)
    {
        return text.AsSpan().IndexOfAny(EmojiCandidateChars) >= 0;
    }

    public static bool ShouldUseEmojiFont(string textElement)
    {
        if (textElement.Contains('\uFE0F', StringComparison.Ordinal) ||
            textElement.Contains('\u20E3', StringComparison.Ordinal) ||
            textElement.Contains('\u200D', StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var rune in textElement.EnumerateRunes())
        {
            if (rune.Value is >= 0x1F000 and <= 0x1FAFF)
                return true;
        }

        return false;
    }
}
