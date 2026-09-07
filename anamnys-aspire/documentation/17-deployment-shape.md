# Chapter 17: Deployment Shape

There is no CI/CD pipeline committed to this repository yet — no workflow files, no
deployment scripts, nothing under a `.github/workflows` or equivalent directory. What
*does* exist is a set of decisions, already made and already enforced in code, about what
a real deployment has to look like and what it has to supply for the application to even
start. This chapter is about that shape, since it's what any future pipeline will have to
satisfy — not a description of a pipeline that exists today.

## One command, two very different outcomes

`aspire run`/`aspire start` and `aspire publish`/`aspire deploy` read the *same*
`AppHost.cs`, but Chapter 4 already showed you the mechanism that makes their output
diverge sharply: `builder.ExecutionContext.IsRunMode` is `true` only for the former. The
same file that orchestrates five separate local processes during development produces,
for a real deployment, a **single container** — the server, with all four SPAs' built
assets baked into sub-paths of its own `wwwroot` via `PublishWithContainerFiles` — plus a
**separately built Keycloak image** from `keycloak/Dockerfile`, with `INCLUDE_DEV_SEED`
correctly forced to `false` because `IsRunMode` is false regardless of what
`ASPNETCORE_ENVIRONMENT` happens to be set to on whatever machine ran the publish command.

This matters enough to restate plainly: **the same Dockerfile builds Keycloak for both
`aspire start` and `aspire publish`.** There is no separate "production Keycloak image."
The dev/production difference lives entirely in that one build argument, computed from
`IsRunMode && IsDevelopment()`, and Chapter 7's `strip-dev-seed.jq` filter is what actually
acts on it. If a real deployment's Keycloak ever came up with the seeded `dev.provider`
account still present, the bug is upstream of Keycloak entirely — in how that image was
built, not in Keycloak's own configuration.

One more deliberate choice worth restating from Chapter 6's design documents: **realm
seeding never uses Aspire's `WithRealmImport`.** That mechanism is development-only and is
silently dropped by `aspire publish` — its documented failure mode is a Keycloak that comes
up looking perfectly healthy (its own readiness check passes) while every login 404s and
the API can't fetch JWKS, because no realm was actually imported at all. The custom
Dockerfile approach exists specifically so that development and production seed by the
**identical mechanism**, removing that entire failure class rather than working around it.

## What a real deployment has to supply that this repository deliberately doesn't

Two hard startup gates, both covered in Chapter 8, only bind outside Development, and
neither has a fallback anywhere in the committed configuration:

- **`DataProtection:CertificateThumbprint`** — without it, `AuthenticationSetup.cs` throws
  on startup rather than persisting DataProtection keys to Redis unencrypted. A real
  deployment needs an actual certificate provisioned into the environment's certificate
  store and its thumbprint supplied as configuration; nothing in this repository generates
  or manages that certificate for you.
- **`KEYCLOAK_ADMIN_USERNAME` / `KEYCLOAK_ADMIN_PASSWORD` / `KEYCLOAK_ADMIN_BASE_ADDRESS`**
  — without all three, `DevOnlyTestClientGuard` throws before the server ever starts
  accepting traffic. `KEYCLOAK_ADMIN_BASE_ADDRESS` in particular has to be a real, absolute
  URL reachable from wherever the server runs — Chapter 4 already flagged that this can't
  be the `https+http://keycloak` service-discovery scheme Aspire uses everywhere else,
  since that scheme has no meaning outside Aspire's own orchestration.

Checking `anamnys-aspire.Server/appsettings.json` confirms this isn't an oversight — it
contains only generic logging configuration and `"AllowedHosts": "*"`. **None of the
production-required settings above appear anywhere in a committed config file**, which is
exactly the intended shape: a real deployment's own configuration or secrets system is
where these values are meant to live, never a file that ships with the source.

The same logic extends to the three OIDC client secrets from Chapter 3 — a real deployment
needs its own values for `provider-client-secret`, `patient-client-secret`, and
`owner-client-secret`, generated and stored the way that environment's own secrets
management works, with no expectation of reusing anything from a developer's machine or
from this repository.

## TLS and realm hardening outside Development

A few settings from earlier chapters flip specifically based on environment, and it's
worth having the full list in one place:

- `RequireHttpsMetadata` on both the OIDC and JWT bearer handlers (Chapter 8) is
  `!IsDevelopment()` — real TLS is required for Keycloak metadata discovery outside a local
  run.
- `sslRequired` in each realm gets tightened from `"external"` to `"all"` by
  `strip-dev-seed.jq` (Chapter 7) — the same filter pass that strips dev users and test
  clients also raises this bar, since `"external"` behind a real TLS-terminating ingress
  would otherwise let Keycloak treat every peer as trusted.
- The health check endpoints (`/health`, `/alive`) are only mapped when
  `IsDevelopment()` is true (Chapter 5) — exposing health check detail outside development
  has its own security implications the Aspire template itself calls out, so this
  repository simply doesn't expose them at all outside dev rather than trying to secure
  them.

## Where deployment tooling would go, when that day comes

`.agents/skills/aspire-deployment/` (mentioned briefly in Chapter 2) contains detailed
reference material for deploying an Aspire application — AWS, Azure, Docker Compose,
Kubernetes, and CI/CD pipeline guidance. It's worth knowing this exists, but also worth
being precise about what it is: **guidance for an AI coding assistant to draw on when
asked to help set up deployment, not a deployment pipeline that already exists in this
repository.** Nothing in `.agents/` runs automatically or is part of the shipped
application. If you're asked to actually stand up a deployment pipeline for this project,
that's a reasonable place to start reading, but expect to be building the pipeline itself
from scratch — the hard requirements this chapter just covered are what it will need to
satisfy, not a solved problem it can lean on.

Chapter 18 closes out this part with the conventions this codebase expects every change,
deployed or not, to follow.
