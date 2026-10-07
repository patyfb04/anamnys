# Provider Calendar

**Date:** 2026-10-05
**Status:** Approved
**Scope:** Delivery 1 of 5 of the scheduling work. A signed-in provider sees their appointments
in a week or day grid and creates, reschedules, cancels and updates the status of one-off
appointments. Closes the "Appointments have no write path" gap in
`design/gaps/2026-10-03-patient-flows-gaps.md`.

Later deliveries, each with its own spec and plan:

1. **Provider calendar (this spec)** — week/day grid, one-off appointment CRUD and status.
2. **Availability** — weekly working hours (`AvailabilityRules`) and blocks / vacation /
   holidays (`AvailabilityExceptions`) rendered on the grid.
3. **Recurrence** — weekly / fortnightly series (`AppointmentSeries`).
4. **Patient self-booking** — free slots, holds (`BookingHolds`), `BookingPolicies`,
   `ServiceOfferings`.
5. **Google Calendar sync** — `CalendarConnections`.

---

## 1. Context

- `Appointments` already exists in the schema with every column this delivery needs:
  `ProviderId`, `PatientId`, `StartsAt`, `EndsAt`, `Timezone` (default `America/Sao_Paulo`),
  `Modality` (`online` | `presencial`), `Status` (`scheduled`, `confirmed`, `attended`,
  `cancelled`, `no_show`), `CreatedBy`, `CancelledAt`, `CancelledBy`, `CancellationReason`,
  `CreatedAt`.
- `Appointments_no_overlap` is `UNIQUE ("ProviderId", "Slot" WITHOUT OVERLAPS)`, where `Slot`
  is a generated `tstzrange` that is `NULL` for cancelled rows. The database already forbids
  two live appointments of one provider overlapping; a cancelled one frees its slot.
- `Appointments_Cancel_ck` requires `CancelledAt IS NOT NULL` exactly when
  `Status = 'cancelled'`.
- The EF `Appointment` entity maps a read-only column subset; inserting through it fails by
  design. Readers today: the patient list (next appointment), the patient record
  ("Próximo/Último atendimento") and the patient portal sessions pages.
- `Patients.LastVisit` exists and nothing updates it.
- The provider sidenav already links `/calendar` (label "Agenda") behind the `calendar`
  feature flag; the provider app has no such route.
- UI reference: `specs/UI/SchedulerCalendar/` (`screen.png`, `code.html`, `DESIGN.md`). Its
  tokens ("Serene Clinical") are already in `packages/shared/src/styles/theme.css`.

## 2. Decisions

| Topic | Decision |
|---|---|
| Views | Week and Day. Month is out. At mobile width (< 768px) the page forces Day. |
| Grid | Built in-house with Tailwind and absolute positioning per minute. No calendar library: `no_overlap` rules out concurrent events, the hard part a library would solve. |
| Appointment kind | One-off only. No series, no service offering, no price. |
| Time zone | Every appointment is created in `America/Sao_Paulo` (the column default; `Providers` has no time zone column). Display uses the appointment's own `Timezone`; the grid header shows the offset (e.g. `GMT-3`). |
| Conflicts | The database decides. No pre-check; exclusion violation `23P01` maps to `409`. |
| Attended | Sets `Patients.LastVisit`. No `Sessions` row: clinical sessions belong to the notes feature. |
| Cancelled | Terminal. Rescheduling a cancelled appointment means creating a new one. |
| Audit | None in this delivery. `AuditEntries` requires a note or document; scheduling is neither. PHI read logging stays in the existing `AccessLogs` gap. |
| Drag and drop | Out. Rescheduling is done in the details panel. |
| Mockup elements left out | "All Clinicians" (the app is per provider), "All Session Types" (needs `ServiceOfferings`), "Lunch Break" blocks (delivery 2), "Synced with Google Calendar" (delivery 5). |

## 3. API

Provider cookie, inside the existing `phi` group. The provider id is resolved from the
session exactly as `PatientRecordEndpoints` does, and every query filters by it. An
appointment or patient owned by another provider is `404`, the same as one that does not
exist.

Files, following `Patients/`: `Appointments/AppointmentEndpoints.cs` (thin handlers),
`Appointments/Appointments.cs` (logic), `Appointments/AppointmentContracts.cs` (records and
`Validate()`).

### `GET /api/phi/providers/me/appointments?from=…&to=…&status=…`

- `from`, `to`: ISO instants, required, `to > from`, range at most 42 days → else `400`.
- `status`: optional, repeatable; filters by status.
- Returns appointments with `StartsAt < to AND EndsAt > from`, ordered by `StartsAt`:

```json
{ "items": [ {
    "id": "…", "patientId": "…", "patientName": "…",
    "startsAt": "…", "endsAt": "…", "timezone": "America/Sao_Paulo",
    "modality": "online", "status": "scheduled", "cancellationReason": null
} ] }
```

