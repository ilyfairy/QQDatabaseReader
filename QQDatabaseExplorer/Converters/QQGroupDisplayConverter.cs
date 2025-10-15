using System;
using System.Globalization;
using Avalonia.Data.Converters;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.Converters;

public class QQGroupDisplayConverter : IValueConverter
{
    public static QQGroupDisplayConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            AvaQQGroup group => group.GroupName | group.GroupId,
            _ => string.Empty,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
