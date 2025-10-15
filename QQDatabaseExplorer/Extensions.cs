namespace QQDatabaseExplorer;

internal static class Extensions
{
    extension<T>(T t)
    {
        public static string? operator |(string? left, T? right)
        {
            if (string.IsNullOrEmpty(left))
                return right?.ToString();
            return left;
        }
    }
}
