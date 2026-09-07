# Chapter 3: Environment Setup & First Run

This chapter gets a clean checkout of `anamnys-aspire` running on a new machine. Follow it
in order the first time — the steps have a real dependency chain, and doing them out of
order produces confusing failures rather than helpful errors, as noted throughout.

## Prerequisites

- **Docker must be running.** The AppHost starts several container-backed resources —
  Redis, Postgres, and Keycloak — directly. If Docker isn't up, `aspire run`/`aspire
  start` will fail trying to create them.
- **Apache Maven must be installed.** This isn't a Java project, but the Keycloak login
  theme is built with Keycloakify, which shells out to `mvn` to package the theme as a
  jar. No Maven, no theme build, no Keycloak image. See Chapter 15 for the full mechanics.
- The .NET SDK version this solution targets, Node (per the `engines` field in the root
  `package.json`: `^20.19.0 || >=22.12.0`), and the Aspire CLI.

## First-time setup: the four secrets

Before the AppHost will start at all, four parameters need values. They're declared in
`AppHost.cs` as `builder.AddParameter(..., secret: true)` with **no default**, which means
Aspire has nothing to fall back on — without them, `aspire run` stops and asks you to
resolve the missing parameters rather than starting with something wrong.

These four are:

- `Parameters:provider-client-secret`
- `Parameters:patient-client-secret`
- `Parameters:owner-client-secret`
- `Parameters:keycloak-admin-password`

(A fifth-looking one, `keycloak-admin-username`, needs no secret — it defaults to
`"admin"` directly in `AppHost.cs`. `postgres-password`, `keycloak-password`, and
`cache-password` are also not on this list; Aspire generates those itself on first run.)

### Why every developer sets their own values

User secrets are stored at `~/.microsoft/usersecrets/<UserSecretsId>/` — outside the
repository, deliberately never committed — which means **a working checkout on someone
else's laptop does not carry these values across.** Every developer, on every machine,
sets their own. This is safe specifically *because* nothing shared or deployed depends on
your particular value: each of the three client secrets is only ever compared against
itself, on one machine, in two places — Keycloak substitutes it into the realm JSON at
import time, and the server uses it for the token exchange. As long as those two agree
with each other locally, it doesn't matter what the value is or that it differs from a
teammate's. **Do not copy a secret value from a teammate** — generating your own is both
sufficient and strictly safer.

### Running it

`dotnet user-secrets` infers which project's secrets you're editing from the `.csproj` in
your current directory, and it needs to be run against the **AppHost** project
specifically — `anamnys-aspire.AppHost/`, not the outer `anamnys-aspire/` solution
directory. Passing `--project` sidesteps the question of where your shell happens to be:

```bash
# from anamnys-aspire/ (the solution directory)
P=anamnys-aspire.AppHost
dotnet user-secrets --project $P set "Parameters:provider-client-secret"  "$(openssl rand -hex 32)"
dotnet user-secrets --project $P set "Parameters:patient-client-secret"   "$(openssl rand -hex 32)"
dotnet user-secrets --project $P set "Parameters:owner-client-secret"     "$(openssl rand -hex 32)"
dotnet user-secrets --project $P set "Parameters:keycloak-admin-password" "$(openssl rand -hex 32)"

# check what landed (values are shown in plaintext, so don't run this on a shared terminal)
dotnet user-secrets --project $P list
```

### If you already ran the AppHost once before setting these

Realm import into Keycloak only happens when the realm doesn't already exist. If you ran
`aspire run` before these secrets were set, Keycloak already imported a realm using
whatever client secret was in play at that time (or none), and it will hold onto that
until the underlying data is wiped. The failure mode is not a helpful one — it's an opaque
client-authentication error, not anything that names the actual mismatch. The fix is to
wipe the data volumes and let import run fresh:

```bash
aspire stop
docker volume ls | grep -E 'keycloak-data|postgres-data'
docker volume rm <keycloak-data>    # one at a time — passing both names to one
docker volume rm <postgres-data>    # invocation can fail on some Docker daemons
```

You'll meet this exact recovery sequence again in Chapter 7, because it's also how you
recover from editing realm JSON directly.

## Running the application

All Aspire commands below are run from `anamnys-aspire/` (the solution directory):

```bash
aspire start        # background start — the form to use if an agent/script needs it
aspire run           # foreground + dashboard — for a human watching at a terminal
aspire stop           # stop everything — do this BEFORE any rebuild
aspire doctor          # environment diagnostics
```

The **"stop before rebuild" rule is not a suggestion.** A running Aspire app holds file
locks on `bin/` and `obj/`. If you see an `MSB3491` or `CS2012` error while building, that
almost always means "an Aspire run is still holding a lock on this output — run `aspire
stop` first," not that the project itself is broken. Chase the lock before you chase a
phantom compile error.

## Running the frontend apps directly

You don't strictly need to run the SPAs outside of the AppHost — `AddViteApp` starts them
as part of the resource graph — but for iterating on one app in isolation, the usual Vite
commands work from that app's directory (`apps/web`, `apps/provider`, `apps/patient`,
`apps/admin`, or `apps/keycloak-theme`), or from the repository root to hit every
workspace at once:

```bash
npm run dev     # vite — starts this app's dev server
npm run build   # tsc -b && vite build — root runs this across every workspace
npm run lint    # eslint — root runs this across every workspace
```

## A sane first hour

Putting the above in order, here's a reasonable sequence for a genuinely first run:

1. Confirm Docker is running and Maven is installed (`docker info`, `mvn -version`).
2. Clone the repo, `cd anamnys-aspire`.
3. `npm install` at the root, to populate all five JavaScript workspaces at once.
4. Set the four user secrets against the AppHost project, as above.
5. `aspire run` from `anamnys-aspire/`, and watch the dashboard. Expect Redis, Postgres
   (with its two logical databases), Keycloak, the server, and all four SPAs to come up.
6. If Keycloak or the server fail to authenticate against each other with an opaque
   error, and you ran the AppHost even once before step 4, go straight to the volume-wipe
   recovery above rather than debugging the error message itself.
7. Once everything is green in the dashboard, you have a fully orchestrated local
   environment — a good moment to move on to Chapter 4 and actually read the file that
   just did all of that for you.

## A few gotchas worth knowing before you hit them

These are covered in depth in their relevant chapters, but worth flagging now so you
recognize them rather than assume something is broken:

- Each app's generated route file (`src/routeTree.gen.ts`) **must stay committed.**
  `npm run build` runs `tsc -b` *before* Vite regenerates it, so a clean checkout without
  it fails to build. If one goes missing, regenerate it with a bare `npx vite build` in
  that app's directory.
- TypeScript is deliberately pinned at `~6.0.3`, not the newest release — see Chapter 11
  for why, but don't "helpfully" bump it.
- `dotnet test` does not work in this solution — see Chapter 16 for the real command
  (`dotnet run --project anamnys-aspire.Tests`) and why the obvious one is a trap.

With the environment running, Part 3 starts where the system itself starts: the AppHost.
