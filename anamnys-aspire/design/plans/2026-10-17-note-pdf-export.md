# Note PDF Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A signed note can be exported as a PDF that is generated once, stored in a private self-hosted SeaweedFS bucket, and downloaded only through one-hour presigned URLs.

**Architecture:** A SeaweedFS container (S3 mode) joins the AppHost. The server talks to it through `INoteFileStore` (AWSSDK.S3), renders the PDF with QuestPDF from a pure `NotePdfModel`, and `NoteExport.ExportAsync` stores it on the first export (note → `Exported`, SHA-256 kept, audited) and only presigns afterwards. The note page's sign card gets "Exportar PDF" / "Baixar PDF".

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, PostgreSQL 18, Aspire 13.5, SeaweedFS (S3), AWSSDK.S3, QuestPDF (Community), xUnit v3 + FluentAssertions; React 19, TanStack Query, i18next.

**Spec:** `design/specs/2026-10-17-note-pdf-export-design.md`

## Global Constraints

- The PDF never contains `RawTranscript` (transcript or typed original text).
- Object key `notes/{providerId}/{noteId}.pdf`; download file name `nota-{CODE}.pdf` where CODE = first 8 hex chars of the note id, upper case. No patient name in any key, file name or URL.
- Presigned GET valid exactly 1 hour; `ExpiresAt = now + 1 h`.
- Bucket `notes`, private (no anonymous access, no public listing).
- First export: `PdfObjectKey`, `PdfSha256` (lower-case hex), `ExportedAt`, `Status = 'Exported'`, audit `exported` (`NewValue` = SHA-256, `ActorId` = provider). Later exports: no upload, audit `pdf_link_issued` (`ActorId` = provider). Both in the same transaction as the note row lock (`NoteLocks.LockAsync`).
- Route `POST /api/phi/providers/me/notes/{noteId}/export` → `200 { url, expiresAt }` | 404 | `409 { message: "Assine a nota antes de exportar.", code: "not_signed" }` | `503 { message: "Armazenamento indisponível. Tente de novo em instantes.", code: "storage_unavailable" }`.
- PDF text (pt-BR): header "CRP {number}/{region}"; "Nascimento: {dd/MM/yyyy}" (only when known); "Atendimento: {dd/MM/yyyy}"; empty section "Nada registrado"; "Assinado eletronicamente no Anamnys por {name}, CRP {crp}, em {dd/MM/yyyy HH:mm}"; footer "Página X de Y" and "Nota {CODE}". Times in `America/Sao_Paulo`.
- Section titles reuse the labels of `noteView.sections` in pt.json: data "Dados", assessment "Avaliação", plan "Plano", subjective "Subjetivo", objective "Objetivo", biological "Biológico", psychological "Psicológico", social "Social". Format names: DAP, SOAP, "Biopsicossocial".
- SeaweedFS image pinned to an exact release tag (never `latest`). Credentials only from `secret: true` AppHost parameters `storage-access-key` / `storage-secret-key`.
- QuestPDF license set to `LicenseType.Community` at startup.
- Every query provider-scoped; no PHI in logs.
- Tests: from `anamnys-aspire/`, `dotnet run --project anamnys-aspire.Tests -- -class "*Name*"`; never `dotnet test`; `aspire stop` before building. Frontend: `npm run build`, `npm run lint` from `anamnys-aspire/`.
- C# style per CLAUDE.md; tests `<Method>_<Conditions>_<Outcome>` with `// Arrange` / `// Act` / `// Assert`.
- **Do not commit** (user rule: the user reviews first). Preserve line endings.

---

## File Structure

- Database: `design/migrations/2026-10-17-note-pdf.sql` (new), `anamnys-db-script.sql`; `Data/Entities/Note.cs`.
- Infra: `anamnys-aspire.AppHost/AppHost.cs`; `anamnys-aspire.Server/Storage/StorageOptions.cs`, `INoteFileStore.cs`, `S3NoteFileStore.cs`, `StorageInitializer.cs`, `StorageSetup.cs` (new); `Program.cs`; server csproj (`AWSSDK.S3`, `QuestPDF`).
- PDF: `anamnys-aspire.Server/Notes/NotePdfModel.cs`, `Notes/NotePdf.cs` (new).
- Export: `anamnys-aspire.Server/Notes/NoteExport.cs` (new), `Notes/NoteEndpoints.cs`, `Notes/NoteQueries.cs` (`ExportedAt`), `anamnys-aspire.Server.http`.
- Tests: `Data/NotePdfMigrationTests.cs`, `Storage/S3NoteFileStoreTests.cs`, `Notes/NotePdfTests.cs`, `Notes/NoteExportTests.cs`, `Notes/FakeNoteFileStore.cs` (new); `SharedAppHostFixture.cs`; `Notes/NoteEndpointTests.cs`.
- Frontend: `packages/shared/src/lib/types.ts`, `packages/shared/src/api/notes.ts`, `pt.json`, `apps/provider/src/components/notes/SignCard.tsx`.
- Docs: `apps/web/src/content/site/faq.md`, `CLAUDE.md`, gaps file, status file, spec status.

---

