using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Data;

public class AnamnysDbContext(DbContextOptions<AnamnysDbContext> options) : DbContext(options)
{
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<BreakGlassGrant> BreakGlassGrants => Set<BreakGlassGrant>();
    public DbSet<AccessLog> AccessLogs => Set<AccessLog>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<PatientDiagnosis> PatientDiagnoses => Set<PatientDiagnosis>();
    public DbSet<MedicationEntry> MedicationEntries => Set<MedicationEntry>();
    public DbSet<TreatmentPlan> TreatmentPlans => Set<TreatmentPlan>();
    public DbSet<PlanObjective> PlanObjectives => Set<PlanObjective>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Provider>(e =>
        {
            e.ToTable("Providers");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ExternalSubject).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<Patient>(e =>
        {
            e.ToTable("Patients");
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

        modelBuilder.Entity<Appointment>(e =>
        {
            e.ToTable("Appointments");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<Note>(e =>
        {
            e.ToTable("Notes");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<PatientDiagnosis>(e =>
        {
            e.ToTable("PatientDiagnoses");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<MedicationEntry>(e =>
        {
            e.ToTable("MedicationEntries");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<TreatmentPlan>(e =>
        {
            e.ToTable("TreatmentPlans");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<PlanObjective>(e =>
        {
            e.ToTable("PlanObjectives");
            e.HasKey(x => x.Id);
        });
    }
}
