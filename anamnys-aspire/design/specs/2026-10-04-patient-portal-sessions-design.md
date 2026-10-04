# Patient Portal: Providers and Sessions

**Date:** 2026-10-04
**Status:** Approved
**Scope:** Delivery 3 of 3 of the patient-portal work. A signed-in patient sees the providers
their account is linked to and, per provider, upcoming sessions and session history.
Read-only. Builds on `2026-10-02-patient-accounts-design.md` (accounts vs records) and
`2026-10-03-portal-invitation-design.md` (how a record gets linked).

---

## 1. Context

- A record (`Patients`) is linked to a portal account by `Patients.AccountId`, set only by
  accepting an invitation and cleared by the provider's "Remover acesso".
- `Appointments` holds `ProviderId`, `PatientId`, `StartsAt`, `EndsAt`, `Timezone`
  (default `America/Sao_Paulo`), `Modality` (`online` | `presencial`) and `Status`
  (`scheduled`, `confirmed`, `attended`, `cancelled`, `no_show`). Nothing writes appointments
  yet (no scheduling UI); the portal shows whatever exists, which today is seeded data.
- Data-model rule: the portal never reaches the clinical record — no notes, diagnoses,
  medications, scales or documents.
- The patient app's home is a placeholder; its session is realm-scoped
  (`/api/auth/me?realm=patient`).

## 2. Decisions

| Topic | Decision |
|---|---|
| Actions | Read-only. Cancelling and rescheduling belong to the scheduling delivery (booking policies). |
| History | All past sessions with their status: Realizada, Cancelada, Falta. |
| Upcoming | `StartsAt > now` and status `scheduled` or `confirmed`. |
| Archived records | Still shown: the schedule is the patient's; archiving is the provider's own organisation. |
| Unlinked providers | Not shown; requesting one is `404`. |
| Time display | In the appointment's own `Timezone`. |

## 3. API

Patient cookie only (`AuthSchemes.PatientCookie`); the account id is the session's
`LocalId`. Every query starts from `Patients WHERE AccountId = account`.

`GET /api/phi/patients/me/providers` →

```json
[{ "providerId": "…", "name": "Dra. Clara Mendes", "crp": "06/12345 SP",
   "nextSession": { "startsAt": "…", "endsAt": "…", "timezone": "America/Sao_Paulo",
                    "modality": "online", "status": "confirmed" } }]
```

One item per linked record, ordered by name. `crp` is null when the provider has none;
`nextSession` is null when there is no upcoming session.

`GET /api/phi/patients/me/providers/{providerId}/sessions?scope=upcoming|past&page=1` →
`{ "items": [Session], "totalCount": n, "page": 1, "pageSize": 20 }`.

- `upcoming`: `StartsAt > now` and status `scheduled`/`confirmed`, ascending.
- `past`: every other appointment of that record (started already, or cancelled / no-show
  even if in the future), descending by `StartsAt`.
- `scope` other than those two, or `page < 1` → `400`.
- No record of that provider linked to the account → `404`.

Only `StartsAt`, `EndsAt`, `Timezone`, `Modality` and `Status` leave the server. No other
table than `Patients`, `Providers` and `Appointments` is read.

Server layout: `PatientPortal/PatientPortalQueries.cs` (static, `(db, accountId, …)`) and
`PatientPortal/PatientPortalEndpoints.cs`. No schema change.

## 4. Frontend (`apps/patient`)

- **Home `/`** → "Meus profissionais": a card per provider with name, CRP and the next
  session ("qui., 9 de out. · 14:00–14:50 · Online") or "Nenhuma sessão agendada"; links to
  the provider page. Empty: "Você ainda não está conectada(o) a nenhum profissional. Peça um
  convite ao seu profissional para acompanhar suas sessões aqui."
- **`/profissionais/$providerId`** → header (name, CRP, back link), tabs **Próximas** and
  **Histórico**. Rows: weekday and date, start–end time, Online/Presencial, status badge.
  History has "Carregar mais". `404` → "Profissional não encontrado" with a back link.
- The invitation page's "Ir para o início" lands on the list.
- Mobile: full-width cards, tabs on top.

## 5. Testing

- Postgres: only linked providers listed; another account sees nothing; removing access
  hides the provider; `nextSession` skips past, cancelled and no-show; `upcoming`/`past`
  split, order and paging; unlinked or foreign provider `404`; archived record still listed.
- HTTP: `401` without a patient session, including with a provider session.
- Browser: the linked test patient with seeded appointments (future, attended, cancelled,
  no-show): list → provider page → both tabs, desktop and 390px.

## 6. Out of scope

Cancelling, rescheduling and booking; patient-side unlinking and account deletion (LGPD
gaps list); reminders.
