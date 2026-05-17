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
    public DbSet<QQGroupMember> GroupMembers { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QQGroup>()
            .Property(v => v.GroupId)
            .HasConversion(UInt32BitPatternConverter.Instance);

        modelBuilder.Entity<QQGroupMember>()
            .HasKey(v => new { v.GroupId, v.NtUid });

        modelBuilder.Entity<QQGroupMember>()
            .Property(v => v.GroupId)
            .HasConversion(UInt32BitPatternConverter.Instance);

        modelBuilder.Entity<QQGroupMember>()
            .Property(v => v.Uin)
            .HasConversion(UInt32BitPatternConverter.Instance);
    }
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
    public uint GroupId { get; set; }

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

/// <summary>
/// 群成员列表。group_member3 的主键是群号 + nt_uid。
/// </summary>
[Table("group_member3")]
public class QQGroupMember
{
    /// <summary>
    /// 群号。
    /// </summary>
    [Column("60001")]
    public uint GroupId { get; set; }

    /// <summary>
    /// nt_uid。
    /// </summary>
    [Column("1000")]
    public string NtUid { get; set; } = string.Empty;

    /// <summary>
    /// QID。
    /// </summary>
    [Column("1001")]
    public string? Qid { get; set; }

    /// <summary>
    /// QQ 号。
    /// </summary>
    [Column("1002")]
    public uint Uin { get; set; }

    /// <summary>
    /// 群名片。
    /// </summary>
    [Column("64003")]
    public string? MemberName { get; set; }

    /// <summary>
    /// 昵称。
    /// </summary>
    [Column("20002")]
    public string? NickName { get; set; }
}
