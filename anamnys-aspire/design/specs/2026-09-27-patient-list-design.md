# Provider Patient List Design

**Date:** 2026-09-27
**Status:** Proposed
**Scope:** The provider app's patient list page (`apps/provider`, route `/patients`),
rebuilt to the `specs/UI/PatientList` mockup, and the provider-scoped server endpoint it
reads from: server-side search, filters, sortable columns and pagination.

---

## 1. Context

- `apps/provider/src/routes/_app/patients/index.tsx` exists but is a mobile-first card
  list that filters client-side over the first 50 rows. It does not match the mockup
  (desktop table, filter button, pagination).
- It calls `patientsApi.list` → `GET /api/patients`, **which does not exist**. The only
  PHI routes today are `/api/phi/{providers,patients}/me/profile`.
- The mockup's columns need data the EF model does not map yet:
  - *Last visit* → `Patients.LastVisit` (mapped).
  - *Upcoming visit* → the `Appointments` table (schema only, no entity, no write flow).
  - *Status* (Draft Pending / Signed) → the `Notes` table (schema only, no entity).
- The shell (Sidenav + TopBar) already exists in `_app.tsx`; the mockup's sidebar and
  top bar are not rebuilt. The mockup's top-bar search is not adopted (see §5).
- HIPAA constraints that shape this design: every query is scoped to the authenticated
  provider's local id resolved from the session; no PHI in URLs.

## 2. Decisions

| Topic | Decision |
|---|---|
| Data | Real endpoint; `Appointments` and `Notes` mapped as read-only EF entities. Columns show "—" until scheduling/notes flows exist. |
| Status column | Status of the patient's **most recent note** (by `CreatedAt`), grouped: `pending` = Draft/Processing/ReadyForReview, `signed` = Signed/Exported, `none` = no notes. |
| Filters | Note status (multi), name, email, last-visit range, next-visit range. |
| Date filters | Closed ranges (`from`/`to`, either side optional), whole days in `America/Sao_Paulo`. |
| Free-text search | On the page, above the table; matches full name **or** email; debounced; server-side. TopBar untouched. |
| Subtitle under name | Patient email, or "Sem portal" when null. No MRN (it does not exist). |
| Last-visit subtitle | Dropped — the list is provider-scoped, the provider name would always be the same. |
| Sorting | Clickable column headers (name, last visit, next visit, status), asc/desc. Default: next visit ascending. |
| Mobile | Table at `md+`; cards below `md`, with a sort dropdown and the filter panel as a full-screen sheet. |

## 3. Scalability

Every query is scoped by `ProviderId`, so its cost depends on **one provider's** data
(solo/small practice: 10²–10³ patients, ~50 appointments and notes per patient per year,
accumulating for years), not on total table size.

The query (approach A) reads the provider's patients via `Patients_ProviderId_idx` and,
per patient, performs two index probes:

- **Next appointment** — `Appointments_Patient_Starts_idx ("PatientId","StartsAt")`
  already exists; the probe seeks straight to the first `StartsAt > now`. Cost is
  independent of history length.
- **Latest note status** — only `Notes_PatientId_idx` exists, which forces reading and
  sorting every note of the patient. This design adds
  `Notes_Patient_Created_idx ("PatientId","CreatedAt" DESC)` so it becomes one probe.

At 5,000 patients that is ~10k index probes, tens of milliseconds. Sorting on computed
columns requires computing them for every patient of the provider before `LIMIT`;
unavoidable without denormalisation, and cheap at this volume.

Rejected alternatives:

- **Postgres view** — same plan as the LINQ projection, gains nothing, touches the schema.
  A materialized view would serve stale data.
- **Denormalised columns** (`Patients.NextAppointmentAt`, `LatestNoteStatus`) — "next
  appointment" is **time-dependent**: it goes stale when its start time passes, with no
  write to trigger an update, so it would need a periodic job. Documented as the escape
  hatch if a single provider exceeds ~10⁴ patients; not built now.

Other scale-motivated choices:

- **Offset pagination**, not keyset — keyset over sortable, nullable computed columns is
  complex and only pays off beyond tens of thousands of rows per provider.
- `pageSize` capped at 100; `AsNoTracking`; exactly one data query plus one `COUNT`, no N+1.
- `ILIKE '%term%'` runs only over the provider's own patients; no `pg_trgm` for now.
- "Now" is a query **parameter** (`DateTimeOffset.UtcNow` at the handler), not SQL
  `now()` — stable plan, deterministic tests.

Known limitation: `DatabaseInitializer` only runs the schema script against an empty
database, so existing dev databases will not get the new index unless their volume is
wiped. This affects performance only, not correctness.

## 4. API

`POST /api/phi/providers/me/patients/search`

