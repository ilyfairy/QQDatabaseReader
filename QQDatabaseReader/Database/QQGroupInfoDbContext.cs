using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace QQDatabaseReader.Database;

/// <summary>
/// group_info.db
/// </summary>
/// <param name="options"></param>
public class QQGroupInfoDbContext(DbContextOptions<QQGroupInfoDbContext> options) : DbContext(options)
{
    public DbSet<QQGroup> GroupList { get; set; }
}

/// <summary>
/// 群聊名称列表
/// </summary>
[Table("group_list")]
public class QQGroup
{
    /// <summary>
    /// 群号
    /// </summary>
    [Key]
    [Column("60001")]
    public int GroupId { get; set; }

    /// <summary>
    /// 群名
    /// </summary>
    [Column("60007")]
    public string? GroupName { get; set; }

    /// <summary>
    /// 群容量
    /// </summary>
    [Column("60005")]
    public int Capacity { get; set; }
}