using Anamnys.Server.Data;
using Anamnys.Tests.Data;
using Npgsql;

namespace Anamnys.Tests.Retention;

// A scratch database from the bootstrap script (which carries the F-06 columns), so the retention tests do not
// depend on the dev database having had design/migrations/2026-10-28-retention-panel.sql applied. Raw SQL for
// the rows whose entities map a column subset (Notes, Appointments).
internal sealed class RetentionScratch : IAsyncDisposable
{
    private readonly ScratchDatabase _scratch;
    private readonly NpgsqlConnection _connection;

    private RetentionScratch(ScratchDatabase scratch, NpgsqlConnection connection)
    {
        _scratch = scratch;
        _connection = connection;
    }

    public static async Task<RetentionScratch> CreateAsync(SharedAppHostFixture fixture, CancellationToken ct)
    {
        var scratch = await ScratchDatabase.CreateAsync(fixture, ct, "anamnys-db-script.sql");
        return new RetentionScratch(scratch, await scratch.OpenAsync(ct));
    }

    public AnamnysDbContext CreateDbContext() => _scratch.CreateDbContext();

    // A second connection, for tests that hold a transaction open while another one writes.
    public Task<NpgsqlConnection> OpenAsync(CancellationToken ct) => _scratch.OpenAsync(ct);

    public async Task<Guid> AddProviderAsync(CancellationToken ct, bool withCrp = false, string checkMode = "blocking")
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name","CrpNumber","CrpRegion","PracticeCity","DocumentCheckMode")
            VALUES (@id, @id::text || '@test.local', gen_random_uuid(), 'Dra. Teste',
                    CASE WHEN @crp THEN '123456' END, CASE WHEN @crp THEN '06' END, CASE WHEN @crp THEN 'São Paulo' END, @mode)
            """, ct, ("id", id), ("crp", withCrp), ("mode", checkMode));
        return id;
    }

    public async Task<Guid> AddPatientAsync(Guid providerId, string firstName, CancellationToken ct, DateTimeOffset? discardedAt = null)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName","DiscardedAt") VALUES (@id, @p, @first, 'Silva', @discarded)""",
            ct, ("id", id), ("p", providerId), ("first", firstName), ("discarded", (object?)discardedAt?.ToUniversalTime() ?? DBNull.Value));
        return id;
    }

    public async Task<Guid> AddNoteAsync(Guid providerId, Guid patientId, DateTimeOffset createdAt, CancellationToken ct, DateTimeOffset? signedAt = null, string? status = null)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO "Notes" ("Id","ProviderId","PatientId","Status","InputMode","Format","SignedAt","SignedByCrp","CreatedAt")
            VALUES (@id, @p, @pa, @status, 'Text', 'PtFunctional', @signed, CASE WHEN @isSigned THEN '123456/06' END, @created)
            """,
            ct, ("id", id), ("p", providerId), ("pa", patientId), ("status", status ?? (signedAt is null ? "ReadyForReview" : "Signed")),
            ("signed", (object?)signedAt?.ToUniversalTime() ?? DBNull.Value), ("isSigned", signedAt is not null), ("created", createdAt.ToUniversalTime()));
        return id;
    }

    public Task AddSectionAsync(Guid noteId, CancellationToken ct) =>
        ExecuteAsync("""INSERT INTO "NoteSections" ("NoteId","Key","Content") VALUES (@n, 'evolucao', 'Texto.')""", ct, ("n", noteId));

    public async Task<Guid> AddSignedDocumentAsync(Guid providerId, Guid patientId, DateTimeOffset signedAt, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO "ClinicalDocuments" ("Id","PatientId","ProviderId","Kind","SignedAt","SignedByCrp","SignedCity","SignedByName","SignedPatientName","CreatedAt",
                                             "RulesetId")
            VALUES (@id, @pa, @p, 'declaracao', @signed, '123456/06', 'São Paulo', 'Dra. Teste', 'Ana Silva', @signed,
                    (SELECT "Id" FROM "ConformityRulesets" WHERE "DocumentKind" = 'declaracao' ORDER BY "EffectiveFrom" DESC LIMIT 1))
            """, ct, ("id", id), ("pa", patientId), ("p", providerId), ("signed", signedAt.ToUniversalTime()));
        return id;
    }

    public async Task<Guid> AddSessionAsync(Guid providerId, Guid patientId, DateTimeOffset createdAt, CancellationToken ct, string attendance = "attended", DateTimeOffset? actualStart = null)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """INSERT INTO "Sessions" ("Id","PatientId","ProviderId","Attendance","CreatedAt","ActualStart") VALUES (@id, @pa, @p, @att, @created, @start)""",
            ct, ("id", id), ("pa", patientId), ("p", providerId), ("att", attendance), ("created", createdAt.ToUniversalTime()),
            ("start", (object?)actualStart?.ToUniversalTime() ?? DBNull.Value));
        return id;
    }

    public async Task<Guid> AddAppointmentAsync(Guid providerId, Guid patientId, DateTimeOffset startsAt, string status, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO "Appointments" ("Id","ProviderId","PatientId","StartsAt","EndsAt","Status","CancelledAt")
            VALUES (@id, @p, @pa, @s, @s + interval '50 minutes', @status, CASE WHEN @status = 'cancelled' THEN now() END)
            """, ct, ("id", id), ("p", providerId), ("pa", patientId), ("s", startsAt.ToUniversalTime()), ("status", status));
        return id;
    }

    // Marks a record discarded after its rows were added: the database refuses new rows for a discarded record.
    public Task MarkDiscardedAsync(Guid patientId, DateTimeOffset at, CancellationToken ct) =>
        ExecuteAsync("""UPDATE "Patients" SET "DiscardedAt" = @at WHERE "Id" = @pa""", ct, ("pa", patientId), ("at", at.ToUniversalTime()));

    public Task SetRetentionAsync(Guid patientId, DateOnly? lastRecordAt, DateOnly? retentionUntil, CancellationToken ct) =>
        ExecuteAsync(
            """UPDATE "Patients" SET "LastRecordAt" = @last, "RetentionUntil" = @until WHERE "Id" = @pa""",
            ct, ("pa", patientId), ("last", (object?)lastRecordAt ?? DBNull.Value), ("until", (object?)retentionUntil ?? DBNull.Value));

    public async Task<(DateOnly? LastRecordAt, DateOnly? RetentionUntil)> RetentionOfAsync(Guid patientId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""SELECT "LastRecordAt", "RetentionUntil" FROM "Patients" WHERE "Id" = @pa""", _connection);
        command.Parameters.AddWithValue("pa", patientId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.IsDBNull(0) ? null : reader.GetFieldValue<DateOnly>(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateOnly>(1));
    }

    public async Task<long> CountAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, _connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, _connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _scratch.DisposeAsync();
    }
}
