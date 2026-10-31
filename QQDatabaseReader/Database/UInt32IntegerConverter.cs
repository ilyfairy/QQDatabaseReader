using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace QQDatabaseReader.Database;

/// <summary>
/// QQ 号和群号 (uint) 与 SQLite 整数列的转换. QQNT 库里大于 2^31 的号码按正数存 (各版本 PC / 安卓备份里都没有负数),
/// 所以写入 (查询参数) 按正数; 读取时遇到负数按 32 位补码换回来, 兼容万一按有符号整数存的库.
/// 按位转成 int 的话, 大于 2^31 的号码作为查询参数会变成负数, 按群号 / QQ 号筛选时查不到
/// </summary>
internal static class UInt32IntegerConverter
{
    public static ValueConverter<uint, long> Instance { get; } = new(
        value => (long)value,
        value => value < 0 ? unchecked((uint)(int)value) : unchecked((uint)value));

    /// <summary>手写 SQL 读取号码时用, 规则和 <see cref="Instance"/> 一样</summary>
    public static uint FromStored(long value) => value < 0 ? unchecked((uint)(int)value) : unchecked((uint)value);
}
