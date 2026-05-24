using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace QQDatabaseReader.Database;

public class QQAndroidMessageDbContext(DbContextOptions<QQAndroidMessageDbContext> options) : DbContext(options)
{
    public DbSet<GroupMessage> GroupMessages { get; set; }
    public DbSet<PrivateMessage> PrivateMessages { get; set; }
    public DbSet<AndroidRecentContact> RecentContacts { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GroupMessage>()
            .Property(v => v.GroupId)
            .HasConversion(UInt32BitPatternConverter.Instance);

        modelBuilder.Entity<GroupMessage>()
            .Property(v => v.SavedGroupId)
            .HasConversion(UInt32BitPatternConverter.Instance);

        modelBuilder.Entity<GroupMessage>()
            .Property(v => v.SenderId)
            .HasConversion(UInt32BitPatternConverter.Instance);

        modelBuilder.Entity<GroupMessage>()
            .Ignore(v => v.MessageReactions);

        modelBuilder.Entity<GroupMessage>()
            .Ignore(v => v.TotalReactionCount1);

        modelBuilder.Entity<GroupMessage>()
            .Ignore(v => v.TotalReactionCount2);

        modelBuilder.Entity<PrivateMessage>()
            .Property(v => v.PeerUin)
            .HasConversion(UInt32BitPatternConverter.Instance);

        modelBuilder.Entity<PrivateMessage>()
            .Property(v => v.SenderId)
            .HasConversion(UInt32BitPatternConverter.Instance);

        modelBuilder.Entity<PrivateMessage>()
            .Ignore(v => v.MessageReactions);

        modelBuilder.Entity<PrivateMessage>()
            .Ignore(v => v.TotalReactionCount1);

        modelBuilder.Entity<PrivateMessage>()
            .Ignore(v => v.TotalReactionCount2);

        modelBuilder.Entity<AndroidRecentContact>()
            .Property(v => v.Uin)
            .HasConversion(UInt32BitPatternConverter.Instance);

        modelBuilder.Entity<AndroidRecentContact>()
            .Property(v => v.Uin2)
            .HasConversion(UInt32BitPatternConverter.Instance);
    }
}

[Table("recent_contact_v3_table")]
public class AndroidRecentContact
{
    [Column("40055")]
    public int RecentCategory { get; set; }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("41102")]
    public long RecentContactId { get; set; }

    [Column("40001")]
    public long LastMessageId { get; set; }

    [Column("40010")]
    public ChatType ChatType { get; set; }

    [Column("40011")]
    public MessageType LastMessageType { get; set; }

    [Column("40021")]
    public string? PeerUin { get; set; }

    [Column("40027")]
    public int ContactSubType { get; set; }

    [Column("40030")]
    public uint Uin { get; set; }

    [Column("40051")]
    public byte[]? LastMessage { get; set; }

    [Column("40041")]
    public int SendStatus { get; set; }

    [Column("40050")]
    public int LastTime { get; set; }

    [Column("41136")]
    public long SortTime { get; set; }

    [Column("40003")]
    public long MessageSeq { get; set; }

    [Column("40094")]
    public string? Source { get; set; }

    [Column("40093")]
    public string? SendNickName { get; set; }

    [Column("40090")]
    public string? SendMemberName { get; set; }

    [Column("40095")]
    public string? SendremarkName { get; set; }

    [Column("40020")]
    public string? NtUid { get; set; }

    [Column("40033")]
    public uint Uin2 { get; set; }

    /// <summary>
    /// 最近联系人头像本地缓存路径。群聊通常是群头像，私聊通常是好友头像。
    /// </summary>
    [Column("41110")]
    public string? GroupAvatar { get; set; }

    [Column("41135")]
    public string? _41135 { get; set; }
}