### Task 1: Migration and entity

**Files:** Create `design/migrations/2026-10-17-note-pdf.sql`; modify `anamnys-db-script.sql`, `anamnys-aspire.Server/Data/Entities/Note.cs`; test `anamnys-aspire.Tests/Data/NotePdfMigrationTests.cs`.

**Interfaces — produces:** `Notes."PdfObjectKey" text`, `"PdfSha256" text`, `"ExportedAt" timestamptz`, constraint `Notes_Exported_ck`; `Note.PdfObjectKey` (`string?`), `Note.PdfSha256` (`string?`), `Note.ExportedAt` (`DateTimeOffset?`).

- [ ] **Step 1: Failing test** — `NotePdfMigrationTests.cs`, a copy of `NoteReviewMigrationTests` with:
  - old DB: every migration through `2026-10-13-note-review.sql`, then `2026-10-17-note-pdf.sql` twice; fresh: bootstrap script + the new migration once; shapes equal; `freshShape` contains `"Notes.PdfObjectKey"`, `"Notes.PdfSha256"`, `"Notes.ExportedAt"`.
  - `ExportedNote_WithoutObjectKey_IsRejected`: insert (same CTE as the existing signed-note test) a note with `Status = 'Exported'`, `SignedAt = now()`, `SignedByCrp = '1/06'` and no PDF columns → `PostgresException` with `ConstraintName` `Notes_Exported_ck`.
  - `Migration_ExistingExportedWithoutFile_BecomesSigned`: on the old DB before the new migration insert such a note, run the migration, assert its `Status` is `Signed`.
- [ ] **Step 2: Run → FAIL** (`-class "*NotePdfMigrationTests*"`).
- [ ] **Step 3: Migration**

```sql
-- Note PDF export (design/specs/2026-10-17-note-pdf-export-design.md §3). Run after
-- 2026-10-13-note-review.sql. Idempotent, and all-or-nothing.
--
--   docker exec -i <postgres container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d anamnysdb -v ON_ERROR_STOP=1' < design/migrations/2026-10-17-note-pdf.sql

BEGIN;

ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "PdfObjectKey" text;
ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "PdfSha256" text;
ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "ExportedAt" timestamp with time zone;
-- Seed rows marked Exported never had a file.
UPDATE "Notes" SET "Status" = 'Signed' WHERE "Status" = 'Exported' AND "PdfObjectKey" IS NULL;
ALTER TABLE "Notes" DROP CONSTRAINT IF EXISTS "Notes_Exported_ck";
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_Exported_ck" CHECK (
    (("Status" = 'Exported') = ("PdfObjectKey" IS NOT NULL))
    AND (("PdfObjectKey" IS NULL) = ("PdfSha256" IS NULL))
    AND (("PdfObjectKey" IS NULL) = ("ExportedAt" IS NULL)));

COMMIT;
```

