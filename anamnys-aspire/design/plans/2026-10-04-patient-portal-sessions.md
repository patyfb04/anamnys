# Patient Portal Sessions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Signed-in patients see their linked providers and, per provider, upcoming sessions and history.

**Architecture:** Static `PatientPortalQueries` starting from `Patients WHERE AccountId = account`; thin endpoints under `/api/phi/patients/me`. Patient app home becomes the provider list; new route `/profissionais/$providerId` with tabs.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, xUnit v3; React 19, TanStack Router/Query, Tailwind 4.

**Spec:** `design/specs/2026-10-04-patient-portal-sessions-design.md`

## Global Constraints

- Only `Patients`, `Providers` and `Appointments` are read; only `StartsAt`, `EndsAt`, `Timezone`, `Modality`, `Status` leave the server.
- Patient cookie only; unlinked provider → 404.
- Upcoming = `StartsAt > now` and `scheduled`/`confirmed`; past = every other appointment.

---

### Task 1: Queries + endpoints
- [ ] `Appointment` entity gains `EndsAt`, `Timezone`, `Modality` (existing columns).
- [ ] Tests `PatientPortalQueriesTests` (Postgres) per spec §5; `PatientPortalEndpointTests` (401 without / with provider session).
- [ ] `PatientPortal/PatientPortalQueries.cs`, `PatientPortal/PatientPortalEndpoints.cs`, register in `Program.cs`. Commit.

### Task 2: Patient app
- [ ] Types + `portalApi`; home list; `/profissionais/$providerId` with tabs and "Carregar mais"; status badge; i18n; build + lint. Commit.

### Task 3: Verify and document
- [ ] Seed appointments for the linked test patient; browser at desktop and 390px; full suite; docs chapter 14; gaps file. Commit.