Cancelled appointments are included (the grid shows them struck through) unless a `status`
filter excludes them.

### `POST /api/phi/providers/me/appointments`

Body: `{ "patientId", "startsAt", "durationMinutes", "modality" }`.

- `durationMinutes` 5–480; `modality` `online` | `presencial` → else `400`.
- Patient must belong to the provider → else `404`; must not be archived → else `422`.
- Inserts with `EndsAt = StartsAt + duration`, `Status = 'scheduled'`,
  `CreatedBy = 'provider'`, `Timezone = 'America/Sao_Paulo'`.
- `201 { "id" }`; overlap → `409 { "message": "Horário já ocupado." }`.

### `PUT /api/phi/providers/me/appointments/{id}`

Body: `{ "startsAt", "durationMinutes", "modality" }`. Same validation as create. Only for
`scheduled` or `confirmed` → else `409`. Overlap → `409` as above. `204`.

### `POST /api/phi/providers/me/appointments/{id}/status`

Body: `{ "status", "reason"? }`. `reason` only with `cancelled`, at most 500 characters.

| From | Allowed to |
|---|---|
| `scheduled` | `confirmed`, `attended`, `no_show`, `cancelled` |
| `confirmed` | `scheduled`, `attended`, `no_show`, `cancelled` |
| `attended`, `no_show` | `scheduled` (undo a misclick) |
| `cancelled` | — |

- `attended` and `no_show` require `StartsAt <= now` → else `409`.
- Any other transition → `409`.
- `cancelled` sets `CancelledAt = now`, `CancelledBy = 'provider'`, `CancellationReason`.
- Moving to `attended` sets `Patients.LastVisit = max(LastVisit, StartsAt)`. Moving away from
  `attended` recomputes `LastVisit` as the latest `StartsAt` of the patient's remaining
  `attended` appointments with this provider, or leaves it unchanged if there are none.
  Both happen in the same transaction as the status change.
- `204`.

### EF entity

`Appointment` maps the writable columns it needs (`CreatedBy`, `CancelledAt`, `CancelledBy`,
`CancellationReason`, `CreatedAt`). `Slot` stays unmapped. The "read-only" comment is
replaced. No schema change: the table already exists.

## 4. Frontend (provider app)

- Route `apps/provider/src/routes/_app/calendar.tsx`. Search params:
  `view=week|day` (default `week`), `date=YYYY-MM-DD` (default today), `status` (optional).
  State lives in the URL, as on the patient list.
- `apps/provider/src/api/appointments.ts` and TanStack Query hooks; every mutation
  invalidates the visible range.
- Header: title "Agenda", period navigator (‹ 5–11 de out. de 2026 ›), "Hoje" button,
  Dia | Semana toggle, primary button "Nova consulta". Filter bar with a status filter only.
- Grid: hour column 07:00–21:00, scrolls to 07:00–08:00 on load; appointments outside that
  window extend the visible range. Time zone offset label, today's column highlighted,
  current-time line, weekend columns dimmed.
- Appointment card: time range, patient name, modality icon (video = online, person =
  presencial), status chip. Colors: Agendada (primary container), Confirmada (secondary
  container), Realizada (surface container, muted), Falta (error container), Cancelada
  (struck through, muted).
- Clicking an empty grid cell opens "Nova consulta" prefilled with that day and time rounded
  down to 15 minutes. Clicking a card opens the details panel.
- "Nova consulta" dialog: patient (search over the provider's non-archived patients, reusing
  the patient search endpoint), date, start time, duration (default 50), modality (default
  online).
- Details panel: patient name linking to the record, date and time, modality, status. Actions
  by current status per the table in §3: Confirmar, Realizada, Falta, Cancelar (optional
  reason), Desfazer (back to Agendada), Editar (reopens the dialog in edit mode).
- `409` shows a toast with the server message; `422` on create shows "Paciente arquivado".
- All strings in `packages/shared/src/lib/i18n/locales/pt.json`.

## 5. Testing

- Unit: request `Validate()` (ranges, duration bounds, modality, reason length); the
  transition table.
- Integration against Postgres (`SharedAppHostFixture`, as the patient tests):
  - another provider's appointment or patient → `404` on every endpoint;
  - overlapping create and overlapping reschedule → `409`; back-to-back (`EndsAt` equal to the
    next `StartsAt`) is allowed;
  - cancelling frees the slot for a new appointment;
  - `attended` sets `LastVisit`; undoing recomputes it;
  - `attended` on a future appointment → `409`;
  - archived patient → `422`;
  - list returns only the requested range and respects the `status` filter.
- Browser, manual: create, reschedule, each status action and conflict toast on desktop week
  view and at 390px day view; seeded appointments appear; the patient record's
  "Próximo/Último atendimento" and the patient portal reflect changes.
