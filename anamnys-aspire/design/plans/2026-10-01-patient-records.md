# Patient Records Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create (tabbed modal), view, edit, archive and delete provider-owned patients
with diagnoses, medications and plan objectives.

**Architecture:** Thin minimal-API endpoints over a static `PatientRecords` class that
takes `(db, providerId, …)` and enforces ownership on every call; contracts validate
themselves. React modal + detail page over `patientsApi`.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, xUnit v3 + FluentAssertions; React 19,
TanStack Query/Router, Tailwind 4, i18next.

**Spec:** `design/specs/2026-10-01-patient-records-design.md`

## Global Constraints

- Patient lookup is always `Id == id && ProviderId == providerId`; a miss is 404.
- No PHI in URLs or logs; ids only in paths.
- Validation messages in Portuguese, camelCase keys.
- Tests run with `dotnet run --project anamnys-aspire.Tests -- -class "*Name*"` (never `dotnet test`).
- `aspire stop` before any .NET build if the app is running.

---

### Task 1: Schema and entities
- [ ] Add `ArchivedAt` to `Patients`, the `PatientDiagnoses` table, index and FK to `anamnys-db-script.sql`.
- [ ] Write `design/migrations/2026-10-01-patient-records.sql` (idempotent) and apply it to the local dev database.
- [ ] Entities `PatientDiagnosis`, `MedicationEntry`, `TreatmentPlan`, `PlanObjective`; `Patient.ArchivedAt`; map in `AnamnysDbContext`.
- [ ] Search: `Archived` on the request, filter in `PatientSearchQuery`; test `Archived_PatientsHiddenUnlessRequested`.
- [ ] Commit `feat: add patient diagnoses table and archived flag`.

### Task 2: Contracts and validation
- [ ] Tests `PatientRecordValidationTests` (required names, future/ancient DOB, item rules, list caps, keyed errors).
- [ ] `PatientRecordContracts.cs` with `Validate(DateOnly today)` on each request.
- [ ] Commit `feat: add patient record contracts with validation`.

### Task 3: Data operations
- [ ] Tests `PatientRecordsTests` against Postgres (create aggregate, get, cross-provider NotFound for each operation, update, delete/conflict cases, archive/unarchive, clinical CRUD, objective creates plan).
- [ ] `PatientRecords.cs`.
- [ ] Commit `feat: add provider-scoped patient record operations`.

### Task 4: Endpoints
- [ ] Tests `PatientRecordEndpointTests` (401, create→get→delete round trip as dev.provider, 400, 404).
- [ ] `PatientRecordEndpoints.cs`, registered in `Program.cs`.
- [ ] Commit `feat: expose patient record endpoints`.

### Task 5: Frontend
- [ ] Types + `patientsApi`.
- [ ] `Modal`, `PatientFormModal` (create tabs / edit), list page wiring, full width, archived filter.
- [ ] Detail page + cards; remove `/patients/new` and `PatientDetailView`.
- [ ] i18n; `npm run build` + `npm run lint` for provider.
- [ ] Commit `feat: patient create modal, detail page, edit, archive and delete`.

### Task 6: Verify and document
- [ ] Full test suite; browser walkthrough; documentation chapter 13.
- [ ] Notion "Modelo de Dados": add `PatientDiagnoses` and `Patients.ArchivedAt`.
