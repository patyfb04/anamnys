using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Data;

public class AnamnysDbContext(DbContextOptions<AnamnysDbContext> options) : DbContext(options)
{
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<PatientAccount> PatientAccounts => Set<PatientAccount>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<BreakGlassGrant> BreakGlassGrants => Set<BreakGlassGrant>();
    public DbSet<AccessLog> AccessLogs => Set<AccessLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Provider>(e =>
        {
            e.ToTable("Providers");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ExternalSubject).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<PatientAccount>(e =>
        {
            e.ToTable("PatientAccounts");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ExternalSubject).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<Staff>(e =>
        {
            e.ToTable("Staff");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ExternalSubject).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<BreakGlassGrant>(e =>
        {
            e.ToTable("BreakGlassGrants");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.StaffId);
            e.HasIndex(x => x.ProviderId);
        });

        modelBuilder.Entity<AccessLog>(e =>
        {
            e.ToTable("AccessLogs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ProviderId);
        });
    }
}
