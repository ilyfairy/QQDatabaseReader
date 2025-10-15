using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia.Data.Converters;
using QQDatabaseReader;

namespace QQDatabaseExplorer.Converters;

public class QQDatabaseTypeToStringConverter : IValueConverter
{
    public static QQDatabaseTypeToStringConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value as QQDatabaseType?) switch 
        {
            QQDatabaseType.GroupInfo => "group_info.db",
            QQDatabaseType.Message => "nt_msg.db",
            _ => string.Empty,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