Follows `ProfileEndpoints`: the realm is in the path, and the handler authenticates the
provider cookie itself (`LocalIdForAsync(httpContext, AuthSchemes.ProviderCookie)`). The
provider id comes from the session, never the body. It is a `POST` with a JSON body
because name and email filters are PHI and must not appear in a URL (proxy and server
logs record query strings).

### Request

All fields optional.

```json
{
  "search": "ana",
  "name": "silva",
  "email": "@gmail",
  "noteStatus": ["pending", "signed", "none"],
  "lastVisitFrom": "2026-09-01", "lastVisitTo": "2026-09-30",
  "nextVisitFrom": "2026-10-01", "nextVisitTo": "2026-10-15",
  "sortBy": "nextVisit",
  "sortDir": "asc",
  "page": 1,
  "pageSize": 25
}
```

- `search` — case-insensitive *contains* on `FirstName + ' ' + LastName` **or** `Email`.
- `name`, `email` — case-insensitive *contains* on that field only.
- `noteStatus` — values combined with OR. Empty or absent = no filter.
- Date ranges — `DateOnly`, inclusive on both ends. `from` becomes 00:00 local and `to`
  becomes 00:00 local of the following day (exclusive), local =
  `America/Sao_Paulo` (the `Appointments.Timezone` default), converted to UTC. A
  next-visit filter excludes patients without a future appointment; a last-visit filter
  excludes patients with `LastVisit` null.
- `sortBy` — `name` | `lastVisit` | `nextVisit` | `noteStatus`; `sortDir` — `asc` |
  `desc`. Default `nextVisit` / `asc`. `name` sorts by last name, then first name.
  `noteStatus` sorts `pending` < `signed` < `none`. **Nulls always last**, in both
  directions. Tie-breakers: last name, first name, id — pagination is stable.
- `page` ≥ 1 (default 1); `pageSize` 1–100 (default 25).

Validation (`request.Validate()` → `Results.ValidationProblem`, same pattern as the
profile contracts), keyed by field name:

- any range with `from > to`;
- unknown `noteStatus`, `sortBy` or `sortDir` value;
- `search`, `name` or `email` longer than 200 characters;
- `page < 1`, `pageSize` outside 1–100.

Text inputs are trimmed; a blank string means "no filter".

### Response `200`

```json
{
  "items": [
    {
      "id": "…",
      "firstName": "Ana",
      "lastName": "Silva",
      "email": "ana@example.com",
      "lastVisit": "2026-09-12T13:00:00Z",
      "nextAppointmentAt": "2026-10-02T17:30:00Z",
      "noteStatus": "pending"
    }
  ],
  "totalCount": 42,
  "page": 1,
  "pageSize": 25
}
```

`email`, `lastVisit` and `nextAppointmentAt` may be null. `noteStatus` is always one of
`pending` | `signed` | `none`.

**Next appointment** = the smallest `StartsAt > now` among the patient's appointments
with `Status IN ('scheduled','confirmed')`.

### Errors

- `401` — no provider cookie (a patient-only session is also 401).
- `400` — validation problem as above.

## 5. Backend

New folder `anamnys-aspire.Server/Patients/`, mirroring `Profile/`:

- **`PatientSearchContracts.cs`** — `PatientSearchRequest` (with `Validate()`),
  `PatientListItem`, `PatientSearchResponse`, and the note-status grouping constants.
- **`PatientSearchQuery.cs`** — all query logic in one static method:
  `ExecuteAsync(AnamnysDbContext db, Guid providerId, DateTimeOffset now, PatientSearchRequest request, CancellationToken ct)`.
  1. `db.Patients.AsNoTracking().Where(p => p.ProviderId == providerId)` — scope first,
     always.
  2. Project to an internal row with `NextAppointmentAt` and `NoteStatusGroup` via
     correlated subqueries. The subqueries also filter `ProviderId` (defence in depth).
     The raw note status is mapped to the group with a conditional expression so it
     translates to SQL `CASE`, allowing filtering and sorting in the database.
  3. Apply text, date and status filters to the projection.
  4. `CountAsync`; then `OrderBy` (nulls last, stable tie-breakers) and `Skip`/`Take`.
- **`PatientEndpoints.cs`** — `MapPatientEndpoints(this RouteGroupBuilder phi)`,
  registered next to `MapProfileEndpoints()` in `Program.cs`. Resolves the provider's
  local id, validates, calls the query with `DateTimeOffset.UtcNow`.

New EF entities in `Data/Entities/`, mapping only the columns used, no navigations:

- `Appointment` — `Id`, `ProviderId`, `PatientId`, `StartsAt`, `Status`.
- `Note` — `Id`, `ProviderId`, `PatientId`, `Status`, `CreatedAt`.

Both mapped in `AnamnysDbContext` to the existing tables. Nothing writes to them yet.

Schema: add to `anamnys-db-script.sql`

```sql
CREATE INDEX "Notes_Patient_Created_idx" ON "Notes" ("PatientId","CreatedAt" DESC);
```