- [ ] **Step 4: Bootstrap script** — in `anamnys-db-script.sql` `CREATE TABLE "Notes"` add the three columns after `"SignedByCrp"` and the `Notes_Exported_ck` constraint next to `Notes_SignedByCrp_ck` (same expression).
- [ ] **Step 5: Entity** — add to `Note`: `public string? PdfObjectKey { get; set; }`, `public string? PdfSha256 { get; set; }`, `public DateTimeOffset? ExportedAt { get; set; }`.
- [ ] **Step 6: Apply to the dev DB** (the migration header's `docker exec` command against the running dev Postgres; start it with `aspire start` only if needed, then `aspire stop` before tests) and run → PASS (`-class "*NotePdfMigrationTests*"`, `-class "*NoteReviewMigrationTests*"`).
- [ ] **Step 7: Stop** — leave uncommitted.

---

### Task 2: SeaweedFS in the AppHost and the file store

**Files:** modify `anamnys-aspire.AppHost/AppHost.cs`, `anamnys-aspire.Server/anamnys-aspire.Server.csproj`, `Program.cs`, `anamnys-aspire.Tests/SharedAppHostFixture.cs`; create `anamnys-aspire.Server/Storage/{StorageOptions,INoteFileStore,S3NoteFileStore,StorageInitializer,StorageSetup}.cs`; test `anamnys-aspire.Tests/Storage/S3NoteFileStoreTests.cs`.

**Interfaces — produces:**
- `StorageOptions { string ServiceUrl; string PublicUrl; string AccessKey; string SecretKey; string Bucket = "notes"; }` bound from section `Storage`.
- `INoteFileStore { Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct); Uri PresignGet(string key, string downloadFileName, TimeSpan validFor); }`
- `S3NoteFileStore(StorageOptions options) : INoteFileStore` with `Task EnsureBucketAsync(CancellationToken ct)`.
- `StorageSetup.AddNoteStorage(this IHostApplicationBuilder builder)` — registers `StorageOptions`, `S3NoteFileStore` as singleton `INoteFileStore`, and `StorageInitializer` (hosted service calling `EnsureBucketAsync`).
- `SharedAppHostFixture.StorageOptions` (a `StorageOptions` for this test run's store).

- [ ] **Step 1: Packages** — `dotnet add anamnys-aspire.Server package AWSSDK.S3` and `dotnet add anamnys-aspire.Server package QuestPDF` (latest stable of each; record the versions in the report).

- [ ] **Step 2: AppHost** — after `anamnysDb`:

```csharp
// Private object store for exported PDFs (design/specs/2026-10-17-note-pdf-export-design.md §4).
// SeaweedFS in S3 mode; the identity file is written from the secret parameters at start so no
// credential is ever committed. -filer.encryptVolumeData encrypts the stored chunks.
var storageAccessKey = builder.AddParameter("storage-access-key", secret: true);
var storageSecretKey = builder.AddParameter("storage-secret-key", secret: true);
var storage = builder.AddContainer("storage", "chrislusf/seaweedfs", "<PINNED TAG>")
    .WithVolume("anamnys-storage-data", "/data")
    .WithEnvironment("S3_ACCESS_KEY", storageAccessKey)
    .WithEnvironment("S3_SECRET_KEY", storageSecretKey)
    .WithEntrypoint("/bin/sh")
    .WithArgs("-c", """
        printf '{"identities":[{"name":"anamnys","credentials":[{"accessKey":"%s","secretKey":"%s"}],"actions":["Admin","Read","Write","List","Tagging"]}]}' "$S3_ACCESS_KEY" "$S3_SECRET_KEY" > /tmp/s3.json && exec weed server -dir=/data -s3 -s3.port=8333 -s3.config=/tmp/s3.json -filer.encryptVolumeData -volume.max=0
        """)
    .WithHttpEndpoint(targetPort: 8333, name: "s3");
```

  `<PINNED TAG>`: the newest `chrislusf/seaweedfs` release tag that is at least two weeks old (check Docker Hub / GitHub releases; `docker pull` it). Run `docker run --rm chrislusf/seaweedfs:<tag> server -h` and confirm `-filer.encryptVolumeData`, `-s3.config` and `-volume.max` exist under those names; adjust only the flag spelling if the release names them differently, and say so in the report.

  On the `server` resource add:

```csharp
    .WithEnvironment("Storage__ServiceUrl", storage.GetEndpoint("s3"))
    // The browser downloads straight from the store, so URLs are signed for the host it can reach.
    // In a dev run that is the same localhost endpoint; a deployment sets its TLS host here.
    .WithEnvironment("Storage__PublicUrl", storage.GetEndpoint("s3"))
    .WithEnvironment("Storage__AccessKey", storageAccessKey)
    .WithEnvironment("Storage__SecretKey", storageSecretKey)
    .WaitFor(storage)
```

  Set the two secrets for your machine: `dotnet user-secrets --project anamnys-aspire.AppHost set "Parameters:storage-access-key" "$(openssl rand -hex 16)"` and the same for `storage-secret-key` with `rand -hex 32`.

- [ ] **Step 3: Failing integration test** — `Storage/S3NoteFileStoreTests.cs`:

```csharp
using System.Security.Cryptography;
using Anamnys.Server.Storage;
using FluentAssertions;

namespace Anamnys.Tests.Storage;

[Collection(SharedAppHostCollection.Name)]
public class S3NoteFileStoreTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PresignGet_AfterPut_DownloadsSameBytesAsAttachment()
    {
        // Arrange
        var store = new S3NoteFileStore(fixture.StorageOptions);
        await store.EnsureBucketAsync(Ct);
        var key = $"tests/{Guid.NewGuid()}.pdf";
        var content = "%PDF-1.7 test"u8.ToArray();
        await store.PutAsync(key, content, "application/pdf", Ct);

        // Act
        var url = store.PresignGet(key, "nota-ABCDEF12.pdf", TimeSpan.FromHours(1));
        using var http = new HttpClient();
        using var response = await http.GetAsync(url, Ct);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        response.Content.Headers.ContentDisposition!.FileName!.Trim('"').Should().Be("nota-ABCDEF12.pdf");
        SHA256.HashData(await response.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(SHA256.HashData(content));
    }

    [Fact]
    public async Task Get_WithoutSignature_IsRefused()
    {
        // Arrange
        var store = new S3NoteFileStore(fixture.StorageOptions);
        await store.EnsureBucketAsync(Ct);
        var key = $"tests/{Guid.NewGuid()}.pdf";
        await store.PutAsync(key, "%PDF"u8.ToArray(), "application/pdf", Ct);

        // Act
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(new Uri(fixture.StorageOptions.PublicUrl), $"{fixture.StorageOptions.Bucket}/{key}"), Ct);

        // Assert
        response.IsSuccessStatusCode.Should().BeFalse();
    }
}
```

  Fixture: after `_app.StartAsync`, wait for `storage` to be running, read the two parameter values (as it does for the Keycloak admin parameters: find the `ParameterResource` named `storage-access-key` / `storage-secret-key` in `appHost.Resources` and `GetValueAsync`), and set `StorageOptions = new StorageOptions { ServiceUrl = url, PublicUrl = url, AccessKey = ..., SecretKey = ..., Bucket = "notes" }` with `url = _app.GetEndpoint("storage", "s3").ToString()` (trailing slash trimmed).

- [ ] **Step 4: Run → FAIL** (types missing).

- [ ] **Step 5: Storage code** (`anamnys-aspire.Server/Storage/`):

```csharp
namespace Anamnys.Server.Storage;

public sealed class StorageOptions
{
    public string ServiceUrl { get; set; } = "";
    public string PublicUrl { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Bucket { get; set; } = "notes";
}
```

```csharp
namespace Anamnys.Server.Storage;

// Private object storage for exported files (design/specs/2026-10-17-note-pdf-export-design.md §5.1).
public interface INoteFileStore
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct);

    Uri PresignGet(string key, string downloadFileName, TimeSpan validFor);
}
```

```csharp
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Anamnys.Server.Storage;

// Uploads go to the internal endpoint; URLs are signed for the public one, because SigV4 signs the
// host the browser will call.
public sealed class S3NoteFileStore(StorageOptions options) : INoteFileStore
{
    private readonly AmazonS3Client _service = Client(options.ServiceUrl, options);
    private readonly AmazonS3Client _public = Client(options.PublicUrl, options);

    public async Task EnsureBucketAsync(CancellationToken ct)
    {
        var buckets = await _service.ListBucketsAsync(ct);
        if (buckets.Buckets?.Any(b => b.BucketName == options.Bucket) != true)
        {
            await _service.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket }, ct);
        }
    }

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        using var stream = new MemoryStream(content, writable: false);
        await _service.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
        }, ct);
    }

    public Uri PresignGet(string key, string downloadFileName, TimeSpan validFor)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(validFor),
            Protocol = options.PublicUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTPS : Protocol.HTTP,
        };
        request.ResponseHeaderOverrides.ContentDisposition = $"attachment; filename=\"{downloadFileName}\"";
        request.ResponseHeaderOverrides.ContentType = "application/pdf";
        return new Uri(_public.GetPreSignedURL(request));
    }

    private static AmazonS3Client Client(string url, StorageOptions options) =>
        new(new BasicAWSCredentials(options.AccessKey, options.SecretKey), new AmazonS3Config
        {
            ServiceURL = url,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        });
}
```

```csharp
namespace Anamnys.Server.Storage;

public sealed class StorageInitializer(S3NoteFileStore store, ILogger<StorageInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await store.EnsureBucketAsync(cancellationToken);
        logger.LogInformation("Note storage bucket ready.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

```csharp
namespace Anamnys.Server.Storage;

public static class StorageSetup
{
    public static void AddNoteStorage(this IHostApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<S3NoteFileStore>();
        builder.Services.AddSingleton<INoteFileStore>(sp => sp.GetRequiredService<S3NoteFileStore>());
        builder.Services.AddHostedService<StorageInitializer>();
    }
}
```

  In `Program.cs`, after `builder.AddNpgsqlDbContext<…>`: `builder.AddNoteStorage();` and `QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;` (QuestPDF Community license: free below US$ 1M annual revenue).

  If the AWSSDK version's API differs (e.g. `ListBucketsAsync` overloads, `Expires` type), adapt minimally and note it.

- [ ] **Step 6: Run → PASS** (`-class "*S3NoteFileStoreTests*"`), then `-class "*NoteEndpointTests*"` to confirm the server still starts with the storage dependency.
- [ ] **Step 7: Stop** — leave uncommitted.

---

### Task 3: PDF model and rendering

**Files:** create `anamnys-aspire.Server/Notes/NotePdfModel.cs`, `Notes/NotePdf.cs`; test `anamnys-aspire.Tests/Notes/NotePdfTests.cs`.

**Interfaces — produces:**
- `record NotePdfSection(string Title, string Content)`.
- `record NotePdfModel(string ProviderName, string Crp, string PatientName, DateOnly? PatientBirthDate, DateTimeOffset SessionDate, string FormatName, IReadOnlyList<NotePdfSection> Sections, DateTimeOffset SignedAt, string Code)`.
- `NotePdfModel.From(Note note, IReadOnlyList<NoteSection> sections, Patient patient, Provider provider, Session? session) : NotePdfModel` — `Crp = note.SignedByCrp`; `SessionDate = session?.ScheduledStart ?? session?.ActualStart ?? note.CreatedAt`; sections ordered by `Position`, titles from a static key → label map (Global Constraints), content trimmed; `Code = note.Id.ToString("N")[..8].ToUpperInvariant()`; `FormatName` = "DAP" | "SOAP" | "Biopsicossocial".
- `NotePdfModel.FileName => $"nota-{Code}.pdf"`.
- `NotePdf.Render(NotePdfModel model) : byte[]`.

- [ ] **Step 1: Failing tests** — `NotePdfTests.cs` (pure, no fixture):

```csharp
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Notes;
using FluentAssertions;

namespace Anamnys.Tests.Notes;

public class NotePdfTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 17, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void From_Sections_OrderedLabelledAndEmptyMarked()
    {
        // Arrange
        var (note, sections, patient, provider) = Sample();

        // Act
        var model = NotePdfModel.From(note, sections, patient, provider, null);

        // Assert
        model.Sections.Select(s => s.Title).Should().Equal("Dados", "Avaliação", "Plano");
        model.Sections.Select(s => s.Content).Should().Equal("Relata insônia.", "", "Retorno.");
        model.FormatName.Should().Be("DAP");
        model.Crp.Should().Be("06/123456");
        model.Code.Should().Be(note.Id.ToString("N")[..8].ToUpperInvariant());
        model.FileName.Should().Be($"nota-{model.Code}.pdf");
    }

    [Fact]
    public void From_NeverCarriesTheTranscript()
    {
        // Arrange
        var (note, sections, patient, provider) = Sample();
        note.RawTranscript = "TRANSCRICAO-SECRETA";

        // Act
        var model = NotePdfModel.From(note, sections, patient, provider, null);

        // Assert
        model.ToString().Should().NotContain("TRANSCRICAO-SECRETA");
        model.Sections.Should().NotContain(s => s.Content.Contains("TRANSCRICAO-SECRETA"));
    }

    [Fact]
    public void From_SessionDate_PrefersScheduledThenActualThenCreated()
    {
        // Arrange
        var (note, sections, patient, provider) = Sample();
        var scheduled = Created.AddDays(-1);
        var actual = Created.AddDays(-2);

        // Act
        var withScheduled = NotePdfModel.From(note, sections, patient, provider, new Session { ScheduledStart = scheduled, ActualStart = actual });
        var withActual = NotePdfModel.From(note, sections, patient, provider, new Session { ActualStart = actual });
        var without = NotePdfModel.From(note, sections, patient, provider, null);

        // Assert
        withScheduled.SessionDate.Should().Be(scheduled);
        withActual.SessionDate.Should().Be(actual);
        without.SessionDate.Should().Be(Created);
    }

    [Fact]
    public void Render_Model_ProducesPdfBytes()
    {
        // Arrange
        var (note, sections, patient, provider) = Sample();
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        // Act
        var bytes = NotePdf.Render(NotePdfModel.From(note, sections, patient, provider, null));

        // Assert
        bytes.Should().NotBeEmpty();
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");
    }

    private static (Note, IReadOnlyList<NoteSection>, Patient, Provider) Sample()
    {
        var note = new Note
        {
            Id = Guid.NewGuid(), Format = "DAP", Status = "Signed", CreatedAt = Created,
            SignedAt = Created.AddHours(1), SignedByCrp = "06/123456",
        };
        IReadOnlyList<NoteSection> sections =
        [
            new() { Key = "plan", Content = " Retorno. ", Position = 2 },
            new() { Key = "data", Content = "Relata insônia.", Position = 0 },
            new() { Key = "assessment", Content = "", Position = 1 },
        ];
        var patient = new Patient { FirstName = "Ana", LastName = "Silva", DateOfBirth = new DateOnly(1990, 5, 4) };
        var provider = new Provider { Name = "Dra. Paula Souza" };
        return (note, sections, patient, provider);
    }
}
```

- [ ] **Step 2: Run → FAIL** (`-class "*NotePdfTests*"`).

- [ ] **Step 3: `NotePdfModel.cs`**

```csharp
using Anamnys.Server.Data.Entities;

namespace Anamnys.Server.Notes;

public sealed record NotePdfSection(string Title, string Content);

// What the exported PDF shows (design/specs/2026-10-17-note-pdf-export-design.md §5.2).
// Built from the signed note only: the transcript / typed original text never reaches it.
public sealed record NotePdfModel(
    string ProviderName, string Crp, string PatientName, DateOnly? PatientBirthDate,
    DateTimeOffset SessionDate, string FormatName, IReadOnlyList<NotePdfSection> Sections,
    DateTimeOffset SignedAt, string Code)
{
    private static readonly Dictionary<string, string> Titles = new()
    {
        ["data"] = "Dados", ["assessment"] = "Avaliação", ["plan"] = "Plano",
        ["subjective"] = "Subjetivo", ["objective"] = "Objetivo",
        ["biological"] = "Biológico", ["psychological"] = "Psicológico", ["social"] = "Social",
    };

    public string FileName => $"nota-{Code}.pdf";

    public static NotePdfModel From(Note note, IReadOnlyList<NoteSection> sections, Patient patient, Provider provider, Session? session) =>
        new(
            provider.Name,
            note.SignedByCrp ?? "",
            $"{patient.FirstName} {patient.LastName}".Trim(),
            patient.DateOfBirth,
            session?.ScheduledStart ?? session?.ActualStart ?? note.CreatedAt,
            note.Format == "Biopsychosocial" ? "Biopsicossocial" : note.Format,
            sections.OrderBy(s => s.Position)
                .Select(s => new NotePdfSection(Titles.GetValueOrDefault(s.Key, s.Key), s.Content.Trim()))
                .ToList(),
            note.SignedAt!.Value,
            note.Id.ToString("N")[..8].ToUpperInvariant());
}
```

- [ ] **Step 4: `NotePdf.cs`**

```csharp
using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace Anamnys.Server.Notes;

public static class NotePdf
{
    private static readonly TimeZoneInfo Practice = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static byte[] Render(NotePdfModel m) =>
        Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2, QuestPDF.Infrastructure.Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(11));

            page.Header().Column(col =>
            {
                col.Item().Text(m.ProviderName).FontSize(14).SemiBold();
                col.Item().Text($"CRP {m.Crp}");
            });

            page.Content().PaddingVertical(12).Column(col =>
            {
                col.Spacing(8);
                col.Item().Text(m.PatientName).FontSize(13).SemiBold();
                if (m.PatientBirthDate is { } birth)
                {
                    col.Item().Text($"Nascimento: {birth:dd/MM/yyyy}");
                }
                col.Item().Text($"Atendimento: {Local(m.SessionDate):dd/MM/yyyy}");
                col.Item().Text($"Formato: {m.FormatName}");
                foreach (var section in m.Sections)
                {
                    col.Item().PaddingTop(6).Text(section.Title).SemiBold();
                    col.Item().Text(section.Content.Length == 0 ? "Nada registrado" : section.Content);
                }
                col.Item().PaddingTop(16).Text(
                    $"Assinado eletronicamente no Anamnys por {m.ProviderName}, CRP {m.Crp}, em {Local(m.SignedAt):dd/MM/yyyy HH:mm}.");
            });

            page.Footer().Row(row =>
            {
                row.RelativeItem().Text($"Nota {m.Code}").FontSize(9);
                row.RelativeItem().AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(9));
                    t.Span("Página ");
                    t.CurrentPageNumber();
                    t.Span(" de ");
                    t.TotalPages();
                });
            });
        })).GeneratePdf();

    private static DateTime Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Practice).DateTime;
}
```

  If the installed QuestPDF version names an API differently, adapt minimally and note it.

- [ ] **Step 5: Run → PASS** (`-class "*NotePdfTests*"`). Also write the sample PDF to a temp file once and open it to eyeball accents and layout (do not commit the file).
- [ ] **Step 6: Stop** — leave uncommitted.

---

### Task 4: Export service and route

**Files:** create `anamnys-aspire.Server/Notes/NoteExport.cs`, `anamnys-aspire.Tests/Notes/FakeNoteFileStore.cs`, `anamnys-aspire.Tests/Notes/NoteExportTests.cs`; modify `Notes/NoteEndpoints.cs`, `Notes/NoteQueries.cs`, `anamnys-aspire.Server.http`, `anamnys-aspire.Tests/Notes/NoteEndpointTests.cs`.

**Interfaces:**
- Consumes: Task 1 columns, Task 2 `INoteFileStore`, Task 3 `NotePdfModel` / `NotePdf`.
- Produces: `enum ExportOutcome { Ok, NotFound, NotSigned, StorageUnavailable }`; `record ExportResult(ExportOutcome Outcome, Uri? Url, DateTimeOffset? ExpiresAt)`; `NoteExport.LinkLifetime = TimeSpan.FromHours(1)`; `NoteExport.ExportAsync(AnamnysDbContext db, INoteFileStore store, Guid providerId, Guid noteId, DateTimeOffset now, CancellationToken ct) : Task<ExportResult>`; `NoteDetail` trailing `DateTimeOffset? ExportedAt`.

- [ ] **Step 1: Fake store** — `FakeNoteFileStore`:

```csharp
using Anamnys.Server.Storage;

