using Microsoft.EntityFrameworkCore;

namespace QQDatabaseReader.Database;

public class QQGroupMessageFtsDbContext(DbContextOptions<QQGroupMessageFtsDbContext> options) : DbContext(options)
{
    public DbSet<GroupMessageFtsEntry> GroupMessages { get; set; }
    public DbSet<GroupMessageFtsSearchRow> SearchRows { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GroupMessageFtsEntry>()
            .Property(v => v.GroupId)
            .HasConversion(UInt32BitPatternConverter.Instance);

        modelBuilder.Entity<GroupMessageFtsSearchRow>()
            .HasNoKey()
            .ToView(null);

        modelBuilder.Entity<GroupMessageFtsSearchRow>()
            .Property(v => v.GroupId)
            .HasConversion(UInt32BitPatternConverter.Instance);
    }
}
