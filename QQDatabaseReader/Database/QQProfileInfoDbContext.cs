using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace QQDatabaseReader.Database;

/// <summary>
/// profile_info.db
/// </summary>
public class QQProfileInfoDbContext(DbContextOptions<QQProfileInfoDbContext> options, DatabaseSchema? schema = null)
    : DbContext(options), ISchemaMappedDbContext
{
    public DbSet<QQBuddyListEntry> BuddyList { get; set; }
    public DbSet<QQProfileInfoEntry> ProfileInfo { get; set; }

    public DatabaseSchema? Schema => schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QQBuddyListEntry>()
            .Property(v => v.Uin)
            .HasConversion(UInt32IntegerConverter.Instance);

        modelBuilder.Entity<QQProfileInfoEntry>()
            .Property(v => v.Uin)
            .HasConversion(UInt32IntegerConverter.Instance);

        schema?.Apply(modelBuilder);
    }
}

/// <summary>
/// profile_info.db/buddy_list
/// </summary>
[Table("buddy_list")]
public class QQBuddyListEntry
{
    [Key]
    [Column("1000")]
    public string NtUid { get; set; } = string.Empty;

    [Column("1001")]
    public string? Qid { get; set; }

    [Column("1002")]
    public uint Uin { get; set; }
}

/// <summary>
/// profile_info.db/profile_info_v6, 旧版 QQNT 是 profile_info_v2
/// </summary>
[Table("profile_info_v6")]
[LegacyTable("profile_info_v2")]
public class QQProfileInfoEntry
{
    [Column("1000")]
    public string? NtUid { get; set; }

    [Column("1001")]
    public string? Qid { get; set; }

    [Key]
    [Column("1002")]
    public uint Uin { get; set; }

    [Column("20002")]
    public string? NickName { get; set; }

    [Column("20009")]
    public string? RemarkName { get; set; }

    [Column("20004")]
    public string? AvatarUrl { get; set; }
}
