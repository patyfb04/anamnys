using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Data;

public class AnamnysDbContext(DbContextOptions<AnamnysDbContext> options) : DbContext(options)
{
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<PatientAccount> PatientAccounts => Set<PatientAccount>();
    public DbSet<PatientInvitation> PatientInvitations => Set<PatientInvitation>();
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

        modelBuilder.Entity<PatientAccount>(e =>
        {
            e.ToTable("PatientAccounts");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ExternalSubject).IsUnique();
        });

        modelBuilder.Entity<Patient>(e =>
        {
            e.ToTable("Patients");
            e.HasKey(x => x.Id);
            e.HasOne<PatientAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.AccountId, x.ProviderId }).IsUnique();
        });

        modelBuilder.Entity<PatientInvitation>(e =>
        {
            e.ToTable("PatientInvitations");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<PatientAccount>().WithMany().HasForeignKey(x => x.AcceptedAccountId).OnDelete(DeleteBehavior.SetNull);
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

        // The foreign keys below mirror the schema's. Declaring them is what makes EF insert
        // a new patient before its diagnoses, medications and plan in one SaveChanges.
        modelBuilder.Entity<PatientDiagnosis>(e =>
        {
            e.ToTable("PatientDiagnoses");
            e.HasKey(x => x.Id);
            e.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MedicationEntry>(e =>
        {
            e.ToTable("MedicationEntries");
            e.HasKey(x => x.Id);
            e.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TreatmentPlan>(e =>
        {
            e.ToTable("TreatmentPlans");
            e.HasKey(x => x.Id);
            e.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlanObjective>(e =>
        {
            e.ToTable("PlanObjectives");
            e.HasKey(x => x.Id);
            e.HasOne<TreatmentPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
