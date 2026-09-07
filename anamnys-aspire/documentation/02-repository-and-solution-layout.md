# Chapter 2: Repository & Solution Layout

## The git root vs. the application

The git repository root is `anamnys/`, on the `develop` branch. It contains almost
nothing of its own — no source code lives directly at the root. Instead, everything is
inside a single subdirectory:

```
anamnys/                     <- git root (branch: develop)
└── anamnys-aspire/          <- the whole project lives here
```

This is worth dwelling on, because the repository's history explains a trap a new
developer can otherwise walk straight into. An earlier version of this product had a
`backend/` (ASP.NET Core, not Aspire-orchestrated) and a `frontend/` (Next.js, not the
current Vite/React SPA setup) pair sitting at the git root, alongside `anamnys-aspire/`.
That pair was **deleted in commit `aee5233`**. If you ever find a reference to `backend/`
or `frontend/` — in an old design doc, a stray comment, an AI assistant's memory of an
earlier conversation — it is describing code that no longer exists. Anything from those
directories worth keeping was already migrated into `anamnys-aspire/`; anything not
present there today was deliberately left behind. Don't go looking for it, and don't cite
it as if it were current.

From this point on, "the project" and `anamnys-aspire/` mean the same thing.

## Top level of `anamnys-aspire/`

```
anamnys-aspire/
├── anamnys-aspire.AppHost/       Aspire orchestration entry point — see Chapter 4
├── anamnys-aspire.Server/        The .NET minimal API — see Chapter 5 onward
├── anamnys-aspire.Tests/         xUnit-style test project — see Chapter 16
├── apps/                         Four Vite + React SPAs, plus the Keycloak theme app
│   ├── web/                      Public marketing site — see Chapter 14
│   ├── provider/                 The clinical app — see Chapter 13
│   ├── patient/                  Patient portal (skeleton today) — see Chapter 14
│   ├── admin/                    Owner/staff app (skeleton today) — see Chapter 14
│   └── keycloak-theme/           Keycloakify login theme — see Chapter 15
├── packages/
│   └── shared/                   Code shared across all four SPAs — see Chapter 12
├── keycloak/                     Realm JSON, Dockerfile, theme build output — Chapter 7
├── design/
│   ├── specs/                    Design specifications (auth, Keycloak) — Chapter 6–8
│   └── plans/                    Implementation plans
├── .agents/                      Aspire-specific agent skills (developer tooling, not
│                                 part of the shipped application — see note below)
├── anamnys-aspire.sln            The .NET solution file tying the C# projects together
├── anamnys-db-script.sql         The full Postgres schema — see Chapter 10
├── package.json                  npm workspace root
├── package-lock.json
├── CLAUDE.md                     Guidance for AI coding assistants working in this repo
└── aspire.config.json
```

A note on `.agents/`: this holds a set of Aspire-related skills (deployment, monitoring,
orchestration guidance, and so on) intended for AI coding assistants working in this
repository, not application code. You can safely ignore this directory when you're
learning how the *product* works; it's tooling for how the repository is *developed*, one
layer removed from the thing itself.

## Two build systems sharing one tree

This is the detail that trips people up fastest: `anamnys-aspire/` is simultaneously the
root of a **.NET solution** and the root of an **npm workspaces monorepo**, and the
overlap between them is not accidental.

### The .NET side: `anamnys-aspire.sln`

The solution file lists eight projects:

| Project | Type | What it is |
|---|---|---|
| `anamnys-aspire.AppHost` | C# executable | The Aspire orchestrator — Chapter 4 |
| `anamnys-aspire.Server` | C# minimal API | The backend — Chapter 5 |
| `web` | `.esproj` | `apps/web`, wired into the solution via Aspire's JS project support |
| `provider` | `.esproj` | `apps/provider` |
| `patient` | `.esproj` | `apps/patient` |
| `admin` | `.esproj` | `apps/admin` |
| `anamnys-aspire.Tests` | C# test project | Chapter 16 |
| `apps` | solution folder | Just a grouping node in the `.sln`, not a real project |

Notice that four of the eight entries are `.esproj` files, not `.csproj` files. `.esproj`
is the MSBuild project type Aspire uses to represent a JavaScript/TypeScript application
inside a .NET solution, so that `dotnet build`, Visual Studio/Rider tooling, and — most
importantly — the AppHost's `AddViteApp()` calls (Chapter 4) can all treat these Vite apps
as first-class solution members, without pretending they're C# projects. `apps/
keycloak-theme` is **not** in the solution file; it's built and consumed independently
(Chapter 15).

### The npm side: workspaces

The root `package.json` declares:

```json
"workspaces": [
  "packages/*",
  "apps/*"
]
```

That's the entire monorepo topology from npm's point of view: every folder under
`packages/` and every folder under `apps/` (including `keycloak-theme`, even though it's
outside the `.sln`) is an npm workspace member. A single `npm install` at the repository
root resolves and hoists dependencies for all five JavaScript projects at once, and root
scripts like `npm run build` and `npm run lint` fan out to every workspace that defines
those scripts (`--workspaces --if-present`).

### Why both, and why it matters to you

The practical upshot: a change to a front-end app is a *JavaScript* change, run and
tested with `npm` commands from that app's directory (or the root, across all
workspaces) — see Chapter 3. But that same app is also a node in the .NET solution's
dependency graph, because the **AppHost** needs to know about it in C# to orchestrate it
(start it, wire environment variables into it, publish its build output into the server's
`wwwroot`). Two build systems, two sets of commands, one coordinated resource graph. You'll
see this concretely in Chapter 4, where `AddViteApp("provider", "../apps/provider")`
is the exact line that bridges the two worlds for one app.

## `packages/shared`: the thing all four apps actually import

One more structural fact worth internalizing before Chapter 12 covers it in depth:
`apps/web`, `apps/provider`, `apps/patient`, and `apps/admin` are not four independent
codebases that happen to look similar. They all depend on `@anamnys/shared`
(`packages/shared`), an npm workspace package that exports an API client, a UI component
kit, shared state, and i18n setup. When you're reading one of the SPAs and see an import
from `@anamnys/shared/...`, that's not a third-party dependency — it's sibling code in
this same repository, and a change there potentially affects all four apps at once.

## A quick map for "where do I make this change?"

| You want to... | Look in... |
|---|---|
| Change what resources start together, or how they're wired | `anamnys-aspire.AppHost/AppHost.cs` |
| Add or change an API endpoint | `anamnys-aspire.Server/` |
| Change how login/sessions work | `anamnys-aspire.Server/Auth/` and `keycloak/` |
| Add a database table or column | `anamnys-db-script.sql`, then map it in `anamnys-aspire.Server/Data/` |
| Change the provider clinical app's UI | `apps/provider/src/` |
| Change something shared across all four apps (a button, the API client) | `packages/shared/src/` |
| Change the public marketing site | `apps/web/src/` |
| Change the Keycloak login page's look | `apps/keycloak-theme/src/` |

With the map in hand, Chapter 3 gets you from a clean checkout to a running system.
