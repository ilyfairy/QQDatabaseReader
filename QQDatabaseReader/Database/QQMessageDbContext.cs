using Microsoft.EntityFrameworkCore;

namespace QQDatabaseReader.Database;

public class QQMessageDbContext(DbContextOptions<QQMessageDbContext> options) : DbContext(options)
{
    public DbSet<GroupMessage> GroupMessages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {

    }
}

