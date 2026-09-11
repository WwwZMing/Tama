using Microsoft.EntityFrameworkCore;
using Tama.Core.Models;

namespace Tama.Data.Database;

public class AuthDbContext : DbContext
{
    public DbSet<AccountData> Accounts => Set<AccountData>();
    public DbSet<SyncCache> SyncCaches => Set<SyncCache>();
    public DbSet<WatchtowerReportCache> WatchtowerReports => Set<WatchtowerReportCache>();

    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountData>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<SyncCache>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<WatchtowerReportCache>(entity =>
        {
            entity.HasKey(e => e.AccountId);
        });
    }
}