namespace Anamnys.Tests.Notes;

internal sealed class FakeNoteFileStore : INoteFileStore
{
    public Dictionary<string, byte[]> Objects { get; } = [];
    public int Puts { get; private set; }
    public bool Fail { get; set; }

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        if (Fail)
        {
            throw new HttpRequestException("store down");
        }
        Puts++;
        Objects[key] = content;
        return Task.CompletedTask;
    }

    public Uri PresignGet(string key, string downloadFileName, TimeSpan validFor) =>
        new($"http://store.test/{key}?name={downloadFileName}&ttl={(int)validFor.TotalSeconds}");
}
```

- [ ] **Step 2: Failing tests** — `NoteExportTests.cs` (`[Collection(SharedAppHostCollection.Name)]`, `PatientSearchSeed`, a signed note helper that inserts a patient, a `Signed` note with `SignedAt`, `SignedByCrp = '06/123456'`, `Format = 'DAP'` and its three DAP sections; set `QuestPDF.Settings.License = LicenseType.Community` in the class constructor):
  - `ExportAsync_FirstExport_UploadsStoresHashMarksExportedAndAudits`: outcome `Ok`; `store.Puts == 1`; key `notes/{provider}/{note}.pdf` exists and starts with `%PDF`; note `Status == "Exported"`, `PdfObjectKey` = that key, `PdfSha256` = lower-case hex SHA-256 of the stored bytes, `ExportedAt == now`; one audit `exported` with `NewValue` = the hash and `ActorId` = provider; `Url` contains `nota-` and `ttl=3600`; `ExpiresAt == now + 1 h`.
  - `ExportAsync_SecondExport_DoesNotUploadAndAuditsLink`: two calls (second at `now + 5 min`); `Puts == 1`; `ExportedAt` still the first `now`; audit actions `["exported", "pdf_link_issued"]`.
  - `ExportAsync_UnsignedNote_NotSigned` (`Status = 'ReadyForReview'`, no `SignedAt`).
  - `ExportAsync_OtherProvider_NotFound`.
  - `ExportAsync_StoreFails_StorageUnavailableAndNoteUnchanged`: `store.Fail = true`; outcome `StorageUnavailable`; note still `Signed`, `PdfObjectKey` null, no audit row.
  - `GetAsync_ExportedNote_ReturnsExportedAt` (after an export, `NoteQueries.GetAsync(...).ExportedAt == now`).
  Add to `NoteEndpointTests.cs`: `Export_WithoutSession_Returns401` (POST `/api/phi/providers/me/notes/{Guid.NewGuid()}/export`).

- [ ] **Step 3: Run → FAIL**.

- [ ] **Step 4: `NoteExport.cs`**

```csharp
using System.Security.Cryptography;
using Anamnys.Server.Data;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