and update `check_consistency.mjs` / generated docs if they track indexes.

Out of scope: `patientsApi.get` and `patientsApi.create` still target the non-existent
`/api/patients` routes; the patient detail and new-patient pages are unchanged.

## 6. Frontend

**API client** — `packages/shared/src/api/patients.ts` gains
`patientsApi.search(request)` → `POST /phi/providers/me/patients/search`; `list` is
removed (its only caller is the page being rewritten). New types in `lib/types.ts`:
`PatientSearchRequest`, `PatientListItem`, `NoteStatusGroup`, `PatientSearchResponse`.

**State** — `apps/provider/src/hooks/usePatientSearch.ts` holds search text, filters,
sort and page in local React state, **never in the URL**. Search text is debounced
300 ms. Changing search, filters or sort resets to page 1. Data via
`useQuery({ queryKey: ["patients", "search", request], placeholderData: keepPreviousData })`
so the table does not flash while paging. (The query key holds name/email in the
in-memory query cache only; nothing is persisted.)

**Components** — `apps/provider/src/components/patients/`:

- `PatientsTable.tsx` (`md+`) — the mockup's grid. Columns: Patient (avatar, name,
  email or "Sem portal"), Last visit (date), Upcoming visit (date + time), Details
  ("Ver detalhes" link), Status. Sortable headers show ↑/↓ and set `aria-sort`;
  clicking the active column flips direction, clicking another starts at `asc`. Rows
  navigate to `/patients/$patientId`.
- `PatientCards.tsx` (`<md`) — one card per patient with the same fields, plus a sort
  dropdown.
- `NoteStatusBadge.tsx` — wraps the shared `Badge`: `pending` neutral with dot,
  `signed` mint, `none` muted neutral.
- `PatientFilterPanel.tsx` — popover anchored to the "Filtrar" button on desktop,
  full-screen sheet on mobile. Fields: name, email, status checkboxes, two date ranges
  (native `<input type="date">`). "Aplicar" and "Limpar". The button shows the active
  filter count.
- Pagination footer — "Mostrando 1–25 de 42 pacientes" with ‹ › buttons.

**Page** — `routes/_app/patients/index.tsx` rewritten: title "Pacientes" and subtitle;
"Filtrar" and "Adicionar paciente" (→ `/patients/new`) buttons; search field; table or
cards. The floating action button is removed. States: loading (skeleton rows), error,
empty with no filters ("Nenhum paciente ainda" + add CTA), empty with filters
("Nenhum resultado" + clear filters).

**TopBar** — the shared `TopBar` already renders a desktop search input with the
`patients.list.searchPlaceholder` placeholder, but it is inert (no handler, no state).
It stays untouched, so at `md+` the `/patients` page shows two search boxes, and only the
page's own search works. Wiring or removing the TopBar input is a separate change.

**i18n** — new `patients.list.*` keys in `pt.json`; dead keys (`recent`, `last48h`,
`allPatients`, `dob`) removed. `searchPlaceholder` stays (the TopBar uses it). Dates and times via `Intl.DateTimeFormat` in the current
locale.

## 7. Testing

xUnit + FluentAssertions against the real Postgres of `SharedAppHostFixture`, as in
`BreakGlassGrantConstraintTests`. Written test-first. There is no frontend test runner.

**`Patients/PatientSearchQueryTests.cs`** — calls `PatientSearchQuery.ExecuteAsync`
directly with an `AnamnysDbContext` on the fixture's database and a fixed `now`. Each
test seeds a fresh provider (own Guid) with its own patients, appointments and notes,
which isolates tests without cleanup and exercises scoping.

- Scoping: another provider's patients, appointments and notes never appear or count.
- Next appointment: ignores past, `cancelled`, `attended`, `no_show`; picks the earliest
  future one; null when none.
- Status: grouping of the most recent note (`ReadyForReview` → pending, `Exported` →
  signed, none → none); older signed + newer draft → pending.
- Filters: `search` over name or email, case-insensitive; `name`; `email`; multi
  `noteStatus`; date ranges including both boundary days in São Paulo time; `from`-only
  and `to`-only.
- Sorting: each `sortBy` asc and desc; nulls last both ways; stable tie-break; default is
  `nextVisit asc`.
- Pagination: `totalCount` reflects filters; `Skip`/`Take`; a page past the end is empty.

**`Patients/PatientSearchValidationTests.cs`** — pure unit tests: reversed ranges,
unknown enum values, over-long text, `page`/`pageSize` out of range → error keyed by
field.

**`Patients/PatientEndpointTests.cs`** — HTTP: no session → 401; `dev.provider` session
via `BrowserSession` → 200 with the response shape; invalid body → 400.

**Frontend** — `tsc -b` and `eslint` clean; manual browser check with seeded data at
desktop and mobile widths covering search, filters, sorting and paging.
