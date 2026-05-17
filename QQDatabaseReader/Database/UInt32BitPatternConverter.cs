using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace QQDatabaseReader.Database;

internal static class UInt32BitPatternConverter
{
    public static ValueConverter<uint, int> Instance { get; } = new(
        value => Unsafe.BitCast<uint, int>(value),
        value => Unsafe.BitCast<int, uint>(value));
}
