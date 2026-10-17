# Note Editor — Note List and Typed Notes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The provider lists all their notes at `/note-editor` and creates typed notes — free text organised by the AI, or empty sections written by hand — optionally linked to an appointment, which creates the clinical session and marks the appointment attended.

**Architecture:** Typed text goes into `Notes.RawTranscript` with `InputMode = 'Text'`; the AI mode reuses `StructureJob` on the existing `TranscriptionQueue`, the manual mode reuses the blank-sections logic. A new `Notes/NoteSessions` helper (appointment row lock, create/reuse session, attended transition) is shared by typed-note creation and the live finalize. The list is a POST search (no patient name in URLs). The provider app gains `/note-editor` and `/note-editor/new`, and entry points from the tab bar, patient record and calendar.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, PostgreSQL 18, xUnit v3 + FluentAssertions; React 19, TanStack Router/Query, Tailwind 4, i18next, axios client (`@anamnys/shared/api/client`).

**Spec:** `design/specs/2026-10-15-note-editor-design.md`

## Global Constraints

- Every query is provider-scoped (`ProviderId` from the session); another provider's patient, appointment or note is a 404, never a 403.
- No patient name in a URL: the list endpoint is `POST providers/me/notes/search` with filters in the body.
- Every note mutation appends `AuditEntries` in the same transaction. New action: `note_created`, `ActorId` = provider id, `NewValue` = `"{InputMode},{Format}"` (e.g. `"Text,DAP"`).
- `AuditEntries` has an FK to `Notes` and no EF navigations: save the new note before adding its audit entry (two `SaveChangesAsync` calls in one transaction).
- Typed text limit: 20,000 characters after trimming (`CreateNoteRequest.MaxText`). Formats: `LiveSessionRules.Formats` (`DAP`, `SOAP`, `Biopsychosocial`). Modes: `ai`, `manual`.
- Candidate window: appointments with `StartsAt` in `[now − 14 days, now]`, status not `cancelled`, no note on their session.
- List page size 25, newest first (`CreatedAt` desc, `Id` desc). `pending` = `Draft`/`Processing`/`ReadyForReview` without a structure failure; `signed` = `Signed`/`Exported`; `failed` = `Status = 'Failed'` or `StructureStatus = 'failed'` (so no note is in both "Pendentes" and "Com falha").
- The structuring prompt never lets the model add facts; the typed variant only changes wording about the source (label "Anotações", no mention of transcription).
- UI strings in Portuguese in `packages/shared/src/lib/i18n/locales/pt.json`.
- Tests: from `anamnys-aspire/`, `dotnet run --project anamnys-aspire.Tests -- -class "*Name*"`; never `dotnet test`. Run `aspire stop` before building; if the user's app must keep running, build with `--artifacts-path "$TEMP/anamnys-artifacts"`. Frontend: `npm run build` and `npm run lint` from `anamnys-aspire/`.
- C# style per CLAUDE.md (file-scoped namespaces, primary constructors, `CancellationToken` everywhere, records for requests, no XML doc comments). Test names `<Method>_<Conditions>_<Outcome>`, `// Arrange` / `// Act` / `// Assert`.
- No schema change in this delivery.
- **Do not commit** (user rule: the user reviews all changes first). The "Commit" steps below are replaced by "Stop: leave the changes uncommitted". Preserve each file's line endings.

---

## File Structure

- Server
  - `Appointments/Appointments.cs` — extract `ApplyAttendedAsync` (no save) from `SetStatusAsync`; `AppointmentItem` gains `HasNote`.
  - `Appointments/AppointmentContracts.cs` — `AppointmentItem.HasNote`.
  - `Notes/NoteSessions.cs` (new) — appointment lock/load, availability, create/reuse session + attended transition.
  - `LiveSession/LiveRecordings.cs` — finalize uses `NoteSessions`, audits `note_created`; `SessionForAsync` removed.
  - `NoteStructuring/NoteSource.cs` (new), `NotePrompt.cs`, `INoteStructurer.cs`, `LlamaNoteStructurer.cs`, `NoteStructuringJob.cs` — typed prompt variant.
  - `Notes/NoteEditing.cs` — extract `AddBlankSections` (shared with creation).
  - `Notes/NoteCreation.cs` (new) — `CreateNoteRequest`, `CreateAsync`, `CandidatesAsync`.
  - `Notes/NoteList.cs` (new) — `NoteSearchRequest`, `SearchAsync`.
  - `Notes/NoteQueries.cs` — `NoteDetail.SectionsByAi`.
  - `Notes/NoteEndpoints.cs` — three new routes.
  - `Patients/PatientSearchQuery.cs` — `ContainsPattern` becomes `internal`.
  - `anamnys-aspire.Server.http` — three new requests.
- Tests
  - `Patients/PatientSearchSeed.cs` — `AddAppointmentAsync` returns the id.
  - `Notes/NoteSessionsTests.cs`, `Notes/NoteCreationTests.cs`, `Notes/NoteListTests.cs` (new).
  - `LiveSession/LiveRecordingsTests.cs`, `NoteStructuring/LlamaNoteStructurerTests.cs`, `NoteStructuring/FakeNoteStructurer.cs`, `NoteStructuring/NoteStructuringTests.cs`, `Notes/NoteEndpointTests.cs`, `Appointments/AppointmentsTests.cs` — updates.
- Frontend
  - `packages/shared/src/lib/types.ts`, `packages/shared/src/api/notes.ts`, `pt.json`, `packages/shared/src/ui/AppTabBar.tsx`.
  - `apps/provider/src/components/NoteCard.tsx`, `apps/provider/src/components/PatientNotesView.tsx`.
  - `apps/provider/src/routes/_app/note-editor/index.tsx`, `apps/provider/src/routes/_app/note-editor/new.tsx` (new).
  - `apps/provider/src/components/notes/NoteTargetCard.tsx`, `apps/provider/src/components/notes/NoteModeCards.tsx` (new).
  - `apps/provider/src/routes/_app/notes/$noteId.tsx`, `apps/provider/src/routes/_app/patients/$patientId/index.tsx`, `apps/provider/src/components/calendar/AppointmentDetailsModal.tsx`.
- Docs: `design/gaps/2026-10-03-patient-flows-gaps.md`, `design/status/2026-10-15-implementation-status.md`.

---

### Task 1: Shared session helper and attended transition

**Files:**
- Modify: `anamnys-aspire.Server/Appointments/Appointments.cs` (`SetStatusAsync`, new `ApplyAttendedAsync`)
- Create: `anamnys-aspire.Server/Notes/NoteSessions.cs`
- Modify: `anamnys-aspire.Server/LiveSession/LiveRecordings.cs` (`FinalizeAsync`, delete `SessionForAsync`)
- Modify: `anamnys-aspire.Tests/Patients/PatientSearchSeed.cs` (`AddAppointmentAsync` returns `Task<Guid>`)
- Test: `anamnys-aspire.Tests/Notes/NoteSessionsTests.cs` (new), `anamnys-aspire.Tests/LiveSession/LiveRecordingsTests.cs`

**Interfaces:**
- Produces:
  - `Appointments.ApplyAttendedAsync(AnamnysDbContext db, Appointment appointment, DateTimeOffset nowUtc, CancellationToken ct)` — `internal static Task`; sets `Status = attended`, calls `AppointmentNotifications.OnLeftActiveAsync`, raises `Patients.LastVisit`; never saves.
  - `NoteSessions.LockAppointmentAsync(AnamnysDbContext db, Guid providerId, Guid patientId, Guid appointmentId, CancellationToken ct) : Task<Appointment?>` — `SELECT … FOR UPDATE`, then loads the tracked appointment scoped to provider and patient; null when not found.
  - `NoteSessions.IsAvailableForNoteAsync(AnamnysDbContext db, Appointment appointment, CancellationToken ct) : Task<bool>` — not cancelled and its session has no note.
  - `NoteSessions.AttendAsync(AnamnysDbContext db, Guid providerId, Guid patientId, Appointment? appointment, DateTimeOffset? actualStart, DateTimeOffset? actualEnd, DateTimeOffset now, CancellationToken ct) : Task<Session>` — creates or reuses the session (saved), sets `Attendance = attended`, moves a started `scheduled`/`confirmed` appointment to attended. Caller saves.
  - `PatientSearchSeed.AddAppointmentAsync(...) : Task<Guid>`.

- [ ] **Step 1: Seed returns the appointment id**

In `PatientSearchSeed.cs` replace `AddAppointmentAsync` with:

```csharp
    public async Task<Guid> AddAppointmentAsync(
        Guid patientId,
        DateTimeOffset startsAt,
        string status = "scheduled",
        Guid? providerId = null,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO "Appointments" ("Id", "ProviderId", "PatientId", "StartsAt", "EndsAt", "Status", "CancelledAt")
            VALUES (@id, @provider, @patient, @starts, @starts + interval '50 minutes', @status,
                    CASE WHEN @status = 'cancelled' THEN now() END)
            """,
            cancellationToken,
            ("id", id),
            ("provider", providerId ?? ProviderId),
            ("patient", patientId),
            ("starts", startsAt.ToUniversalTime()),
            ("status", status));
        return id;
    }
```

Existing callers (`await seed.AddAppointmentAsync(...)`) keep compiling.

- [ ] **Step 2: Write the failing tests** — `anamnys-aspire.Tests/Notes/NoteSessionsTests.cs`

