# Patient Portal Invitation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Providers invite a patient to the portal by email; the patient accepts with the same email and the record gets linked to their account.

**Architecture:** `PatientInvitations` table (hashed single-use tokens). `PatientInvitations` static operations take `(db, providerId|accountId, IEmailSender, …)`; thin endpoints map outcomes. `IEmailSender` has Resend and Mailpit HTTP implementations chosen by configuration. Provider record page gets a portal block; patient app gets `/convite/$token`.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, xUnit v3 + FluentAssertions; React 19, TanStack Router/Query, Tailwind 4.

**Spec:** `design/specs/2026-10-03-portal-invitation-design.md`

## Global Constraints

- Never log or store the plaintext token; store SHA-256 only.
- Tests must never reach Resend: the test fixture empties `Parameters:resend-api-key`.
- Apply the migration to the dev database before running Postgres tests.
- Error codes for accept: `invalid`, `email_mismatch`, `already_linked`.
- Portal status values: `none`, `invited`, `expired`, `active`.

---

### Task 1: Schema, migration, entity
- [ ] `anamnys-db-script.sql`: table, partial unique index, check constraints, FKs.
- [ ] `design/migrations/2026-10-04-patient-invitations.sql`, idempotent; extend `PatientAccountsMigrationTests`' no-op test pattern with a check that the bootstrap schema plus this migration is a no-op (`Migration_OnTheCurrentBootstrapSchema_IsANoOp` runs all post-bootstrap migrations).
- [ ] `PatientInvitation` entity + mapping. Apply migration to dev DB. Commit.

### Task 2: Email senders
- [ ] `Email/EmailMessage.cs`, `IEmailSender`, `ResendEmailSender`, `MailpitEmailSender`, `EmailServiceCollectionExtensions.AddEmail(IConfiguration)` (registers none when `Email:Provider` absent).
- [ ] Unit tests with a capturing `HttpMessageHandler`: Resend request shape (URL, bearer, JSON fields); Mailpit request shape; failure status → `EmailSendException`.
- [ ] AppHost: `Email__Provider`, `Email__ResendApiKey`, `Email__From`, `Email__FromName`, `Email__MailpitBaseUrl`, `PatientPortal__BaseUrl` on the server; test fixture forces Mailpit. Commit.

### Task 3: Invitation operations
- [ ] `Patients/PatientInvitationTokens.cs` (generate, hash) + unit tests.
- [ ] `Patients/PatientInvitationMessage.cs` (compose subject/text/html) + unit test.
- [ ] `Patients/PatientInvitations.cs`: `InviteAsync`, `CancelAsync`, `RemoveAccessAsync`, `AcceptAsync`, `StatusAsync`.
- [ ] Postgres tests (`PatientInvitationsTests`) per spec §7 with a fake sender. Commit.

### Task 4: Endpoints + record detail status
- [ ] `PatientInvitationEndpoints.cs` (provider routes + patient accept). `PatientDetailResponse` gains `PortalStatus`, `InvitationSentAt`, `InvitationExpiresAt`.
- [ ] HTTP tests: 401s; dev.provider invites a throwaway record, message read back from Mailpit; accept with provider session → 401. Commit.

### Task 5: Provider UI
- [ ] API client + types; portal block on record page; opt-in checkbox in create modal; i18n. Build + lint. Commit.

### Task 6: Patient UI
- [ ] `/convite/$token` route outside `_app`; accept call; states; i18n. Build + lint. Commit.

### Task 7: Verify and document
- [ ] Full suite; browser walkthrough (spec §7); docs chapter 13 + patient app chapter. Commit.
