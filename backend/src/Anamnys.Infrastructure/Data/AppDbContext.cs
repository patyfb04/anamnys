using Anamnys.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Text.Json;

namespace Anamnys.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<ContactMessage> ContactMessages => Set<ContactMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Provider
        modelBuilder.Entity<Provider>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Specialty).HasConversion<string>();
            e.Property(x => x.PreferredNoteFormat).HasConversion<string>();
            // BillingSystem stored as integer (EF default for enums — matches snapshot)
        });

        // RecoveryCode
        modelBuilder.Entity<RecoveryCode>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Provider)
                .WithMany(p => p.RecoveryCodes)
                .HasForeignKey(x => x.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ProviderId);
        });

        // Patient
        modelBuilder.Entity<Patient>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Diagnoses)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null)!);
            e.Property(x => x.CurrentMedications)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null)!);
        });

        // Note
        modelBuilder.Entity<Note>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.InputMode).HasConversion<string>();

            // Store StructuredNote as JSONB.
            // Plain HasConversion (not OwnsOne(...).ToJson()) — EF Core's .ToJson() owned/complex
            // JSON mapping throws a NullReferenceException while shaping any Dictionary<,> property
            // nested inside it (StructuredNote.Sections here); open bug, still present as of
            // EF Core 10.0.10 / Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3:
            // https://github.com/dotnet/efcore/issues/36805
            //
            // Every HasConversion below also gets an explicit JSON-structural ValueComparer.
            // Without one, EF's change tracker falls back to reference equality for converted
            // reference-type properties, so in-place edits — note.AuditTrail.Add(...) in
            // Update/Sign/Export/PipelineJob, note.StructuredContent.Sections = ... in Update —
            // are never seen as "modified" and SaveChangesAsync silently no-ops on them.
            e.Property(x => x.StructuredContent)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<StructuredNote>(v, (JsonSerializerOptions?)null))
                .Metadata.SetValueComparer(JsonValueComparer<StructuredNote>());

            // Store billing codes and audit trail as JSONB
            e.Property(x => x.BillingCodes)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<BillingCode>>(v, (JsonSerializerOptions?)null)!)
                .Metadata.SetValueComparer(JsonValueComparer<List<BillingCode>>());

            e.Property(x => x.AuditTrail)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<AuditEntry>>(v, (JsonSerializerOptions?)null)!)
                .Metadata.SetValueComparer(JsonValueComparer<List<AuditEntry>>());

            e.HasOne(x => x.Patient)
                .WithMany(p => p.Notes)
                .HasForeignKey(x => x.PatientId);

            e.HasIndex(x => x.PatientId);
            e.HasIndex(x => x.ProviderId);
            e.HasIndex(x => x.Status);
        });

        // ContactMessage — plain scalar fields, no conversions needed.
        modelBuilder.Entity<ContactMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CreatedAt);
        });
    }

    // Structural (not reference) equality for JSON-converted properties, so EF's change
    // tracker detects in-place mutations (List.Add, nested property reassignment) as modified.
    private static ValueComparer<T> JsonValueComparer<T>() => new(
        (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
        v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null)!);
}
