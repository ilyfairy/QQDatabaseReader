using Microsoft.EntityFrameworkCore;

namespace QQDatabaseReader.Database;

public class QQMessageDbContext(DbContextOptions<QQMessageDbContext> options) : DbContext(options)
{
    public DbSet<GroupMessage> GroupMessages { get; set; }
    public DbSet<PrivateMessage> PrivateMessages { get; set; }
    public DbSet<RecentContact> RecentContacts { get; set; }

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

        modelBuilder.Entity<PrivateMessage>()
            .Property(v => v.PeerUin)
            .HasConversion(UInt32BitPatternConverter.Instance);

        modelBuilder.Entity<PrivateMessage>()
            .Property(v => v.SenderId)
            .HasConversion(UInt32BitPatternConverter.Instance);

        modelBuilder.Entity<RecentContact>()
            .Property(v => v.Uin)
            .HasConversion(UInt32BitPatternConverter.Instance);

        modelBuilder.Entity<RecentContact>()
            .Property(v => v.Uin2)
            .HasConversion(UInt32BitPatternConverter.Instance);
    }
}

