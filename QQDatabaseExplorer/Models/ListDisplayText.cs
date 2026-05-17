using System.Text;

namespace QQDatabaseExplorer.Models;

internal static class ListDisplayText
{
    private const string Ellipsis = "...";

    public static string SingleLine(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        var text = builder.ToString().Trim();
        if (maxLength <= 0 || text.Length <= maxLength)
            return text;

        if (maxLength <= Ellipsis.Length)
            return Ellipsis[..maxLength];

        var cutLength = maxLength - Ellipsis.Length;
        if (cutLength > 0
            && cutLength < text.Length
            && char.IsHighSurrogate(text[cutLength - 1])
            && char.IsLowSurrogate(text[cutLength]))
        {
            cutLength--;
        }

        return text[..cutLength].TrimEnd() + Ellipsis;
    }
}
