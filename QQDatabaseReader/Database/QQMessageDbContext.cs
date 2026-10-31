using Microsoft.EntityFrameworkCore;

namespace QQDatabaseReader.Database;

public class QQMessageDbContext(DbContextOptions<QQMessageDbContext> options, DatabaseSchema? schema = null)
    : DbContext(options), ISchemaMappedDbContext
{
    public DbSet<GroupMessage> GroupMessages { get; set; }
    public DbSet<PrivateMessage> PrivateMessages { get; set; }
    public DbSet<RecentContact> RecentContacts { get; set; }

    public DatabaseSchema? Schema => schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GroupMessage>()
            .Property(v => v.GroupId)
            .HasConversion(UInt32IntegerConverter.Instance);

        modelBuilder.Entity<GroupMessage>()
            .Property(v => v.SavedGroupId)
            .HasConversion(UInt32IntegerConverter.Instance);

        modelBuilder.Entity<GroupMessage>()
            .Property(v => v.SenderId)
            .HasConversion(UInt32IntegerConverter.Instance);

        modelBuilder.Entity<PrivateMessage>()
            .Property(v => v.PeerUin)
            .HasConversion(UInt32IntegerConverter.Instance);

        modelBuilder.Entity<PrivateMessage>()
            .Property(v => v.SenderId)
            .HasConversion(UInt32IntegerConverter.Instance);

        modelBuilder.Entity<RecentContact>()
            .Property(v => v.Uin)
            .HasConversion(UInt32IntegerConverter.Instance);

        modelBuilder.Entity<RecentContact>()
            .Property(v => v.Uin2)
            .HasConversion(UInt32IntegerConverter.Instance);

        schema?.Apply(modelBuilder);
    }
}

