# Anamnys

A specialty-native clinical note drafting system for solo and small-group mental health
and physical therapy practices. A provider dictates a session, Whisper transcribes it,
a local LLM structures the transcript into a clinical note (SOAP/DAP), and a billing
engine derives CPT/ICD-10 codes with a denial-risk score — the provider reviews, signs,
and exports.

## Where the code lives

```
anamnys/                     <- git root (branch: develop)
└── anamnys-aspire/          <- the whole project: the only source tree
    ├── anamnys-aspire.AppHost/   AppHost.cs — resource graph (.NET Aspire)
    ├── anamnys-aspire.Server/    minimal API + service defaults
    ├── apps/                     four Vite + React + TanStack Router SPAs:
    │                             web, provider, patient, admin
    ├── apps/keycloak-theme/      Keycloakify theme for the Keycloak login pages
    ├── keycloak/                 realm JSON, Dockerfile, theme build output
    ├── documentation/            the engineering handbook (start at 00-index.md)
    └── design/                   specs and dated implementation plans
```

There is nothing outside `anamnys-aspire/` — no separate backend or frontend
repo, past or planned.

## Start here

- **New to the codebase?** Read
  [`anamnys-aspire/documentation/00-index.md`](anamnys-aspire/documentation/00-index.md)
  — a from-zero engineering handbook, eighteen chapters from product vision through
  deployment shape. It also names, plainly, which parts of the data model and frontend
  describe functionality that has no server-side implementation yet.
- **Setting up a machine for the first time?**
  [`anamnys-aspire/CLAUDE.md`](anamnys-aspire/CLAUDE.md) has the exact commands —
  user secrets, Docker/Maven prerequisites, `aspire start`, and the gotchas that bite
  a fresh clone.
- **Working on a specific feature?** Check `anamnys-aspire/design/specs/` (what's
  built) and `anamnys-aspire/design/plans/` (dated, in-progress design docs) before
  writing code — a spec for the area you're touching probably already exists.

## Tech stack, at a glance

.NET 10 / ASP.NET Core Minimal APIs orchestrated by .NET Aspire, PostgreSQL, Redis,
and Keycloak (three realms: providers, patients, owners) — all Aspire-managed
containers, both locally and in production. Four React 19 + Vite + TanStack Router
SPAs, same-origin with the API via a Backend-for-Frontend: the server holds tokens,
the browser holds only an `HttpOnly` session cookie.

## A constraint worth knowing up front

Anamnys handles PHI. Every AI inference step runs in-process on the server (no cloud
AI APIs), and every PHI-touching dependency is self-hosted, not a managed third-party
service, unless it has a signed BAA. This is treated as a hard product requirement
throughout the codebase, not a suggestion — see CLAUDE.md's *HIPAA constraints*
section before introducing a new external dependency.

## Status

Actively developed on feature branches off `develop`. Large parts of the intended
product (real-time transcription, insurance billing, a provider marketplace) are
already visible in the data model and frontend but have no backend behind them yet —
the handbook's introduction chapter explains how to tell which is which.
