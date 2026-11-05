using Anamnys.Server.Data.Entities;
using Anamnys.Server.Documents;
using Anamnys.Server.Notes;
using Anamnys.Tests.Retention;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Integrity;

// Signed records sealed through the real signing paths, on a RetentionScratch database.
internal static class IntegrityTestData
{
    public static readonly DateTimeOffset Now = new(2026, 11, 3, 14, 0, 0, TimeSpan.Zero);

    // One fake authority for the tests where the timestamp does not matter (it lives for the test process).
    public static readonly FakeTimestampAuthority Tsa = new();

    private static readonly Dictionary<string, string> CompleteAtestado = new()
    {
        ["nome_solicitante"] = "Escola X",
        ["finalidade"] = "Afastamento",
        ["descricao_condicoes_psicologicas"] = "Linha um.\nLinha dois.",
        ["prazo_validade"] = "30 dias.",
    };

    private static readonly string[] AtestadoConfirmations = ["informacao_alem_do_solicitado"];

    // A DAP note with its three sections, ready to be signed.
    public static async Task<Guid> AddReadyNoteAsync(RetentionScratch s, Guid provider, Guid patient, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await s.ExecuteAsync(
            """
            INSERT INTO "Notes" ("Id","ProviderId","PatientId","Status","InputMode","Format","StructureStatus","RawTranscript")
            VALUES (@id, @p, @pa, 'ReadyForReview', 'VoiceLive', 'DAP', 'done', 'transcricao')
            """, ct, ("id", id), ("p", provider), ("pa", patient));
        await s.ExecuteAsync(
            """
            INSERT INTO "NoteSections" ("Id","NoteId","Key","Content","Position","GeneratedAt","Revision") VALUES
            (gen_random_uuid(), @id, 'data', 'a', 0, now(), 0),
            (gen_random_uuid(), @id, 'assessment', 'b', 1, now(), 0),
            (gen_random_uuid(), @id, 'plan', 'c', 2, now(), 0)
            """, ct, ("id", id));
        return id;
    }

    public static async Task<Guid> SignedNoteAsync(RetentionScratch s, Guid provider, Guid patient, DateTimeOffset at, CancellationToken ct)
    {
        var id = await AddReadyNoteAsync(s, provider, patient, ct);
        await using var db = s.CreateDbContext();
        var outcome = await NoteEditing.SignAsync(db, provider, id, at, ct);
        if (outcome != SignOutcome.Signed)
        {
            throw new InvalidOperationException($"Note not signed: {outcome}");
        }
        return id;
    }

    public static async Task<Guid> SignedDocumentAsync(RetentionScratch s, Guid provider, Guid patient, DateTimeOffset at, CancellationToken ct)
    {
        await using var db = s.CreateDbContext();
        var id = (await ClinicalDocumentService.CreateAsync(db, provider, patient, "atestado", null, at, ct)).Id!.Value;
        await ClinicalDocumentService.SaveAsync(db, provider, id, CompleteAtestado, AtestadoConfirmations, [], at, ct);
        var result = await ClinicalDocumentService.SignAsync(db, provider, id, false, at, ct);
        if (result.Outcome != DocumentOutcome.Ok)
        {
            throw new InvalidOperationException($"Document not signed: {result.Outcome}");
        }
        return id;
    }

    public static async Task<List<IntegritySeal>> SealsAsync(RetentionScratch s, Guid provider, CancellationToken ct)
    {
        await using var db = s.CreateDbContext();
        return await db.IntegritySeals.AsNoTracking().Where(x => x.ProviderId == provider).OrderBy(x => x.Sequence).ToListAsync(ct);
    }

    // Runs SQL against the seals with their append-only trigger off, as only a test (or an attacker with the owner role) can.
    public static Task TamperSealsAsync(RetentionScratch s, string sql, CancellationToken ct, params (string Name, object Value)[] parameters) =>
        s.ExecuteAsync(
            $"""
            ALTER TABLE "IntegritySeals" DISABLE TRIGGER "IntegritySeals_append_only";
            {sql};
            ALTER TABLE "IntegritySeals" ENABLE TRIGGER "IntegritySeals_append_only";
            """, ct, parameters);
}