public enum ExportOutcome { Ok, NotFound, NotSigned, StorageUnavailable }

public sealed record ExportResult(ExportOutcome Outcome, Uri? Url, DateTimeOffset? ExpiresAt);

// PDF export of a signed note (design/specs/2026-10-17-note-pdf-export-design.md §5.3): generated and
// stored on the first export, only presigned afterwards.
public static class NoteExport
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    public static async Task<ExportResult> ExportAsync(
        AnamnysDbContext db, INoteFileStore store, Guid providerId, Guid noteId, DateTimeOffset now, CancellationToken ct)
    {
        var outcome = ExportOutcome.NotFound;
        string? key = null;
        string? fileName = null;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            outcome = ExportOutcome.NotFound;
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await NoteLocks.LockAsync(db, noteId, ct);

            var note = await db.Notes.SingleOrDefaultAsync(n => n.Id == noteId && n.ProviderId == providerId, ct);
            if (note is null)
            {
                return;
            }
            if (note.Status is not ("Signed" or "Exported"))
            {
                outcome = ExportOutcome.NotSigned;
                return;
            }
            fileName = $"nota-{note.Id.ToString("N")[..8].ToUpperInvariant()}.pdf";

            if (note.PdfObjectKey is null)
            {
                var sections = await db.NoteSections.AsNoTracking().Where(s => s.NoteId == noteId).ToListAsync(ct);
                var patient = await db.Patients.AsNoTracking().SingleAsync(p => p.Id == note.PatientId && p.ProviderId == providerId, ct);
                var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == providerId, ct);
                var session = note.SessionId is { } sessionId
                    ? await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId, ct)
                    : null;
                var bytes = NotePdf.Render(NotePdfModel.From(note, sections, patient, provider, session));
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var objectKey = $"notes/{providerId}/{noteId}.pdf";
                try
                {
                    await store.PutAsync(objectKey, bytes, "application/pdf", ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    outcome = ExportOutcome.StorageUnavailable;
                    return;
                }
                note.PdfObjectKey = objectKey;
                note.PdfSha256 = hash;
                note.ExportedAt = now;
                note.Status = "Exported";
                note.UpdatedAt = now;
                NoteAudit.Add(db, noteId, providerId, "exported", null, null, hash, now);
            }
            else
            {
                NoteAudit.Add(db, noteId, providerId, "pdf_link_issued", null, null, null, now);
            }
            key = note.PdfObjectKey;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            outcome = ExportOutcome.Ok;
        });

        return outcome == ExportOutcome.Ok
            ? new ExportResult(outcome, store.PresignGet(key!, fileName!, LinkLifetime), now + LinkLifetime)
            : new ExportResult(outcome, null, null);
    }
}
```

  Logs never include the patient name or note content.

- [ ] **Step 5: Route** — in `NoteEndpoints.MapNoteEndpoints`:

```csharp
        notes.MapPost("{noteId:guid}/export", async (Guid noteId, HttpContext http, AnamnysDbContext db, INoteFileStore store, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await NoteExport.ExportAsync(db, store, providerId, noteId, DateTimeOffset.UtcNow, ct);
            return result.Outcome switch
            {
                ExportOutcome.Ok => Results.Ok(new { url = result.Url, expiresAt = result.ExpiresAt }),
                ExportOutcome.NotFound => Results.NotFound(),
                ExportOutcome.NotSigned => Results.Conflict(new { message = "Assine a nota antes de exportar.", code = "not_signed" }),
                _ => Results.Json(new { message = "Armazenamento indisponível. Tente de novo em instantes.", code = "storage_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
            };
        });
```

- [ ] **Step 6: `NoteDetail.ExportedAt`** — trailing record parameter, projected from `n.ExportedAt`.
- [ ] **Step 7: `.http`** — append a request in the file's style:

```http
### Export a signed note as PDF (returns a presigned URL valid for 1 hour)
POST {{Server_HostAddress}}/api/phi/providers/me/notes/00000000-0000-0000-0000-000000000000/export
```

- [ ] **Step 8: Run → PASS** (`-class "*NoteExportTests*"`, `-class "*NoteEndpointTests*"`, `-class "*NoteSigningTests*"`, `-class "*NoteEditingTests*"`).
- [ ] **Step 9: Stop** — leave uncommitted.

---

### Task 5: Export buttons in the provider app

**Files:** modify `packages/shared/src/lib/types.ts`, `packages/shared/src/api/notes.ts`, `packages/shared/src/lib/i18n/locales/pt.json`, `apps/provider/src/components/notes/SignCard.tsx`.

**Interfaces — produces:** `NoteDetail.exportedAt: string | null`; `notesApi.exportPdf(noteId: string): Promise<{ url: string; expiresAt: string }>` (replaces the legacy `export`).

- [ ] **Step 1: Types and API** — add `exportedAt: string | null` to `NoteDetail`; in `notes.ts` replace `export` with:

```ts
  exportPdf: async (noteId: string): Promise<{ url: string; expiresAt: string }> => {
    const { data } = await api.post<{ url: string; expiresAt: string }>(`/phi/providers/me/notes/${noteId}/export`);
    return data;
  },
```

- [ ] **Step 2: i18n** — under `noteView` add:

```json
    "exportPdf": "Exportar PDF",
    "downloadPdf": "Baixar PDF",
    "exportedAt": "Exportada em {{date}}",
    "exportFailed": "Não foi possível exportar a nota."
```

- [ ] **Step 3: `SignCard.tsx`** — in the `note.signedAt` branch, below the signed text:

```tsx
        {note.exportedAt && (
          <p className="mt-1 text-body-sm text-onSurfaceVariant">
            {t("noteView.exportedAt", { date: new Date(note.exportedAt).toLocaleString("pt-BR") })}
          </p>
        )}
        <button
          type="button"
          disabled={exporting}
          onClick={() => void exportPdf()}
          className="mt-3 inline-flex w-full items-center justify-center gap-2 rounded-radii-lg border border-primary px-4 py-3 text-label-lg text-primary disabled:opacity-60"
        >
          <FileDown size={16} aria-hidden />
          {note.exportedAt ? t("noteView.downloadPdf") : t("noteView.exportPdf")}
        </button>
```

  with, at the top of the component:

```tsx
  const [exporting, setExporting] = useState(false);

  const exportPdf = async () => {
    setExporting(true);
    try {
      const { url } = await notesApi.exportPdf(note.id);
      if (!note.exportedAt) onSigned(); // refresh: the note is now Exported
      window.location.assign(url);
    } catch (e) {
      toast.error(e instanceof ApiError && (e.status === 409 || e.status === 503) ? e.message : t("noteView.exportFailed"));
    } finally {
      setExporting(false);
    }
  };
```

  Imports: `FileDown` from `lucide-react`, `ApiError` from `@anamnys/shared/api/client`, `toast` from `@anamnys/shared/ui/Toaster`. (`onSigned` is the page's refetch callback; keep its name.)

- [ ] **Step 4: Build and lint** → pass.
- [ ] **Step 5: Stop** — leave uncommitted.

---

### Task 6: Documentation and full verification

**Files:** `apps/web/src/content/site/faq.md`, `anamnys-aspire/CLAUDE.md`, `design/gaps/2026-10-03-patient-flows-gaps.md`, `design/status/2026-10-15-implementation-status.md`, spec status.

- [ ] **Step 1: FAQ** — replace the paragraph starting "**Os documentos oficiais são PDFs assinados por você**" with:

```markdown
**Os documentos oficiais são PDFs assinados eletronicamente por você no Anamnys** — com seu
nome, seu CRP, a data e a hora da assinatura. Você pode baixá-los e arquivá-los onde quiser
desde o primeiro dia.
```

- [ ] **Step 2: CLAUDE.md** — "Backend — present": add "SeaweedFS (S3 mode, Aspire-managed container) as the private object store for exported PDFs; the server uses AWSSDK.S3 and presigns one-hour GET URLs" and "QuestPDF (Community license) for note PDFs". "First-time setup": add the two `dotnet user-secrets --project $P set "Parameters:storage-access-key" "$(openssl rand -hex 16)"` / `storage-secret-key` (`rand -hex 32`) lines to the block and say the store's data volume is `anamnys-storage-data`.
- [ ] **Step 3: Gaps file** — `## Notas clínicas`: replace "PDF export (needs private object storage and 1 h presigned URLs)" in the "Still missing" list by nothing (done) and add: ICP-Brasil digital signature in the PDF (future); production storage host with TLS for presigned URLs (`Storage__PublicUrl`); backup of the SeaweedFS volume; QuestPDF Community license holds only below US$ 1M annual revenue; exported PDF not yet sealed into `DocumentHashes` (F-45; the SHA-256 is stored in `Notes.PdfSha256`); manual browser check of export/download pending. Update "Last updated".
- [ ] **Step 4: Status file** — "Exportação em PDF + S3 + URLs assinadas" → done (SeaweedFS instead of S3 cloud); F-28 note: "no export includes the transcript (PDF built only from signed sections)".
- [ ] **Step 5: Full suite** — `aspire stop`; `dotnet run --project anamnys-aspire.Tests`; record totals. `npm run build` and `npm run lint`.
- [ ] **Step 6: Spec status** → `Implemented`.
- [ ] **Step 7: Stop** — report; leave uncommitted.
