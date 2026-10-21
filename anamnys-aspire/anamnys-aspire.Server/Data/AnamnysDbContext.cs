using System.Text.Json;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

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
    public DbSet<NoteSection> NoteSections => Set<NoteSection>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<PatientDiagnosis> PatientDiagnoses => Set<PatientDiagnosis>();
    public DbSet<MedicationEntry> MedicationEntries => Set<MedicationEntry>();
    public DbSet<TreatmentPlan> TreatmentPlans => Set<TreatmentPlan>();
    public DbSet<PlanObjective> PlanObjectives => Set<PlanObjective>();
    public DbSet<AppointmentConfirmation> AppointmentConfirmations => Set<AppointmentConfirmation>();
    public DbSet<AppointmentConfirmationToken> AppointmentConfirmationTokens => Set<AppointmentConfirmationToken>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<BookingPolicy> BookingPolicies => Set<BookingPolicy>();
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<ScaleApplication> ScaleApplications => Set<ScaleApplication>();
    public DbSet<ProviderInstrumentOptIn> ProviderInstrumentOptIns => Set<ProviderInstrumentOptIn>();
    public DbSet<ClinicalDocument> ClinicalDocuments => Set<ClinicalDocument>();
    public DbSet<ConformityRuleset> ConformityRulesets => Set<ConformityRuleset>();
    public DbSet<ConformityCheck> ConformityChecks => Set<ConformityCheck>();

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

        modelBuilder.Entity<NoteSection>(e =>
        {
            e.ToTable("NoteSections");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditEntries");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<Session>(e =>
        {
            e.ToTable("Sessions");
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

        modelBuilder.Entity<AppointmentConfirmation>(e =>
        {
            e.ToTable("AppointmentConfirmations");
            e.HasKey(x => x.Id);
            e.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppointmentConfirmationToken>(e =>
        {
            e.ToTable("AppointmentConfirmationTokens");
            e.HasKey(x => x.Id);
            e.HasOne<AppointmentConfirmation>().WithMany().HasForeignKey(x => x.ConfirmationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Reminder>(e =>
        {
            e.ToTable("Reminders");
            e.HasKey(x => x.Id);
            e.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppointmentConfirmation>().WithMany().HasForeignKey(x => x.ConfirmationId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Notification>(e =>
        {
            e.ToTable("Notifications");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<BookingPolicy>(e =>
        {
            e.ToTable("BookingPolicies");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<Instrument>(e =>
        {
            e.ToTable("Instruments");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<ScaleApplication>(e =>
        {
            e.ToTable("ScaleApplications");
            e.HasKey(x => x.Id);
            e.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Instrument>().WithMany().HasForeignKey(x => x.InstrumentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProviderInstrumentOptIn>(e =>
        {
            e.ToTable("ProviderInstrumentOptIns");
            e.HasKey(x => x.Id);
            e.HasOne<Provider>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Instrument>().WithMany().HasForeignKey(x => x.InstrumentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ConformityRuleset>(e =>
        {
            e.ToTable("ConformityRulesets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Elements).HasColumnType("jsonb");
            e.Property(x => x.Prohibitions).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ClinicalDocument>(e =>
        {
            e.ToTable("ClinicalDocuments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Fields).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new(),
                new ValueComparer<Dictionary<string, string>>(
                    (a, b) => a != null && b != null && a.Count == b.Count && !a.Except(b).Any(),
                    v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                    v => new Dictionary<string, string>(v)));
            e.Property(x => x.Confirmations).HasColumnType("jsonb");
            e.HasOne<ConformityRuleset>().WithMany().HasForeignKey(x => x.RulesetId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ConformityCheck>(e =>
        {
            e.ToTable("ConformityChecks");
            e.HasKey(x => x.Id);
            e.Property(x => x.MissingElements).HasColumnType("jsonb");
            e.Property(x => x.ForbiddenFound).HasColumnType("jsonb");
            e.HasOne<ClinicalDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ConformityRuleset>().WithMany().HasForeignKey(x => x.RulesetId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