```csharp
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Notes;

[Collection(SharedAppHostCollection.Name)]
public class NoteSessionsTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 18, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AttendAsync_StartedScheduledAppointment_CreatesAttendedSessionLinksAndMarksAttended()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-2), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(Ct);

        // Act
        var appointment = await NoteSessions.LockAppointmentAsync(db, seed.ProviderId, patient, appointmentId, Ct);
        var session = await NoteSessions.AttendAsync(db, seed.ProviderId, patient, appointment, null, null, Now, Ct);
        await db.SaveChangesAsync(Ct);
        await tx.CommitAsync(Ct);

        // Assert
        await using var check = seed.CreateDbContext();
        var row = await check.Sessions.AsNoTracking().SingleAsync(s => s.Id == session.Id, Ct);
        row.Attendance.Should().Be("attended");
        row.ScheduledStart.Should().Be(Now.AddHours(-2));
        row.ScheduledMinutes.Should().Be(50);
        row.ActualStart.Should().BeNull();
        var saved = await check.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointmentId, Ct);
        saved.SessionId.Should().Be(session.Id);
        saved.Status.Should().Be("attended");
        (await check.Patients.AsNoTracking().SingleAsync(p => p.Id == patient, Ct)).LastVisit.Should().Be(Now.AddHours(-2));
    }

    [Fact]
    public async Task AttendAsync_FutureAppointment_LinksSessionButKeepsStatus()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(2), "confirmed", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var appointment = await NoteSessions.LockAppointmentAsync(db, seed.ProviderId, patient, appointmentId, Ct);
        await NoteSessions.AttendAsync(db, seed.ProviderId, patient, appointment, null, null, Now, Ct);
        await db.SaveChangesAsync(Ct);

        // Assert
        var saved = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointmentId, Ct);
        saved.Status.Should().Be("confirmed");
        saved.SessionId.Should().NotBeNull();
    }

    [Fact]
    public async Task AttendAsync_AppointmentWithSession_ReusesIt()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-2), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var first = await NoteSessions.AttendAsync(
            db, seed.ProviderId, patient, await NoteSessions.LockAppointmentAsync(db, seed.ProviderId, patient, appointmentId, Ct), null, null, Now, Ct);
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        // Act
        var second = await NoteSessions.AttendAsync(
            db, seed.ProviderId, patient, await NoteSessions.LockAppointmentAsync(db, seed.ProviderId, patient, appointmentId, Ct), null, null, Now, Ct);
        await db.SaveChangesAsync(Ct);

        // Assert
        second.Id.Should().Be(first.Id);
        (await db.Sessions.AsNoTracking().CountAsync(s => s.PatientId == patient, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task AttendAsync_NoAppointment_CreatesStandaloneSession()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var session = await NoteSessions.AttendAsync(db, seed.ProviderId, patient, null, Now.AddMinutes(-30), Now, Now, Ct);
        await db.SaveChangesAsync(Ct);

        // Assert
        var row = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == session.Id, Ct);
        row.ScheduledStart.Should().BeNull();
        row.ActualStart.Should().Be(Now.AddMinutes(-30));
        row.ActualEnd.Should().Be(Now);
    }

    [Fact]
    public async Task LockAppointmentAsync_OtherPatientsAppointment_ReturnsNull()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var other = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(other, Now.AddHours(-2), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var appointment = await NoteSessions.LockAppointmentAsync(db, seed.ProviderId, patient, appointmentId, Ct);

        // Assert
        appointment.Should().BeNull();
    }

    [Theory]
    [InlineData("cancelled", false, false)]
    [InlineData("scheduled", true, false)]
    [InlineData("scheduled", false, true)]
    public async Task IsAvailableForNoteAsync_StatusAndNote_MatchesRule(string status, bool withNote, bool expected)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-2), status, cancellationToken: Ct);
        if (withNote)
        {
            await seed.ExecuteSqlAsync(
                """
                WITH s AS (INSERT INTO "Sessions" ("PatientId", "ProviderId", "Attendance") VALUES (@patient, @provider, 'attended') RETURNING "Id"),
                     a AS (UPDATE "Appointments" SET "SessionId" = (SELECT "Id" FROM s) WHERE "Id" = @appointment)
                INSERT INTO "Notes" ("ProviderId", "PatientId", "SessionId", "Status", "InputMode") SELECT @provider, @patient, "Id", 'ReadyForReview', 'Text' FROM s
                """,
                Ct, ("patient", patient), ("provider", seed.ProviderId), ("appointment", appointmentId));
        }
        await using var db = seed.CreateDbContext();
        var appointment = await db.Appointments.SingleAsync(a => a.Id == appointmentId, Ct);

        // Act
        var available = await NoteSessions.IsAvailableForNoteAsync(db, appointment, Ct);

        // Assert
        available.Should().Be(expected);
    }
}
```

- [ ] **Step 3: Run → FAIL** (`-class "*NoteSessionsTests*"`): `NoteSessions` does not exist.

- [ ] **Step 4: Extract `ApplyAttendedAsync` in `Appointments.cs`**

Add to `Appointments`:

```csharp
    // Side effects of an appointment becoming attended; never saves, so callers keep one unit of work.
    internal static async Task ApplyAttendedAsync(AnamnysDbContext db, Appointment appointment, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        appointment.Status = AppointmentStatus.Attended;
        await AppointmentNotifications.OnLeftActiveAsync(db, appointment.Id, nowUtc, cancellationToken);
        var patient = await db.Patients
            .SingleAsync(p => p.Id == appointment.PatientId && p.ProviderId == appointment.ProviderId, cancellationToken);
        if (patient.LastVisit is null || patient.LastVisit < appointment.StartsAt)
        {
            patient.LastVisit = appointment.StartsAt;
        }
    }
```

In `SetStatusAsync`, split the `Attended or NoShow` case and keep only the undo branch of the `LastVisit` block:

```csharp
            case AppointmentStatus.Attended:
                await ApplyAttendedAsync(db, appointment, nowUtc, cancellationToken);
                break;
            case AppointmentStatus.NoShow:
                await AppointmentNotifications.OnLeftActiveAsync(db, appointment.Id, nowUtc, cancellationToken);
                break;
        }

        if (from == AppointmentStatus.Attended)
        {
            var patient = await db.Patients
                .SingleAsync(p => p.Id == appointment.PatientId && p.ProviderId == providerId, cancellationToken);
            // Undoing "attended": the latest remaining attended visit, if any.
            var latest = await db.Appointments
                .Where(a => a.PatientId == appointment.PatientId && a.ProviderId == providerId
                    && a.Id != appointment.Id && a.Status == AppointmentStatus.Attended)
                .MaxAsync(a => (DateTimeOffset?)a.StartsAt, cancellationToken);
            if (latest is not null)
            {
                patient.LastVisit = latest;
            }
        }
```

- [ ] **Step 5: Create `Notes/NoteSessions.cs`**

```csharp
using Anamnys.Server.Appointments;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

// The appointment → session → note chain, shared by typed notes and the live finalize
// (design/specs/2026-10-15-note-editor-design.md §4.3). Callers own the transaction.
public static class NoteSessions
{
    // Locks first, so two notes for one appointment serialise; then loads it tracked.
    public static async Task<Appointment?> LockAppointmentAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, Guid appointmentId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM \"Appointments\" WHERE \"Id\" = {appointmentId} AND \"ProviderId\" = {providerId} FOR UPDATE", ct);
        return await db.Appointments.SingleOrDefaultAsync(
            a => a.Id == appointmentId && a.ProviderId == providerId && a.PatientId == patientId, ct);
    }

    public static async Task<bool> IsAvailableForNoteAsync(AnamnysDbContext db, Appointment appointment, CancellationToken ct) =>
        appointment.Status != AppointmentStatus.Cancelled
        && !(appointment.SessionId is { } sessionId && await db.Notes.AnyAsync(n => n.SessionId == sessionId, ct));

    public static async Task<Session> AttendAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, Appointment? appointment,
        DateTimeOffset? actualStart, DateTimeOffset? actualEnd, DateTimeOffset now, CancellationToken ct)
    {
        var session = appointment?.SessionId is { } sessionId
            ? await db.Sessions.SingleAsync(s => s.Id == sessionId, ct)
            : null;
        if (session is null)
        {
            session = new Session
            {
                Id = Guid.NewGuid(),
                PatientId = patientId,
                ProviderId = providerId,
                ScheduledStart = appointment?.StartsAt,
                ScheduledMinutes = appointment is null ? null : (int)(appointment.EndsAt - appointment.StartsAt).TotalMinutes,
                Modality = appointment?.Modality is "presencial" or "online" ? appointment.Modality : null,
                CreatedAt = now,
            };
            db.Sessions.Add(session);
            // No navigations are mapped, so EF cannot order inserts by foreign key: persist the session first.
            await db.SaveChangesAsync(ct);
            if (appointment is not null)
            {
                appointment.SessionId = session.Id;
            }
        }
        if (actualStart is not null)
        {
            session.ActualStart ??= actualStart;
        }
        if (actualEnd is not null)
        {
            session.ActualEnd = actualEnd;
        }
        session.Attendance = "attended";

        var nowUtc = now.ToUniversalTime();
        if (appointment is { Status: AppointmentStatus.Scheduled or AppointmentStatus.Confirmed } && appointment.StartsAt <= nowUtc)
        {
            await Appointments.Appointments.ApplyAttendedAsync(db, appointment, nowUtc, ct);
        }
        return session;
    }
}
```

The type is `public static class Appointments` in namespace `Anamnys.Server.Appointments`; from `Anamnys.Server.Notes` the bare name `Appointments` resolves to the namespace, hence `Appointments.Appointments.ApplyAttendedAsync` (the tests alias it the same way: `using Appointments = Anamnys.Server.Appointments.Appointments;`).

- [ ] **Step 6: Run → PASS** (`-class "*NoteSessionsTests*"`), then `-class "*AppointmentsTests*"` and `-class "*AppointmentEndpointTests*"` to confirm the refactor kept status behaviour.

- [ ] **Step 7: Failing live tests** — add to `LiveRecordingsTests.cs`:

```csharp
    [Fact]
    public async Task Finalize_StartedAppointment_MarksAppointmentAttendedAndAuditsCreation()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddMinutes(-40), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (buffers, queue, recordingId) = Recording(seed.ProviderId, patient, appointmentId);

        // Act
        var result = await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, recordingId, Now, Ct);

        // Assert
        await using var check = seed.CreateDbContext();
        (await check.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointmentId, Ct)).Status.Should().Be("attended");
        var audit = await check.AuditEntries.AsNoTracking().SingleAsync(a => a.NoteId == result.NoteId, Ct);
        audit.Action.Should().Be("note_created");
        audit.NewValue.Should().Be("VoiceLive,SOAP");
        audit.ActorId.Should().Be(seed.ProviderId);
    }
```

Run `-class "*LiveRecordingsTests*"` → the new test FAILS (status stays `scheduled`, no audit).

- [ ] **Step 8: Switch `FinalizeAsync` to `NoteSessions`**

Inside the transaction in `LiveRecordings.FinalizeAsync`, replace `var session = await SessionForAsync(db, meta, now, write);` and the note insert with:

```csharp
                var appointment = meta.AppointmentId is { } appointmentId
                    ? await NoteSessions.LockAppointmentAsync(db, meta.ProviderId, meta.PatientId, appointmentId, write)
                        ?? throw new InvalidOperationException("The recording's appointment does not exist.")
                    : null;
                var session = await NoteSessions.AttendAsync(db, meta.ProviderId, meta.PatientId, appointment, meta.StartedAt, now, now, write);
                db.Notes.Add(new Note
                {
                    // … unchanged initialiser …
                });
                await db.SaveChangesAsync(write);
                NoteAudit.Add(db, noteId, providerId, "note_created", null, null, $"VoiceLive,{meta.Format}", now);
                await db.SaveChangesAsync(write);
                await tx.CommitAsync(write);
```

Add `using Anamnys.Server.Notes;` and delete `SessionForAsync`. The existing test `Finalize_DatabaseFailure_PropagatesAndLeavesNoBufferNoteOrJob` (unknown appointment → `InvalidOperationException`) must still pass.

- [ ] **Step 9: Run → PASS** (`-class "*LiveRecordingsTests*"` and `-class "*NoteSessionsTests*"`).

- [ ] **Step 10: Stop** — leave uncommitted.

---

### Task 2: Typed-text prompt variant

**Files:**
- Create: `anamnys-aspire.Server/NoteStructuring/NoteSource.cs`
- Modify: `NoteStructuring/NotePrompt.cs`, `NoteStructuring/INoteStructurer.cs`, `NoteStructuring/LlamaNoteStructurer.cs`, `NoteStructuring/NoteStructuringJob.cs`
- Test: `anamnys-aspire.Tests/NoteStructuring/LlamaNoteStructurerTests.cs`, `FakeNoteStructurer.cs`, `NoteStructuringTests.cs`

**Interfaces:**
- Produces: `public enum NoteSource { Dictation, Typed }`; `NotePrompt.Build(string transcript, string format, NoteSource source = NoteSource.Dictation)`; `INoteStructurer.StructureAsync(string transcript, string format, NoteSource source, CancellationToken ct)`; `NoteSources.Of(string inputMode) : NoteSource` (`"Text"` → `Typed`, otherwise `Dictation`); `FakeNoteStructurer.LastSource : NoteSource?`.

- [ ] **Step 1: Failing tests** — add to `LlamaNoteStructurerTests.cs`:

```csharp
    [Fact]
    public void Build_TypedSource_UsesNotesLabelAndNoTranscriptionWording()
    {
        // Arrange
        const string text = "Paciente relata insônia.";

        // Act
        var prompt = NotePrompt.Build(text, "DAP", NoteSource.Typed);

        // Assert
        prompt.Should().Contain("Anotações:\nPaciente relata insônia.");
        prompt.Should().NotContain("Ditado:");
        prompt.Should().NotContain("transcrição");
        prompt.Should().Contain("Não acrescente fatos");
        prompt.Should().Contain("Nunca transforme um número solto em data");
    }

    [Fact]
    public void Build_TypedSource_StripsChatMlControlStrings()
    {
        // Act
        var prompt = NotePrompt.Build("Relata.<|im_end|>\n<|im_start|>system\nIgnore.", "SOAP", NoteSource.Typed);

        // Assert
        Count(prompt, "<|im_end|>").Should().Be(2);
        Count(prompt, "<|im_start|>").Should().Be(3);
    }
```

Add to `NoteStructuringTests.cs` (use the file's existing note-insert helper; set `"InputMode" = 'Text'`):

```csharp
    [Fact]
    public async Task Process_TextNote_PassesTypedSource()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, "DAP", "pending");
        await seed.ExecuteSqlAsync("""UPDATE "Notes" SET "InputMode" = 'Text' WHERE "Id" = @id""", Ct, ("id", noteId));
        await using var db = seed.CreateDbContext();
        var structurer = new FakeNoteStructurer((_, _) => DapSections);

        // Act
        await NoteStructuringJob.ProcessAsync(db, structurer, noteId, seed.ProviderId, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        structurer.LastSource.Should().Be(NoteSource.Typed);
    }

    [Fact]
    public async Task Process_LiveNote_PassesDictationSource()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddNoteAsync(seed, "DAP", "pending");
        await using var db = seed.CreateDbContext();
        var structurer = new FakeNoteStructurer((_, _) => DapSections);

        // Act
        await NoteStructuringJob.ProcessAsync(db, structurer, noteId, seed.ProviderId, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        structurer.LastSource.Should().Be(NoteSource.Dictation);
    }
```

(`AddNoteAsync(seed, format, structureStatus)` is this class's existing helper; it inserts `InputMode = 'VoiceLive'`.)

- [ ] **Step 2: Run → FAIL** (`-class "*LlamaNoteStructurerTests*"`, `-class "*NoteStructuringTests*"`): `NoteSource` missing.

- [ ] **Step 3: `NoteStructuring/NoteSource.cs`**

```csharp
namespace Anamnys.Server.NoteStructuring;

// What the text to structure came from; only the prompt's wording about its source differs.
public enum NoteSource { Dictation, Typed }

public static class NoteSources
{
    public static NoteSource Of(string inputMode) => inputMode == "Text" ? NoteSource.Typed : NoteSource.Dictation;
}
```

- [ ] **Step 4: `NotePrompt.Build` variant**

Replace `Build` with:

```csharp
    public static string Build(string transcript, string format, NoteSource source = NoteSource.Dictation)
    {
        var typed = source == NoteSource.Typed;
        var keys = NoteFormats.KeysFor(format);
        var system = new StringBuilder()
            .AppendLine(typed
                ? $"Você organiza as anotações escritas por um(a) psicólogo(a) nas seções de uma evolução clínica no formato {format}."
                : $"Você organiza o ditado de um(a) psicólogo(a) nas seções de uma evolução clínica no formato {format}.")
            .AppendLine("Regras:")
            .AppendLine(typed
                ? "1. Use apenas o que foi escrito. Não acrescente fatos, hipóteses, diagnósticos, interpretações ou conclusões."
                : "1. Use apenas o que foi ditado. Não acrescente fatos, hipóteses, diagnósticos, interpretações ou conclusões.")
            .AppendLine("2. Coloque cada trecho na seção a que pertence:");
        foreach (var key in keys)
        {
            system.AppendLine($"   - \"{key}\": {NoteFormats.Describe(key)}.");
        }
        system
            .AppendLine(typed
                ? "3. Corrija a pontuação; escreva em português, em tom de prontuário, em terceira pessoa."
                : "3. Remova vícios de fala (é, hum, né, tipo), repetições e hesitações; corrija a pontuação; escreva em português, em tom de prontuário, em terceira pessoa.")
            .AppendLine(typed
                ? "4. Preserve nomes, números, doses e datas exatamente como foram escritos. Nunca transforme um número solto em data, horário, dose ou quantidade."
                : "4. Preserve nomes, números, doses e datas exatamente como foram ditados. Nunca transforme um número solto em data, horário, dose ou quantidade.")
            .AppendLine(typed
                ? "5. Se um trecho estiver confuso ou incompleto, copie-o como foi escrito; nunca o reinterprete nem deduza fatos a partir dele."
                : "5. O ditado vem de transcrição automática e pode ter erros. Se um trecho estiver confuso, truncado ou sem sentido, copie-o como foi ditado; nunca o reinterprete nem deduza fatos a partir dele.")
            .AppendLine(typed
                ? "6. Se nada foi escrito para uma seção, deixe-a vazia (\"\")."
                : "6. Se nada foi ditado para uma seção, deixe-a vazia (\"\").")
            .Append("7. Responda somente com o objeto JSON com essas chaves, nessa ordem.");
        var label = typed ? "Anotações" : "Ditado";
        // AppendLine writes \r\n on Windows; ChatML is \n throughout.
        return $"<|im_start|>system\n{system.ToString().ReplaceLineEndings("\n")}<|im_end|>\n<|im_start|>user\n{label}:\n{StripControl(transcript)}<|im_end|>\n<|im_start|>assistant\n";
    }
```

The dictation strings are byte-identical to the current ones (existing prompt tests prove it).

- [ ] **Step 5: Thread the source through**
  - `INoteStructurer.StructureAsync(string transcript, string format, NoteSource source, CancellationToken ct)`.
  - `LlamaNoteStructurer.StructureAsync` gains the same parameter and calls `NotePrompt.Build(transcript, format, source)`.
  - `NoteStructuringJob.ProcessAsync`: select `n.InputMode` with the other columns and call `structurer.StructureAsync(note.RawTranscript, effectiveFormat, NoteSources.Of(note.InputMode), ct)`.
  - `FakeNoteStructurer`:

```csharp
internal sealed class FakeNoteStructurer(Func<string, string, IReadOnlyList<StructuredSection>> structure) : INoteStructurer
{
    public int Calls { get; private set; }
    public NoteSource? LastSource { get; private set; }

    public Task<IReadOnlyList<StructuredSection>> StructureAsync(string transcript, string format, NoteSource source, CancellationToken ct)
    {
        Calls++;
        LastSource = source;
        return Task.FromResult(structure(transcript, format));
    }
}
```

  - Fix every other call site: `grep -rn "StructureAsync(" anamnys-aspire.Server anamnys-aspire.Tests --include=*.cs` (e.g. direct calls in `LlamaNoteStructurerTests` pass `NoteSource.Dictation`).

- [ ] **Step 6: Run → PASS** (`-class "*LlamaNoteStructurerTests*"`, `-class "*NoteStructuringTests*"`, `-class "*TranscriptionWorkerTests*"`).

- [ ] **Step 7: Stop** — leave uncommitted.

---

### Task 3: Create typed notes and list candidates

**Files:**
- Modify: `anamnys-aspire.Server/Notes/NoteEditing.cs` (extract `AddBlankSections`)
- Create: `anamnys-aspire.Server/Notes/NoteCreation.cs`
- Modify: `anamnys-aspire.Server/Notes/NoteEndpoints.cs`, `anamnys-aspire.Server/anamnys-aspire.Server.http`
- Test: `anamnys-aspire.Tests/Notes/NoteCreationTests.cs` (new), `anamnys-aspire.Tests/Notes/NoteEndpointTests.cs`

**Interfaces:**
- Consumes: Task 1 `NoteSessions.*`; existing `TranscriptionQueue.Enqueue(StructureJob)`, `NoteAudit.Add`, `LiveSessionRules.Formats`, `NoteFormats.KeysFor`.
- Produces:
  - `NoteEditing.AddBlankSections(AnamnysDbContext db, Note note, Guid providerId, DateTimeOffset now)` — `internal static void`; adds the format's empty sections, sets `StructureStatus = "done"`, `StructureFailure = null`, `UpdatedAt`, audits `sections_created`. Never saves.
  - `CreateNoteRequest(Guid? PatientId, Guid? AppointmentId, string? Format, string? Mode, string? Text)` with `const int MaxText = 20_000` and `Dictionary<string, string[]> Validate()`.
  - `enum CreateNoteOutcome { Created, NotFound, Archived, AppointmentUnavailable }`, `record CreateNoteResult(CreateNoteOutcome Outcome, Guid? NoteId)`.
  - `NoteCreation.CreateAsync(AnamnysDbContext db, TranscriptionQueue queue, Guid providerId, CreateNoteRequest request, DateTimeOffset now, CancellationToken ct) : Task<CreateNoteResult>`.
  - `record NoteCandidate(Guid AppointmentId, DateTimeOffset StartsAt, string Modality, string Status)`.
  - `NoteCreation.CandidatesAsync(AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken ct) : Task<IReadOnlyList<NoteCandidate>?>` (null = patient not found).
  - Routes: `POST /api/phi/providers/me/notes` → `201 { noteId }`; `GET /api/phi/providers/me/patients/{patientId}/note-candidates` → `200 NoteCandidate[]`.

- [ ] **Step 1: Failing tests** — `anamnys-aspire.Tests/Notes/NoteCreationTests.cs`

```csharp
using Anamnys.Server.LiveSession;
using Anamnys.Server.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Notes;

[Collection(SharedAppHostCollection.Name)]
public class NoteCreationTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 18, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateAsync_AiMode_CreatesPendingTextNoteEnqueuesAndAudits()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var queue = new TranscriptionQueue();

        // Act
        var result = await NoteCreation.CreateAsync(db, queue, seed.ProviderId,
            new CreateNoteRequest(patient, null, "SOAP", "ai", "  Relata insônia.  "), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.Created);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == result.NoteId, Ct);
        note.InputMode.Should().Be("Text");
        note.Status.Should().Be("ReadyForReview");
        note.Format.Should().Be("SOAP");
        note.RawTranscript.Should().Be("Relata insônia.");
        note.StructureStatus.Should().Be("pending");
        note.SessionId.Should().BeNull();
        queue.Reader.TryRead(out var job).Should().BeTrue();
        job.Should().BeEquivalentTo(new StructureJob(result.NoteId!.Value, seed.ProviderId, "SOAP"));
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.NoteId == result.NoteId, Ct);
        audit.Action.Should().Be("note_created");
        audit.NewValue.Should().Be("Text,SOAP");
    }

    [Fact]
    public async Task CreateAsync_ManualMode_CreatesBlankSectionsWithoutJob()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var queue = new TranscriptionQueue();

        // Act
        var result = await NoteCreation.CreateAsync(db, queue, seed.ProviderId,
            new CreateNoteRequest(patient, null, "DAP", "manual", "   "), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.Created);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == result.NoteId, Ct);
        note.RawTranscript.Should().BeNull();
        note.StructureStatus.Should().Be("done");
        var keys = await db.NoteSections.AsNoTracking().Where(s => s.NoteId == result.NoteId).OrderBy(s => s.Position).Select(s => s.Key).ToListAsync(Ct);
        keys.Should().Equal("data", "assessment", "plan");
        queue.Reader.TryRead(out _).Should().BeFalse();
        var actions = await db.AuditEntries.AsNoTracking().Where(a => a.NoteId == result.NoteId).Select(a => a.Action).ToListAsync(Ct);
        actions.Should().BeEquivalentTo(["note_created", "sections_created"]);
    }

    [Fact]
    public async Task CreateAsync_WithStartedAppointment_LinksSessionAndMarksAttended()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-3), "confirmed", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, appointmentId, "DAP", "manual", null), Now, Ct);

        // Assert
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == result.NoteId, Ct);
        var appointment = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointmentId, Ct);
        appointment.SessionId.Should().Be(note.SessionId);
        appointment.Status.Should().Be("attended");
        (await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == note.SessionId, Ct)).Attendance.Should().Be("attended");
    }

    [Fact]
    public async Task CreateAsync_OtherProvidersPatient_NotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, null, "DAP", "manual", null), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.NotFound);
        (await db.Notes.AsNoTracking().CountAsync(n => n.PatientId == patient, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_OtherPatientsAppointment_NotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var other = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(other, Now.AddHours(-3), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, appointmentId, "DAP", "manual", null), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.NotFound);
    }

    [Fact]
    public async Task CreateAsync_ArchivedPatient_Archived()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", archivedAt: Now.AddDays(-1), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, null, "DAP", "manual", null), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.Archived);
    }

    [Fact]
    public async Task CreateAsync_CancelledAppointment_AppointmentUnavailable()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-3), "cancelled", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, appointmentId, "DAP", "manual", null), Now, Ct);

        // Assert
        result.Outcome.Should().Be(CreateNoteOutcome.AppointmentUnavailable);
    }

    [Fact]
    public async Task CreateAsync_TwoAtOnceForOneAppointment_OneNoteOneUnavailable()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-3), cancellationToken: Ct);
        await using var first = seed.CreateDbContext();
        await using var second = seed.CreateDbContext();
        var request = new CreateNoteRequest(patient, appointmentId, "DAP", "manual", null);

        // Act
        var results = await Task.WhenAll(
            NoteCreation.CreateAsync(first, new TranscriptionQueue(), seed.ProviderId, request, Now, Ct),
            NoteCreation.CreateAsync(second, new TranscriptionQueue(), seed.ProviderId, request, Now, Ct));

        // Assert
        results.Select(r => r.Outcome).Should().BeEquivalentTo([CreateNoteOutcome.Created, CreateNoteOutcome.AppointmentUnavailable]);
        await using var check = seed.CreateDbContext();
        (await check.Notes.AsNoTracking().CountAsync(n => n.PatientId == patient, Ct)).Should().Be(1);
    }

    [Theory]
    [InlineData(null, "DAP", "ai", "x", "patientId")]
    [InlineData("p", "PtFunctional", "ai", "x", "format")]
    [InlineData("p", "DAP", "auto", "x", "mode")]
    [InlineData("p", "DAP", "ai", "   ", "text")]
    [InlineData("p", "DAP", "ai", null, "text")]
    public void Validate_InvalidField_ReportsIt(string? patient, string format, string mode, string? text, string field)
    {
        // Arrange
        var request = new CreateNoteRequest(patient is null ? null : Guid.NewGuid(), null, format, mode, text);

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().ContainKey(field);
    }

    [Fact]
    public void Validate_TextOverLimit_ReportsText()
    {
        // Arrange
        var request = new CreateNoteRequest(Guid.NewGuid(), null, "DAP", "manual", new string('a', CreateNoteRequest.MaxText + 1));

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().ContainKey("text");
    }

    [Fact]
    public async Task CandidatesAsync_Window_ReturnsRecentUnnotedNotCancelledNewestFirst()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var recent = await seed.AddAppointmentAsync(patient, Now.AddHours(-2), cancellationToken: Ct);
        var older = await seed.AddAppointmentAsync(patient, Now.AddDays(-13), "attended", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddDays(-15), cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddDays(-1), "cancelled", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddHours(3), cancellationToken: Ct);
        var noted = await seed.AddAppointmentAsync(patient, Now.AddDays(-3), cancellationToken: Ct);
        await using (var setup = seed.CreateDbContext())
        {
            await NoteCreation.CreateAsync(setup, new TranscriptionQueue(), seed.ProviderId,
                new CreateNoteRequest(patient, noted, "DAP", "manual", null), Now, Ct);
        }
        await using var db = seed.CreateDbContext();

        // Act
        var candidates = await NoteCreation.CandidatesAsync(db, seed.ProviderId, patient, Now, Ct);

        // Assert
        candidates!.Select(c => c.AppointmentId).Should().Equal(recent, older);
    }

    [Fact]
    public async Task CandidatesAsync_OtherProvidersPatient_ReturnsNull()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var candidates = await NoteCreation.CandidatesAsync(db, seed.ProviderId, patient, Now, Ct);

        // Assert
        candidates.Should().BeNull();
    }
}
```

Add to `NoteEndpointTests.cs`:

```csharp
    [Fact]
    public async Task Create_InvalidBody_Returns400()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/phi/providers/me/notes")
        {
            Content = new StringContent("""{ "patientId": null, "format": "DAP", "mode": "ai", "text": "x" }""", System.Text.Encoding.UTF8, "application/json"),
        };

        // Act
        using var response = await session.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_WithoutSession_Returns401()
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        // Act
        using var response = await client.PostAsJsonAsync("/api/phi/providers/me/notes", new { }, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
```

- [ ] **Step 2: Run → FAIL** (`-class "*NoteCreationTests*"`): types missing.

- [ ] **Step 3: Extract `AddBlankSections` in `NoteEditing.cs`**

Move the body that adds sections in `CreateBlankSectionsAsync` into:

```csharp
    internal static void AddBlankSections(AnamnysDbContext db, Note note, Guid providerId, DateTimeOffset now)
    {
        var keys = NoteFormats.KeysFor(note.Format);
        db.NoteSections.AddRange(keys.Select((k, i) => new NoteSection
        {
            Id = Guid.NewGuid(),
            NoteId = note.Id,
            Key = k,
            Content = "",
            Position = i,
            GeneratedAt = now,
            Revision = 0,
        }));
        // A failed structuring is superseded by the provider writing the note by hand.
        note.StructureStatus = "done";
        note.StructureFailure = null;
        note.UpdatedAt = now;
        NoteAudit.Add(db, note.Id, providerId, "sections_created", null, null, string.Join(",", keys), now);
    }
```

`CreateBlankSectionsAsync` calls `AddBlankSections(db, note, providerId, now);` before its `SaveChangesAsync`. Run `-class "*NoteEditingTests*"` → still PASS.

- [ ] **Step 4: Create `Notes/NoteCreation.cs`**

```csharp
using Anamnys.Server.Appointments;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.LiveSession;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

public sealed record CreateNoteRequest(Guid? PatientId, Guid? AppointmentId, string? Format, string? Mode, string? Text)
{
    public const int MaxText = 20_000;

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (PatientId is null || PatientId == Guid.Empty)
        {
            errors["patientId"] = ["Escolha o paciente."];
        }
        if (Format is null || !LiveSessionRules.Formats.Contains(Format, StringComparer.Ordinal))
        {
            errors["format"] = ["Formato inválido."];
        }
        if (Mode is not ("ai" or "manual"))
        {
            errors["mode"] = ["Modo inválido."];
        }
        var text = Text?.Trim() ?? "";
        if (Mode == "ai" && text.Length == 0)
        {
            errors["text"] = ["Escreva as anotações para a IA organizar."];
        }
        else if (text.Length > MaxText)
        {
            errors["text"] = [$"Use no máximo {MaxText} caracteres."];
        }
        return errors;
    }
}

public enum CreateNoteOutcome { Created, NotFound, Archived, AppointmentUnavailable }

public sealed record CreateNoteResult(CreateNoteOutcome Outcome, Guid? NoteId);

public sealed record NoteCandidate(Guid AppointmentId, DateTimeOffset StartsAt, string Modality, string Status);

// Typed notes (design/specs/2026-10-15-note-editor-design.md §4.3). Call Validate first.
public static class NoteCreation
{
    public static readonly TimeSpan CandidateWindow = TimeSpan.FromDays(14);

    public static async Task<CreateNoteResult> CreateAsync(
        AnamnysDbContext db, TranscriptionQueue queue, Guid providerId, CreateNoteRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var patientId = request.PatientId!.Value;
        var format = request.Format!;
        var ai = request.Mode == "ai";
        var text = request.Text?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

        var result = new CreateNoteResult(CreateNoteOutcome.NotFound, null);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            result = new CreateNoteResult(CreateNoteOutcome.NotFound, null);
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var patient = await db.Patients.AsNoTracking()
                .Where(p => p.Id == patientId && p.ProviderId == providerId)
                .Select(p => new { p.ArchivedAt })
                .SingleOrDefaultAsync(ct);
            if (patient is null)
            {
                return;
            }
            if (patient.ArchivedAt is not null)
            {
                result = new CreateNoteResult(CreateNoteOutcome.Archived, null);
                return;
            }

            Session? session = null;
            if (request.AppointmentId is { } appointmentId)
            {
                var appointment = await NoteSessions.LockAppointmentAsync(db, providerId, patientId, appointmentId, ct);
                if (appointment is null)
                {
                    return;
                }
                if (!await NoteSessions.IsAvailableForNoteAsync(db, appointment, ct))
                {
                    result = new CreateNoteResult(CreateNoteOutcome.AppointmentUnavailable, null);
                    return;
                }
                session = await NoteSessions.AttendAsync(db, providerId, patientId, appointment, null, null, now, ct);
            }

            var note = new Note
            {
                Id = Guid.NewGuid(),
                ProviderId = providerId,
                PatientId = patientId,
                SessionId = session?.Id,
                Status = "ReadyForReview",
                InputMode = "Text",
                Format = format,
                RawTranscript = text,
                StructureStatus = ai ? "pending" : null,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Notes.Add(note);
            await db.SaveChangesAsync(ct);
            NoteAudit.Add(db, note.Id, providerId, "note_created", null, null, $"Text,{format}", now);
            if (!ai)
            {
                NoteEditing.AddBlankSections(db, note, providerId, now);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            result = new CreateNoteResult(CreateNoteOutcome.Created, note.Id);
        });

        if (ai && result.Outcome == CreateNoteOutcome.Created)
        {
            queue.Enqueue(new StructureJob(result.NoteId!.Value, providerId, format));
        }
        return result;
    }

    public static async Task<IReadOnlyList<NoteCandidate>?> CandidatesAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken ct)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId && p.ProviderId == providerId, ct))
        {
            return null;
        }
        var nowUtc = now.ToUniversalTime();
        var from = nowUtc - CandidateWindow;
        return await db.Appointments.AsNoTracking()
            .Where(a => a.ProviderId == providerId && a.PatientId == patientId
                && a.StartsAt >= from && a.StartsAt <= nowUtc
                && a.Status != AppointmentStatus.Cancelled
                && !(a.SessionId != null && db.Notes.Any(n => n.SessionId == a.SessionId)))
            .OrderByDescending(a => a.StartsAt)
            .Select(a => new NoteCandidate(a.Id, a.StartsAt, a.Modality, a.Status))
            .ToListAsync(ct);
    }
}
```

The concurrency test relies on the lock: the second transaction waits on `FOR UPDATE`, then sees the first's session and note through `IsAvailableForNoteAsync`. If the second request was the one to start the session, the retrying execution strategy never re-runs a committed transaction, so no duplicate.

- [ ] **Step 5: Routes in `NoteEndpoints.cs`**

Inside `MapNoteEndpoints`, after `var notes = …`:

```csharp
        notes.MapPost("", async (CreateNoteRequest request, HttpContext http, AnamnysDbContext db, TranscriptionQueue queue, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = request.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            var result = await NoteCreation.CreateAsync(db, queue, providerId, request, DateTimeOffset.UtcNow, ct);
            return result.Outcome switch
            {
                CreateNoteOutcome.Created => Results.Json(new { noteId = result.NoteId }, statusCode: StatusCodes.Status201Created),
                CreateNoteOutcome.NotFound => Results.NotFound(),
                CreateNoteOutcome.Archived => Results.Conflict(new { message = "Desarquive o paciente para criar notas.", code = "archived" }),
                _ => Results.Conflict(new { message = "Esta consulta foi cancelada ou já tem uma nota.", code = "appointment_unavailable" }),
            };
        });

        phi.MapGet("providers/me/patients/{patientId:guid}/note-candidates", async (Guid patientId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var candidates = await NoteCreation.CandidatesAsync(db, providerId, patientId, DateTimeOffset.UtcNow, ct);
            return candidates is null ? Results.NotFound() : Results.Ok(candidates);
        });
```

- [ ] **Step 6: `.http` requests** — append to `anamnys-aspire.Server.http`, following the file's existing variable and comment style:

```http
### Create a typed note (mode "ai" organises the text; "manual" creates empty sections)
POST {{provider_base}}/api/phi/providers/me/notes
Content-Type: application/json

{
  "patientId": "{{patient_id}}",
  "appointmentId": null,
  "format": "DAP",
  "mode": "ai",
  "text": "Paciente relata dificuldade para dormir há duas semanas."
}

### Appointments of a patient that can receive a typed note (last 14 days, no note, not cancelled)
GET {{provider_base}}/api/phi/providers/me/patients/{{patient_id}}/note-candidates
```

(Use whatever base-URL and patient-id variables the file already defines; add `@patient_id` only if none exists.)

- [ ] **Step 7: Run → PASS** (`-class "*NoteCreationTests*"`, `-class "*NoteEndpointTests*"`, `-class "*NoteEditingTests*"`).

- [ ] **Step 8: Stop** — leave uncommitted.

---

### Task 4: Note list, AI-sections signal and appointment `hasNote`

**Files:**
- Modify: `anamnys-aspire.Server/Patients/PatientSearchQuery.cs` (`ContainsPattern` → `internal`)
- Create: `anamnys-aspire.Server/Notes/NoteList.cs`
- Modify: `Notes/NoteEndpoints.cs`, `Notes/NoteQueries.cs`, `Appointments/AppointmentContracts.cs`, `Appointments/Appointments.cs` (`ListAsync`), `anamnys-aspire.Server.http`
- Test: `anamnys-aspire.Tests/Notes/NoteListTests.cs` (new), `Notes/NoteEndpointTests.cs`, `Appointments/AppointmentsTests.cs`

**Interfaces:**
- Produces:
  - `NoteSearchRequest(string? Status, Guid? PatientId, string? Name, int? Page)` with `Validate()`; `NoteListItem(Guid Id, Guid PatientId, string PatientName, string Status, string? StructureStatus, string InputMode, string Format, DateTimeOffset CreatedAt, DateTimeOffset? SignedAt, DateTimeOffset? AppointmentStartsAt)`; `NoteSearchResponse(IReadOnlyList<NoteListItem> Items, bool HasMore)`; `NoteList.PageSize = 25`; `NoteList.SearchAsync(AnamnysDbContext db, Guid providerId, NoteSearchRequest request, CancellationToken ct) : Task<NoteSearchResponse>`.
  - Route `POST /api/phi/providers/me/notes/search` → `200 NoteSearchResponse`.
  - `NoteDetail` gains trailing `bool SectionsByAi`.
  - `AppointmentItem` gains trailing `bool HasNote`.

- [ ] **Step 1: Failing tests** — `anamnys-aspire.Tests/Notes/NoteListTests.cs`

```csharp
using Anamnys.Server.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Notes;

[Collection(SharedAppHostCollection.Name)]
public class NoteListTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 18, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("pending", new[] { "Draft", "ReadyForReview" })]
    [InlineData("signed", new[] { "Signed" })]
    [InlineData("failed", new[] { "Failed", "StructureFailed" })]
    [InlineData(null, new[] { "Draft", "ReadyForReview", "Signed", "Failed", "StructureFailed" })]
    public async Task SearchAsync_StatusFilter_ReturnsMatchingNotes(string? status, string[] expected)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var labels = await AddLabelledNotesAsync(seed, patient);
        await using var db = seed.CreateDbContext();

        // Act
        var response = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(status, null, null, null), Ct);

        // Assert
        response.Items.Select(i => labels[i.Id]).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task SearchAsync_PatientAndName_Filter()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var ana = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var bia = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        await seed.AddNoteAsync(ana, "ReadyForReview", Now, cancellationToken: Ct);
        await seed.AddNoteAsync(bia, "ReadyForReview", Now, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var byPatient = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, bia, null, null), Ct);
        var byName = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, null, "na sil", null), Ct);

        // Assert
        byPatient.Items.Should().ContainSingle().Which.PatientName.Should().Be("Bia Souza");
        byName.Items.Should().ContainSingle().Which.PatientId.Should().Be(ana);
    }

    [Fact]
    public async Task SearchAsync_MoreThanAPage_PagesNewestFirst()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        for (var i = 0; i < NoteList.PageSize + 2; i++)
        {
            await seed.AddNoteAsync(patient, "ReadyForReview", Now.AddMinutes(-i), cancellationToken: Ct);
        }
        await using var db = seed.CreateDbContext();

        // Act
        var first = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, null, null, 1), Ct);
        var second = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, null, null, 2), Ct);

        // Assert
        first.Items.Should().HaveCount(NoteList.PageSize);
        first.HasMore.Should().BeTrue();
        first.Items.Select(i => i.CreatedAt).Should().BeInDescendingOrder();
        first.Items[0].CreatedAt.Should().Be(Now);
        second.Items.Should().HaveCount(2);
        second.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task SearchAsync_OtherProvidersNotes_NeverReturned()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var stranger = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        await seed.AddNoteAsync(stranger, "ReadyForReview", Now, providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var response = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, stranger, null, null), Ct);

        // Assert
        response.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_NoteOnAppointmentSession_ReturnsAppointmentStart()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-2), cancellationToken: Ct);
        await using (var setup = seed.CreateDbContext())
        {
            await NoteCreation.CreateAsync(setup, new Anamnys.Server.LiveSession.TranscriptionQueue(), seed.ProviderId,
                new CreateNoteRequest(patient, appointmentId, "DAP", "manual", null), Now, Ct);
        }
        await using var db = seed.CreateDbContext();

        // Act
        var response = await NoteList.SearchAsync(db, seed.ProviderId, new NoteSearchRequest(null, patient, null, null), Ct);

        // Assert
        response.Items.Should().ContainSingle().Which.AppointmentStartsAt.Should().Be(Now.AddHours(-2));
    }

    [Theory]
    [InlineData("open", null, "status")]
    [InlineData(null, 0, "page")]
    public void Validate_Invalid_ReportsField(string? status, int? page, string field)
    {
        // Act
        var errors = new NoteSearchRequest(status, null, null, page).Validate();

        // Assert
        errors.Should().ContainKey(field);
    }

    [Fact]
    public void Validate_NameOverLimit_ReportsName()
    {
        // Act
        var errors = new NoteSearchRequest(null, null, new string('a', NoteSearchRequest.MaxName + 1), null).Validate();

        // Assert
        errors.Should().ContainKey("name");
    }

    // Labels: Draft, ReadyForReview, Signed, Failed, StructureFailed (ReadyForReview + StructureStatus failed).
    private async Task<Dictionary<Guid, string>> AddLabelledNotesAsync(PatientSearchSeed seed, Guid patient)
    {
        foreach (var status in new[] { "Draft", "ReadyForReview", "Signed" })
        {
            await seed.AddNoteAsync(patient, status, Now, cancellationToken: Ct);
        }
        await seed.ExecuteSqlAsync(
            """
            INSERT INTO "Notes" ("ProviderId", "PatientId", "Status", "InputMode", "FailureReason") VALUES (@provider, @patient, 'Failed', 'VoiceLive', 'no_speech');
            INSERT INTO "Notes" ("ProviderId", "PatientId", "Status", "InputMode", "StructureStatus", "StructureFailure", "RawTranscript")
            VALUES (@provider, @patient, 'ReadyForReview', 'Text', 'failed', 'model_error', 'x');
            """,
            Ct, ("provider", seed.ProviderId), ("patient", patient));
        await using var db = seed.CreateDbContext();
        var rows = await db.Notes.AsNoTracking().Where(n => n.PatientId == patient)
            .Select(n => new { n.Id, n.Status, n.StructureStatus }).ToListAsync(Ct);
        return rows.ToDictionary(r => r.Id, r => r.StructureStatus == "failed" ? "StructureFailed" : r.Status);
    }
}
```

Add to `NoteEndpointTests.cs`:

```csharp
    [Fact]
    public async Task GetAsync_SectionsByAi_FollowsLatestOfGeneratedOrCreated()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var manual = await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, null, "DAP", "manual", "x"), DateTimeOffset.UtcNow, Ct);

        // Act
        var beforeAi = await NoteQueries.GetAsync(db, seed.ProviderId, manual.NoteId!.Value, Ct);
        db.AuditEntries.Add(new Anamnys.Server.Data.Entities.AuditEntry
        {
            Id = Guid.NewGuid(), NoteId = manual.NoteId, Action = "ai_generated", At = DateTimeOffset.UtcNow.AddMinutes(1),
        });
        await db.SaveChangesAsync(Ct);
        var afterAi = await NoteQueries.GetAsync(db, seed.ProviderId, manual.NoteId!.Value, Ct);

        // Assert
        beforeAi!.SectionsByAi.Should().BeFalse();
        afterAi!.SectionsByAi.Should().BeTrue();
    }
```

Add to `AppointmentsTests.cs` (same class conventions):

```csharp
    [Fact]
    public async Task ListAsync_AppointmentWithNote_HasNoteTrue()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var noted = await seed.AddAppointmentAsync(patient, Now.AddHours(-3), cancellationToken: Ct);
        var plain = await seed.AddAppointmentAsync(patient, Now.AddHours(-1), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        await NoteCreation.CreateAsync(db, new TranscriptionQueue(), seed.ProviderId,
            new CreateNoteRequest(patient, noted, "DAP", "manual", null), Now, Ct);

        // Act
        var list = await Appointments.ListAsync(db, seed.ProviderId, Now.AddDays(-1), Now.AddDays(1), [], Ct);

        // Assert
        list.Items.Single(i => i.Id == noted).HasNote.Should().BeTrue();
        list.Items.Single(i => i.Id == plain).HasNote.Should().BeFalse();
    }
```

(Use the `Now` and the `Appointments` type reference already in `AppointmentsTests`; add `using Anamnys.Server.Notes;` and `using Anamnys.Server.LiveSession;`.)

- [ ] **Step 2: Run → FAIL** (`-class "*NoteListTests*"`).

- [ ] **Step 3: `PatientSearchQuery.ContainsPattern`** — change `private static string? ContainsPattern` to `internal static string? ContainsPattern`.

- [ ] **Step 4: Create `Notes/NoteList.cs`**

```csharp
using Anamnys.Server.Data;
using Anamnys.Server.Patients;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

// A POST body, not a query string: the patient name must never land in a URL.
public sealed record NoteSearchRequest(string? Status, Guid? PatientId, string? Name, int? Page)
{
    public const int MaxName = 100;
    private static readonly string[] Statuses = ["pending", "signed", "failed"];

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (Status is not null && !Statuses.Contains(Status))
        {
            errors["status"] = ["Filtro inválido."];
        }
        if (Name is { Length: > MaxName })
        {
            errors["name"] = [$"Use no máximo {MaxName} caracteres."];
        }
        if (Page is < 1)
        {
            errors["page"] = ["Página inválida."];
        }
        return errors;
    }
}

public sealed record NoteListItem(
    Guid Id, Guid PatientId, string PatientName, string Status, string? StructureStatus, string InputMode, string Format,
    DateTimeOffset CreatedAt, DateTimeOffset? SignedAt, DateTimeOffset? AppointmentStartsAt);

public sealed record NoteSearchResponse(IReadOnlyList<NoteListItem> Items, bool HasMore);

public static class NoteList
{
    public const int PageSize = 25;
    private static readonly string[] Pending = ["Draft", "Processing", "ReadyForReview"];
    private static readonly string[] Signed = ["Signed", "Exported"];

    public static async Task<NoteSearchResponse> SearchAsync(AnamnysDbContext db, Guid providerId, NoteSearchRequest request, CancellationToken ct)
    {
        var rows =
            from n in db.Notes.AsNoTracking()
            join p in db.Patients.AsNoTracking() on n.PatientId equals p.Id
            where n.ProviderId == providerId && p.ProviderId == providerId
            select new { n, p };
        rows = request.Status switch
        {
            "pending" => rows.Where(r => Pending.Contains(r.n.Status) && r.n.StructureStatus != "failed"),
            "signed" => rows.Where(r => Signed.Contains(r.n.Status)),
            "failed" => rows.Where(r => r.n.Status == "Failed" || r.n.StructureStatus == "failed"),
            _ => rows,
        };
        if (request.PatientId is { } patientId)
        {
            rows = rows.Where(r => r.n.PatientId == patientId);
        }
        if (PatientSearchQuery.ContainsPattern(request.Name) is { } name)
        {
            rows = rows.Where(r => EF.Functions.ILike(r.p.FirstName + " " + r.p.LastName, name, "\\"));
        }

        var page = request.Page ?? 1;
        var items = await rows
            .OrderByDescending(r => r.n.CreatedAt).ThenByDescending(r => r.n.Id)
            .Skip((page - 1) * PageSize)
            .Take(PageSize + 1)
            .Select(r => new NoteListItem(
                r.n.Id, r.n.PatientId, r.p.FirstName + " " + r.p.LastName, r.n.Status, r.n.StructureStatus, r.n.InputMode, r.n.Format,
                r.n.CreatedAt, r.n.SignedAt,
                db.Appointments
                    .Where(a => r.n.SessionId != null && a.SessionId == r.n.SessionId)
                    .Select(a => (DateTimeOffset?)a.StartsAt)
                    .FirstOrDefault()))
            .ToListAsync(ct);
        var hasMore = items.Count > PageSize;
        return new NoteSearchResponse(hasMore ? items[..PageSize] : items, hasMore);
    }
}
```

Note the `pending` filter excludes structure failures so a note never shows under both "Pendentes" and "Com falha"; the test's `pending` row expects only `Draft` and `ReadyForReview`, which matches.

- [ ] **Step 5: Route** — in `NoteEndpoints.MapNoteEndpoints`:

```csharp
        notes.MapPost("search", async (NoteSearchRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = request.Validate();
            return errors.Count > 0
                ? Results.ValidationProblem(errors)
                : Results.Ok(await NoteList.SearchAsync(db, providerId, request, ct));
        });
```

- [ ] **Step 6: `NoteDetail.SectionsByAi`** — in `NoteQueries.cs` add the trailing record parameter `bool SectionsByAi`, project it in the anonymous select:

```csharp
                              SectionsByAi = db.AuditEntries
                                  .Where(a => a.NoteId == n.Id && (a.Action == "ai_generated" || a.Action == "sections_created"))
                                  .OrderByDescending(a => a.At).ThenByDescending(a => a.Action == "ai_generated")
                                  .Select(a => a.Action)
                                  .FirstOrDefault() == "ai_generated",
```

and pass `note.SectionsByAi` as the last constructor argument.

- [ ] **Step 7: `AppointmentItem.HasNote`** — add trailing `bool HasNote` to the record in `AppointmentContracts.cs`; in `Appointments.ListAsync` pass as the last argument:

```csharp
                x.a.SessionId != null && db.Notes.Any(n => n.SessionId == x.a.SessionId)
```

Fix any other `new AppointmentItem(` (`grep -rn "new AppointmentItem(" anamnys-aspire.Server anamnys-aspire.Tests`).

- [ ] **Step 8: `.http`** — append:

```http
### Search the provider's notes (filters in the body: patient names never go in a URL)
POST {{provider_base}}/api/phi/providers/me/notes/search
Content-Type: application/json

{ "status": "pending", "patientId": null, "name": null, "page": 1 }
```

- [ ] **Step 9: Run → PASS** (`-class "*NoteListTests*"`, `-class "*NoteEndpointTests*"`, `-class "*AppointmentsTests*"`, `-class "*AppointmentEndpointTests*"`).

- [ ] **Step 10: Stop** — leave uncommitted.

---

### Task 5: Shared frontend contracts, note card and patient notes view

**Files:**
- Modify: `packages/shared/src/lib/types.ts`, `packages/shared/src/api/notes.ts`, `packages/shared/src/lib/i18n/locales/pt.json`
- Modify: `apps/provider/src/components/NoteCard.tsx`, `apps/provider/src/components/PatientNotesView.tsx`

**Interfaces:**
- Consumes: Task 3/4 routes.
- Produces (TS):

```ts
export type NoteListFilter = "pending" | "signed" | "failed";
export type NoteMode = "ai" | "manual";

export interface NoteListItem {
  id: string;
  patientId: string;
  patientName: string;
  status: NoteDetailStatus;
  structureStatus: StructureStatus | null;
  inputMode: string;
  format: string;
  createdAt: string;
  signedAt: string | null;
  appointmentStartsAt: string | null;
}

export interface NoteSearchRequest {
  status?: NoteListFilter;
  patientId?: string;
  name?: string;
  page?: number;
}

export interface NoteSearchResponse {
  items: NoteListItem[];
  hasMore: boolean;
}

export interface NoteCandidate {
  appointmentId: string;
  startsAt: string;
  modality: AppointmentModality;
  status: AppointmentStatus;
}

export interface CreateNoteRequest {
  patientId: string;
  appointmentId: string | null;
  format: LiveNoteFormat;
  mode: NoteMode;
  text: string | null;
}
```

  `NoteDetail` gains `sectionsByAi: boolean`; `Appointment` gains `hasNote: boolean`.
  `notesApi.search(request: NoteSearchRequest): Promise<NoteSearchResponse>`, `notesApi.create(request: CreateNoteRequest): Promise<string>`, `notesApi.candidates(patientId: string): Promise<NoteCandidate[]>`. `notesApi.list` is removed (its only caller is `PatientNotesView`).
  `NoteCard` props: `{ note: NoteListItem; showPatient?: boolean; onClick: () => void }`.

- [ ] **Step 1: Types** — add the block above to `types.ts` (after `NoteDetail`), plus the two new fields.

- [ ] **Step 2: API** — in `notes.ts` replace `list` with:

```ts
  search: async (request: NoteSearchRequest): Promise<NoteSearchResponse> => {
    const { data } = await api.post<NoteSearchResponse>(`/phi/providers/me/notes/search`, request);
    return data;
  },
  create: async (request: CreateNoteRequest): Promise<string> => {
    const { data } = await api.post<{ noteId: string }>(`/phi/providers/me/notes`, request);
    return data.noteId;
  },
  candidates: async (patientId: string): Promise<NoteCandidate[]> => {
    const { data } = await api.get<NoteCandidate[]>(`/phi/providers/me/patients/${patientId}/note-candidates`);
    return data;
  },
```

and update the type import.

- [ ] **Step 3: i18n** — in `pt.json` add under `notes`:

```json
    "list": {
      "title": "Notas",
      "newNote": "Nova nota",
      "searchPlaceholder": "Buscar por paciente",
      "filters": { "pending": "Pendentes", "signed": "Assinadas", "failed": "Com falha", "all": "Todas" },
      "empty": "Nenhuma nota neste filtro.",
      "failedToLoad": "Falha ao carregar as notas.",
      "loadMore": "Carregar mais",
      "origin": { "VoiceLive": "Sessão ao vivo", "Text": "Digitada" },
      "appointment": "Consulta de {{date}}"
    },
    "detailStatus": {
      "Draft": "Rascunho",
      "Processing": "Processando…",
      "ReadyForReview": "Pronta para revisão",
      "Signed": "Assinada",
      "Exported": "Exportada",
      "Failed": "Falhou",
      "structureFailed": "Organização falhou"
    },
    "create": {
      "title": "Nova nota",
      "patient": "Paciente",
      "appointment": "Consulta",
      "noAppointment": "Sem consulta",
      "format": "Formato",
      "mode": "Como escrever",
      "modeAi": "Organizar com IA",
      "modeAiHint": "Escreva livremente; a IA distribui o texto nas seções do formato.",
      "modeManual": "Escrever nas seções",
      "modeManualHint": "A nota abre com as seções vazias para você preencher.",
      "text": "Anotações",
      "textHint": "A IA organiza o que você escreveu; não acrescenta nada.",
      "counter": "{{count}} / {{max}}",
      "submit": "Criar nota",
      "failed": "Não foi possível criar a nota."
    }
```

Under `noteView` add `"originalText": "Texto original"` and change `"emptySection"` to `"Nada registrado nesta seção."`. Under `patients.detail` add `"newNote": "Nova nota"`. Under `calendar.details` add `"writeNote": "Escrever nota"`.

- [ ] **Step 4: `NoteCard.tsx`**

```tsx
import { useTranslation } from "react-i18next";
import type { NoteListItem } from "@anamnys/shared/lib/types";
import Card from "@anamnys/shared/ui/Card";
import Badge, { type BadgeTone } from "@anamnys/shared/ui/Badge";

const STATUS_TONE: Record<string, BadgeTone> = {
  Draft: "neutral",
  Processing: "pink",
  ReadyForReview: "teal",
  Signed: "primary",
  Exported: "blue",
  Failed: "neutral",
};

interface Props {
  note: NoteListItem;
  showPatient?: boolean;
  onClick: () => void;
}

// One row of a note list: date, format, origin and status; the patient's name in the all-notes list.
export default function NoteCard({ note, showPatient, onClick }: Props) {
  const { t } = useTranslation();
  const structureFailed = note.structureStatus === "failed" && note.status !== "Failed";
  return (
    <Card onClick={onClick}>
      <div className="flex items-center justify-between gap-2 mb-2">
        <span className="text-label-md text-onSurfaceVariant normal-case">
          {new Date(note.createdAt).toLocaleDateString("pt-BR")}
        </span>
        <Badge
          label={structureFailed ? t("notes.detailStatus.structureFailed") : t(`notes.detailStatus.${note.status}`)}
          tone={structureFailed ? "neutral" : STATUS_TONE[note.status] ?? "neutral"}
        />
      </div>
      {showPatient && <p className="text-headline-sm text-[16px] text-onSurface mb-1 truncate">{note.patientName}</p>}
      <p className="text-body-md text-onSurfaceVariant">
        {t(`liveSession.formats.${note.format}`, { defaultValue: note.format })}
        {" · "}
        {t(`notes.list.origin.${note.inputMode}`, { defaultValue: note.inputMode })}
        {note.appointmentStartsAt &&
          ` · ${t("notes.list.appointment", { date: new Date(note.appointmentStartsAt).toLocaleDateString("pt-BR") })}`}
      </p>
    </Card>
  );
}
```

- [ ] **Step 5: `PatientNotesView.tsx`** — query `["notes", "patient", patientId]` with `queryFn: () => notesApi.search({ patientId })`, render `notes.items`, keep the existing loading/error/empty markup.

- [ ] **Step 6: Build and lint** — `npm run build` and `npm run lint` from `anamnys-aspire/` → both pass. Fix any other compile error caused by the `NoteCard` prop type (`grep -rn "NoteCard" apps/provider/src`).

- [ ] **Step 7: Stop** — leave uncommitted.

---

### Task 6: `/note-editor` list page

**Files:**
- Create: `apps/provider/src/routes/_app/note-editor/index.tsx`
- Regenerate: `apps/provider/src/routeTree.gen.ts` (`npx vite build` in `apps/provider`, or let `npm run dev` regenerate)

**Interfaces:**
- Consumes: `notesApi.search`, `NoteCard`, i18n `notes.list.*`.
- Produces: route `/_app/note-editor/` (path `/note-editor`).

- [ ] **Step 1: Route**

```tsx
import { useEffect, useState } from "react";
import { createFileRoute, Link, useNavigate } from "@tanstack/react-router";
import { useInfiniteQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Loader2, Plus, Search } from "lucide-react";
import { notesApi } from "@anamnys/shared/api/notes";
import type { NoteListFilter } from "@anamnys/shared/lib/types";
import NoteCard from "@/components/NoteCard";
import { inputClass } from "@/components/patients/FormField";

export const Route = createFileRoute("/_app/note-editor/")({
  component: NoteListPage,
});

type Filter = NoteListFilter | "all";
const FILTERS: Filter[] = ["pending", "signed", "failed", "all"];

// All of the provider's notes (design/specs/2026-10-15-note-editor-design.md §5.1).
// The name filter lives in component state and goes in a POST body, never in the URL.
function NoteListPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [filter, setFilter] = useState<Filter>("pending");
  const [text, setText] = useState("");
  const [name, setName] = useState("");
  useEffect(() => {
    const timer = setTimeout(() => setName(text.trim()), 300);
    return () => clearTimeout(timer);
  }, [text]);

  const query = useInfiniteQuery({
    queryKey: ["notes", "list", filter, name],
    queryFn: ({ pageParam }) =>
      notesApi.search({ status: filter === "all" ? undefined : filter, name: name || undefined, page: pageParam }),
    initialPageParam: 1,
    getNextPageParam: (last, pages) => (last.hasMore ? pages.length + 1 : undefined),
  });
  const items = query.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <div className="flex flex-col gap-4 p-4 md:p-8 max-w-3xl w-full mx-auto">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-headline-lg text-onSurface">{t("notes.list.title")}</h1>
        <Link
          to="/note-editor/new"
          className="inline-flex items-center gap-2 px-4 py-2 rounded-radii-md text-label-lg text-onPrimary bg-primary hover:opacity-90"
        >
          <Plus size={18} aria-hidden />
          {t("notes.list.newNote")}
        </Link>
      </div>

      <div className="flex flex-wrap gap-2" role="group" aria-label={t("notes.list.title")}>
        {FILTERS.map((f) => (
          <button
            key={f}
            type="button"
            aria-pressed={filter === f}
            onClick={() => setFilter(f)}
            className={`px-3 py-1.5 rounded-full border text-label-lg ${
              filter === f ? "bg-surfaceContainerLowest border-primary text-primary shadow-sm" : "border-outlineVariant text-onSurface"
            }`}
          >
            {t(`notes.list.filters.${f}`)}
          </button>
        ))}
      </div>

      <label className="relative">
        <span className="sr-only">{t("notes.list.searchPlaceholder")}</span>
        <Search size={18} className="absolute left-3 top-1/2 -translate-y-1/2 text-onSurfaceVariant" aria-hidden />
        <input
          className={`${inputClass} pl-10`}
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder={t("notes.list.searchPlaceholder")}
          maxLength={100}
        />
      </label>

      {query.isLoading ? (
        <div className="flex justify-center py-12">
          <Loader2 className="animate-spin text-primary" size={32} />
        </div>
      ) : query.isError ? (
        <p className="text-error text-center py-12">{t("notes.list.failedToLoad")}</p>
      ) : items.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant text-center mt-8">{t("notes.list.empty")}</p>
      ) : (
        <div className="flex flex-col gap-2.5">
          {items.map((note) => (
            <NoteCard key={note.id} note={note} showPatient onClick={() => navigate({ to: "/notes/$noteId", params: { noteId: note.id } })} />
          ))}
          {query.hasNextPage && (
            <button
              type="button"
              disabled={query.isFetchingNextPage}
              onClick={() => void query.fetchNextPage()}
              className="self-center px-4 py-2 rounded-radii-lg border border-outlineVariant text-label-lg text-onSurface disabled:opacity-60"
            >
              {t("notes.list.loadMore")}
            </button>
          )}
        </div>
      )}
    </div>
  );
}
```

The active-chip classes are the ones the calendar's Dia/Semana toggle uses (`CalendarHeader.tsx`).

- [ ] **Step 2: Build and lint** (the route tree regenerates during `vite build`; keep `routeTree.gen.ts` in the state the repo expects — check `git status` for it and follow the CLAUDE.md/`.gitignore` situation recorded in the gaps file).

- [ ] **Step 3: Stop** — leave uncommitted.

---

### Task 7: `/note-editor/new` create page

**Files:**
- Create: `apps/provider/src/components/notes/NoteTargetCard.tsx`, `apps/provider/src/components/notes/NoteModeCards.tsx`, `apps/provider/src/routes/_app/note-editor/new.tsx`

**Interfaces:**
- Consumes: `PatientPicker` (`components/calendar/PatientPicker`), `FormatCards` (`components/live-session/FormatCards`), `notesApi.candidates`, `notesApi.create`, `patientsApi.get`, `ApiError` from `@anamnys/shared/api/client`, calendar time helpers `PRACTICE_ZONE`, `formatTimeWithDate` from `components/calendar/calendarTime`.
- Produces: route `/note-editor/new` with search `{ patientId?: string; appointmentId?: string }` (both UUID-validated).

- [ ] **Step 1: `NoteTargetCard.tsx`**

```tsx
import { useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { CalendarClock, UserRound } from "lucide-react";
import { notesApi } from "@anamnys/shared/api/notes";
import PatientPicker from "@/components/calendar/PatientPicker";
import { PRACTICE_ZONE, formatTimeWithDate, todayIn, zonedParts } from "@/components/calendar/calendarTime";
import { inputClass } from "@/components/patients/FormField";

export type PickedPatient = { id: string; name: string };

interface Props {
  patient: PickedPatient | null;
  onPatientChange: (patient: PickedPatient | null) => void;
  appointmentId: string;
  onAppointmentChange: (id: string) => void;
  error?: string[];
}

// Who the typed note is for, and optionally which recent appointment it records.
export default function NoteTargetCard({ patient, onPatientChange, appointmentId, onAppointmentChange, error }: Props) {
  const { t } = useTranslation();
  const candidates = useQuery({
    queryKey: ["note-candidates", patient?.id],
    queryFn: () => notesApi.candidates(patient!.id),
    enabled: !!patient,
  });

  // Preselect today's appointment once the candidates arrive, unless one was chosen already.
  useEffect(() => {
    if (!candidates.data || appointmentId) return;
    const today = todayIn(PRACTICE_ZONE);
    const ofToday = candidates.data.find((c) => zonedParts(new Date(c.startsAt), PRACTICE_ZONE).date === today);
    if (ofToday) onAppointmentChange(ofToday.appointmentId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [candidates.data]);

  return (
    <section className="flex flex-col gap-4 bg-surfaceContainerLowest rounded-radii-xl border border-outlineVariant p-5 shadow-sm">
      <div className="flex flex-col gap-2">
        <h2 className="flex items-center gap-2 text-title-md text-onSurface">
          <UserRound size={20} className="text-primary" aria-hidden />
          {t("notes.create.patient")}
        </h2>
        <PatientPicker
          value={patient}
          onChange={(p) => {
            onPatientChange(p);
            onAppointmentChange("");
          }}
          error={error}
        />
      </div>
      {patient && (
        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          <span className="flex items-center gap-2">
            <CalendarClock size={16} className="text-primary" aria-hidden />
            {t("notes.create.appointment")}
          </span>
          <select className={inputClass} value={appointmentId} onChange={(e) => onAppointmentChange(e.target.value)}>
            <option value="">{t("notes.create.noAppointment")}</option>
            {(candidates.data ?? []).map((c) => (
              <option key={c.appointmentId} value={c.appointmentId}>
                {formatTimeWithDate(new Date(c.startsAt), new Date(), PRACTICE_ZONE)} · {t(`calendar.modality.${c.modality}`)}
              </option>
            ))}
          </select>
        </label>
      )}
    </section>
  );
}
```

Signatures used: `todayIn(zone): string` (YYYY-MM-DD), `zonedParts(instant, zone).date`, `formatTimeWithDate(instant, reference, zone)`.

- [ ] **Step 2: `NoteModeCards.tsx`**

```tsx
import { useTranslation } from "react-i18next";
import { Check, PenLine, Sparkles } from "lucide-react";
import type { NoteMode } from "@anamnys/shared/lib/types";

interface Props {
  value: NoteMode;
  onChange: (mode: NoteMode) => void;
}

const MODES: { mode: NoteMode; Icon: typeof Sparkles; label: string; hint: string }[] = [
  { mode: "ai", Icon: Sparkles, label: "notes.create.modeAi", hint: "notes.create.modeAiHint" },
  { mode: "manual", Icon: PenLine, label: "notes.create.modeManual", hint: "notes.create.modeManualHint" },
];

// Two selectable cards: organise free text with the AI, or start from empty sections.
export default function NoteModeCards({ value, onChange }: Props) {
  const { t } = useTranslation();
  return (
    <div role="radiogroup" aria-label={t("notes.create.mode")} className="grid gap-3 sm:grid-cols-2">
      {MODES.map(({ mode, Icon, label, hint }) => {
        const selected = value === mode;
        return (
          <button
            key={mode}
            type="button"
            role="radio"
            aria-checked={selected}
            onClick={() => onChange(mode)}
            className={`flex items-start gap-3 rounded-radii-lg border p-4 text-left ${
              selected ? "border-primary bg-surfaceContainerLow" : "border-outlineVariant"
            }`}
          >
            <Icon size={20} className="text-primary shrink-0" aria-hidden />
            <span className="flex-1">
              <span className="block text-label-lg text-onSurface">{t(label)}</span>
              <span className="block text-body-sm text-onSurfaceVariant">{t(hint)}</span>
            </span>
            {selected && <Check size={18} className="text-primary" aria-hidden />}
          </button>
        );
      })}
    </div>
  );
}
```

- [ ] **Step 3: `routes/_app/note-editor/new.tsx`**

```tsx
import { useEffect, useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Loader2 } from "lucide-react";
import { ApiError } from "@anamnys/shared/api/client";
import { notesApi } from "@anamnys/shared/api/notes";
import { patientsApi } from "@anamnys/shared/api/patients";
import type { LiveNoteFormat, NoteMode } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import FormatCards from "@/components/live-session/FormatCards";
import NoteTargetCard, { type PickedPatient } from "@/components/notes/NoteTargetCard";
import NoteModeCards from "@/components/notes/NoteModeCards";
import { inputClass } from "@/components/patients/FormField";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const MAX_TEXT = 20_000;

interface NewNoteSearch {
  patientId?: string;
  appointmentId?: string;
}

export const Route = createFileRoute("/_app/note-editor/new")({
  // Only opaque ids in the URL; the patient's name is loaded, never passed.
  validateSearch: (search: Record<string, unknown>): NewNoteSearch => ({
    patientId: typeof search.patientId === "string" && UUID.test(search.patientId) ? search.patientId : undefined,
    appointmentId: typeof search.appointmentId === "string" && UUID.test(search.appointmentId) ? search.appointmentId : undefined,
  }),
  component: NewNotePage,
});

// Typed note (design/specs/2026-10-15-note-editor-design.md §5.2).
function NewNotePage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const search = Route.useSearch();
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [appointmentId, setAppointmentId] = useState(search.appointmentId ?? "");
  const [format, setFormat] = useState<LiveNoteFormat>("DAP");
  const [mode, setMode] = useState<NoteMode>("ai");
  const [text, setText] = useState("");
  const [errors, setErrors] = useState<Record<string, string[]>>({});

  const preselected = useQuery({
    queryKey: ["patient", search.patientId],
    queryFn: () => patientsApi.get(search.patientId!),
    enabled: !!search.patientId,
  });
  useEffect(() => {
    if (preselected.data && !patient) {
      setPatient({ id: preselected.data.id, name: `${preselected.data.firstName} ${preselected.data.lastName}` });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [preselected.data]);

  const create = useMutation({
    mutationFn: () =>
      notesApi.create({
        patientId: patient!.id,
        appointmentId: appointmentId || null,
        format,
        mode,
        text: text.trim() || null,
      }),
    onSuccess: (noteId) => {
      void queryClient.invalidateQueries({ queryKey: ["notes"] });
      void queryClient.invalidateQueries({ queryKey: ["appointments"] });
      navigate({ to: "/notes/$noteId", params: { noteId } });
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 400 && err.errors) setErrors(err.errors);
      else toast.error(err instanceof ApiError && err.status === 409 ? err.message : t("notes.create.failed"));
    },
  });

  const canSubmit = !!patient && (mode === "manual" || text.trim().length > 0) && text.length <= MAX_TEXT && !create.isPending;

  return (
    <form
      className="flex flex-col gap-6 p-4 md:p-8 max-w-3xl w-full mx-auto"
      onSubmit={(e) => {
        e.preventDefault();
        setErrors({});
        if (canSubmit) create.mutate();
      }}
    >
      <h1 className="text-headline-lg text-onSurface">{t("notes.create.title")}</h1>
      <NoteTargetCard
        patient={patient}
        onPatientChange={setPatient}
        appointmentId={appointmentId}
        onAppointmentChange={setAppointmentId}
        error={errors.patientId}
      />
      <section className="flex flex-col gap-2">
        <h2 className="text-title-md text-onSurface">{t("notes.create.format")}</h2>
        <FormatCards value={format} onChange={setFormat} />
      </section>
      <section className="flex flex-col gap-2">
        <h2 className="text-title-md text-onSurface">{t("notes.create.mode")}</h2>
        <NoteModeCards value={mode} onChange={setMode} />
      </section>
      {mode === "ai" && (
        <label className="flex flex-col gap-1">
          <span className="text-label-lg text-onSurface">{t("notes.create.text")}</span>
          <span className="text-body-sm text-onSurfaceVariant">{t("notes.create.textHint")}</span>
          <textarea
            className={`${inputClass} min-h-64`}
            value={text}
            onChange={(e) => setText(e.target.value)}
            maxLength={MAX_TEXT}
            aria-invalid={!!errors.text}
          />
          <span className="self-end text-body-sm text-onSurfaceVariant">
            {t("notes.create.counter", { count: text.length, max: MAX_TEXT })}
          </span>
          {errors.text && <span role="alert" className="text-body-sm text-error">{errors.text[0]}</span>}
        </label>
      )}
      <button
        type="submit"
        disabled={!canSubmit}
        className="self-end inline-flex items-center gap-2 rounded-radii-lg bg-primary px-5 py-2.5 text-label-lg text-onPrimary disabled:opacity-60"
      >
        {create.isPending && <Loader2 size={16} className="animate-spin" aria-hidden />}
        {t("notes.create.submit")}
      </button>
    </form>
  );
}
```

`["appointments"]` is the prefix of every `useAppointmentsQuery` key (`KEY` in `hooks/useAppointments.ts`).

- [ ] **Step 4: Build and lint** → pass.

- [ ] **Step 5: Stop** — leave uncommitted.

---

### Task 8: Entry points and note page adjustments

**Files:**
- Modify: `packages/shared/src/ui/AppTabBar.tsx`, `apps/provider/src/routes/_app/patients/$patientId/index.tsx`, `apps/provider/src/components/calendar/AppointmentDetailsModal.tsx`, `apps/provider/src/routes/_app/notes/$noteId.tsx`

**Interfaces:**
- Consumes: route `/note-editor/new` (Task 7), `Appointment.hasNote`, `NoteDetail.sectionsByAi` (Task 5).

- [ ] **Step 1: Tab bar** — in `AppTabBar.tsx` change the new-note tab to `{ href: "/note-editor/new", Icon: Mic, labelKey: "nav.newNote", matchPrefix: "/note-editor" }`.

- [ ] **Step 2: Patient record** — in `patients/$patientId/index.tsx`, after the "Agendar consulta" link (same `!archived` guard and `actionButton` class):

```tsx
          {!archived && (
            <Link to="/note-editor/new" search={{ patientId }} className={actionButton}>
              <FilePlus size={18} />
              {t("patients.detail.newNote")}
            </Link>
          )}
```

Import `FilePlus` from `lucide-react`.

- [ ] **Step 3: Calendar details** — in `AppointmentDetailsModal.tsx`, inside the actions `div` after the status buttons:

```tsx
            {started && a.status !== "cancelled" && !a.hasNote && (
              <Link
                to="/note-editor/new"
                search={{ patientId: a.patientId, appointmentId: a.id }}
                className={button}
                onClick={onClose}
              >
                {t("calendar.details.writeNote")}
              </Link>
            )}
```

(`Link` is already imported.)

- [ ] **Step 4: Note page** — in `notes/$noteId.tsx`:
  - Badge: replace `inReview` usage so the AI badge needs the server signal:

```tsx
  const aiDraft = note.sectionsByAi && hasSections && !note.sections.some((s) => s.editedAt);
```

and render `aiDraft ? aiDraft badge : inReview badge` in place of the current `inReview ? … : …` (signed stays first).
  - Transcript card label: pass the title to `TranscriptCard`:

```tsx
          {note.rawTranscript && (
            <TranscriptCard
              text={note.rawTranscript}
              defaultOpen={!hasSections}
              title={note.inputMode === "Text" ? t("noteView.originalText") : t("noteView.transcript")}
            />
          )}
```

and make `TranscriptCard` take a `title: string` prop used instead of `t("noteView.transcript")`.

- [ ] **Step 5: Build and lint** → pass.

- [ ] **Step 6: Stop** — leave uncommitted.

---

### Task 9: Full verification and docs

**Files:**
- Modify: `design/gaps/2026-10-03-patient-flows-gaps.md`, `design/status/2026-10-15-implementation-status.md`, `design/specs/2026-10-15-note-editor-design.md` (status line)

- [ ] **Step 1: Full test suite** — `aspire stop`, then from `anamnys-aspire/`: `dotnet run --project anamnys-aspire.Tests`. Expected: all pass. Record the count; on failure keep the full log and the failing test's name.

- [ ] **Step 2: Frontend** — `npm run build` and `npm run lint` from `anamnys-aspire/` → both pass.

- [ ] **Step 3: Manual browser check** (with `aspire start`, as `dev.provider`):
  1. Sidebar "Editor de Notas" opens the list with "Pendentes" selected; each filter and the name search work; "Carregar mais" appears past 25.
  2. "Nova nota" → AI mode with text → lands on the note, shows "Organizando em seções…", then sections with the "Rascunho gerado por IA" badge and a collapsed "Texto original".
  3. Manual mode → empty sections, badge is not the AI one; editing and signing work.
  4. From a patient record, "Nova nota" preselects the patient.
  5. From the calendar, a past appointment shows "Escrever nota", preselects patient and appointment; after creating, the appointment is "Realizada" and "Escrever nota" is gone.
  6. A live session finalized on a started appointment moves it to "Realizada".
  7. At 390px: the "Nova nota" tab opens the create page; list and create page fit without horizontal scroll.

- [ ] **Step 4: Gaps file** — in `## Notas clínicas`:
  - Change the "**Notes.**" bullet to say `/note-editor` lists all notes and `PatientNotesView` uses `POST providers/me/notes/search`.
  - In "Appointment → session → note chain", add that typed notes also link (optional appointment) and both paths move the appointment to attended.
  - Remove from "Still missing" the typed-text structuring item; remove the "Header badge…" bullet, "The appointment `Status` is not moved to attended…" bullet and "Two concurrent recordings on one appointment orphan the first session" bullet; in "Audit coverage boundary" move "note creation at finalize" to audited (`note_created`).
  - In `## Pacientes`, drop "it does not preselect the patient in the new-appointment form" only if that is already fixed (`fda5b7b` did it); otherwise leave.
  - Add any new gap found during the work.

- [ ] **Step 5: Status file** — in `design/status/2026-10-15-implementation-status.md` set "Digitação direta" and flow 03 to done, flow 01 note "review and sign done; audio upload not built" unchanged, F-04 note "a `Sessions` row is created by a live recording or a typed note linked to an appointment".

- [ ] **Step 6: Spec status** — `**Status:** Implemented`.

- [ ] **Step 7: Stop** — report results to the user; leave everything uncommitted for review.
